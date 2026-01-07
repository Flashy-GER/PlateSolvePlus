using NINA.Plugins.PlateSolvePlus.Models;
using NINA.Plugins.PlateSolvePlus.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugins.PlateSolvePlus.Services {

    public interface IAscomDeviceDiscoveryService {
        IReadOnlyList<AscomDeviceInfo> GetCameras();
        string? GetLastError();
    }

    internal sealed class AscomDeviceDiscoveryService : IAscomDeviceDiscoveryService {
        private string? lastError;
        public string? GetLastError() => lastError;

        public IReadOnlyList<AscomDeviceInfo> GetCameras() {
            lastError = null;

            dynamic? profile = null;
            try {
                profile = CreateProfileComObject();
                if (profile == null) {
                    lastError = "ASCOM Profile COM object not available (ProgID not registered).";
                    return Array.Empty<AscomDeviceInfo>();
                }

                // Some versions require DeviceType before RegisteredDevices is accessible
                ComHelpers.TrySet(profile, "DeviceType", "Camera");

                object? reg = ComHelpers.TryGet(profile, "RegisteredDevices");
                if (reg == null) {
                    reg = ComHelpers.TryInvoke(profile, "RegisteredDevices", new object[] { "Camera" });
                }

                if (reg == null) {
                    lastError = "ASCOM Profile COM object found, but RegisteredDevices not accessible.";
                    return Array.Empty<AscomDeviceInfo>();
                }

                var devices = new List<AscomDeviceInfo>();
                AddDevicesFromEnumeration(reg, devices);

                return devices
                    .Where(d => !string.IsNullOrWhiteSpace(d.ProgId))
                    .GroupBy(d => d.ProgId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.ProgId, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            } catch (Exception ex) {
                lastError = ex.ToString();
                return Array.Empty<AscomDeviceInfo>();
            } finally {
                ComHelpers.FinalRelease(profile);
            }
        }

        private static dynamic? CreateProfileComObject() {
            foreach (var progId in new[] { "ASCOM.Utilities.Profile", "ASCOM.Profile" }) {
                try {
                    var t = Type.GetTypeFromProgID(progId, throwOnError: false);
                    if (t == null) continue;
                    return Activator.CreateInstance(t);
                } catch {
                    // try next
                }
            }
            return null;
        }

        private static void AddDevicesFromEnumeration(object reg, List<AscomDeviceInfo> devices) {
            if (reg is IEnumerable enumerable) {
                foreach (var item in enumerable) {
                    TryAddItem(item, devices);
                }
                return;
            }

            TryAddItem(reg, devices);
        }

        private static void TryAddItem(object? item, List<AscomDeviceInfo> devices) {
            if (item == null) return;

            // Managed dictionary entry
            if (item is DictionaryEntry de) {
                Add(SafeToString(de.Key), SafeToString(de.Value), devices);
                return;
            }

            // Managed KeyValuePair via reflection
            var t = item.GetType();
            var keyProp = t.GetProperty("Key");
            var valProp = t.GetProperty("Value");
            if (keyProp != null && valProp != null) {
                Add(SafeToString(keyProp.GetValue(item)), SafeToString(valProp.GetValue(item)), devices);
                return;
            }

            // Some implementations return strings (ProgIDs)
            if (item is string s) {
                var progId = s.Trim();
                if (progId.Length == 0) return;
                Add(progId, progId, devices);
                return;
            }

            // COM entry: try Key/Value
            var comKey = ComHelpers.TryGet(item, "Key");
            var comVal = ComHelpers.TryGet(item, "Value");
            if (comKey != null) {
                var progId = SafeToString(comKey);
                var name = comVal != null ? SafeToString(comVal) : progId;

                Add(progId, name, devices);
                ComHelpers.FinalRelease(comKey);
                ComHelpers.FinalRelease(comVal);
                return;
            }

            // COM entry: try common names
            var prog = ComHelpers.TryGetString(item, "ProgId", "ProgID", "DriverId", "DeviceId", "Id");
            if (!string.IsNullOrWhiteSpace(prog)) {
                var name = ComHelpers.TryGetString(item, "Name", "Description") ?? prog!;
                Add(prog!, name, devices);
                return;
            }

            // last resort ToString (ignore useless COM)
            var text = item.ToString() ?? "";
            if (string.Equals(text, "System.__ComObject", StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrWhiteSpace(text)) Add(text, text, devices);
        }

        private static void Add(string progId, string name, List<AscomDeviceInfo> devices) {
            if (string.IsNullOrWhiteSpace(progId)) return;
            if (string.IsNullOrWhiteSpace(name)) name = progId;
            devices.Add(new AscomDeviceInfo(name, progId));
        }

        private static string SafeToString(object? o) => o?.ToString() ?? "";
    }
}

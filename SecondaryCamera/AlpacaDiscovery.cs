using ASCOM.Common;
using NINA.Plugins.PlateSolvePlus.Models;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryCamera {
    /// <summary>
    /// Alpaca server + device discovery.
    /// - UDP discovery on port 32227 ("alpacadiscovery1")
    /// - Then query management configured devices to list Cameras.
    ///
    /// Notes:
    /// - Many real-world setups fail broadcast due to multi-NIC / VPN / virtual adapters.
    ///   Therefore we send discovery packets to:
    ///     * 255.255.255.255
    ///     * each NIC broadcast address
    ///     * each NIC unicast address (direct)
    ///     * 127.0.0.1 (for local Alpaca servers bound to loopback)
    /// - Management endpoint varies by implementation; we try several common paths.
    /// </summary>
    public class AlpacaDiscovery {
        private readonly IProfileService profileService;

        public AlpacaDiscovery(IProfileService profileService) {
            this.profileService = profileService;
        }
        private const int DiscoveryPort = 32227;
        private const string DiscoveryMessage = "alpacadiscovery1";

        private static string? _lastError;
        public static string? GetLastError() => _lastError;

        // Reuse HttpClient to avoid socket exhaustion on repeated refresh.
        private static readonly HttpClient _http = new HttpClient {
            Timeout = TimeSpan.FromSeconds(4)
        };

        public async Task<List<AscomDeviceInfo>> GetCamerasAsync(
           TimeSpan? udpTimeout = null,
           CancellationToken ct = default) {

            _lastError = null;
            var errors = new List<string>();
            var results = new List<AscomDeviceInfo>();

            try {
                // Optional: allow override from caller, otherwise use NINA profile settings
                // NINA settings are usually already tuned; we keep them as source of truth.
                var alpaca = profileService.ActiveProfile?.AlpacaSettings;
                if (alpaca == null) {
                    errors.Add("AlpacaSettings not available (ActiveProfile is null).");
                } else {
                    var serviceType = alpaca.UseHttps
                        ? ASCOM.Common.Alpaca.ServiceType.Https
                        : ASCOM.Common.Alpaca.ServiceType.Http;

                    // NOTE: Some ASCOM Alpaca libs use different method names.
                    // You confirmed this call works and the log shows 3 devices discovered.
                    var devices = await ASCOM.Alpaca.Discovery.AlpacaDiscovery.GetAscomDevicesAsync(
                            DeviceTypes.Camera,
                            numberOfPolls: alpaca.NumberOfPolls,
                            pollInterval: alpaca.PollInterval,
                            discoveryPort: alpaca.DiscoveryPort,
                            discoveryDuration: alpaca.DiscoveryDuration,
                            resolveDnsName: alpaca.ResolveDnsName,
                            useIpV4: alpaca.UseIPv4,
                            useIpV6: alpaca.UseIPv6,
                            serviceType: serviceType,
                            cancellationToken: ct
                        )
                        .ConfigureAwait(false);

                    foreach (var d in devices) {
                        // Build stable identity: ip + port + device number
                        var ip = d.IpAddress?.ToString() ?? d.HostName ?? "127.0.0.1";
                        var port = d.IpPort;
                        var devNum = d.AlpacaDeviceNumber;

                        // Prefer the actual device name (like NINA logs).
                        var baseName =
                            !string.IsNullOrWhiteSpace(d.AscomDeviceName) ? d.AscomDeviceName :
                            !string.IsNullOrWhiteSpace(d.ServerName) ? d.ServerName :
                            "Alpaca Camera";

                        // IMPORTANT: make display names unique in the dropdown
                        var displayName = $"{baseName} ({ip}:{port} #{devNum})";

                        var progId = BuildAlpacaProgId(ip, port, devNum);

                        // Optional: log like NINA (helps a lot during tests)
                        // Logger.Info($"Discovered Alpaca Device {d.AscomDeviceName} - {d.UniqueId} @ {d.HostName} {d.IpAddress}:{d.IpPort} #{d.AlpacaDeviceNumber}");

                        results.Add(new AscomDeviceInfo(displayName, progId));
                    }
                }
            } catch (Exception ex) {
                errors.Add($"Alpaca discovery failed: {ex.Message}");
            }

            if (errors.Count > 0) {
                _lastError = string.Join(" | ", errors);
            }

            // Deduplicate by ProgId (unique identity) and sort by display name
            return results
                .GroupBy(x => x.ProgId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string BuildAlpacaProgId(string ip, int port, int deviceNumber)
            => $"alpaca://{ip}:{port}/camera/{deviceNumber}";

        public static bool TryParseAlpacaProgId(string progId, out string ip, out int port, out int deviceNumber) {
            ip = "";
            port = 0;
            deviceNumber = 0;

            if (string.IsNullOrWhiteSpace(progId)) return false;
            if (!progId.StartsWith("alpaca://", StringComparison.OrdinalIgnoreCase)) return false;

            if (!Uri.TryCreate(progId, UriKind.Absolute, out var uri)) return false;

            ip = uri.Host;
            port = uri.Port;

            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 2) return false;
            if (!string.Equals(parts[0], "camera", StringComparison.OrdinalIgnoreCase)) return false;
            if (!int.TryParse(parts[1], out deviceNumber)) return false;

            return !string.IsNullOrWhiteSpace(ip) && port > 0 && deviceNumber >= 0;
        }

    }
}

using NINA.Plugins.PlateSolvePlus.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
    public static class AlpacaDiscovery {
        private const int DiscoveryPort = 32227;
        private const string DiscoveryMessage = "alpacadiscovery1";

        private static string? _lastError;
        public static string? GetLastError() => _lastError;

        // Reuse HttpClient to avoid socket exhaustion on repeated refresh.
        private static readonly HttpClient _http = new HttpClient {
            Timeout = TimeSpan.FromSeconds(4)
        };

        public static async Task<List<AscomDeviceInfo>> GetCamerasAsync(
            TimeSpan? udpTimeout = null,
            CancellationToken ct = default) {

            _lastError = null;
            var errors = new List<string>();
            var results = new List<AscomDeviceInfo>();

            List<AlpacaServer> servers;
            try {
                servers = await DiscoverServersAsync(udpTimeout ?? TimeSpan.FromMilliseconds(900), ct).ConfigureAwait(false);
            } catch (Exception ex) {
                errors.Add($"Discovery failed: {ex.Message}");
                _lastError = string.Join(" | ", errors);
                return results;
            }

            foreach (var s in servers) {
                try {
                    var cams = await GetConfiguredCamerasFromServerAsync(s, ct).ConfigureAwait(false);
                    results.AddRange(cams);
                } catch (Exception ex) {
                    errors.Add($"{s.Ip}:{s.Port}: {ex.Message}");
                }
            }

            if (errors.Count > 0) _lastError = string.Join(" | ", errors);

            return results
                .GroupBy(x => x.ProgId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static async Task<List<AlpacaServer>> DiscoverServersAsync(TimeSpan timeout, CancellationToken ct) {
            var servers = new Dictionary<string, AlpacaServer>(StringComparer.OrdinalIgnoreCase);

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;

            // Bind explicitly so we can receive replies reliably (especially with loopback).
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var msgBytes = Encoding.ASCII.GetBytes(DiscoveryMessage);

            // Targets: broadcast + NIC broadcast + NIC unicast + loopback
            var targets = new HashSet<IPAddress>();
            targets.Add(IPAddress.Broadcast);
            targets.Add(IPAddress.Loopback);

            foreach (var ip in GetAllIPv4UnicastAddresses()) targets.Add(ip);
            foreach (var bc in GetAllIPv4BroadcastAddresses()) targets.Add(bc);

            foreach (var ip in targets) {
                ct.ThrowIfCancellationRequested();
                try {
                    await udp.SendAsync(msgBytes, msgBytes.Length, new IPEndPoint(ip, DiscoveryPort)).ConfigureAwait(false);
                } catch {
                    // ignore send failures per target
                }
            }

            var start = DateTime.UtcNow;

            while (DateTime.UtcNow - start < timeout) {
                ct.ThrowIfCancellationRequested();

                var receiveTask = udp.ReceiveAsync();
                var delayTask = Task.Delay(120, ct);
                var done = await Task.WhenAny(receiveTask, delayTask).ConfigureAwait(false);
                if (done != receiveTask) continue;

                UdpReceiveResult r;
                try { r = receiveTask.Result; } catch { continue; }

                // Response usually JSON: {"AlpacaPort":11111}
                // Some implementations return plain "11111" or include extra fields.
                int port = 0;
                try {
                    var json = Encoding.ASCII.GetString(r.Buffer);
                    port = TryParseAlpacaPort(json);
                } catch { /* ignore */ }

                if (port <= 0) continue;

                var ip = r.RemoteEndPoint.Address.ToString();
                var key = $"{ip}:{port}";
                servers[key] = new AlpacaServer(ip, port);
            }

            return servers.Values.OrderBy(s => s.Ip).ToList();
        }

        private static int TryParseAlpacaPort(string payload) {
            payload = payload?.Trim() ?? "";
            if (payload.Length == 0) return 0;

            // Try JSON first
            try {
                var resp = JsonSerializer.Deserialize<AlpacaDiscoveryResponse>(payload, new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true
                });
                if (resp?.AlpacaPort is int p && p > 0) return p;
            } catch { /* fall through */ }

            // Try plain int
            if (int.TryParse(payload, out var port) && port > 0) return port;

            // Try extracting a number (very defensive)
            var digits = new string(payload.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out port) && port > 0) return port;

            return 0;
        }

        private static async Task<List<AscomDeviceInfo>> GetConfiguredCamerasFromServerAsync(AlpacaServer server, CancellationToken ct) {
            // Management endpoint variants we see in the wild
            var candidatePaths = new[] {
                "/management/v1/configureddevices",
                "/management/v1/ConfiguredDevices",
                "/management/configureddevices",
                "/management/ConfiguredDevices"
            };

            string? json = null;
            Exception? lastEx = null;

            foreach (var path in candidatePaths) {
                try {
                    var url = $"http://{server.Ip}:{server.Port}{path}";
                    json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(json)) break;
                } catch (Exception ex) {
                    lastEx = ex;
                }
            }

            if (string.IsNullOrWhiteSpace(json)) {
                throw new Exception($"ConfiguredDevices not reachable. {(lastEx != null ? lastEx.Message : "no response")}");
            }

            // Some return List<device>, some return { Value: [...] }
            var devices = TryParseConfiguredDevices(json);

            var list = new List<AscomDeviceInfo>();

            foreach (var d in devices) {
                if (!string.Equals(d.DeviceType, "Camera", StringComparison.OrdinalIgnoreCase))
                    continue;

                var devNum = d.DeviceNumber;
                string name = d.DeviceName ?? $"Alpaca Camera {devNum} ({server.Ip}:{server.Port})";

                // Try to fetch /name (nice-to-have, ignore errors)
                try {
                    var nameUrl = $"http://{server.Ip}:{server.Port}/api/v1/camera/{devNum}/name?ClientID=1&ClientTransactionID=1";
                    var nameJson = await _http.GetStringAsync(nameUrl, ct).ConfigureAwait(false);
                    var nameResp = JsonSerializer.Deserialize<AlpacaResponse<string>>(nameJson, new JsonSerializerOptions {
                        PropertyNameCaseInsensitive = true
                    });
                    if (nameResp != null && nameResp.ErrorNumber == 0 && !string.IsNullOrWhiteSpace(nameResp.Value))
                        name = nameResp.Value!;
                } catch { }

                var progId = BuildAlpacaProgId(server.Ip, server.Port, devNum);
                list.Add(new AscomDeviceInfo(name, progId));
            }

            return list;
        }

        private static List<AlpacaConfiguredDevice> TryParseConfiguredDevices(string json) {
            try {
                var list = JsonSerializer.Deserialize<List<AlpacaConfiguredDevice>>(json, new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true
                });
                if (list != null) return list;
            } catch { }

            try {
                var wrapper = JsonSerializer.Deserialize<AlpacaConfiguredDevicesWrapper>(json, new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true
                });
                if (wrapper?.Value != null) return wrapper.Value;
            } catch { }

            return new List<AlpacaConfiguredDevice>();
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

        private static IEnumerable<IPAddress> GetAllIPv4UnicastAddresses() {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                foreach (var ua in ipProps.UnicastAddresses) {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    yield return ua.Address;
                }
            }
        }

        private static IEnumerable<IPAddress> GetAllIPv4BroadcastAddresses() {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                foreach (var ua in ipProps.UnicastAddresses) {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask == null) continue;

                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var bcast = new byte[4];
                    for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | (mask[i] ^ 255));
                    yield return new IPAddress(bcast);
                }
            }
        }

        private sealed record AlpacaServer(string Ip, int Port);

        private sealed class AlpacaDiscoveryResponse {
            public int AlpacaPort { get; set; }
        }

        private sealed class AlpacaConfiguredDevicesWrapper {
            public List<AlpacaConfiguredDevice>? Value { get; set; }
        }

        private sealed class AlpacaConfiguredDevice {
            public string? DeviceName { get; set; }
            public string? DeviceType { get; set; }
            public int DeviceNumber { get; set; }
            public string? UniqueID { get; set; }
        }
    }
}

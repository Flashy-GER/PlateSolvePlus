using EmbedIO;
using EmbedIO.Cors;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;
using NINA.Core.Utility;
using NINA.Plugins.PlateSolvePlus.PlatesolveplusDockables;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Plugins.PlateSolvePlus.Services.Api {
    /// <summary>
    /// Local REST + WebSocket API host for PlateSolvePlus.
    /// Intended for Touch-N-Stars integration.
    /// Includes a startup self-test and URLACL hinting for LAN binding.
    /// </summary>
    public sealed class PlateSolvePlusApiHost : IDisposable {
        private readonly PlatesolveplusDockables.CameraDockable _dockable;
        private readonly Dispatcher _dispatcher;

        private WebServer? _server;
        private PlateSolvePlusWsModule? _ws;

        public int Port { get; }
        public bool Enabled { get; private set; }

        // Optional auth:
        public bool RequireToken { get; }
        public string? Token { get; }

        public bool IsRunning => _server != null;

        /// <summary>
        /// Timeout used for the post-start health probe.
        /// </summary>
        public TimeSpan SelfTestTimeout { get; set; } = TimeSpan.FromSeconds(2);

        public PlateSolvePlusApiHost(
            PlatesolveplusDockables.CameraDockable dockable,
            int port,
            bool enabled = true,
            bool requireToken = false,
            string? token = null) {
            _dockable = dockable ?? throw new ArgumentNullException(nameof(dockable));
            Port = port <= 0 ? 1899 : port;
            Enabled = enabled;

            RequireToken = requireToken;
            Token = token;

            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        public void Start() {
            if (!Enabled) return;
            if (_server != null) return;

            // Bind only to localhost for safety:
            // var url = $"http://127.0.0.1:{Port}/";

            // Bind to all local interfaces (LAN + localhost)
            var url = $"http://+:{Port}/";

            // Preflight: check if port is already taken (common issue)
            if (!IsTcpPortFreeOnLoopback(Port)) {
                Logger.Error($"[PlateSolvePlusApiHost] Port {Port} is already in use. API host will not start.");
                return;
            }

            _ws = new PlateSolvePlusWsModule("/ws/platesolveplus");

            _server = new WebServer(o => o
                    .WithUrlPrefix(url)
                    .WithMode(HttpListenerMode.EmbedIO))
                // CORS: Touch-N-Stars runs in a browser -> allow localhost origins.
                // You can tighten this later to specific origins.
                .WithModule(new CorsModule("/", "*", "*", "*"))
                // Optional token auth
                .WithModule(new PlateSolvePlusAuthModule(() => RequireToken, () => Token))
                // WebSocket module
                .WithModule(_ws)
                // REST API
                .WithWebApi("/api", m => m
                    .WithController(() => new PlateSolvePlusApiController(_dockable, _dispatcher, _ws))
                    .WithController(() => new PlateSolvePlusHealthController()));

            try {
                _server.RunAsync(); // fire-and-forget
                Logger.Info($"[PlateSolvePlusApiHost] Started at {url}");
                Logger.Info($"[PlateSolvePlusApiHost] Listening on all interfaces at port {Port}");

                // Post-start: self-test (health probe)
                _ = RunSelfTestAsync();
            } catch (HttpListenerException hex) {
                LogHttpListenerException(url, hex);
                Stop();
            } catch (Exception ex) {
                Logger.Error($"[PlateSolvePlusApiHost] Start failed: {ex}");
                Stop();
            }
        }

        public void Stop() {
            try {
                _server?.Dispose();
            } catch { } finally {
                _server = null;
                _ws = null;
            }
        }

        public void Dispose() => Stop();

        // ============================================================
        // Self-test & helpers
        // ============================================================
        private static bool IsTcpPortFreeOnLoopback(int port) {
            try {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            } catch {
                return false;
            }
        }

        private async Task RunSelfTestAsync() {
            // Always probe via 127.0.0.1 to avoid name resolution and to keep the test stable.
            var probeUrl = $"http://127.0.0.1:{Port}/api/health";

            try {
                using var cts = new CancellationTokenSource(SelfTestTimeout);

                using var http = new HttpClient {
                    Timeout = SelfTestTimeout
                };

                var req = new HttpRequestMessage(HttpMethod.Get, probeUrl);

                // If token auth is enabled, send the expected header (matches PlateSolvePlusAuthModule).
                if (RequireToken && !string.IsNullOrWhiteSpace(Token)) {
                    req.Headers.TryAddWithoutValidation("X-PSP-Token", Token);
                }

                var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode) {
                    Logger.Warning($"[PlateSolvePlusApiHost] SelfTest failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Body={body}");
                    return;
                }

                Logger.Info($"[PlateSolvePlusApiHost] SelfTest OK: {probeUrl} -> {body}");
            } catch (TaskCanceledException) {
                Logger.Warning($"[PlateSolvePlusApiHost] SelfTest timeout: {probeUrl} did not respond within {SelfTestTimeout.TotalMilliseconds:0}ms");
            } catch (Exception ex) {
                Logger.Warning($"[PlateSolvePlusApiHost] SelfTest exception: {ex.Message}");
            }
        }

        private static void LogHttpListenerException(string url, HttpListenerException hex) {
            Logger.Error($"[PlateSolvePlusApiHost] HttpListenerException starting {url}: {hex.Message} (Code={hex.ErrorCode}, Native={hex.NativeErrorCode})");

            // NativeErrorCode 5 == Access denied (URLACL missing when binding to http://+:/)
            if (hex.NativeErrorCode == 5) {
                Logger.Error("[PlateSolvePlusApiHost] Access denied. If you bind to all interfaces, you likely need a URLACL reservation.");
                Logger.Error($"[PlateSolvePlusApiHost] Run (elevated): netsh http add urlacl url={url} user=Everyone");
                Logger.Error("[PlateSolvePlusApiHost] Or switch back to localhost binding (127.0.0.1).");
            }
        }
    }

    // ============================================================
    // Health Controller (used by Self-Test)
    // ============================================================
    internal sealed class PlateSolvePlusHealthController : WebApiController {
        [Route(HttpVerbs.Get, "/health")]
        public async Task GetHealth() {
            HttpContext.Response.ContentType = "application/json; charset=utf-8";
            HttpContext.Response.Headers["Cache-Control"] = "no-store";

            var json = JsonSerializer.Serialize(new {
                ok = true,
                service = "PlateSolvePlus",
                utc = DateTime.UtcNow
            }, new JsonSerializerOptions { WriteIndented = false });

            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }
    }

    // ============================================================
    // REST Controller
    // ============================================================
    internal sealed class PlateSolvePlusApiController : WebApiController {
        private readonly PlatesolveplusDockables.CameraDockable _dockable;
        private readonly Dispatcher _dispatcher;
        private readonly PlateSolvePlusWsModule _ws;

        public PlateSolvePlusApiController(
            PlatesolveplusDockables.CameraDockable dockable,
            Dispatcher dispatcher,
            PlateSolvePlusWsModule ws) {
            _dockable = dockable;
            _dispatcher = dispatcher;
            _ws = ws;
        }

        [Route(HttpVerbs.Get, "/platesolveplus/status")]
        public async Task GetStatus() {
            var status = await RunOnUiAsync(() => _dockable.GetApiStatusObject());
            await JsonAsync(status);
        }

        // ============================================================
        // Settings (Camera / Scope / Platesolve)
        // ============================================================

        public sealed class PutSettingsRequest {
            public CameraSettings? camera { get; set; }
            public PlatesolveSettings? platesolve { get; set; }
        }

        public sealed class CameraSettings {
            public double? exposureSeconds { get; set; }
            public int? binning { get; set; }
            public int? gain { get; set; }
            public double? focalLengthMm { get; set; }
            public bool? useCameraPixelSize { get; set; }
            public double? pixelSizeUm { get; set; }

            public bool? previewDebayerEnabled { get; set; }
            public bool? previewAutoStretchEnabled { get; set; }
            public bool? previewUnlinkedStretchEnabled { get; set; }
        }

        public sealed class PlatesolveSettings {
            public double? centeringThresholdArcmin { get; set; }
            public int? centeringMaxAttempts { get; set; }
            public bool? useSlewInsteadOfSync { get; set; }
        }
        [Route(HttpVerbs.Get, "/platesolveplus/settings")]
        public async Task GetSettings() {
            var settings = await RunOnUiAsync(() => {
                var s = _dockable.PluginSettings?.Settings;

                // camera
                var camera = new {
                    driver = _dockable.SelectedSecondaryCameraProgId ?? _dockable.SecondaryCameraService?.ProgId,
                    connected = _dockable.IsSecondaryConnected,
                    exposureSeconds = _dockable.GuideExposureSeconds,
                    binning = _dockable.GuideBinning,
                    // gain: -1 means auto in our UI
                    gain = _dockable.GuideGain,
                    focalLengthMm = _dockable.GuideFocalLengthMm,
                    useCameraPixelSize = _dockable.UseCameraPixelSize,
                    pixelSizeUm = _dockable.GuidePixelSizeUm,
                    // derived: image scale in arcsec/px
                    scopeScaleArcsecPerPixel = ComputeArcsecPerPixel(_dockable.GuideFocalLengthMm, _dockable.GuidePixelSizeUm),
                    // preview/render
                    previewDebayerEnabled = _dockable.PreviewDebayerEnabled,
                    previewAutoStretchEnabled = _dockable.PreviewAutoStretchEnabled,
                    previewUnlinkedStretchEnabled = _dockable.PreviewUnlinkedStretchEnabled,
                };

                // scope / offset
                const double eps = 1e-6;
                var ra = _dockable.OffsetRaArcsec;
                var dec = _dockable.OffsetDecArcsec;
                var hasDelta = Math.Abs(ra) > eps || Math.Abs(dec) > eps;

                var qw = _dockable.RotationQw;
                var qx = _dockable.RotationQx;
                var qy = _dockable.RotationQy;
                var qz = _dockable.RotationQz;
                var hasRot = Math.Abs(qw - 1.0) > eps || Math.Abs(qx) > eps || Math.Abs(qy) > eps || Math.Abs(qz) > eps;
                var offsetSet = hasDelta || hasRot;

                var scope = new {
                    mountConnected = _dockable.IsMountConnected,
                    mountState = _dockable.MountState.ToString(),
                    offsetEnabled = _dockable.OffsetEnabled,
                    offsetMode = _dockable.OffsetMode.ToString(),
                    offsetSet,
                    offsetRaArcsec = ra,
                    offsetDecArcsec = dec,
                    rotation = new { qw, qx, qy, qz },
                    offsetLastCalibratedUtc = _dockable.OffsetLastCalibratedUtc
                };

                // platesolve / centering
                var platesolve = new {
                    centeringThresholdArcmin = s?.CenteringThresholdArcmin ?? 1.0,
                    centeringMaxAttempts = s?.CenteringMaxAttempts ?? 5,
                    // true means "Center+Solve" uses Slew/Center workflow; false means Sync
                    useSlewInsteadOfSync = _dockable.UseSlewInsteadOfSync,
                };

                return new {
                    camera,
                    scope,
                    platesolve
                };
            });

            await JsonAsync(settings);
        }

        [Route(HttpVerbs.Put, "/platesolveplus/settings")]
        public async Task PutSettings() {
            var req = await HttpContext.GetRequestDataAsync<PutSettingsRequest>();

            // Apply on UI thread because we touch WPF-bound properties/settings.
            var ok = await RunOnUiAsync(() => {
                var s = _dockable.PluginSettings?.Settings;
                if (s == null) return false;

                // ---- Camera ----
                if (req?.camera != null) {
                    var c = req.camera;

                    if (c.exposureSeconds.HasValue) s.GuideExposureSeconds = Clamp(c.exposureSeconds.Value, 0.05, 60.0);
                    if (c.binning.HasValue) s.GuideBinning = Math.Clamp(c.binning.Value, 1, 4);
                    if (c.gain.HasValue) s.GuideGain = Math.Clamp(c.gain.Value, -1, 600);
                    if (c.focalLengthMm.HasValue) s.GuideFocalLengthMm = Clamp(c.focalLengthMm.Value, 1.0, 10000.0);
                    if (c.useCameraPixelSize.HasValue) s.UseCameraPixelSize = c.useCameraPixelSize.Value;
                    if (c.pixelSizeUm.HasValue) s.GuidePixelSizeUm = Clamp(c.pixelSizeUm.Value, 0.1, 50.0);

                    if (c.previewDebayerEnabled.HasValue) s.PreviewDebayerEnabled = c.previewDebayerEnabled.Value;
                    if (c.previewAutoStretchEnabled.HasValue) s.PreviewAutoStretchEnabled = c.previewAutoStretchEnabled.Value;
                    if (c.previewUnlinkedStretchEnabled.HasValue) s.PreviewUnlinkedStretchEnabled = c.previewUnlinkedStretchEnabled.Value;
                }

                // ---- Platesolve ----
                if (req?.platesolve != null) {
                    var p = req.platesolve;

                    if (p.centeringThresholdArcmin.HasValue) s.CenteringThresholdArcmin = Clamp(p.centeringThresholdArcmin.Value, 0.05, 120.0);
                    if (p.centeringMaxAttempts.HasValue) s.CenteringMaxAttempts = Math.Clamp(p.centeringMaxAttempts.Value, 1, 25);

                    if (p.useSlewInsteadOfSync.HasValue) {
                        // this is runtime behavior in the dockable (not persisted in settings)
                        _dockable.UseSlewInsteadOfSync = p.useSlewInsteadOfSync.Value;
                    }
                }

                return true;
            });

            if (!ok) {
                HttpContext.Response.StatusCode = 409;
                await JsonAsync(new { ok = false, error = "Settings not available yet (importsReady=false?)" });
                return;
            }

            // After applying: return the current settings snapshot.
            var settings = await RunOnUiAsync(() => {
                var s = _dockable.PluginSettings?.Settings;

                var camera = new {
                    driver = _dockable.SelectedSecondaryCameraProgId ?? _dockable.SecondaryCameraService?.ProgId,
                    connected = _dockable.IsSecondaryConnected,
                    exposureSeconds = _dockable.GuideExposureSeconds,
                    binning = _dockable.GuideBinning,
                    gain = _dockable.GuideGain,
                    focalLengthMm = _dockable.GuideFocalLengthMm,
                    useCameraPixelSize = _dockable.UseCameraPixelSize,
                    pixelSizeUm = _dockable.GuidePixelSizeUm,
                    scopeScaleArcsecPerPixel = ComputeArcsecPerPixel(_dockable.GuideFocalLengthMm, _dockable.GuidePixelSizeUm),
                    previewDebayerEnabled = _dockable.PreviewDebayerEnabled,
                    previewAutoStretchEnabled = _dockable.PreviewAutoStretchEnabled,
                    previewUnlinkedStretchEnabled = _dockable.PreviewUnlinkedStretchEnabled,
                };

                const double eps = 1e-6;
                var ra = _dockable.OffsetRaArcsec;
                var dec = _dockable.OffsetDecArcsec;
                var hasDelta = Math.Abs(ra) > eps || Math.Abs(dec) > eps;

                var qw = _dockable.RotationQw;
                var qx = _dockable.RotationQx;
                var qy = _dockable.RotationQy;
                var qz = _dockable.RotationQz;
                var hasRot = Math.Abs(qw - 1.0) > eps || Math.Abs(qx) > eps || Math.Abs(qy) > eps || Math.Abs(qz) > eps;
                var offsetSet = hasDelta || hasRot;

                var scope = new {
                    mountConnected = _dockable.IsMountConnected,
                    mountState = _dockable.MountState.ToString(),
                    offsetEnabled = _dockable.OffsetEnabled,
                    offsetMode = _dockable.OffsetMode.ToString(),
                    offsetSet,
                    offsetRaArcsec = ra,
                    offsetDecArcsec = dec,
                    rotation = new { qw, qx, qy, qz },
                    offsetLastCalibratedUtc = _dockable.OffsetLastCalibratedUtc
                };

                var platesolve = new {
                    centeringThresholdArcmin = s?.CenteringThresholdArcmin ?? 1.0,
                    centeringMaxAttempts = s?.CenteringMaxAttempts ?? 5,
                    useSlewInsteadOfSync = _dockable.UseSlewInsteadOfSync,
                };

                return new { ok = true, camera, scope, platesolve };
            });

            await JsonAsync(settings);
        }

        [Route(HttpVerbs.Post, "/platesolveplus/capture")]
        public async Task CaptureOnly() {
            var jobId = Guid.NewGuid().ToString("N");
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("CaptureStarted", new { jobId, utc = DateTime.UtcNow }));

            var result = await RunOnUiAsync(() => _dockable.ApiCaptureOnlyAsync());
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("CaptureFinished", new { jobId, result, utc = DateTime.UtcNow }));

            await JsonAsync(new { jobId, result });
        }

        [Route(HttpVerbs.Post, "/platesolveplus/solve")]
        public async Task Solve() {
            var jobId = Guid.NewGuid().ToString("N");
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("SolveStarted", new { jobId, utc = DateTime.UtcNow }));

            var result = await RunOnUiAsync(() => _dockable.ApiCaptureAndSolveAsync());
            var status = await RunOnUiAsync(() => _dockable.GetApiStatusObject());

            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("SolveFinished", new {
                jobId,
                result,
                utc = DateTime.UtcNow,
                previewUrl = "/api/platesolveplus/preview/latest.jpg",
                status
            }));

            await JsonAsync(new { jobId, result, previewUrl = "/api/platesolveplus/preview/latest.jpg", status });
        }


        // ============================================================
        // New Actions (Sync / Center / Target Center)
        // ============================================================

        [Route(HttpVerbs.Post, "/platesolveplus/sync")]
        public async Task CaptureAndSync() {
            var jobId = Guid.NewGuid().ToString("N");
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("SyncStarted", new { jobId, utc = DateTime.UtcNow }));

            var result = await RunOnUiAsync(() => _dockable.ApiCaptureAndSyncAsync());
            var status = await RunOnUiAsync(() => _dockable.GetApiStatusObject());

            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("SyncFinished", new { jobId, result, utc = DateTime.UtcNow, status }));
            await JsonAsync(new { jobId, result, status });
        }

        [Route(HttpVerbs.Post, "/platesolveplus/center")]
        public async Task CaptureAndCenter() {
            var jobId = Guid.NewGuid().ToString("N");
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("CenterStarted", new { jobId, utc = DateTime.UtcNow }));

            var result = await RunOnUiAsync(() => _dockable.ApiCaptureAndCenterAsync());
            var status = await RunOnUiAsync(() => _dockable.GetApiStatusObject());

            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("CenterFinished", new { jobId, result, utc = DateTime.UtcNow, status }));
            await JsonAsync(new { jobId, result, status });
        }

        public sealed class SetTargetRequest {
            public double? raDeg { get; set; }
            public double? decDeg { get; set; }
        }

        [Route(HttpVerbs.Get, "/platesolveplus/target")]
        public async Task GetTarget() {
            var ra = await RunOnUiAsync(() => _dockable.TargetRaDeg);
            var dec = await RunOnUiAsync(() => _dockable.TargetDecDeg);
            await JsonAsync(new { targetRaDeg = ra, targetDecDeg = dec });
        }


        [Route(HttpVerbs.Put, "/platesolveplus/target")]
        public async Task SetTarget() {
            var req = await HttpContext.GetRequestDataAsync<SetTargetRequest>();
            var ra = req?.raDeg;
            var dec = req?.decDeg;

            if (!ra.HasValue || !dec.HasValue) {
                HttpContext.Response.StatusCode = 400;
                await JsonAsync(new { ok = false, error = "raDeg and decDeg are required (degrees)." });
                return;
            }

            await RunOnUiAsync(() => {
                _dockable.TargetRaDeg = ra.Value;
                _dockable.TargetDecDeg = dec.Value;
                return true;
            });

            await JsonAsync(new { ok = true, targetRaDeg = ra.Value, targetDecDeg = dec.Value });
        }

        [Route(HttpVerbs.Post, "/platesolveplus/target/center")]
        public async Task SlewTargetAndCenter() {
            var req = await HttpContext.GetRequestDataAsync<SetTargetRequest>();
            var ra = req?.raDeg;
            var dec = req?.decDeg;

            if (!ra.HasValue || !dec.HasValue) {
                HttpContext.Response.StatusCode = 400;
                await JsonAsync(new { ok = false, error = "raDeg and decDeg are required (degrees)." });
                return;
            }

            var jobId = Guid.NewGuid().ToString("N");
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("TargetCenterStarted", new { jobId, targetRaDeg = ra.Value, targetDecDeg = dec.Value, utc = DateTime.UtcNow }));

            var result = await RunOnUiAsync(() => _dockable.ApiSlewToTargetAndCenterAsync(ra.Value, dec.Value));
            var status = await RunOnUiAsync(() => _dockable.GetApiStatusObject());

            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("TargetCenterFinished", new { jobId, result, utc = DateTime.UtcNow, status }));
            await JsonAsync(new { jobId, result, status });
        }

        [Route(HttpVerbs.Post, "/platesolveplus/offset/calibrate")]
        public async Task CalibrateOffset() {
            var jobId = Guid.NewGuid().ToString("N");
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("OffsetCalibrateStarted", new { jobId, utc = DateTime.UtcNow }));

            var result = await RunOnUiAsync(() => _dockable.ApiCalibrateOffsetAsync());
            var status = await RunOnUiAsync(() => _dockable.GetApiStatusObject());

            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("OffsetCalibrateFinished", new { jobId, result, utc = DateTime.UtcNow, status }));

            await JsonAsync(new { jobId, result, status });
        }

        [Route(HttpVerbs.Post, "/platesolveplus/offset/reset")]
        public async Task ResetOffset() {
            var jobId = Guid.NewGuid().ToString("N");
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("OffsetResetStarted", new { jobId, utc = DateTime.UtcNow }));

            var ok = await RunOnUiAsync(() => {
                var s = _dockable.PluginSettings?.Settings;
                if (s == null) return false;
                s.ResetOffset();
                return true;
            });

            var status = await RunOnUiAsync(() => _dockable.GetApiStatusObject());
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("OffsetResetFinished", new { jobId, ok, utc = DateTime.UtcNow, status }));

            await JsonAsync(new { jobId, ok, status });
        }

        [Route(HttpVerbs.Post, "/platesolveplus/offset/reset-rotation")]
        public async Task ResetRotationOffset() {
            var jobId = Guid.NewGuid().ToString("N");
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("RotationOffsetResetStarted", new { jobId, utc = DateTime.UtcNow }));

            var ok = await RunOnUiAsync(() => {
                var s = _dockable.PluginSettings?.Settings;
                if (s == null) return false;
                s.RotationQw = 1.0;
                s.RotationQx = 0.0;
                s.RotationQy = 0.0;
                s.RotationQz = 0.0;
                s.OffsetLastCalibratedUtc = null;
                return true;
            });

            var status = await RunOnUiAsync(() => _dockable.GetApiStatusObject());
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("RotationOffsetResetFinished", new { jobId, ok, utc = DateTime.UtcNow, status }));

            await JsonAsync(new { jobId, ok, status });
        }

        [Route(HttpVerbs.Get, "/platesolveplus/preview/latest.jpg")]
        public async Task GetLatestPreviewJpeg() {
            // NOTE: We run on UI thread because BitmapSource access/encoding can be dispatcher-affine.
            var bytes = await RunOnUiAsync(() => _dockable.GetLastPreviewAsJpegBytes());

            if (bytes == null || bytes.Length == 0) {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.SendStringAsync("No preview available.", "text/plain", Encoding.UTF8);
                return;
            }

            HttpContext.Response.ContentType = "image/jpeg";
            HttpContext.Response.Headers["Cache-Control"] = "no-store";
            await HttpContext.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        // ---------- helpers ----------
        private Task<T> RunOnUiAsync<T>(Func<T> func) {
            if (_dispatcher.CheckAccess())
                return Task.FromResult(func());

            return _dispatcher.InvokeAsync(func, DispatcherPriority.Background).Task;
        }

        private async Task JsonAsync(object obj) {
            HttpContext.Response.ContentType = "application/json; charset=utf-8";
            HttpContext.Response.Headers["Cache-Control"] = "no-store";
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false });
            await HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
        }

        private static double ComputeArcsecPerPixel(double focalLengthMm, double pixelSizeUm) {
            // Image scale: 206.265 * pixel_size(um) / focal_length(mm)
            if (focalLengthMm <= 0 || pixelSizeUm <= 0) return 0;
            return 206.265 * (pixelSizeUm / focalLengthMm);
        }

        private static double Clamp(double v, double min, double max) {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        // Camera Interface
        [Route(HttpVerbs.Get, "/platesolveplus/secondary/drivers")]
        public async Task GetSecondaryDrivers() {
            var drivers = _dockable.AscomDiscovery.GetCameras();
            await JsonAsync(drivers);
        }

        [Route(HttpVerbs.Get, "/platesolveplus/secondary/selection")]
        public async Task GetSecondarySelection() {
            var svc = _dockable.SecondaryCameraService;
            await JsonAsync(new {
                progId = _dockable.SelectedSecondaryCameraProgId ?? svc?.ProgId,
                connected = svc?.IsConnected ?? false
            });

        }

        public sealed class SelectSecondaryRequest { public string? progId { get; set; } }

        [Route(HttpVerbs.Put, "/platesolveplus/secondary/selection")]
        public async Task SetSecondarySelection() {
            var req = await HttpContext.GetRequestDataAsync<SelectSecondaryRequest>();
            var progId = req?.progId?.Trim();

            if (string.IsNullOrWhiteSpace(progId)) {
                HttpContext.Response.StatusCode = 400;
                await JsonAsync(new { ok = false, error = "progId is required." });
                return;
            }

            var svc = _dockable.SecondaryCameraService;
            if (svc != null && svc.IsConnected) {
                HttpContext.Response.StatusCode = 409;
                await JsonAsync(new { ok = false, error = "Disconnect secondary camera before changing driver." });
                return;
            }

            await RunOnUiAsync(() => {
                _dockable.SelectedSecondaryCameraProgId = progId;
                _dockable.RefreshSecondaryCameraListCommand?.Execute(null);
                return Task.CompletedTask;
            });

            var svc2 = _dockable.SecondaryCameraService;
            await JsonAsync(new {
                ok = true,
                progId = svc2?.ProgId ?? progId,
                connected = svc2?.IsConnected ?? false
            });
        }

        [Route(HttpVerbs.Post, "/platesolveplus/secondary/connect")]
        public async Task SecondaryConnect() {
            await RunOnUiAsync(async () => {
                var svc = _dockable.SecondaryCameraService;
                if (svc == null) { HttpContext.Response.StatusCode = 409; await JsonAsync(new { ok = false, error = "No driver selected." }); return; }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await svc.ConnectAsync(cts.Token);

                // sync dockable state so /status reflects reality
                _dockable.RefreshSecondaryCameraListCommand?.Execute(null);

                await JsonAsync(new { ok = true, connected = svc.IsConnected, progId = svc.ProgId });
            });
        }

        [Route(HttpVerbs.Post, "/platesolveplus/secondary/disconnect")]
        public async Task SecondaryDisconnect() {
            await RunOnUiAsync(async () => {
                var svc = _dockable.SecondaryCameraService;
                if (svc == null) { await JsonAsync(new { ok = true, connected = false }); return; }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await svc.DisconnectAsync(cts.Token);

                _dockable.RefreshSecondaryCameraListCommand?.Execute(null);

                await JsonAsync(new { ok = true, connected = svc.IsConnected, progId = svc.ProgId });
            });
        }

        [Route(HttpVerbs.Post, "/platesolveplus/secondary/setup-dialog")]
        public async Task SecondarySetupDialog() {
            var svc = _dockable.SecondaryCameraService;
            if (svc == null) { HttpContext.Response.StatusCode = 409; await JsonAsync(new { ok = false, error = "No driver selected." }); return; }

            var ok = await svc.OpenSetupDialogAsync(); // nutzt STA already :contentReference[oaicite:4]{index=4}
            await JsonAsync(new { ok });
        }

    }

    // ============================================================
    // WebSocket Module
    // ============================================================
    internal sealed class PlateSolvePlusWsModule : WebSocketModule {
        public PlateSolvePlusWsModule(string urlPath) : base(urlPath, true) {
        }

        protected override Task OnClientConnectedAsync(IWebSocketContext context) {
            var hello = new PlateSolvePlusWsEvent("Hello", new {
                utc = System.DateTime.UtcNow,
                message = "PlateSolvePlus WS connected"
            });

            var json = JsonSerializer.Serialize(hello, new JsonSerializerOptions { WriteIndented = false });
            return SendAsync(context, Encoding.UTF8.GetBytes(json));
        }

        // REQUIRED by EmbedIO version
        protected override Task OnMessageReceivedAsync(
            IWebSocketContext context,
            byte[] buffer,
            IWebSocketReceiveResult result) {
            // Optional: handle client messages (ping, subscribe, etc.)
            // For now we just ignore incoming messages safely.
            // parse JSON commands here later.

            // Example: respond to "ping"
            try {
                if (result?.Count > 0) {
                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (msg.Trim().Equals("ping", System.StringComparison.OrdinalIgnoreCase)) {
                        var pong = Encoding.UTF8.GetBytes("pong");
                        return SendAsync(context, pong);
                    }
                }
            } catch {
                // ignore parse errors
            }

            return Task.CompletedTask;
        }

        public Task BroadcastAsync(PlateSolvePlusWsEvent evt) {
            var json = JsonSerializer.Serialize(evt, new JsonSerializerOptions { WriteIndented = false });
            var bytes = Encoding.UTF8.GetBytes(json);
            return BroadcastAsync(bytes);
        }
    }

    internal sealed record PlateSolvePlusWsEvent(string type, object payload);

    // ============================================================
    // Optional Token Auth Module
    // ============================================================
    internal sealed class PlateSolvePlusAuthModule : WebModuleBase {
        private readonly Func<bool> _enabled;
        private readonly Func<string?> _tokenProvider;

        public PlateSolvePlusAuthModule(Func<bool> enabled, Func<string?> tokenProvider)
            : base("/") {
            _enabled = enabled;
            _tokenProvider = tokenProvider;
        }

        public override bool IsFinalHandler => false;

        protected override Task OnRequestAsync(IHttpContext context) {
            if (!_enabled()) return Task.CompletedTask;

            if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            var expected = _tokenProvider();
            if (string.IsNullOrWhiteSpace(expected)) {
                context.Response.StatusCode = 500;
                return context.SendStringAsync("Token auth enabled but no token configured.", "text/plain", Encoding.UTF8);
            }

            // 1) header token (REST)
            var token = context.Request.Headers["X-PSP-Token"];

            // 2) query token (WebSocket from browser can't send custom headers)
            if (string.IsNullOrWhiteSpace(token)) {
                // EmbedIO provides query via context.Request.QueryString (NameValueCollection)
                token = context.Request.QueryString["token"];
            }

            if (!string.Equals(token, expected, StringComparison.Ordinal)) {
                context.Response.StatusCode = 401;
                return context.SendStringAsync("Unauthorized", "text/plain", Encoding.UTF8);
            }

            return Task.CompletedTask;
        }


    }

}

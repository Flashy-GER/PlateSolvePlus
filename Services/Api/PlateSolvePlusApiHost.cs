using EmbedIO;
using EmbedIO.Cors;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;
using NINA.Core.Utility;
using System;
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

        [Route(HttpVerbs.Post, "/platesolveplus/offset/calibrate")]
        public async Task CalibrateOffset() {
            var jobId = Guid.NewGuid().ToString("N");
            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("OffsetCalibrateStarted", new { jobId, utc = DateTime.UtcNow }));

            var result = await RunOnUiAsync(() => _dockable.ApiCalibrateOffsetAsync());
            var status = await RunOnUiAsync(() => _dockable.GetApiStatusObject());

            await _ws.BroadcastAsync(new PlateSolvePlusWsEvent("OffsetCalibrateFinished", new { jobId, result, utc = DateTime.UtcNow, status }));

            await JsonAsync(new { jobId, result, status });
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

            // Allow CORS preflight
            if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            var expected = _tokenProvider();

            // Hardening: if RequireToken is enabled but token is empty, deny all
            if (string.IsNullOrWhiteSpace(expected)) {
                context.Response.StatusCode = 500;
                return context.SendStringAsync("Token auth enabled but no token configured.", "text/plain", Encoding.UTF8);
            }

            var token = context.Request.Headers["X-PSP-Token"];
            if (!string.Equals(token, expected, StringComparison.Ordinal)) {
                context.Response.StatusCode = 401;
                return context.SendStringAsync("Unauthorized", "text/plain", Encoding.UTF8);
            }

            return Task.CompletedTask;
        }

    }
}

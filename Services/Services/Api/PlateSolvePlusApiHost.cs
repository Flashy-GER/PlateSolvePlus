// File: Services/Api/PlateSolvePlusApiHost.cs

using EmbedIO;
using EmbedIO.Cors;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;
using NINA.Core.Utility;
using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Plugins.PlateSolvePlus.Services.Api {
    /// <summary>
    /// Local REST + WebSocket API host for PlateSolvePlus.
    /// Runs on localhost only. Intended for Touch-N-Stars integration.
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

            // Bind only to localhost for safety.
            var url = $"http://127.0.0.1:{Port}/";

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
                .WithWebApi("/api", m => m.WithController(() => new PlateSolvePlusApiController(_dockable, _dispatcher, _ws)));

            try {
                _server.RunAsync(); // fire-and-forget
                Logger.Info($"[PlateSolvePlusApiHost] Started at {url}");
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

        // ✅ REQUIRED by your EmbedIO version
        protected override Task OnMessageReceivedAsync(
            IWebSocketContext context,
            byte[] buffer,
            IWebSocketReceiveResult result) {
            // Optional: handle client messages (ping, subscribe, etc.)
            // For now we just ignore incoming messages safely.
            // If you'd like, we can parse JSON commands here later.

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

            var expected = _tokenProvider();
            if (string.IsNullOrWhiteSpace(expected)) return Task.CompletedTask;

            var token = context.Request.Headers["X-PSP-Token"];
            if (!string.Equals(token, expected, StringComparison.Ordinal)) {
                context.Response.StatusCode = 401;
                return context.SendStringAsync("Unauthorized", "text/plain", Encoding.UTF8);
            }

            return Task.CompletedTask;
        }
    }
}

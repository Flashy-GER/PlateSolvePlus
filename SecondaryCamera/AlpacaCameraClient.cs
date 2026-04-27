﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryCamera {
    /// <summary>
    /// Robust minimal Alpaca Camera client (HTTP/REST).
    /// NOTE: imagearray responses can be very large and slow. We therefore do NOT rely on HttpClient.Timeout
    /// and instead apply per-request timeouts (separate defaults for "control" vs "image" calls).
    /// </summary>
    public sealed class AlpacaCameraClient : IDisposable {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly uint _clientId;
        private uint _tx;
        private readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web) {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Default timeout for small control/status calls.</summary>
        public TimeSpan ControlTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Default timeout for large payload calls like imagearray.</summary>
        public TimeSpan ImageTimeout { get; set; } = TimeSpan.FromSeconds(120);

        public AlpacaCameraClient(string host, int port, int deviceNumber, uint clientId = 1, TimeSpan? controlTimeout = null, TimeSpan? imageTimeout = null, bool https = false) {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host required", nameof(host));
            if (port <= 0) throw new ArgumentOutOfRangeException(nameof(port));
            if (deviceNumber < 0) throw new ArgumentOutOfRangeException(nameof(deviceNumber));

            var scheme = https ? "https" : "http";
            _baseUrl = $"{scheme}://{host}:{port}/api/v1/camera/{deviceNumber}";
            _clientId = clientId;
            _tx = 0;

            if (controlTimeout.HasValue) ControlTimeout = controlTimeout.Value;
            if (imageTimeout.HasValue) ImageTimeout = imageTimeout.Value;

            // Critical: disable global HttpClient.Timeout so large image downloads don't get killed at 20s.
            _http = new HttpClient {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public AlpacaCameraClient(string host, int port, int deviceNumber, uint clientId, TimeSpan? timeout, bool https)
            : this(host, port, deviceNumber, clientId, controlTimeout: timeout, imageTimeout: null, https: https) {
        }

        public void Dispose() => _http?.Dispose();

        private uint NextTx() => unchecked(++_tx);

        /// <summary>
        /// Alpaca GET endpoints typically expect ClientID/ClientTransactionID in the query string.
        /// Some Alpaca servers (including some proxy / bridge implementations) reject *form* requests
        /// (x-www-form-urlencoded) that also have query keys and will return HTTP 400 like:
        /// "A Form request should not have any Query Keys. Unknown Query Key(s): ClientID, ClientTransactionID".
        ///
        /// To be compatible with those servers, we send the IDs in the query string for GET and in the
        /// form body for PUT.
        /// </summary>
        private string BuildGetUrl(string method) {
            var tx = NextTx();
            var sep = method.Contains('?') ? "&" : "?";
            return $"{_baseUrl}/{method}{sep}ClientID={_clientId}&ClientTransactionID={tx}";
        }

        private string BuildPutUrl(string method) {
            // Keep the path clean (no ClientID/ClientTransactionID in query) for form requests.
            return $"{_baseUrl}/{method}";
        }

        private Dictionary<string, string> EnsurePutFields(Dictionary<string, string>? formFields) {
            var tx = NextTx();
            var fields = formFields == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(formFields, StringComparer.OrdinalIgnoreCase);

            // Only set if caller didn't already provide them.
            if (!fields.ContainsKey("ClientID")) fields["ClientID"] = _clientId.ToString(CultureInfo.InvariantCulture);
            if (!fields.ContainsKey("ClientTransactionID")) fields["ClientTransactionID"] = tx.ToString(CultureInfo.InvariantCulture);
            return fields;
        }

        private async Task<string> SendAsync(HttpMethod method, string url, HttpContent? content, TimeSpan timeout, CancellationToken ct) {
            // Guard rails to avoid NullReferenceException and provide actionable errors
            if (_http == null) throw new AlpacaTransportException("HttpClient is null. AlpacaCameraClient was not initialized correctly.", new NullReferenceException(nameof(_http)));
            if (method == null) throw new ArgumentNullException(nameof(method));
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL is null/empty.", nameof(url));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var req = new HttpRequestMessage(method, url);
            if (content != null) req.Content = content;

            try {
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                    throw new AlpacaTransportException($"HTTP {(int)resp.StatusCode}: {json}", new HttpRequestException(resp.ReasonPhrase));

                return json;
            } catch (OperationCanceledException) {
                // Preserve cancellation semantics for caller (NINA uses this a lot)
                throw;
            } catch (Exception ex) {
                throw new AlpacaTransportException($"{method} {url} failed.", ex);
            }
        }

        public async Task<T> GetValueAsync<T>(string method, CancellationToken ct, TimeSpan? timeoutOverride = null) {
            var url = BuildGetUrl(method);
            var timeout = timeoutOverride ?? ControlTimeout;

            try {
                var json = await SendAsync(HttpMethod.Get, url, null, timeout, ct).ConfigureAwait(false);

                var parsed = JsonSerializer.Deserialize<AlpacaResponse<T>>(json, _jsonOpts);
                if (parsed == null)
                    throw new AlpacaTransportException("Invalid Alpaca JSON response (null).", new JsonException("null"));

                if (parsed.ErrorNumber != 0)
                    throw new AlpacaException(parsed.ErrorMessage ?? "Alpaca error", parsed.ErrorNumber, parsed.ClientTransactionID, parsed.ServerTransactionID);

                return parsed.Value!;
            } catch (OperationCanceledException) { throw; } catch (AlpacaException) { throw; } catch (AlpacaTransportException) { throw; } catch (Exception ex) {
                throw new AlpacaTransportException($"GET {method} failed.", ex);
            }
        }

        public async Task PutAsync(string method, Dictionary<string, string>? formFields, CancellationToken ct, TimeSpan? timeoutOverride = null) {
            var url = BuildPutUrl(method);
            var timeout = timeoutOverride ?? ControlTimeout;

            try {
                var fields = EnsurePutFields(formFields);
                HttpContent content = new FormUrlEncodedContent(fields);

                var json = await SendAsync(HttpMethod.Put, url, content, timeout, ct).ConfigureAwait(false);

                var parsed = JsonSerializer.Deserialize<AlpacaResponse>(json, _jsonOpts);
                if (parsed == null)
                    throw new AlpacaTransportException("Invalid Alpaca JSON response (null).", new JsonException("null"));

                if (parsed.ErrorNumber != 0)
                    throw new AlpacaException(parsed.ErrorMessage ?? "Alpaca error", parsed.ErrorNumber, parsed.ClientTransactionID, parsed.ServerTransactionID);
            } catch (OperationCanceledException) { throw; } catch (AlpacaException) { throw; } catch (AlpacaTransportException) { throw; } catch (Exception ex) {
                throw new AlpacaTransportException($"PUT {method} failed.", ex);
            }
        }

        // ---- Camera helpers ----

        public Task<bool> GetConnectedAsync(CancellationToken ct)
            => GetValueAsync<bool>("connected", ct);

        public Task SetConnectedAsync(bool connected, CancellationToken ct)
            => PutAsync("connected", new Dictionary<string, string> { ["Connected"] = connected ? "true" : "false" }, ct);

        public Task StartExposureAsync(double durationSeconds, bool light, CancellationToken ct)
            => PutAsync("startexposure",
                new Dictionary<string, string> {
                    ["Duration"] = durationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    ["Light"] = light ? "true" : "false"
                }, ct);

        public Task<bool> GetImageReadyAsync(CancellationToken ct)
            => GetValueAsync<bool>("imageready", ct);

        public Task AbortExposureAsync(CancellationToken ct)
            => PutAsync("abortexposure", null, ct);

        public Task StopExposureAsync(CancellationToken ct)
            => PutAsync("stopexposure", null, ct);

        public Task SetBinXAsync(int binX, CancellationToken ct)
            => PutAsync("binx", new Dictionary<string, string> { ["BinX"] = binX.ToString(CultureInfo.InvariantCulture) }, ct);

        public Task SetBinYAsync(int binY, CancellationToken ct)
            => PutAsync("biny", new Dictionary<string, string> { ["BinY"] = binY.ToString(CultureInfo.InvariantCulture) }, ct);

        // ---- Frame / ROI helpers ----

        /// <summary>
        /// Sensor width in unbinned pixels.
        /// </summary>
        public Task<int> GetCameraXSizeAsync(CancellationToken ct)
            => GetValueAsync<int>("cameraxsize", ct);

        /// <summary>
        /// Sensor height in unbinned pixels.
        /// </summary>
        public Task<int> GetCameraYSizeAsync(CancellationToken ct)
            => GetValueAsync<int>("cameraysize", ct);

        public Task SetStartXAsync(int startX, CancellationToken ct)
            => PutAsync("startx", new Dictionary<string, string> { ["StartX"] = startX.ToString(CultureInfo.InvariantCulture) }, ct);

        public Task SetStartYAsync(int startY, CancellationToken ct)
            => PutAsync("starty", new Dictionary<string, string> { ["StartY"] = startY.ToString(CultureInfo.InvariantCulture) }, ct);

        public Task SetNumXAsync(int numX, CancellationToken ct)
            => PutAsync("numx", new Dictionary<string, string> { ["NumX"] = numX.ToString(CultureInfo.InvariantCulture) }, ct);

        public Task SetNumYAsync(int numY, CancellationToken ct)
            => PutAsync("numy", new Dictionary<string, string> { ["NumY"] = numY.ToString(CultureInfo.InvariantCulture) }, ct);

        /// <summary>
        /// Optional; not every Alpaca camera implements gain.
        /// </summary>
        public Task SetGainAsync(int gain, CancellationToken ct)
            => PutAsync("gain", new Dictionary<string, string> { ["Gain"] = gain.ToString(CultureInfo.InvariantCulture) }, ct);

        /// <summary>
        /// Optional; returns camera max ADU (often 255, 4095, 16383, 65535).
        /// Not all devices implement it.
        /// </summary>
        public Task<int> GetMaxAduAsync(CancellationToken ct)
            => GetValueAsync<int>("maxadu", ct);

        public Task<double> GetPixelSizeXAsync(CancellationToken ct)
            => GetValueAsync<double>("pixelsizex", ct);

        public Task<double> GetPixelSizeYAsync(CancellationToken ct)
            => GetValueAsync<double>("pixelsizey", ct);

        public async Task<int[,]> GetImageArray2DAsync(CancellationToken ct) {
            // imagearray can be very large -> use ImageTimeout
            var jagged = await GetValueAsync<int[][]>("imagearray", ct, timeoutOverride: ImageTimeout).ConfigureAwait(false);
            if (jagged == null || jagged.Length == 0) return new int[0, 0];

            int h = jagged.Length;
            int w = jagged[0]?.Length ?? 0;

            var arr = new int[h, w];
            for (int y = 0; y < h; y++) {
                var row = jagged[y] ?? Array.Empty<int>();
                for (int x = 0; x < w; x++)
                    arr[y, x] = x < row.Length ? row[x] : 0;
            }

            return arr;
        }
    }
}

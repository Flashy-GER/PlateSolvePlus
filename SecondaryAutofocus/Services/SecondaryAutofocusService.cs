﻿using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Plot;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.State;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services {
    public sealed class SecondaryAutofocusService : ISecondaryAutofocusService {
        private readonly ISecondaryCameraCaptureService _capture;
        private readonly IStarMetricService _metric;
        private readonly IFocusMotorService _focus;
        private readonly ICurveFitService _fit;
        private readonly ISecondaryAfStatusPublisher _publisher;
        private readonly ISecondaryAutofocusPlotSink? _plot;
        private readonly Equipment.Interfaces.Mediator.IFocuserMediator? _focuserMediator;

        // Publish pipeline can be re-entrant (Publish -> subscriber updates state -> Publish ...),
        // which can cause stack exhaustion ("stack guard page" crash). We therefore coalesce
        // publish requests and dispatch them asynchronously.
        private int _pushScheduled = 0;
        private SecondaryAutofocusRunState? _pendingPushState;
        private readonly Dispatcher? _uiDispatcher;

        private readonly SemaphoreSlim _runLock = new(1, 1);
        private CancellationTokenSource? _internalCts;
        private string? _disconnectReason;
        private CurveFitResult fit;

        public SecondaryAutofocusService(
            ISecondaryCameraCaptureService capture,
            IStarMetricService metric,
            IFocusMotorService focus,
            ICurveFitService fit,
            ISecondaryAfStatusPublisher publisher,
            Dispatcher? uiDispatcher = null,
            Equipment.Interfaces.Mediator.IFocuserMediator focuserMediator = null,
            ISecondaryAutofocusPlotSink? plotSink = null) {
            _capture = capture;
            _metric = metric;
            _focus = focus;
            _fit = fit;
            _publisher = publisher ?? NullSecondaryAfStatusPublisher.Instance;
            _uiDispatcher = uiDispatcher;
            _plot = plotSink;
            _focuserMediator = focuserMediator;
        }

        public void Cancel() {
            try { _internalCts?.Cancel(); } catch { }
        }

        public async Task<SecondaryAutofocusResult> RunAsync(
            SecondaryAutofocusSettings settings,
            SecondaryAutofocusRunState state,
            CancellationToken ct) {
            // === HARD GUARDS ===
            if (_focus == null) {
                state.Phase = SecondaryAfPhase.Failed;
                state.StatusText = "Focuser not connected.";
                Push(state);
                return new SecondaryAutofocusResult {
                    Success = false,
                    Cancelled = false,
                    Error = "Focuser not connected",
                    StartPosition = state.CurrentPosition,
                    BestPosition = state.BestPosition,
                    BestHfr = state.BestHfr,
                    Samples = state.Samples?.ToList() ?? new List<FocusSample>()
                };
            }

            if (_capture == null) {
                state.Phase = SecondaryAfPhase.Failed;
                state.StatusText = "Camera not connected.";
                Push(state);
                return new SecondaryAutofocusResult {
                    Success = false,
                    Cancelled = false,
                    Error = "Camera not connected",
                    StartPosition = state.CurrentPosition,
                    BestPosition = state.BestPosition,
                    BestHfr = state.BestHfr,
                    Samples = state.Samples?.ToList() ?? new List<FocusSample>()
                };
            }

            System.Diagnostics.Debug.WriteLine("### PSP AF: RunAsync ENTERED ###");

            await _runLock.WaitAsync(ct).ConfigureAwait(false);
            _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _disconnectReason = null;
            var connectionSubscriptions = new List<IDisposable>();
            var autofocusCompleted = false;

            try {
                var lct = _internalCts.Token;
                SubscribeConnectionMonitoring(connectionSubscriptions);
                EnsureDevicesReady();

                // Erst ab hier "running"/"preparing"
                Update(state, SecondaryAfPhase.Preparing, "Preparing…", 0);
                state.StatusText = "Autofocus running...";
                state.Progress = 0;
                Push(state);

                int startPos = state.CurrentPosition > 0
                    ? state.CurrentPosition
                    : await WithCancel(_focus.GetPositionAsync(lct), lct).ConfigureAwait(false);

                var positions = BuildPositions(startPos, settings);
                if (positions.Count < 5)
                    throw new InvalidOperationException("Zu wenige Fokus-Positionen. StepsOut/StepsIn/StepSize prüfen.");

                // Apply optional bounds
                if (settings.MinFocuserPosition < settings.MaxFocuserPosition) {
                    positions = positions
                        .Where(p => p >= settings.MinFocuserPosition && p <= settings.MaxFocuserPosition)
                        .ToList();
                }

                double bestHfr = double.PositiveInfinity;
                int bestPos = startPos;

                var samples = new List<FocusSample>(positions.Count);
                int total = positions.Count;

                // --- OPEN PLOT POPUP (new run) ---
                _plot?.StartNewRun(total);

                for (int i = 0; i < total; i++) {
                    lct.ThrowIfCancellationRequested();
                    int pos = positions[i];

                    Update(state, SecondaryAfPhase.Moving, $"Moving to {pos}", (double)i / total);
                    _plot?.SetPhase($"Step {i + 1}/{total} – moving to {pos} …");
                    await WithCancel(MoveWithBacklashAsync(pos, settings, lct), lct).ConfigureAwait(false);

                    Update(state, SecondaryAfPhase.Settling, $"Settling ({settings.SettleTimeMs}ms)", (double)i / total);
                    _plot?.SetPhase($"Step {i + 1}/{total} – settling …");
                    await _focus.SettleAsync(settings.SettleTimeMs, lct).ConfigureAwait(false);

                    Update(state, SecondaryAfPhase.Capturing, $"Capturing {settings.ExposureSeconds:0.0}s", (double)i / total);
                    _plot?.SetPhase($"Step {i + 1}/{total} – capturing {settings.ExposureSeconds:0.0}s …");
                    EnsureDevicesReady();
                    var frame = await WithCancel(
                        ExecuteDeviceCallAsync(
                            () => _capture.CaptureAsync(
                                new SecondaryCaptureRequest(settings.ExposureSeconds, settings.BinX, settings.BinY, settings.Gain),
                                lct),
                            "Camera"),
                        lct).ConfigureAwait(false);

                    Update(state, SecondaryAfPhase.Measuring, "Measuring stars (HFR)…", (double)i / total);
                    _plot?.SetPhase($"Step {i + 1}/{total} – measuring HFR …");
                    var metric = await WithCancel(_metric.MeasureAsync(frame, settings, lct), lct).ConfigureAwait(false);

                    var ts = DateTime.UtcNow;
                    var sample = new FocusSample(
                        pos,
                        metric.Hfr,
                        metric.StarCount,
                        ts,
                        double.IsNaN(metric.Hfr) ? "HFR=NaN (too few stars?)" : null);

                    samples.Add(sample);
                    await AddSampleAsync(state, sample).ConfigureAwait(false);

                    state.CurrentPosition = pos;
                    state.CurrentHfr = metric.Hfr;
                    state.CurrentStars = metric.StarCount;

                    if (!double.IsNaN(metric.Hfr) && metric.StarCount >= settings.MinStars && metric.Hfr < bestHfr) {
                        bestHfr = metric.Hfr;
                        bestPos = pos;
                        state.BestHfr = bestHfr;
                        state.BestPosition = bestPos;
                    }

                    state.Progress = (double)(i + 1) / total;
                    Push(state);

                    // --- live plot point + best marker + status line ---
                    _plot?.AddSample(sample, i + 1, total, state.BestPosition, state.BestHfr);
                }

                // Fit curve
                Update(state, SecondaryAfPhase.Fitting, "Fitting curve…", 0.95);
                _plot?.SetPhase("Fitting curve…");

                CurveFitResult? fitResult = null;
                try {
                    fitResult = ((dynamic)_fit).Fit(samples);
                } catch {
                    ((dynamic)_fit).Fit(samples);
                }

                int minPos = positions.Min();
                int maxPos = positions.Max();

                int bestFromFit;
                try {
                    bestFromFit = ((dynamic)_fit).GetBestPosition(fitResult, fallbackBest: bestPos, minPos: minPos, maxPos: maxPos);
                } catch {
                    try {
                        bestFromFit = ((dynamic)_fit).GetBestPosition(fallbackBest: bestPos, minPos: minPos, maxPos: maxPos);
                    } catch {
                        bestFromFit = bestPos;
                    }
                }

                // Optional: estimate HFR at bestFromFit (simple: take nearest measured or vertex eval isn't exposed)
                double bestFromFitHfr = samples
                    .Where(s => !double.IsNaN(s.Hfr))
                    .OrderBy(s => Math.Abs(s.Position - bestFromFit))
                    .Select(s => s.Hfr)
                    .FirstOrDefault(double.NaN);

                _plot?.SetFitBest(bestFromFit, bestFromFitHfr);

                // Move to best
                Update(state, SecondaryAfPhase.MovingToBest, $"Moving to best ({bestFromFit})", 0.98);
                _plot?.SetPhase($"Moving to best ({bestFromFit}) …");
                await WithCancel(MoveWithBacklashAsync(bestFromFit, settings, lct), lct).ConfigureAwait(false);
                await _focus.SettleAsync(settings.SettleTimeMs, lct).ConfigureAwait(false);

                Update(state, SecondaryAfPhase.Completed, "Autofocus complete", 1.0);
                _plot?.SetPhase("Autofocus complete (window stays open)");

                // Final state
                state.Phase = SecondaryAfPhase.Completed;
                state.StatusText = "Autofocus completed";
                state.Progress = 1.0;
                Push(state);

                var result = new SecondaryAutofocusResult {
                    Success = true,
                    Cancelled = false,
                    StartPosition = startPos,
                    BestPosition = bestFromFit,
                    BestHfr = double.IsFinite(bestHfr)
                        ? bestHfr
                        : samples.Where(s => !double.IsNaN(s.Hfr)).Select(s => s.Hfr).DefaultIfEmpty(double.NaN).Min(),
                    Samples = samples,
                    Fit = fitResult
                };
                autofocusCompleted = true;
                return result;
            }
            catch (DeviceDisconnectedException ex) {
                state.Phase = SecondaryAfPhase.Failed;
                state.StatusText = "Autofocus failed";
                state.Status = "Autofocus failed";
                state.LastError = ex.Message;
                Push(state);
                _plot?.SetPhase($"Autofocus failed: {ex.Message}");

                var result = new SecondaryAutofocusResult {
                    Success = false,
                    Cancelled = false,
                    Error = ex.Message,
                    StartPosition = state.CurrentPosition,
                    BestPosition = state.BestPosition,
                    BestHfr = state.BestHfr,
                    Samples = state.Samples?.ToList() ?? new List<FocusSample>()
                };
                autofocusCompleted = true;
                return result;
            }
            catch (OperationCanceledException) {
                if (!string.IsNullOrWhiteSpace(_disconnectReason)) {
                    state.Phase = SecondaryAfPhase.Failed;
                    state.StatusText = "Autofocus failed";
                    state.Status = "Autofocus failed";
                    state.LastError = _disconnectReason;
                    Push(state);
                    _plot?.SetPhase($"Autofocus failed: {_disconnectReason}");

                    var result = new SecondaryAutofocusResult {
                        Success = false,
                        Cancelled = false,
                        Error = _disconnectReason,
                        StartPosition = state.CurrentPosition,
                        BestPosition = state.BestPosition,
                        BestHfr = state.BestHfr,
                        Samples = state.Samples?.ToList() ?? new List<FocusSample>()
                    };
                    autofocusCompleted = true;
                    return result;
                }

                state.Phase = SecondaryAfPhase.Cancelled;
                state.StatusText = "Autofocus cancelled";
                state.Status = "Autofocus cancelled";
                Push(state);
                _plot?.SetPhase("Autofocus cancelled (window stays open)");

                var cancelResult = new SecondaryAutofocusResult {
                    Success = false,
                    Cancelled = true,
                    Error = "Cancelled",
                    StartPosition = state.CurrentPosition,
                    BestPosition = state.BestPosition,
                    BestHfr = state.BestHfr,
                    Samples = state.Samples?.ToList() ?? new List<FocusSample>()
                };
                autofocusCompleted = true;
                return cancelResult;
            }
            catch (Exception ex) {
                state.Phase = SecondaryAfPhase.Failed;
                state.StatusText = "Autofocus failed";
                state.Status = "Autofocus failed";
                state.LastError = ex.Message;
                Push(state);
                _plot?.SetPhase($"Autofocus failed: {ex.Message}");

                var errorResult = new SecondaryAutofocusResult {
                    Success = false,
                    Cancelled = false,
                    Error = ex.Message,
                    StartPosition = state.CurrentPosition,
                    BestPosition = state.BestPosition,
                    BestHfr = state.BestHfr,
                    Samples = state.Samples?.ToList() ?? new List<FocusSample>()
                };
                autofocusCompleted = true;
                return errorResult;
            }
            finally {
                foreach (var subscription in connectionSubscriptions) {
                    subscription.Dispose();
                }
                connectionSubscriptions.Clear();
                if (!autofocusCompleted && IsRunningPhase(state.Phase)) {
                    state.Phase = string.IsNullOrWhiteSpace(_disconnectReason)
                        ? SecondaryAfPhase.Cancelled
                        : SecondaryAfPhase.Failed;
                    state.StatusText = state.Phase == SecondaryAfPhase.Failed ? "Autofocus failed" : "Autofocus cancelled";
                    state.Status = state.StatusText;
                    if (!string.IsNullOrWhiteSpace(_disconnectReason)) {
                        state.LastError = _disconnectReason;
                    }
                    Push(state);
                }
                _internalCts?.Dispose();
                _internalCts = null;
                _runLock.Release();
            }
        }

        private static List<int> BuildPositions(int startPos, SecondaryAutofocusSettings s) {
            // Classic NINA-ish: sample from outside -> through -> inside (monotonic)
            // Positions: start + outSteps*step ... start ... start - inSteps*step
            var list = new List<int>();

            for (int i = s.StepsOut; i >= 1; i--)
                list.Add(startPos + i * s.StepSize);

            list.Add(startPos);

            for (int i = 1; i <= s.StepsIn; i++)
                list.Add(startPos - i * s.StepSize);

            // de-dup + stable ordering
            return list.Distinct().ToList();
        }

        private async Task MoveWithBacklashAsync(int targetPos, SecondaryAutofocusSettings s, CancellationToken ct) {
            Debug.WriteLine($"### PSP AF: ABOUT TO MOVE FOCUSER to {targetPos} ###");
            EnsureDevicesReady();
            if (s.BacklashMode == BacklashMode.None || s.BacklashSteps <= 0) {
                await ExecuteDeviceCallAsync(() => _focus.MoveToAsync(targetPos, ct), "Focuser").ConfigureAwait(false);
                return;
            }

            if (s.BacklashMode == BacklashMode.OvershootReturn) {
                // Overshoot then return to approach from one direction
                int overshoot = targetPos + s.BacklashSteps;
                await ExecuteDeviceCallAsync(() => _focus.MoveToAsync(overshoot, ct), "Focuser").ConfigureAwait(false);
                await ExecuteDeviceCallAsync(() => _focus.MoveToAsync(targetPos, ct), "Focuser").ConfigureAwait(false);
                return;
            }

            if (s.BacklashMode == BacklashMode.OneWayApproach) {
                // Always approach from higher position (example policy)
                int cur = await ExecuteDeviceCallAsync(() => _focus.GetPositionAsync(ct), "Focuser").ConfigureAwait(false);
                if (cur < targetPos) {
                    int pre = targetPos + s.BacklashSteps;
                    await ExecuteDeviceCallAsync(() => _focus.MoveToAsync(pre, ct), "Focuser").ConfigureAwait(false);
                }
                await ExecuteDeviceCallAsync(() => _focus.MoveToAsync(targetPos, ct), "Focuser").ConfigureAwait(false);
            }
        }

        private void Update(SecondaryAutofocusRunState state, SecondaryAfPhase phase, string status, double progress) {
            state.Phase = phase;
            state.Status = status;
            state.Progress = Math.Clamp(progress, 0, 1);
            Push(state);
        }

        private void Push(SecondaryAutofocusRunState state) {
            // Coalesce: keep only the latest state. Never publish synchronously to avoid re-entrancy.
            _pendingPushState = state;

            // Only schedule one publisher at a time.
            if (Interlocked.Exchange(ref _pushScheduled, 1) == 1)
                return;

            void PublishLoop() {
                try {
                    while (true) {
                        // Grab and clear the pending state. If nothing pending -> stop.
                        var s = Interlocked.Exchange(ref _pendingPushState, null);
                        if (s == null)
                            return;

                        _publisher.Publish(s);
                        // If another update arrived while publishing, it will be in _pendingPushState -> loop continues.
                    }
                } finally {
                    // Allow another schedule.
                    Volatile.Write(ref _pushScheduled, 0);

                    // If a new state arrived after we cleared scheduled, schedule again.
                    if (_pendingPushState != null && Interlocked.Exchange(ref _pushScheduled, 1) == 0) {
                        if (_uiDispatcher != null) {
                            _uiDispatcher.BeginInvoke((Action)PublishLoop);
                        } else {
                            _ = Task.Run((Action)PublishLoop);
                        }
                    }
                }
            }

            if (_uiDispatcher != null) {
                _uiDispatcher.BeginInvoke((Action)PublishLoop);
            } else {
                _ = Task.Run((Action)PublishLoop);
            }
        }

        private Task AddSampleAsync(SecondaryAutofocusRunState state, FocusSample sample) {
            // If WPF UI binds to ObservableCollection, add on UI thread.
            // IMPORTANT: Use BeginInvoke (fire-and-forget) to avoid Dispatcher re-entrancy / deep call stacks.
            if (_uiDispatcher == null || _uiDispatcher.CheckAccess()) {
                state.Samples.Add(sample);
                Push(state);
                return Task.CompletedTask;
            }

            _uiDispatcher.BeginInvoke((Action)(() => {
                state.Samples.Add(sample);
                Push(state);
            }));

            return Task.CompletedTask;
        }

        public async Task RunAsync(PlateSolvePlusSettings.SecondaryAutofocusSettings settings, SecondaryAutofocusRunState runState, CancellationToken token) {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (runState == null) throw new ArgumentNullException(nameof(runState));

            // Map plugin settings -> internal AF settings model
            var modelSettings = MapToModelSettings(settings);

            // Run the actual autofocus routine (the overload that returns SecondaryAutofocusResult)
            await RunAsync(modelSettings, runState, token).ConfigureAwait(false);
        }

        private static SecondaryAutofocusSettings MapToModelSettings(PlateSolvePlusSettings.SecondaryAutofocusSettings src) {
            var dst = new SecondaryAutofocusSettings();

            // Reflection-based copy keeps this resilient to future setting additions without hard dependencies.
            var srcType = src.GetType();
            var dstType = typeof(SecondaryAutofocusSettings);

            foreach (var dp in dstType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) {
                if (!dp.CanWrite) continue;

                var sp = srcType.GetProperty(dp.Name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (sp == null || !sp.CanRead) continue;

                object? sv;
                try { sv = sp.GetValue(src); } catch { continue; }
                if (sv == null) continue;

                try {
                    var targetType = Nullable.GetUnderlyingType(dp.PropertyType) ?? dp.PropertyType;

                    // Direct assign
                    if (targetType.IsInstanceOfType(sv)) {
                        dp.SetValue(dst, sv);
                        continue;
                    }

                    // Enum mapping by name/value
                    if (targetType.IsEnum) {
                        object enumVal;
                        if (sv is string s) {
                            enumVal = Enum.Parse(targetType, s, ignoreCase: true);
                        } else if (sv.GetType().IsEnum) {
                            enumVal = Enum.Parse(targetType, sv.ToString() ?? string.Empty, ignoreCase: true);
                        } else {
                            enumVal = Enum.ToObject(targetType, sv);
                        }
                        dp.SetValue(dst, enumVal);
                        continue;
                    }

                    // Numeric / simple conversion
                    var converted = Convert.ChangeType(sv, targetType, System.Globalization.CultureInfo.InvariantCulture);
                    dp.SetValue(dst, converted);
                } catch {
                    // ignore non-convertible properties
                }
            }

            return dst;
        }

        private static async Task<T> WithCancel<T>(Task<T> task, CancellationToken ct) {
            var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var done = await Task.WhenAny(task, cancelTask).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return await task.ConfigureAwait(false);
        }

        private static async Task WithCancel(Task task, CancellationToken ct) {
            var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var done = await Task.WhenAny(task, cancelTask).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await task.ConfigureAwait(false);
        }

        private void SubscribeConnectionMonitoring(List<IDisposable> subscriptions) {
            var cameraSource = ResolveCameraConnectionSource();
            var focuserSource = ResolveFocuserConnectionSource();

            SubscribeConnectionEvents(cameraSource, "Camera", subscriptions);
            SubscribeConnectionEvents(focuserSource, "Focuser", subscriptions);
        }

        private void SubscribeConnectionEvents(object? source, string deviceLabel, List<IDisposable> subscriptions) {
            if (source == null) return;

            void EvaluateConnection() {
                if (TryGetConnectionState(source, out var connected) && !connected) {
                    TriggerDisconnect($"{deviceLabel} disconnected.");
                }
            }

            foreach (var eventName in new[] { "ConnectedChanged", "ConnectionChanged", "IsConnectedChanged" }) {
                var evt = source.GetType().GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (evt == null) continue;

                var callback = new ConnectionCallback(EvaluateConnection);
                var handler = callback.CreateHandler(evt.EventHandlerType);
                if (handler == null) continue;

                evt.AddEventHandler(source, handler);
                subscriptions.Add(new EventSubscription(source, evt, handler, callback));
            }

            if (source is INotifyPropertyChanged notify) {
                PropertyChangedEventHandler handler = (_, args) => {
                    if (string.IsNullOrWhiteSpace(args.PropertyName)
                        || string.Equals(args.PropertyName, "IsConnected", StringComparison.Ordinal)
                        || string.Equals(args.PropertyName, "Connected", StringComparison.Ordinal)) {
                        EvaluateConnection();
                    }
                };
                notify.PropertyChanged += handler;
                subscriptions.Add(new DelegateSubscription(() => notify.PropertyChanged -= handler));
            }
        }

        private void EnsureDevicesReady() {
            if (TryGetConnectionState(ResolveCameraConnectionSource(), out var cameraConnected) && !cameraConnected) {
                TriggerDisconnect("Camera disconnected.");
                throw new DeviceDisconnectedException("Camera disconnected.");
            }

            if (TryGetConnectionState(ResolveFocuserConnectionSource(), out var focuserConnected) && !focuserConnected) {
                TriggerDisconnect("Focuser disconnected.");
                throw new DeviceDisconnectedException("Focuser disconnected.");
            }
        }

        private async Task<T> ExecuteDeviceCallAsync<T>(Func<Task<T>> action, string deviceLabel) {
            try {
                return await action().ConfigureAwait(false);
            } catch (TimeoutException ex) {
                var message = $"{deviceLabel} unreachable (timeout).";
                TriggerDisconnect(message);
                throw new DeviceDisconnectedException(message, ex);
            }
        }

        private async Task ExecuteDeviceCallAsync(Func<Task> action, string deviceLabel) {
            try {
                await action().ConfigureAwait(false);
            } catch (TimeoutException ex) {
                var message = $"{deviceLabel} unreachable (timeout).";
                TriggerDisconnect(message);
                throw new DeviceDisconnectedException(message, ex);
            }
        }

        private void TriggerDisconnect(string message) {
            _disconnectReason ??= message;
            try { _internalCts?.Cancel(); } catch { }
        }

        private object? ResolveCameraConnectionSource() {
            if (_capture == null) return null;
            var type = _capture.GetType();
            var field = type.GetField("_secondaryCameraService", BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(_capture) ?? _capture;
        }

        private object? ResolveFocuserConnectionSource() => _focuserMediator ?? _focus;

        private static bool TryGetConnectionState(object? source, out bool isConnected) {
            isConnected = true;
            if (source == null) return false;

            var type = source.GetType();
            var prop = type.GetProperty("IsConnected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       ?? type.GetProperty("Connected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null || prop.PropertyType != typeof(bool)) {
                return false;
            }

            try {
                isConnected = (bool)(prop.GetValue(source) ?? false);
                return true;
            } catch {
                return false;
            }
        }

        private static bool IsRunningPhase(SecondaryAfPhase phase) =>
            phase is SecondaryAfPhase.Preparing
                or SecondaryAfPhase.Moving
                or SecondaryAfPhase.Settling
                or SecondaryAfPhase.Capturing
                or SecondaryAfPhase.Measuring
                or SecondaryAfPhase.Fitting
                or SecondaryAfPhase.MovingToBest;

        private sealed class ConnectionCallback {
            private readonly Action _evaluate;

            public ConnectionCallback(Action evaluate) {
                _evaluate = evaluate;
            }

            public void Handle(object? sender, EventArgs args) {
                _evaluate();
            }

            public Delegate? CreateHandler(Type? handlerType) {
                if (handlerType == null) return null;
                var method = GetType().GetMethod(nameof(Handle), BindingFlags.Instance | BindingFlags.Public);
                return method == null
                    ? null
                    : Delegate.CreateDelegate(handlerType, this, method, throwOnBindFailure: false);
            }
        }

        private sealed class EventSubscription : IDisposable {
            private readonly object _source;
            private readonly EventInfo _eventInfo;
            private readonly Delegate _handler;
            private readonly ConnectionCallback _callback;

            public EventSubscription(object source, EventInfo eventInfo, Delegate handler, ConnectionCallback callback) {
                _source = source;
                _eventInfo = eventInfo;
                _handler = handler;
                _callback = callback;
            }

            public void Dispose() {
                _eventInfo.RemoveEventHandler(_source, _handler);
            }
        }

        private sealed class DelegateSubscription : IDisposable {
            private readonly Action _unsubscribe;

            public DelegateSubscription(Action unsubscribe) {
                _unsubscribe = unsubscribe;
            }

            public void Dispose() {
                _unsubscribe();
            }
        }

        private sealed class DeviceDisconnectedException : Exception {
            public DeviceDisconnectedException(string message) : base(message) { }
            public DeviceDisconnectedException(string message, Exception innerException) : base(message, innerException) { }
        }
    }
}

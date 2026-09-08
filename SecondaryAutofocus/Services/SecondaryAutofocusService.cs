using NINA.Core.Utility;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Models;
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

        private readonly Func<bool>? _isCameraConnected;
        private readonly Func<bool>? _isFocuserConnected;

        // Publish pipeline can be re-entrant (Publish -> subscriber updates state -> Publish ...),
        // which can cause stack exhaustion ("stack guard page" crash). We therefore coalesce
        // publish requests and dispatch them asynchronously.
        private int _pushScheduled = 0;
        private SecondaryAutofocusRunState? _pendingPushState;
        private readonly Dispatcher? _uiDispatcher;

        private readonly SemaphoreSlim _runLock = new(1, 1);
        private CancellationTokenSource? _internalCts;
        private string? _disconnectReason;

        public SecondaryAutofocusService(
            ISecondaryCameraCaptureService capture,
            IStarMetricService metric,
            IFocusMotorService focus,
            ICurveFitService fit,
            ISecondaryAfStatusPublisher publisher,
            Dispatcher? uiDispatcher = null,
            Equipment.Interfaces.Mediator.IFocuserMediator? focuserMediator = null,
            ISecondaryAutofocusPlotSink? plotSink = null,
            Func<bool>? isCameraConnected = null,
            Func<bool>? isFocuserConnected = null
        ) {
            _capture = capture;
            _metric = metric;
            _focus = focus;
            _fit = fit;
            _publisher = publisher;
            _uiDispatcher = uiDispatcher;
            _focuserMediator = focuserMediator;
            _plot = plotSink;
            _isCameraConnected = isCameraConnected;
            _isFocuserConnected = isFocuserConnected;
        }

        public void Cancel() {
            try { _internalCts?.Cancel(); } catch { }
        }

        public async Task<SecondaryAutofocusResult> RunAsync(
            SecondaryAutofocusSettings settings,
            SecondaryAutofocusRunState state,
            CancellationToken token) {

            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (state == null) throw new ArgumentNullException(nameof(state));

            Debug.WriteLine("### PSP AF: RunAsync ENTERED ###");
            Logger.Debug($"[PlateSolvePlus] AF Settings instance hash: {settings.GetHashCode()}");
            Logger.Debug($"[PlateSolvePlus] Secondary AF Settings:" +
                $" Exposure={settings.ExposureSeconds}s" +
                $" Gain={settings.Gain}" +
                $" Bin={settings.BinX}x{settings.BinY}" +
                $" StepSize={settings.StepSize}" +
                $" StepsOut={settings.StepsOut}" +
                $" StepsIn={settings.StepsIn}" +
                $" Settle={settings.SettleTimeMs}ms" +
                $" BacklashSteps={settings.BacklashSteps}" +
                $" BacklashMode={settings.BacklashMode}" +
                $" MinStars={settings.MinStars}" +
                $" MaxStars={settings.MaxStars}" +
                $" Timeout={settings.TimeoutSeconds}s");

            await _runLock.WaitAsync(token).ConfigureAwait(false);
            _internalCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _disconnectReason = null;
            var connectionSubscriptions = new List<IDisposable>();
            var autofocusCompleted = false;

            try {
                var lct = _internalCts.Token;
                SubscribeConnectionMonitoring(connectionSubscriptions);
                StartDisconnectPoller(lct);

                EnsureDevicesReady();

                // initial
                state.Phase = SecondaryAfPhase.Preparing;
                state.StatusText = "Preparing";
                state.Status = "Preparing";
                state.LastError = null;
                state.Progress = 0;
                Push(state);

                // Determine start position
                int startPos = state.CurrentPosition > 0
                    ? state.CurrentPosition
                    : await ExecuteDeviceCallAsync(() => _focus.GetPositionAsync(lct), "Focuser").ConfigureAwait(false);

                state.CurrentPosition = startPos;
                Push(state);

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

                    // IMPORTANT: this is your actual capture signature (from your original file)
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

                Update(state, SecondaryAfPhase.Fitting, "Fitting curve…", 0.98);
                _plot?.SetPhase("Fitting curve …");

                object? fitResult = null;
                int bestFromFit = bestPos;
                int minPos = positions.Min();
                int maxPos = positions.Max();

                // Use existing dynamic fit pattern (as in your original file)
                try {
                    fitResult = ((dynamic)_fit).Fit(samples);
                } catch {
                    try { ((dynamic)_fit).Fit(samples); } catch { }
                }

                try {
                    bestFromFit = ((dynamic)_fit).GetBestPosition(fitResult, fallbackBest: bestPos, minPos: minPos, maxPos: maxPos);
                } catch {
                    try {
                        bestFromFit = ((dynamic)_fit).GetBestPosition(fallbackBest: bestPos, minPos: minPos, maxPos: maxPos);
                    } catch {
                        bestFromFit = bestPos;
                    }
                }

                state.BestPosition = bestFromFit;
                state.BestHfr = bestHfr;
                Push(state);

                EnsureDevicesReady();

                Update(state, SecondaryAfPhase.MovingToBest, $"Moving to best {bestFromFit}", 0.99);
                _plot?.SetPhase($"Moving to best {bestFromFit} …");

                await WithCancel(MoveWithBacklashAsync(bestFromFit, settings, lct), lct).ConfigureAwait(false);

                state.Phase = SecondaryAfPhase.Completed;
                state.StatusText = "Autofocus completed";
                state.Status = "Autofocus completed";
                state.Progress = 1;
                Push(state);

                var okResult = new SecondaryAutofocusResult {
                    Success = true,
                    Cancelled = false,
                    Error = null,
                    StartPosition = startPos,
                    BestPosition = state.BestPosition,
                    BestHfr = state.BestHfr,
                    Samples = state.Samples?.ToList() ?? new List<FocusSample>()
                };
                autofocusCompleted = true;
                return okResult;
            } catch (DeviceDisconnectedException ex) {
                state.Phase = SecondaryAfPhase.Failed;
                state.StatusText = "Autofocus failed";
                state.Status = "Autofocus failed";
                state.LastError = ex.Message;
                Push(state);
                _plot?.SetPhase($"Autofocus failed: {ex.Message}");

                var failResult = new SecondaryAutofocusResult {
                    Success = false,
                    Cancelled = false,
                    Error = ex.Message,
                    StartPosition = state.CurrentPosition,
                    BestPosition = state.BestPosition,
                    BestHfr = state.BestHfr,
                    Samples = state.Samples?.ToList() ?? new List<FocusSample>()
                };
                autofocusCompleted = true;
                return failResult;
            } catch (OperationCanceledException) {
                // If we cancelled due to disconnect, treat it as failed with a clear message
                if (!string.IsNullOrWhiteSpace(_disconnectReason)) {
                    state.Phase = SecondaryAfPhase.Failed;
                    state.StatusText = "Autofocus failed";
                    state.Status = "Autofocus failed";
                    state.LastError = _disconnectReason;
                    Push(state);
                    _plot?.SetPhase($"Autofocus failed: {_disconnectReason}");

                    var failResult = new SecondaryAutofocusResult {
                        Success = false,
                        Cancelled = false,
                        Error = _disconnectReason,
                        StartPosition = state.CurrentPosition,
                        BestPosition = state.BestPosition,
                        BestHfr = state.BestHfr,
                        Samples = state.Samples?.ToList() ?? new List<FocusSample>()
                    };
                    autofocusCompleted = true;
                    return failResult;
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
            } catch (Exception ex) {
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
            } finally {
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

        private async Task MoveWithBacklashAsync(int targetPos,SecondaryAutofocusSettings s, CancellationToken ct) {
            Debug.WriteLine($"### PSP AF: ABOUT TO MOVE FOCUSER to {targetPos} ###");

            // --- NEW: never allow invalid positions (prevents "move to -1") ---
            if (targetPos < 0) {
                TriggerDisconnect("Invalid focuser position (device disconnected?).");
                throw new DeviceDisconnectedException("Invalid focuser position (device disconnected?).");
            }

            EnsureDevicesReady();

            if (s.BacklashMode == BacklashMode.None || s.BacklashSteps <= 0) {
                await ExecuteDeviceCallAsync(() => _focus.MoveToAsync(targetPos, ct), "Focuser").ConfigureAwait(false);
                return;
            }

            if (s.BacklashMode == BacklashMode.OvershootReturn) {
                int overshoot = targetPos + s.BacklashSteps;
                await ExecuteDeviceCallAsync(() => _focus.MoveToAsync(overshoot, ct), "Focuser").ConfigureAwait(false);
                await ExecuteDeviceCallAsync(() => _focus.MoveToAsync(targetPos, ct), "Focuser").ConfigureAwait(false);
                return;
            }

            if (s.BacklashMode == BacklashMode.OneWayApproach) {
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
            _pendingPushState = state;

            if (Interlocked.Exchange(ref _pushScheduled, 1) == 1)
                return;

            void PublishLoop() {
                try {
                    while (true) {
                        var s = Interlocked.Exchange(ref _pendingPushState, null);
                        if (s == null)
                            return;

                        _publisher.Publish(s);
                    }
                } finally {
                    Interlocked.Exchange(ref _pushScheduled, 0);

                    if (_pendingPushState != null) {
                        if (Interlocked.Exchange(ref _pushScheduled, 1) == 0) {
                            SchedulePublish(PublishLoop);
                        }
                    }
                }
            }

            SchedulePublish(PublishLoop);
        }

        private void SchedulePublish(Action publishLoop) {
            if (_uiDispatcher != null) {
                _uiDispatcher.BeginInvoke(publishLoop);
            } else {
                Task.Run(publishLoop);
            }
        }

        private async Task AddSampleAsync(SecondaryAutofocusRunState state, FocusSample sample) {
            // Keep UI thread happy if needed
            if (_uiDispatcher != null && !_uiDispatcher.CheckAccess()) {
                await _uiDispatcher.InvokeAsync(() => {
                    state.Samples.Add(sample);
                });
            } else {
                state.Samples.Add(sample);
            }
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


        private void StartDisconnectPoller(CancellationToken ct) {
            // Some device wrappers don't expose connection events/properties in a way we can reflect reliably.
            // A lightweight poll makes disconnect detection robust (camera unplug, focuser disconnect, Alpaca drop).
            if (_isCameraConnected == null && _isFocuserConnected == null) return;

            _ = Task.Run(async () => {
                try {
                    while (!ct.IsCancellationRequested) {
                        if (_isCameraConnected != null && !_isCameraConnected()) {
                            TriggerDisconnect("Camera disconnected/unreachable.");
                            return;
                        }
                        if (_isFocuserConnected != null && !_isFocuserConnected()) {
                            TriggerDisconnect("Focuser disconnected/unreachable.");
                            return;
                        }
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }
                } catch (OperationCanceledException) {
                    // normal shutdown
                } catch {
                    // ignore poll errors; device calls will still fail-fast via ExecuteDeviceCallAsync
                }
            }, ct);
        }

        private void EnsureDevicesReady() {
            // --- NEW: if monitoring already detected a disconnect/unreachable, fail fast ---
            if (!string.IsNullOrWhiteSpace(_disconnectReason)) {
                throw new DeviceDisconnectedException(_disconnectReason);
            }

            // Prefer explicit connection providers (passed from the host VM) if available.
            if (_isCameraConnected != null && !_isCameraConnected()) {
                TriggerDisconnect("Camera disconnected/unreachable.");
                throw new DeviceDisconnectedException("Camera disconnected/unreachable.");
            }
            if (_isFocuserConnected != null && !_isFocuserConnected()) {
                TriggerDisconnect("Focuser disconnected/unreachable.");
                throw new DeviceDisconnectedException("Focuser disconnected/unreachable.");
            }

            if (TryGetConnectionState(ResolveCameraConnectionSource(), out var cameraConnected) && !cameraConnected) {
                TriggerDisconnect("Camera disconnected/unreachable.");
                throw new DeviceDisconnectedException("Camera disconnected/unreachable.");
            }

            if (TryGetConnectionState(ResolveFocuserConnectionSource(), out var focuserConnected) && !focuserConnected) {
                TriggerDisconnect("Focuser disconnected/unreachable.");
                throw new DeviceDisconnectedException("Focuser disconnected/unreachable.");
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

        private object? ResolveFocuserConnectionSource()
                 => (object?)_focuserMediator ?? _focus;

        // --- CRITICAL FIX: treat exceptions while reading Connected as "disconnected/unreachable" ---
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
                // if Connected getter throws (Alpaca timeout wrapped etc.), treat as not connected
                isConnected = false;
                return true;
            }
        }

        private static async Task WithCancel(Task task, CancellationToken ct) {
            var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var completed = await Task.WhenAny(task, cancelTask).ConfigureAwait(false);
            if (completed == cancelTask) ct.ThrowIfCancellationRequested();
            await task.ConfigureAwait(false);
        }

        private static async Task<T> WithCancel<T>(Task<T> task, CancellationToken ct) {
            var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var completed = await Task.WhenAny(task, cancelTask).ConfigureAwait(false);
            if (completed == cancelTask) ct.ThrowIfCancellationRequested();
            return await task.ConfigureAwait(false);
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

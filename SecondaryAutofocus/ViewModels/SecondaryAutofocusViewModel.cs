using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.Services;
using NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.State;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PlateSolvePlus.SecondaryAutofocus.ViewModels;

public sealed class SecondaryAutofocusViewModel : ObservableObject {
    private readonly ISecondaryAutofocusService _af;
    private readonly Action _beforeRun;
    private CancellationTokenSource? _cts;

    // beim Start: settings aus PluginSettings kopieren
    public PlateSolvePlusSettings.SecondaryAutofocusSettings Settings { get; } = new();
    public SecondaryAutofocusRunState RunState { get; } = new();

    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public SecondaryAutofocusViewModel(ISecondaryAutofocusService af, Action beforeRun) {
        _af = af;
        _beforeRun = beforeRun ?? (() => { });
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        CancelCommand = new RelayCommand(Cancel, CanCancel);
    }

    private bool CanStart() =>
        RunState.Phase is SecondaryAfPhase.Idle or SecondaryAfPhase.Completed or SecondaryAfPhase.Failed or SecondaryAfPhase.Cancelled;

    private bool CanCancel() =>
        RunState.Phase is not (SecondaryAfPhase.Idle or SecondaryAfPhase.Completed or SecondaryAfPhase.Failed or SecondaryAfPhase.Cancelled);

    private async Task StartAsync() {
        _cts?.Dispose();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.TimeoutSeconds));

        // Ensure dependencies (camera selection, setting sync, etc.)
        try { _beforeRun(); } catch { /* never crash UI */ }

        RunState.Samples.Clear();
        RunState.LastError = null;
        RunState.Progress = 0;
        RunState.Phase = SecondaryAfPhase.Preparing;

        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        try {
            await _af.RunAsync(Settings, RunState, _cts.Token);
        } catch (NotImplementedException) {
            // defensive: avoid unhandled exceptions if service is stubbed
            RunState.LastError = "Secondary Autofocus is not implemented yet.";
        } catch (OperationCanceledException) {
            // user cancel/timeout - no need to treat as crash
        } catch (Exception ex) {
            RunState.LastError = ex.Message;
            RunState.Phase = SecondaryAfPhase.Failed;

        } finally {
            StartCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private void Cancel() {
        try { _cts?.Cancel(); } catch { }
        _af.Cancel();
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}

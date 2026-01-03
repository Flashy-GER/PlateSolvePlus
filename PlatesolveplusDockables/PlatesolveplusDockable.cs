using NINA.Core.Model;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Plugins.PlateSolvePlus;
using NINA.Plugins.PlateSolvePlus.PlateSolving;
using NINA.Plugins.PlateSolvePlus.SecondaryCamera;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NINA.Plugins.PlateSolvePlus.PlatesolveplusDockables {
    // WICHTIG: das ist DER Dockable-Contract, den dein NINA scannt
    [Export(typeof(NINA.Equipment.Interfaces.ViewModel.IDockableVM))]
    public class PlateSolvePlusDockable : DockableVM, NINA.Equipment.Interfaces.ViewModel.IDockableVM {
        private readonly IProfileService profileService;

        // Zugriff auf Plugin-Optionen (FocalLength, PixelSize, etc.)
        [Import(AllowDefault = true)]
        public Platesolveplus? PluginSettings { get; set; }

        [Import(AllowDefault = true)]
        public IPlateSolverFactory? PlateSolverFactory { get; set; }

        [Import(AllowDefault = true)]
        public IImageDataFactory? ImageDataFactory { get; set; }

        private ISecondaryCamera? secondaryCamera;
        private const string ProgId = "ASCOM.Simulator.Camera";

        public ICommand ConnectSecondaryCommand { get; }
        public ICommand DisconnectSecondaryCommand { get; }
        public ICommand CaptureAndSolveCommand { get; }

        private string _statusText = "Idle";
        public string StatusText {
            get => _statusText;
            set { _statusText = value; RaisePropertyChanged(nameof(StatusText)); }
        }

        private string _detailsText = "";
        public string DetailsText {
            get => _detailsText;
            set { _detailsText = value; RaisePropertyChanged(nameof(DetailsText)); }
        }

        private bool _isSecondaryConnected;
        public bool IsSecondaryConnected {
            get => _isSecondaryConnected;
            set { _isSecondaryConnected = value; RaisePropertyChanged(nameof(IsSecondaryConnected)); }
        }

        [ImportingConstructor]
        public PlateSolvePlusDockable(IProfileService profileService)
            : base(profileService) {
            this.profileService = profileService;

            Title = "PlateSolvePlus";
            IsVisible = true;

            ConnectSecondaryCommand = new SimpleAsyncCommand(ConnectSecondaryAsync);
            DisconnectSecondaryCommand = new SimpleAsyncCommand(DisconnectSecondaryAsync);
            CaptureAndSolveCommand = new SimpleAsyncCommand(CaptureAndSolveAsync);

            StatusText = "Ready";
            DetailsText = "Connect secondary camera to start.";
        }

        private async Task ConnectSecondaryAsync() {
            try {
                StatusText = "Connecting secondary camera...";
                DetailsText = $"ProgID: {ProgId}";

                secondaryCamera?.Dispose();
                secondaryCamera = new AscomComSecondaryCamera(ProgId);

                await secondaryCamera.ConnectAsync(CancellationToken.None);
                IsSecondaryConnected = secondaryCamera.IsConnected;

                StatusText = IsSecondaryConnected
                    ? "Secondary camera connected ✅"
                    : "Secondary camera not connected ❌";
            } catch (Exception ex) {
                StatusText = "Connect failed ❌";
                DetailsText = ex.ToString();
                IsSecondaryConnected = false;
            }
        }

        private async Task DisconnectSecondaryAsync() {
            try {
                StatusText = "Disconnecting secondary camera...";
                if (secondaryCamera != null)
                    await secondaryCamera.DisconnectAsync(CancellationToken.None);

                secondaryCamera?.Dispose();
                secondaryCamera = null;

                IsSecondaryConnected = false;
                StatusText = "Disconnected ✅";
            } catch (Exception ex) {
                StatusText = "Disconnect failed ❌";
                DetailsText = ex.ToString();
            }
        }

        private async Task CaptureAndSolveAsync() {
            if (secondaryCamera == null || !secondaryCamera.IsConnected) {
                StatusText = "Secondary camera not connected ❌";
                DetailsText = "Click Connect first.";
                return;
            }

            try {
                // ---------- Factories ----------
                var imgFactory = ImageDataFactory ?? TryResolve<IImageDataFactory>();
                var psFactory = PlateSolverFactory ?? TryResolve<IPlateSolverFactory>();

                if (imgFactory == null || psFactory == null) {
                    StatusText = "Missing NINA factories ❌";
                    DetailsText = $"ImageDataFactory={imgFactory != null}, PlateSolverFactory={psFactory != null}";
                    return;
                }

                // ---------- Capture ----------
                StatusText = "Capturing frame...";
                DetailsText = "";

                var frame = await secondaryCamera.CaptureAsync(
                    exposureSeconds: 2.0,
                    binX: 1,
                    binY: 1,
                    gain: null,
                    ct: CancellationToken.None);

                // ---------- Build IImageData ----------
                StatusText = "Building IImageData...";
                var meta = new ImageMetaData();
                ushort[] packed = ConvertToUShortRowMajor(frame.Pixels, frame.Width, frame.Height);

                var imageData = imgFactory.CreateBaseImageData(
                    input: packed,
                    width: frame.Width,
                    height: frame.Height,
                    bitDepth: frame.BitDepth,
                    isBayered: false,
                    metaData: meta);

                // ---------- PlateSolve Parameters ----------
                var settings = profileService.ActiveProfile.PlateSolveSettings;

                double sr = ReadDouble(settings, "SearchRadius", "SearchRadiusDeg", "Radius", "RadiusDeg") ?? 5.0;
                int ds = ReadInt(settings, "Downsample", "DownSample", "DownSampling", "DownSamplingFactor") ?? 2;
                int to = ReadInt(settings, "Timeout", "TimeoutSeconds", "SolveTimeout", "SolveTimeoutSeconds") ?? 60;

                double focalLengthMm = PluginSettings?.GuideFocalLengthMm ?? 240.0;
                double pixelSizeUm = PluginSettings?.GuidePixelSizeUm ?? 3.75;

                var parameter = NinaPlateSolveParameterFactory.Create(
                    searchRadiusDeg: sr,
                    downsample: ds,
                    timeoutSec: to,
                    focalLengthMm: focalLengthMm,
                    pixelSizeUm: pixelSizeUm
                );


                // ---------- Solve ----------
                var progress = new Progress<ApplicationStatus>(s => {
                    if (!string.IsNullOrWhiteSpace(s?.Status))
                        StatusText = $"Solving: {s.Status}";
                });

                var solver = psFactory.GetPlateSolver(settings);

                StatusText = "Solving (ASTAP / configured solver)...";
                var result = await solver.SolveAsync(imageData, parameter, progress, CancellationToken.None);

                StatusText = "Solve finished ✅";
                DetailsText =
                    $"Frame: {frame.Width}x{frame.Height}, bitDepth={frame.BitDepth}\n" +
                    $"SearchRadius={sr}°, Downsample={ds}, Timeout={to}s\n" +
                    $"FocalLength={focalLengthMm}mm, PixelSize={pixelSizeUm}µm\n\n" +
                    DumpObject(result);
            } catch (Exception ex) {
                StatusText = "Capture / Solve failed ❌";
                DetailsText = ex.ToString();
            }
        }

        // ---------- Helpers ----------

        private static T? TryResolve<T>() where T : class {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types) {
                    if (t.IsAbstract || t.IsInterface) continue;
                    if (!typeof(T).IsAssignableFrom(t)) continue;
                    if (t.GetConstructor(Type.EmptyTypes) == null) continue;

                    try {
                        if (Activator.CreateInstance(t) is T ok)
                            return ok;
                    } catch { }
                }
            }
            return null;
        }

        private static ushort[] ConvertToUShortRowMajor(int[,] pixels, int width, int height) {
            var arr = new ushort[width * height];
            int idx = 0;

            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) {
                    int v = pixels[y, x];
                    if (v < 0) v = 0;
                    if (v > 65535) v = 65535;
                    arr[idx++] = (ushort)v;
                }

            return arr;
        }

        private static double? ReadDouble(object obj, params string[] names) {
            foreach (var n in names) {
                var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                if (double.TryParse(v.ToString(), out var d)) return d;
            }
            return null;
        }

        private static int? ReadInt(object obj, params string[] names) {
            foreach (var n in names) {
                var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                if (int.TryParse(v.ToString(), out var i)) return i;
            }
            return null;
        }

        private static string DumpObject(object obj) {
            if (obj == null) return "<null>";
            var props = obj.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead)
                .OrderBy(p => p.Name);

            return string.Join("\n", props.Select(p => {
                object? v = null;
                try { v = p.GetValue(obj); } catch { }
                return $"{p.Name} = {v}";
            }));
        }
    }

    public sealed class SimpleAsyncCommand : ICommand {
        private readonly Func<Task> execute;
        private bool isExecuting;

        public SimpleAsyncCommand(Func<Task> execute) => this.execute = execute;

        public bool CanExecute(object parameter) => !isExecuting;
        public event EventHandler? CanExecuteChanged;

        public async void Execute(object parameter) {
            if (isExecuting) return;
            try {
                isExecuting = true;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
                await execute();
            } finally {
                isExecuting = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

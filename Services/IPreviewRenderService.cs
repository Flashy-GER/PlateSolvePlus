using NINA.Plugins.PlateSolvePlus.Models;
using System.Windows.Media.Imaging;

namespace NINA.Plugins.PlateSolvePlus.Services {
    internal sealed class PreviewRenderOptions {
        public bool AutoStretch { get; set; } = true;
        public double StretchLowPercentile { get; set; } = 0.01;
        public double StretchHighPercentile { get; set; } = 0.995;
        public double Gamma { get; set; } = 0.9;
    }

    internal interface IPreviewRenderService {
        BitmapSource RenderPreview(CapturedFrame frame, PreviewRenderOptions options);
    }
}


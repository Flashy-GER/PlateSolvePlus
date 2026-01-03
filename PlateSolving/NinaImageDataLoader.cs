using NINA.Image.Interfaces;
using System;
using System.Linq;
using System.Reflection;

namespace NINA.Plugins.PlateSolvePlus.PlateSolving {
    /// <summary>
    /// Lädt ein FITS-Bild als N.I.N.A IImageData.
    /// Da sich Loader-/Klassennamen zwischen Builds ändern können,
    /// suchen wir zur Laufzeit nach einem passenden Typ (Reflection).
    /// </summary>
    public static class NinaImageDataLoader {
        public static IImageData LoadFitsAsImageData(string fitsPath) {
            if (string.IsNullOrWhiteSpace(fitsPath))
                throw new ArgumentNullException(nameof(fitsPath));

            // 1) Versuche: Typ implementiert IImageData und hat ctor(string path)
            var img = TryCreateViaCtor(fitsPath);
            if (img != null) return img;

            // 2) Versuche: static Load/FromFile/Open(string path) -> IImageData
            img = TryCreateViaStaticLoader(fitsPath);
            if (img != null) return img;

            throw new InvalidOperationException(
                "Konnte FITS nicht als IImageData laden. " +
                "Bitte poste die verfügbaren Typen aus dem NINA.Image Namespace (oder sag mir, ob du eine Klasse wie FitsImageData siehst).");
        }

        private static IImageData? TryCreateViaCtor(string path) {
            foreach (var t in GetAllTypesSafe()) {
                try {
                    if (!typeof(IImageData).IsAssignableFrom(t)) continue;
                    var ctor = t.GetConstructor(new[] { typeof(string) });
                    if (ctor == null) continue;

                    var obj = ctor.Invoke(new object[] { path });
                    if (obj is IImageData img) return img;
                } catch { /* ignore */ }
            }
            return null;
        }

        private static IImageData? TryCreateViaStaticLoader(string path) {
            string[] methodNames = { "Load", "LoadFromFile", "FromFile", "Open", "Read" };

            foreach (var t in GetAllTypesSafe()) {
                try {
                    // Kandidaten: Klassen, die IImageData zurückgeben oder eine Methode haben, die IImageData liefert
                    foreach (var mn in methodNames) {
                        var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                  .FirstOrDefault(m => {
                                      if (!string.Equals(m.Name, mn, StringComparison.OrdinalIgnoreCase)) return false;
                                      var ps = m.GetParameters();
                                      return ps.Length == 1 && ps[0].ParameterType == typeof(string);
                                  });

                        if (mi == null) continue;

                        var res = mi.Invoke(null, new object[] { path });
                        if (res is IImageData img) return img;
                    }
                } catch { /* ignore */ }
            }
            return null;
        }

        private static Type[] GetAllTypesSafe() {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => {
                    try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                })
                .ToArray();
        }
    }
}


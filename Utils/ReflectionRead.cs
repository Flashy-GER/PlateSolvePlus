using System;
using System.Linq;
using System.Reflection;

namespace NINA.Plugins.PlateSolvePlus.Utils {
    internal static class ReflectionRead {
        public static bool TryReadNumber(object src, string[] names, out double value) {
            value = 0;
            if (src == null) return false;

            foreach (var n in names) {
                var p = src.GetType().GetProperty(
                    n,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);

                if (p == null) continue;

                object? v = null;
                try { v = p.GetValue(src); } catch { }

                if (v == null) continue;

                if (v is double d) { value = d; return true; }
                if (v is float f) { value = f; return true; }
                if (v is int i) { value = i; return true; }
                if (double.TryParse(v.ToString(), out var parsed)) { value = parsed; return true; }
            }

            return false;
        }

        public static double? ReadDouble(object obj, params string[] names) {
            if (obj == null) return null;

            foreach (var n in names) {
                var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) continue;

                object? v = null;
                try { v = p.GetValue(obj); } catch { }

                if (v == null) continue;

                if (double.TryParse(v.ToString(), out var d)) return d;
            }

            return null;
        }

        public static int? ReadInt(object obj, params string[] names) {
            if (obj == null) return null;

            foreach (var n in names) {
                var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p == null) continue;

                object? v = null;
                try { v = p.GetValue(obj); } catch { }

                if (v == null) continue;

                if (int.TryParse(v.ToString(), out var i)) return i;
            }

            return null;
        }

        public static string DumpObject(object obj) {
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
}

using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NINA.Plugins.PlateSolvePlus.Utils {

    internal static class ComHelpers {

        public static void FinalRelease(object? comObject) {
            try {
                if (comObject != null && Marshal.IsComObject(comObject)) {
                    Marshal.FinalReleaseComObject(comObject);
                }
            } catch {
                // ignore
            }
        }

        public static object? TryGet(object target, string propertyName) {
            try {
                return target.GetType().InvokeMember(
                    propertyName,
                    BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    Array.Empty<object>());
            } catch {
                return null;
            }
        }

        public static bool TrySet(object target, string propertyName, object value) {
            try {
                target.GetType().InvokeMember(
                    propertyName,
                    BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    new[] { value });
                return true;
            } catch {
                return false;
            }
        }

        public static object? TryInvoke(object target, string methodName, object[] args) {
            try {
                return target.GetType().InvokeMember(
                    methodName,
                    BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    args);
            } catch {
                return null;
            }
        }

        public static string? TryGetString(object target, params string[] propertyNames) {
            foreach (var name in propertyNames) {
                var o = TryGet(target, name);
                if (o == null) continue;

                var s = o.ToString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (string.Equals(s, "System.__ComObject", StringComparison.OrdinalIgnoreCase)) continue;
                return s;
            }
            return null;
        }
    }
}

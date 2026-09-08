using System;
using System.Globalization;
using System.Windows.Data;

namespace NINA.Plugins.PlateSolvePlus.Converters {
    public class DoubleConverter : IValueConverter {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value is double d) return d.ToString("G", culture);
            if (value is float f) return f.ToString("G", culture);
            if (value is decimal m) return m.ToString("G", culture);
            return value?.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value == null)
                return Binding.DoNothing;

            var text = value.ToString()?.Trim();
            if (string.IsNullOrEmpty(text))
                return Binding.DoNothing;

            if (IsIntermediateNumber(text))
                return Binding.DoNothing;

            if (double.TryParse(text, NumberStyles.Float, culture, out var result) ||
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
                double.TryParse(NormalizeDecimalSeparator(text), NumberStyles.Float, CultureInfo.InvariantCulture, out result)) {
                return result;
            }

            return Binding.DoNothing;
        }

        private static bool IsIntermediateNumber(string text) =>
            text == "+" ||
            text == "-" ||
            text.EndsWith(",", StringComparison.Ordinal) ||
            text.EndsWith(".", StringComparison.Ordinal);

        private static string NormalizeDecimalSeparator(string text) =>
            text.Replace(',', '.');
    }
}

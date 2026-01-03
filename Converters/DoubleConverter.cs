using System;
using System.Globalization;
using System.Windows.Data;

namespace NINA.Plugins.PlateSolvePlus.Converters {
    public class DoubleConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            return value?.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value == null)
                return Binding.DoNothing;

            var text = value.ToString()
                            .Replace(',', '.');

            if (double.TryParse(
                    text,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var result)) {
                return result;
            }

            return Binding.DoNothing;
        }
    }
}

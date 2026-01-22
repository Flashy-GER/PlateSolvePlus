using System;
using System.Globalization;
using System.Windows.Data;

namespace NINA.Plugins.PlateSolvePlus.Converters {

    public class DoubleDegreesOrDashConverter : IValueConverter {

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value == null) return "–";

            if (value is double d) {
                if (!double.IsFinite(d)) return "–";
                if (Math.Abs(d) < 1e-9) return "–";
                return d.ToString("F6", CultureInfo.InvariantCulture);
            }

            return "–";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            // read-only UI → no back conversion
            return Binding.DoNothing;
        }
    }
}

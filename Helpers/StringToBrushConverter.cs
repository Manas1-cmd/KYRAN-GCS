using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SimpleDroneGCS.Helpers
{
    public class StringToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is string hex && !string.IsNullOrEmpty(hex))
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch { }
            return new SolidColorBrush(Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
using System;
using System.Globalization;
using System.Windows.Data;

namespace PCConsoleMode
{
    /// <summary>
    /// Returns true when the bound string is NOT a valid non-negative integer.
    /// Used by the NumericTextBox style in App.xaml to highlight invalid input.
    /// </summary>
    public class IsNotIntConverter : IValueConverter
    {
        public static readonly IsNotIntConverter Instance = new IsNotIntConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && int.TryParse(s, out var n) && n >= 0)
                return false;
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}

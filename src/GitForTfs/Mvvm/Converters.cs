using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GitForTfs.Mvvm
{
    /// <summary>Maps <c>true</c> to <see cref="Visibility.Collapsed"/> and <c>false</c> to Visible.</summary>
    public sealed class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var flag = value is bool b && b;
            return flag ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility v && v != Visibility.Visible;
        }
    }
}

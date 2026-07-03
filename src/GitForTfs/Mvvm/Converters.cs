using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

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

    /// <summary><c>true</c> → Bold (e.g. the current branch), otherwise Normal.</summary>
    public sealed class BoolToFontWeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? FontWeights.Bold : FontWeights.Normal;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>An empty/whitespace string is Visible (used to show a watermark); otherwise Collapsed.</summary>
    public sealed class EmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Colours a git status letter the way source-control tooling conventionally does:
    /// green = add/untracked, amber = modified, red = deleted, blue = rename/copy.
    /// </summary>
    public sealed class GitStatusToBrushConverter : IValueConverter
    {
        private static readonly Brush Add      = Frozen(0x4C, 0xAF, 0x50);
        private static readonly Brush Modified = Frozen(0xE2, 0xA0, 0x3F);
        private static readonly Brush Deleted  = Frozen(0xE0, 0x52, 0x52);
        private static readonly Brush Renamed  = Frozen(0x3B, 0x9C, 0xE0);
        private static readonly Brush Conflict = Frozen(0xE0, 0x66, 0x3B);
        private static readonly Brush Neutral  = Frozen(0x9A, 0xA0, 0xA6);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var code = (value as string ?? string.Empty).Trim();
            if (code.Length == 0)
                return Neutral;

            switch (char.ToUpperInvariant(code[0]))
            {
                case 'A':
                case '?': return Add;
                case 'M': return Modified;
                case 'D': return Deleted;
                case 'R':
                case 'C': return Renamed;
                case 'U': return Conflict;
                default:  return Neutral;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}

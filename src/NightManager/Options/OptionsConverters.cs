using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NightManager.Plugin.Options {
    /// <summary>Drops a leading label ("This rig: ") when a tile already shows it as a caption. Display only.</summary>
    public sealed class StripPrefixConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            var text = value as string ?? "";
            foreach (var prefix in (parameter as string ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)) {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    text = text[prefix.Length..].TrimStart(' ', ':');
                    break;
                }
            }
            return text.Length > 0 ? char.ToUpper(text[0], culture) + text[1..] : text;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Collapses a line with no text, so empty messages don't leave gaps.</summary>
    public sealed class EmptyToCollapsedConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Navigation: true when the current page is the parameter; checking a nav item opens its page.</summary>
    public sealed class PageIsConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(value as string, parameter as string, StringComparison.Ordinal);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? parameter : Binding.DoNothing;
    }

    /// <summary>Shows a page only while it's the current one.</summary>
    public sealed class PageVisibilityConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(value as string, parameter as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

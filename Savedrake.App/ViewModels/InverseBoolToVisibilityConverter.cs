using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Savedrake.App.ViewModels
{
    // True -> Collapsed, False -> Visible. The mirror of the framework BooleanToVisibilityConverter: it hides the
    // normal Folders card while the first-run welcome panel (which uses BooleanToVisibilityConverter on the same
    // NeedsSetup flag) is showing, so exactly one of the two ever occupies the card slot.
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

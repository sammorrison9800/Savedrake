using System;
using System.Globalization;
using System.Windows.Data;

namespace Savedrake.App.ViewModels
{
    // Non-null -> true. Used to disable the Characters-tab "Switch to" button when no row is selected, so an empty
    // click is not a silent no-op (SwitchCharacter early-returns on a null/invalid name).
    public sealed class NotNullToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value != null;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

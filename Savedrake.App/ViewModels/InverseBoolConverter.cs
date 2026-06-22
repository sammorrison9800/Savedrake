using System;
using System.Globalization;
using System.Windows.Data;

namespace Savedrake.App.ViewModels
{
    // Bool -> inverted bool, two-way. Used by the second "Backup name format" radio so both radios are driven by the
    // single settable property UseRandomName (UseTimestampName is read-only/computed, so it cannot be a TwoWay target).
    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : (object)false;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : (object)false;
    }
}

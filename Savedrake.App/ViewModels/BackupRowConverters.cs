using System;
using System.Globalization;
using System.Windows.Data;

namespace Savedrake.App.ViewModels
{
    // Small display-only converters for the Backups list row template. They turn a BackupRow's pin/status state into
    // the brush key, glyph, and pill text the dark theme uses, so the DataTemplate stays declarative. Brushes are
    // resolved by key against the merged theme dictionary at use time.

    // IsPinned -> a theme brush KEY (string). The row binds this through StaticResource indirection in XAML, so we
    // return the key and let a second converter resolve it. Simpler: resolve the brush here directly.
    public sealed class PinnedToBrushConverter : IValueConverter
    {
        // Brush keys for the "on" (pinned / true) and "off" states. Set in XAML.
        public string OnKey { get; set; }
        public string OffKey { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool on = value is bool b && b;
            string key = on ? OnKey : OffKey;
            object res = System.Windows.Application.Current?.TryFindResource(key);
            return res ?? Binding.DoNothing;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    // (IsPinned, Status) -> the pill TEXT: "pinned" when pinned, otherwise the lower-cased status ("protected",
    // "legacy", "validated", "corrupt", "missing"). A MultiBinding so the pill refreshes when "Validate all" upgrades
    // an observable Status in place.
    public sealed class PinStatusToPillTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool pinned = values.Length > 0 && values[0] is bool b && b;
            if (pinned) return "pinned";
            string status = values.Length > 1 ? values[1] as string : null;
            return (status ?? "").ToLowerInvariant();
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => null;
    }

    // (IsPinned, Status) -> the pill FOREGROUND brush: gold when pinned; green for a healthy backup
    // (Protected / Validated); red for a broken one (Corrupt / Missing); muted for Legacy or anything else.
    public sealed class PinStatusToPillBrushConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool pinned = values.Length > 0 && values[0] is bool b && b;
            string status = values.Length > 1 ? values[1] as string : null;

            string key;
            if (pinned) key = "AccentGoldBrush";
            else if (Eq(status, "Protected") || Eq(status, "Validated")) key = "SuccessBrush";
            else if (Eq(status, "Corrupt") || Eq(status, "Missing")) key = "DangerBrush";
            else key = "TextSecondaryBrush";

            object res = System.Windows.Application.Current?.TryFindResource(key);
            return res ?? Binding.DoNothing;
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => null;
    }
}

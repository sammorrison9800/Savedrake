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

    // A BackupRow -> the pill TEXT: "pinned" when pinned, otherwise the lower-cased status ("protected" / "legacy").
    public sealed class RowToPillTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var row = value as BackupRow;
            if (row == null) return "";
            if (row.IsPinned) return "pinned";
            return (row.Status ?? "").ToLowerInvariant();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    // A BackupRow -> the pill FOREGROUND brush: gold when pinned, green ("Success") when Protected, muted
    // (TextSecondary) for Legacy / anything else.
    public sealed class RowToPillBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var row = value as BackupRow;
            string key = "TextSecondaryBrush";
            if (row != null)
            {
                if (row.IsPinned) key = "AccentGoldBrush";
                else if (string.Equals(row.Status, "Protected", StringComparison.OrdinalIgnoreCase)) key = "SuccessBrush";
                else key = "TextSecondaryBrush";
            }
            object res = System.Windows.Application.Current?.TryFindResource(key);
            return res ?? Binding.DoNothing;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}

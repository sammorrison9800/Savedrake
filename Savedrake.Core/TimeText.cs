using System;

namespace Savedrake
{
    // Human-friendly relative time for the backup list ("just now", "5 min ago", "2 hours ago", "yesterday",
    // "3 days ago", else an absolute date). DISPLAY ONLY. Moved verbatim from Main.cs (FriendlyTime) into
    // Savedrake.Core during the WPF migration; the app keeps a thin forwarder.
    public static class TimeText
    {
        public static string Friendly(DateTime when)
        {
            TimeSpan ago = DateTime.Now - when;
            if (ago < TimeSpan.Zero) return when.ToString("MMM d, h:mm tt");
            if (ago.TotalSeconds < 45) return "just now";
            if (ago.TotalMinutes < 60) return (int)Math.Round(ago.TotalMinutes) + " min ago";
            if (ago.TotalHours < 24) { int h = (int)Math.Round(ago.TotalHours); return h + (h == 1 ? " hour ago" : " hours ago"); }
            if (ago.TotalDays < 2) return "yesterday";
            if (ago.TotalDays < 8) return (int)ago.TotalDays + " days ago";
            return when.ToString("MMM d, h:mm tt");
        }
    }
}

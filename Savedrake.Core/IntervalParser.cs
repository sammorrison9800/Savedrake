using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Savedrake
{
    // Locale-tolerant autobackup-interval parsing, the single source of truth for the interval text the user types or
    // the presets list shows. Moved verbatim from Main.cs (TryParseInterval / CanonicalizeInterval / the regex) into
    // Savedrake.Core during the WPF migration; the app keeps thin forwarders so call sites are unchanged.
    //
    //  - accepts the canonical forms plus common case/spacing/abbreviation variants ("5min", "5 Minutes", "1 Hr"),
    //  - parses the number with InvariantCulture over ASCII [0-9] only, so a non-US Windows locale can't make int
    //    parsing throw or misread,
    //  - returns false (rather than throwing) for anything unrecognized or out of TimeSpan range.
    public static class IntervalParser
    {
        public static readonly Regex Regex = new Regex(
            @"^\s*(?<num>[0-9]+)\s*(?<unit>minutes|minute|mins|min|hours|hour|hrs|hr)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsInterval(string text)
        {
            return Regex.IsMatch(text ?? string.Empty);
        }

        public static bool TryParse(string input, out TimeSpan interval)
        {
            interval = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            Match m = Regex.Match(input);
            if (!m.Success)
                return false;

            // NumberStyles.None: the regex already isolated bare ASCII digits, so disallow sign/whitespace.
            if (!int.TryParse(m.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                return false; // too many digits to fit an int, etc.

            bool isHours = m.Groups["unit"].Value.ToLowerInvariant()[0] == 'h';
            try
            {
                interval = isHours ? TimeSpan.FromHours(value) : TimeSpan.FromMinutes(value);
            }
            catch (OverflowException)
            {
                interval = TimeSpan.Zero;
                return false; // absurdly large value that overflows TimeSpan
            }
            return true;
        }

        // Canonical spelling for a recognized interval, PRESERVING the user's unit (no minutes<->hours conversion):
        // "5min"/"5 Minutes"/"5  minutes" -> "5 minutes", "1 Hr" -> "1 hour", "2 hr" -> "2 hours". Returns the input
        // unchanged if it isn't an interval.
        public static string Canonicalize(string input)
        {
            Match m = Regex.Match(input ?? string.Empty);
            if (!m.Success)
                return input;
            if (!int.TryParse(m.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                return input;

            bool isHours = m.Groups["unit"].Value.ToLowerInvariant()[0] == 'h';
            if (isHours)
                return value == 1 ? "1 hour" : value + " hours";
            return value == 1 ? "1 minute" : value + " minutes";
        }
    }
}

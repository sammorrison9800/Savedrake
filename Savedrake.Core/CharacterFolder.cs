using System;
using System.IO;

namespace Savedrake
{
    // A "character" is a named subfolder of the user's Backups location that holds that character's own backup history.
    // DD2 has a single save slot, so this lets a player keep multiple playthroughs side by side. This helper validates a
    // character name as a safe single-segment folder name and clamps an untrusted/settings value to a usable one. No I/O.
    public static class CharacterFolder
    {
        public const string Default = "Default";

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        // A valid character name is a safe single-segment Windows folder name: non-empty, no path separators or invalid
        // filename characters, not "." / "..", no leading/trailing whitespace or trailing dot, not a reserved device
        // name, and <= 40 chars (so the per-character "(Pre-Restore)....zip.savedrake.tmp" path stays clear of MAX_PATH
        // on systems without long-path support).
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;   // legacy null/empty path must never NRE
            if (name != name.Trim()) return false;               // no leading/trailing whitespace
            if (name.Length > 40) return false;
            if (name == "." || name == "..") return false;
            if (name.EndsWith(".")) return false;                // Windows silently strips a trailing dot
            if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0 || name.IndexOf(':') >= 0) return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

            // Reserved device names are reserved with or without an extension (CON, CON.zip, ...).
            int dot = name.IndexOf('.');
            string baseName = dot >= 0 ? name.Substring(0, dot) : name;
            foreach (string r in ReservedNames)
                if (string.Equals(name, r, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(baseName, r, StringComparison.OrdinalIgnoreCase))
                    return false;

            return true;
        }

        // The guard used wherever a character name becomes a path segment: a valid name (trimmed), or "Default"
        // otherwise, so a corrupted settings value can never produce an unsafe or empty path segment.
        public static string SafeName(string name)
            => IsValidName(name) ? name.Trim() : Default;
    }
}

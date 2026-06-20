using System;
using System.IO;

namespace Savedrake
{
    // Lightweight, dependency-free rolling logger (P2). Writes one file per day to %APPDATA%\Savedrake\Logs, keeps the
    // most recent ~14, and NEVER throws into the app. Logs events, not save contents; personal data (the user profile
    // path and the Steam account id in a save path) is redacted.
    //
    // Moved verbatim from the WinForms app's Program.cs into Savedrake.Core during the WPF migration (Phase 1). It is
    // `public` (was `internal`) so both the app and the test harness can reach it from the Core assembly.
    public static class Log
    {
        private static readonly object _gate = new object();
        private static string _file;
        private static string _userProfile;

        public static string Directory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Savedrake", "Logs");
        }

        public static void Init()
        {
            try
            {
                _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dir = Directory();
                System.IO.Directory.CreateDirectory(dir);
                _file = Path.Combine(dir, "savedrake-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                Prune(dir, 14);
            }
            catch { /* logging setup must never break startup */ }
        }

        public static void Info(string message) { Write("INFO", message, null); }
        public static void Warn(string message) { Write("WARN", message, null); }
        public static void Error(string message, Exception ex) { Write("ERROR", message, ex); }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                if (_file == null) Init();
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + Redact(message);
                if (ex != null) line += " | " + Redact(ex.ToString());
                lock (_gate) File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch { /* logging must never throw into the app */ }
        }

        // Strip personal data: the Windows user profile path and the Steam account id in a save path.
        public static string Redact(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (!string.IsNullOrEmpty(_userProfile))
                s = s.Replace(_userProfile, "%USERPROFILE%");
            return System.Text.RegularExpressions.Regex.Replace(
                s, @"(userdata[\\/])\d+", "$1<redacted>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static void Prune(string dir, int keep)
        {
            try
            {
                string[] files = System.IO.Directory.GetFiles(dir, "savedrake-*.log");
                if (files.Length <= keep) return;
                Array.Sort(files); // yyyyMMdd names sort chronologically
                for (int i = 0; i < files.Length - keep; i++)
                    try { File.Delete(files[i]); } catch { }
            }
            catch { }
        }
    }
}

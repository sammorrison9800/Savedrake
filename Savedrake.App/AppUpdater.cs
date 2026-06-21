using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Savedrake.App
{
    // The app side of the update flow (the version compare itself is Savedrake.UpdateCheck in Core). Writes the
    // version-handshake file the external Savedrake-Updater.exe reads, runs a silent check at startup, a feedback-y
    // check from Help > Check for Updates, and launches the updater when a newer release exists. Ported from the
    // WinForms ExecuteUpdateProcess / CreateVersionText, same owner/repo and same user-facing messages.
    internal static class AppUpdater
    {
        private const string Owner = "sammorrison9800";
        private const string Repo = "Savedrake";

        private static string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Savedrake");
        private static string VersionFilePath => Path.Combine(AppDataDir, "version.txt");
        private static string UpdaterXmlPath => Path.Combine(AppDataDir, "savedrake-updater.xml");

        public static string CurrentVersion =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0";

        // Best-effort write of %APPDATA%\Savedrake\version.txt so the external updater knows the installed version.
        public static void WriteVersionFile()
        {
            try
            {
                if (UpdateCheck.TryParseVersion(CurrentVersion, out Version v))
                {
                    Directory.CreateDirectory(AppDataDir);
                    File.WriteAllText(VersionFilePath, v.ToString());
                }
            }
            catch { /* read-only working dir etc. — non-fatal */ }
        }

        // The user opted out of the startup auto-check (savedrake-updater.xml <CheckBox1>true</CheckBox1>).
        private static bool SkipStartupCheck()
        {
            try
            {
                if (!File.Exists(UpdaterXmlPath)) return false;
                var root = System.Xml.Linq.XElement.Load(UpdaterXmlPath);
                var cb = root.Element("CheckBox1");
                return cb != null && bool.TryParse(cb.Value, out bool b) && b;
            }
            catch { return false; }
        }

        // Startup check: silent unless an update exists (then launch the updater). Never nags on up-to-date / offline.
        public static async Task RunStartupCheckAsync()
        {
            if (SkipStartupCheck()) return;
            try
            {
                UpdateCheck.Result r = await UpdateCheck.CheckAsync(CurrentVersion, Owner, Repo);
                if (r.UpdateAvailable) LaunchUpdater();
            }
            catch { /* never let a background update check surface an error */ }
        }

        // Manual "Check for Updates": always give feedback (up to date / couldn't check / launching the updater).
        public static async Task RunManualCheckAsync()
        {
            UpdateCheck.Result r;
            try { r = await UpdateCheck.CheckAsync(CurrentVersion, Owner, Repo); }
            catch { r = new UpdateCheck.Result { ApiError = true }; }

            if (r.UpdateAvailable)
                LaunchUpdater();
            else if (r.ApiError)
                MessageBox.Show("Could not check for updates. Please check your internet connection and try again.",
                    "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show("Your Savedrake is up to date.", "No Updates Available",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Launch the external updater, resolved next to this exe (not the working directory). Mirrors the WinForms
        // message when it can't be found.
        private static void LaunchUpdater()
        {
            try { System.Diagnostics.Process.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Savedrake-Updater.exe")); }
            catch
            {
                MessageBox.Show("A new update is available, but Savedrake-Updater.exe could not be started. Make sure the " +
                    "file is present in the Savedrake folder.", "Savedrake-Updater.exe Missing",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}

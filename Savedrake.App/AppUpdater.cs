using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace Savedrake.App
{
    // The app side of the update flow (the version compare itself is Savedrake.UpdateCheck in Core). Checks GitHub for a
    // newer release at startup (silent) and from Help > Check for Updates (with feedback). When a newer release exists it
    // points the user at the Nexus Mods downloads page so they can update by hand. Savedrake no longer ships a
    // self-updater: the app only tells you an update is out and opens the page where you can download it.
    internal static class AppUpdater
    {
        private const string Owner = "sammorrison9800";
        private const string Repo = "Savedrake";
        private const string NexusDownloadsUrl = "https://www.nexusmods.com/dragonsdogma2/mods/772?tab=files";

        public static string CurrentVersion =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0";

        // Startup check: silent unless a newer release exists. Never nags on up-to-date / offline.
        public static async Task RunStartupCheckAsync()
        {
            try
            {
                UpdateCheck.Result r = await UpdateCheck.CheckAsync(CurrentVersion, Owner, Repo);
                if (r.UpdateAvailable) PromptToUpdate(r);
            }
            catch { /* never let a background update check surface an error */ }
        }

        // Manual "Check for Updates": always give feedback (newer available / up to date / couldn't check).
        public static async Task RunManualCheckAsync()
        {
            UpdateCheck.Result r;
            try { r = await UpdateCheck.CheckAsync(CurrentVersion, Owner, Repo); }
            catch { r = new UpdateCheck.Result { ApiError = true }; }

            if (r.UpdateAvailable)
                PromptToUpdate(r);
            else if (r.ApiError)
                MessageBox.Show("Could not check for updates. Please check your internet connection and try again.",
                    "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show("Your Savedrake is up to date.", "No Updates Available",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Tell the user a newer release exists and offer to open the Nexus Mods downloads page so they can grab it.
        private static void PromptToUpdate(UpdateCheck.Result r)
        {
            string latest = string.IsNullOrWhiteSpace(r.LatestTag) ? "A newer version" : r.LatestTag;
            MessageBoxResult choice = MessageBox.Show(
                latest + " of Savedrake is available (you have " + CurrentVersion + ").\n\n" +
                "Open the Nexus Mods downloads page to update?",
                "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes) OpenNexusDownloads();
        }

        private static void OpenNexusDownloads()
        {
            try { Process.Start(NexusDownloadsUrl); }
            catch
            {
                MessageBox.Show("Could not open your browser. Update Savedrake from:\n\n" + NexusDownloadsUrl,
                    "Update Savedrake", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}

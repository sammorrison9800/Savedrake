using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Savedrake.App
{
    // The app side of the update flow (the version compare itself is Savedrake.UpdateCheck in Core).
    //
    // Two distribution channels, chosen at COMPILE time (see the Channel property in Savedrake.App.csproj):
    //
    //  * DEFAULT = the Nexus-hosted build. It makes NO automatic network call. "Check for Updates" (user-initiated only)
    //    reads the latest GitHub release version and, if newer, offers to open the Nexus Mods downloads page. The app
    //    never downloads or installs anything itself. This keeps the Nexus build within Nexus's file rules, which forbid
    //    executables that pull files from external sources.
    //
    //  * GITHUB_BUILD = the build published on GitHub. It adds an opt-in one-click updater: a silent check at startup and
    //    from "Check for Updates", and on the user's confirmation it downloads update.zip from the matching GitHub
    //    release, verifies it is a real Savedrake package, swaps the files in place, and relaunches. Integrity-only
    //    (HTTPS + valid-zip-containing-Savedrake.exe), NOT signed - this build is never uploaded to Nexus.
    internal static class AppUpdater
    {
        private const string Owner = "sammorrison9800";
        private const string Repo = "Savedrake";
        private const string NexusDownloadsUrl = "https://www.nexusmods.com/dragonsdogma2/mods/772?tab=files";

        public static string CurrentVersion =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0";

        // Startup check. GitHub build: silent, then offer the one-click update. Nexus build: a no-op, so the Nexus build
        // makes no network call on its own (the version check there is user-initiated only).
        public static async Task RunStartupCheckAsync()
        {
#if GITHUB_BUILD
            try
            {
                UpdateCheck.Result r = await UpdateCheck.CheckAsync(CurrentVersion, Owner, Repo);
                if (r.UpdateAvailable) await PromptAndApplyAsync(r);
            }
            catch { /* never let a background update check surface an error */ }
#else
            await Task.CompletedTask;
#endif
        }

        // Manual "Check for Updates": always give feedback (newer available / up to date / couldn't check).
        public static async Task RunManualCheckAsync()
        {
            UpdateCheck.Result r;
            try { r = await UpdateCheck.CheckAsync(CurrentVersion, Owner, Repo); }
            catch { r = new UpdateCheck.Result { ApiError = true }; }

            if (r.UpdateAvailable)
            {
#if GITHUB_BUILD
                await PromptAndApplyAsync(r);
#else
                PromptOpenNexus(r);
#endif
            }
            else if (r.ApiError)
                MessageBox.Show("Could not check for updates. Please check your internet connection and try again.",
                    "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show("Your Savedrake is up to date.", "No Updates Available",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }

#if GITHUB_BUILD
        // Offer the one-click update, then download + install it on the user's confirmation.
        private static async Task PromptAndApplyAsync(UpdateCheck.Result r)
        {
            string latest = string.IsNullOrWhiteSpace(r.LatestTag) ? "A newer version" : r.LatestTag;
            if (MessageBox.Show(
                    latest + " of Savedrake is available (you have " + CurrentVersion + ").\n\n" +
                    "Download and install it now? Savedrake will restart when it's done.",
                    "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;
            try { await ApplyUpdateAsync(r.LatestTag); }
            catch (Exception ex)
            {
                MessageBox.Show("The update could not be installed: " + ex.Message +
                    "\n\nYou can download it manually from:\n" + NexusDownloadsUrl,
                    "Update Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Download update.zip from the matching GitHub release into a temp folder, verify it is a real Savedrake package
        // (a readable zip containing Savedrake.exe - integrity, mirroring the old updater), extract it, then hand off to a
        // tiny script that waits for THIS process to exit, copies the new files over the install folder, and relaunches.
        // The app shuts down so its own files are free to be replaced. All the blocking IO runs off the UI thread.
        private static async Task ApplyUpdateAsync(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) throw new InvalidOperationException("no release tag was found.");

            string work = Path.Combine(Path.GetTempPath(), "savedrake_update");
            string zipPath = Path.Combine(work, "update.zip");
            string newDir = Path.Combine(work, "new");
            string cmdPath = Path.Combine(work, "apply.cmd");
            string url = "https://github.com/" + Owner + "/" + Repo + "/releases/download/" +
                         Uri.EscapeDataString(tag) + "/update.zip";
            string installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            int pid = Process.GetCurrentProcess().Id;

            await Task.Run(() =>
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* leftover from a prior run */ }
                Directory.CreateDirectory(newDir);

                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using (var wc = new System.Net.WebClient())
                    wc.DownloadFile(url, zipPath);

                using (var zip = Ionic.Zip.ZipFile.Read(zipPath))
                {
                    bool hasExe = false;
                    foreach (var e in zip.Entries)
                    {
                        string leaf = Path.GetFileName(e.FileName.Replace('/', '\\'));
                        if (string.Equals(leaf, "Savedrake.exe", StringComparison.OrdinalIgnoreCase)) { hasExe = true; break; }
                    }
                    if (!hasExe) throw new InvalidOperationException("the download was not a valid Savedrake package.");
                    zip.ExtractAll(newDir, Ionic.Zip.ExtractExistingFileAction.OverwriteSilently);
                }

                // Wait for this PID to exit, copy the extracted files in (robocopy handles spaced paths cleanly), relaunch.
                string script =
                    "@echo off\r\n" +
                    "setlocal\r\n" +
                    ":wait\r\n" +
                    "tasklist /FI \"PID eq " + pid + "\" /NH 2>nul | find /I \"Savedrake.exe\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                    "robocopy \"" + newDir + "\" \"" + installDir + "\" /e >nul\r\n" +
                    "start \"\" \"" + exePath + "\"\r\n";
                File.WriteAllText(cmdPath, script);
            });

            Process.Start(new ProcessStartInfo
            {
                FileName = cmdPath,
                WorkingDirectory = work,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            });

            Application.Current.Shutdown();
        }
#else
        // Nexus build: offer to open the Nexus downloads page. The app downloads and installs nothing itself.
        private static void PromptOpenNexus(UpdateCheck.Result r)
        {
            string latest = string.IsNullOrWhiteSpace(r.LatestTag) ? "A newer version" : r.LatestTag;
            if (MessageBox.Show(
                    latest + " of Savedrake is available (you have " + CurrentVersion + ").\n\n" +
                    "Open the Nexus Mods downloads page to update?",
                    "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                try { Process.Start(NexusDownloadsUrl); } catch { /* no browser; nothing else to do */ }
            }
        }
#endif
    }
}

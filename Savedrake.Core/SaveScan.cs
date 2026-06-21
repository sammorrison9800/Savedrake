using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Savedrake
{
    // UI-agnostic save-folder scanning + backup-location advice. Moved verbatim from the WinForms app's Main.cs into
    // Savedrake.Core during the WPF migration (Phase 0); the app keeps thin forwarders so call sites are unchanged.
    // All methods are pure System.IO/string/Linq (no DotNetZip, no crypto), so Savedrake.Core stays dependency-free.
    public static class SaveScan
    {
        // Dragon's Dogma 2's Steam app id; part of the userdata\<id>\2054970\remote\win64_save layout.
        private const string Dd2AppId = "2054970";

        // Existing DD2 save folders under a Steam root: <root>\userdata\<id>\2054970\remote\win64_save. Most-recently-used
        // first. Static + parameterised so the headless harness can test it against a crafted tree.
        public static List<string> FindDd2SaveFoldersUnder(string steamRoot)
        {
            var found = new List<string>();
            try
            {
                string userdata = Path.Combine(steamRoot, "userdata");
                if (!Directory.Exists(userdata)) return found;
                foreach (string profile in Directory.GetDirectories(userdata))
                {
                    string save = Path.Combine(profile, Dd2AppId, "remote", "win64_save");
                    if (Directory.Exists(save)) found.Add(save);
                }
            }
            catch { }
            found.Sort((a, b) => DirLastWriteUtc(b).CompareTo(DirLastWriteUtc(a)));
            return found;
        }

        private static DateTime DirLastWriteUtc(string d) { try { return Directory.GetLastWriteTimeUtc(d); } catch { return DateTime.MinValue; } }

        // The Steam install root: HKCU\Software\Valve\Steam\SteamPath, falling back to %ProgramFiles(x86)%\Steam.
        // Returns null if neither exists. Never throws. Moved from the WinForms app during the WPF migration so the
        // "detect save folder" feature works the same in both shells.
        public static string GetSteamRoot()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string p = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p)) { p = p.Replace('/', '\\'); if (Directory.Exists(p)) return p; }
                }
            }
            catch { }
            try
            {
                string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
                if (Directory.Exists(def)) return def;
            }
            catch { }
            return null;
        }

        // All existing DD2 save folders on this machine (most-recently-used first), discovered under the detected
        // Steam root. Empty list when Steam / the saves aren't found. Never throws.
        public static List<string> FindDd2SaveFolders()
        {
            string root = GetSteamRoot();
            return root != null ? FindDd2SaveFoldersUnder(root) : new List<string>();
        }

        // QoL: a one-line caution about a chosen backup folder, or null if it's fine. Advisory, not blocking. Flags a
        // backup folder inside the save folder, in a cloud-synced folder (OneDrive/Dropbox/Google Drive — sync churn),
        // or on the same drive as the saves (one disk failure loses both). Pure/static so the harness can test it.
        public static string BackupLocationWarning(string saveDir, string backupDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(saveDir) || string.IsNullOrWhiteSpace(backupDir)) return null;
                string save = Path.GetFullPath(saveDir).TrimEnd('\\', '/');
                string backup = Path.GetFullPath(backupDir).TrimEnd('\\', '/');

                if (string.Equals(backup, save, StringComparison.OrdinalIgnoreCase) ||
                    backup.StartsWith(save + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return "Your backup folder is inside your save folder. Pick a separate folder so backups don't pile up inside your saves.";

                string lower = "\\" + backup.ToLowerInvariant() + "\\";
                if (lower.Contains("\\onedrive") || lower.Contains("\\dropbox") || lower.Contains("\\google drive") || lower.Contains("\\googledrive"))
                    return "Your backup folder is in a cloud-synced folder (OneDrive, Dropbox, etc.). That works, but cloud sync can be slow or conflict during a backup. A plain local folder is more reliable.";

                string sroot = Path.GetPathRoot(save), broot = Path.GetPathRoot(backup);
                if (!string.IsNullOrEmpty(sroot) && string.Equals(sroot, broot, StringComparison.OrdinalIgnoreCase))
                    return "Your backups are on the same drive as your saves. If that drive fails you would lose both. A different drive (or an extra copy elsewhere) is safer.";

                return null;
            }
            catch { return null; }
        }

        public static string FindLatestPreRestoreCheckpoint(string backupDir)
        {
            try
            {
                return Directory.GetFiles(backupDir, "*.zip")
                    .Where(f => Path.GetFileName(f).StartsWith("(Pre-Restore)", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        // True if the live save folder currently holds real DD2 save data (data*.bin / system.bin), searched
        // recursively. Fail-OPEN: an unreadable folder returns false (never blocks a load; the (Pre-Load) snapshot
        // path applies its own fail-CLOSED skip). Never throws.
        public static bool LiveFolderHasRealSave(string liveDir)
        {
            if (string.IsNullOrWhiteSpace(liveDir)) return false;
            try
            {
                return Directory.EnumerateFiles(liveDir, "*", SearchOption.AllDirectories)
                    .Any(p => IsRealSaveEntry(Path.GetFileName(p)));
            }
            catch { return false; }
        }

        // The newest REAL backup zip in a character's folder, by CreationTime (matching RefreshBackups' "newest
        // first"), EXCLUDING (Pre-Restore) and (Pre-Load) snapshots so a load never targets its own safety checkpoint
        // (a checkpoint's CreationTime is "now", so it would otherwise win). Null when the folder is missing/empty or
        // holds only checkpoints. Never throws.
        public static string FindLatestRealBackup(string backupDir)
        {
            try
            {
                return Directory.GetFiles(backupDir, "*.zip")
                    .Where(f =>
                    {
                        string n = Path.GetFileName(f);
                        return !n.StartsWith("(Pre-Restore)", StringComparison.OrdinalIgnoreCase)
                            && !n.StartsWith("(Pre-Load)", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(f => File.GetCreationTimeUtc(f))
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        public static bool IsRealSaveEntry(string entryFileName)
        {
            string leaf = Path.GetFileName(entryFileName.Replace('/', Path.DirectorySeparatorChar));
            return System.Text.RegularExpressions.Regex.IsMatch(
                       leaf, "^data.*\\.bin$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                || string.Equals(leaf, "system.bin", StringComparison.OrdinalIgnoreCase);
        }
    }
}

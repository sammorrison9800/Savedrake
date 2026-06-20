using System;
using System.Collections.Generic;
using System.IO;

namespace Savedrake
{
    // Automatic thinning of old autobackups, lifted out of the WinForms CleanUpOldAutobackups minus its UI side
    // effects (status text + list refresh stay with the caller). Eligible = autobackups only ("(Auto)"/"auto"
    // prefix); manual backups and the "(Pre-Restore)" checkpoint are never touched, pinned backups are never
    // touched, and a corrupt archive is never auto-removed. Which ones to drop is decided by the same
    // RetentionPolicy.SelectAutobackupsToThin the shipped app uses (keep recent + a spread of older ones).
    public static class AutobackupCleanup
    {
        // Removes the surplus old autobackups under backupDir and returns how many were removed. toRecycle sends
        // them to the Recycle Bin instead of deleting outright. nowTicksUtc is passed in (not read from the clock)
        // so the thinning decision stays deterministic and testable. Never throws: per-file failures are logged and
        // skipped, and any unexpected top-level error is swallowed (cleanup is best-effort housekeeping).
        public static int Run(string backupDir, int maxKeep, bool toRecycle, long nowTicksUtc)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir)) return 0;

                var files = new List<string>();
                var ticks = new List<long>();
                foreach (string file in Directory.GetFiles(backupDir, "*.zip"))
                {
                    string name = Path.GetFileName(file);
                    if (!(name.StartsWith("(Auto)") || name.StartsWith("auto"))) continue;
                    if (Pinning.IsPinnedBackup(name)) continue; // pinned backups are never removed
                    files.Add(file);
                    ticks.Add(File.GetLastWriteTimeUtc(file).Ticks);
                }
                if (files.Count == 0) return 0;

                int keep = maxKeep > 0 ? maxKeep : 0;
                int[] toRemove = RetentionPolicy.SelectAutobackupsToThin(ticks.ToArray(), nowTicksUtc, keep);

                int removed = 0;
                foreach (int i in toRemove)
                {
                    try
                    {
                        if (Manifest.ClassifyBackupFully(files[i]) == "Corrupt") continue; // never auto-remove a corrupt backup
                        if (toRecycle)
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(files[i],
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        else
                            File.Delete(files[i]);
                        removed++;
                    }
                    catch (Exception ex) { Log.Warn("Could not remove old autobackup " + Path.GetFileName(files[i]) + ": " + ex.Message); }
                }
                return removed;
            }
            catch (Exception ex)
            {
                Log.Warn("Auto-cleanup of old autobackups failed: " + ex.Message);
                return 0;
            }
        }
    }
}

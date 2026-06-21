using System.IO;

namespace Savedrake
{
    // The persisted "how many autobackups exist" counter. It is a single integer in a text file under the app data
    // directory; the autobackup limit is enforced against it. The real source of truth is the files on disk: the
    // count is RECOMPUTED from the backup folder and written back after every backup/restore/delete/cleanup, so the
    // file is only ever a cache. Reading is deliberately forgiving (missing/garbled/locked -> 0) and writing is
    // best-effort (a write hiccup must never crash or wedge the autobackup loop). Recompute matches the shipped
    // WinForms LoadBackupHistory exactly: count = zips named "(Auto)"/"auto" and NOT pinned (pinned autobackups are
    // protected from cleanup and excluded from the limit).
    public static class AutobackupCountStore
    {
        public static int Read(string countFilePath)
        {
            int count = 0;
            try
            {
                if (!string.IsNullOrEmpty(countFilePath) && File.Exists(countFilePath))
                {
                    int.TryParse(File.ReadAllText(countFilePath), out count);
                }
            }
            catch
            {
                // Locked / unreadable count file: treat as 0. Worst case is one extra eligible attempt, which the
                // change-aware gate and the on-disk recount immediately correct.
            }
            return count;
        }

        // Best-effort persist of the count. A write failure is swallowed: the count is a cache, not a source of truth.
        public static void Write(string countFilePath, int count)
        {
            try
            {
                if (!string.IsNullOrEmpty(countFilePath))
                    File.WriteAllText(countFilePath, count.ToString());
            }
            catch { }
        }

        // The on-disk autobackup count: zips whose name starts with "(Auto)" or "auto" and are not pinned. Matches
        // WinForms LoadBackupHistory's autoBackupFiles filter byte-for-byte. Never throws (an unreadable folder -> 0).
        public static int CountAutobackups(string backupDir)
        {
            if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir)) return 0;
            int n = 0;
            try
            {
                foreach (string file in Directory.GetFiles(backupDir, "*.zip"))
                {
                    string name = Path.GetFileName(file);
                    if (!(name.StartsWith("(Auto)") || name.StartsWith("auto"))) continue;
                    if (Pinning.IsPinnedBackup(name)) continue;
                    n++;
                }
            }
            catch { }
            return n;
        }

        // Recompute the autobackup count from the backup folder and write it to the count file. Returns the count.
        // This is what keeps the "Keep at most N" limit honest; the WPF app calls it after refreshing the backup list.
        public static int RecomputeAndWrite(string backupDir, string countFilePath)
        {
            int count = CountAutobackups(backupDir);
            Write(countFilePath, count);
            return count;
        }
    }
}

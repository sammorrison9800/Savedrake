using System;
using System.IO;
using System.Linq;

namespace Savedrake
{
    // The outcome of a one-time flat -> folder-per-character migration.
    public sealed class CharacterMigrationResult
    {
        public bool Ran;            // false = nothing to do (skipped)
        public int MovedZips;
        public int SkippedZips;     // locked or name-collision: left in place, never overwritten
        public bool MovedCountFile;
        public string DefaultDir;
    }

    // One-time, NON-DESTRUCTIVE migration from the legacy flat layout (backups loose directly under the Backups folder)
    // to the folder-per-character layout, by moving the loose backups into a "Default" character. Move-only (never
    // deletes a backup), idempotent, and resumable (an interrupted run finishes on the next launch). Never throws.
    public static class CharacterMigration
    {
        // True when the Backups folder still holds the legacy flat layout: it exists, has at least one loose top-level
        // *.zip, and has no character subfolders other than (possibly) "Default". Any other subfolder means a real
        // second character already exists -> already organized -> hands off. Allowing a pre-existing "Default" is what
        // makes an interrupted migration resumable: leftover loose zips still trigger the finish on the next launch.
        public static bool NeedsMigration(string backupDir)
        {
            if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir)) return false;
            try
            {
                bool hasLooseZip = Directory.EnumerateFiles(backupDir, "*.zip", SearchOption.TopDirectoryOnly).Any();
                if (!hasLooseZip) return false;
                string[] subs = Directory.GetDirectories(backupDir);
                if (subs.Length == 0) return true;
                return subs.Length == 1 &&
                       string.Equals(Path.GetFileName(subs[0]), CharacterFolder.Default, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Move every loose top-level *.zip into BackupDir\Default. A locked file or a name collision is skipped and left
        // in place (never overwritten), so it is retried on the next launch. Best-effort: pull a legacy global count
        // cache into Default, and reclaim any orphaned *.savedrake.tmp at the parent root. Returns Ran=false if there is
        // nothing to migrate. Never throws.
        public static CharacterMigrationResult MigrateLooseToDefault(string backupDir, string legacyCountFile = null)
        {
            var r = new CharacterMigrationResult();
            if (!NeedsMigration(backupDir)) return r;
            try
            {
                string defaultDir = Path.Combine(backupDir, CharacterFolder.Default);
                r.DefaultDir = defaultDir;
                Directory.CreateDirectory(defaultDir);

                foreach (string zip in Directory.EnumerateFiles(backupDir, "*.zip", SearchOption.TopDirectoryOnly).ToList())
                {
                    string dest = Path.Combine(defaultDir, Path.GetFileName(zip));
                    if (File.Exists(dest)) { r.SkippedZips++; continue; }     // collision: never overwrite an existing backup
                    try { File.Move(zip, dest); r.MovedZips++; }
                    catch { r.SkippedZips++; }                                // locked: leave in place, retry next launch
                }

                // Bring the legacy global count cache into Default, but only if Default has none yet.
                if (!string.IsNullOrWhiteSpace(legacyCountFile) && File.Exists(legacyCountFile))
                {
                    string destCount = Path.Combine(defaultDir, "count_of_autobackups.txt");
                    if (!File.Exists(destCount))
                    {
                        try { File.Move(legacyCountFile, destCount); r.MovedCountFile = true; } catch { }
                    }
                }

                // Reclaim any orphaned temp from a legacy hard-killed backup at the parent root (per-character sweeps
                // will never look here again).
                try
                {
                    foreach (string tmp in Directory.EnumerateFiles(backupDir, "*.savedrake.tmp", SearchOption.TopDirectoryOnly).ToList())
                        File.Delete(tmp);
                }
                catch { }

                r.Ran = true;
            }
            catch { /* never throw out of migration */ }
            return r;
        }
    }
}

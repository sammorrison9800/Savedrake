using System;
using System.IO;

namespace Savedrake
{
    // "Is the save safe to capture right now?" An autobackup must only ever zip a save the game has finished writing,
    // never one it is in the middle of writing. The save watcher already debounces a burst of writes into one backup
    // after a quiet window; this is the final gate immediately before the capture: if any save file is still held open
    // for writing without sharing (the game mid-save), defer this round and let the next quiet window / interval tick
    // (or the on-close backup) retry once the write has finished.
    //
    // Mechanism: try to open every file under the save folder for reading. We declare FileShare.ReadWrite, so the open
    // succeeds whenever a read is actually possible — which is exactly the condition the zip step needs. If the game
    // holds a file exclusively (FileShare.None, i.e. writing without sharing), the open throws and we report "not
    // settled". A file the game merely keeps open but shares is still readable, so it counts as settled (we never
    // over-defer just because a handle is open).
    //
    // Fail direction mirrors BackupService's "don't block a legitimate backup": a folder that can't be enumerated
    // (a permission quirk on an unusual path) returns settled (true) — the backup path's own verify-on-create against
    // the SHA-256 manifest is the backstop. A missing or absent folder returns false (nothing meaningful to capture
    // yet). This method never throws.
    public static class SaveReadiness
    {
        public static bool IsSaveSettled(string saveDir)
        {
            if (string.IsNullOrWhiteSpace(saveDir) || !Directory.Exists(saveDir)) return false;
            try
            {
                foreach (string path in Directory.EnumerateFiles(saveDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
                    }
                    catch (IOException) { return false; }           // locked / being written without sharing
                    catch (UnauthorizedAccessException) { }         // a permission quirk, not a write lock — keep going
                }
                return true;
            }
            catch { return true; } // can't enumerate the folder — fail open, never block a legitimate backup
        }
    }
}

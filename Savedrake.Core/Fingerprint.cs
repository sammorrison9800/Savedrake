using System;
using System.IO;
using System.Linq;

namespace Savedrake
{
    // UI-agnostic save-folder content fingerprint for change-aware autobackup (PR1). Reuses the backup manifest's
    // per-file path/length/SHA-256 and hashes only the content fields, so the autobackup timer can skip a tick when
    // nothing changed instead of writing a redundant identical backup. Moved verbatim from the WinForms app's Main.cs
    // into Savedrake.Core during the WPF migration (Phase 0); the app keeps a thin forwarder so call sites are unchanged.
    public static class Fingerprint
    {
        // Change-aware autobackup (PR1): a stable content fingerprint of the save folder, so the autobackup timer can
        // skip a tick when nothing changed instead of writing a redundant identical backup that eats into the user's
        // limit. Reuses BuildBackupManifest (the same per-file path/length/SHA-256 a backup records), then hashes only
        // the content fields, so it is immune to the manifest's volatile createdUtc/tool stamps. Returns null (never
        // throws) when the folder is missing/locked/unreadable or holds no real save data; callers treat null as
        // "not safely comparable" and SKIP (fail-closed) rather than zip a folder they cannot fully read.
        public static string ComputeSaveFingerprint(string saveDir)
        {
            try
            {
                if (string.IsNullOrEmpty(saveDir) || !Directory.Exists(saveDir)) return null;
                // Only fingerprint a folder that actually holds DD2 save data, mirroring BackupOperation's hasSaveData gate.
                bool hasSave = Directory.EnumerateFiles(saveDir, "*", SearchOption.AllDirectories)
                    .Any(p => SaveScan.IsRealSaveEntry(Path.GetFileName(p)));
                if (!hasSave) return null;
                // BuildBackupManifest opens each file for read; a file the game is actively writing is exclusively
                // locked and throws IOException, which we catch below and surface as null (skip this tick).
                return Manifest.StableManifestHash(Manifest.BuildBackupManifest(saveDir));
            }
            catch (UnauthorizedAccessException) { return null; }
            catch (System.IO.IOException) { return null; }
            catch (Exception) { return null; } // never let it throw onto the UI/timer thread
        }
    }
}

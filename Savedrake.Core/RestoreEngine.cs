using System;
using System.IO;
using System.Linq;
using System.Text;
using Ionic.Zip;

namespace Savedrake
{
    // UI-agnostic transactional-restore primitives. Moved VERBATIM (byte-for-byte logic) from the WinForms app's
    // Main.cs into Savedrake.Core during the WPF migration (Phase 0). The app keeps thin forwarders so call sites
    // are unchanged. SAFETY-CRITICAL: a bug here can delete a user's only save — do not "improve" or reorder.
    // Cross-references to already-extracted Core logic are qualified (SaveScan.*, Manifest.*, BackupNaming.*);
    // calls to other helpers in THIS class are unqualified (same type).
    public static class RestoreEngine
    {
        public static bool CreatePreRestoreCheckpoint(string liveDir, string backupDir)
        {
            // Can't safely stage a zip into the very folder being snapshotted.
            if (string.Equals(Path.GetFullPath(liveDir), Path.GetFullPath(backupDir), StringComparison.OrdinalIgnoreCase))
                return true;

            // Nothing to lose if the live folder holds no DD2 save data — skip and let the restore proceed.
            bool hasSaveData;
            try
            {
                hasSaveData = Directory
                    .EnumerateFiles(liveDir, "*", SearchOption.AllDirectories)
                    .Any(p => SaveScan.IsRealSaveEntry(Path.GetFileName(p)));
            }
            catch (UnauthorizedAccessException) { hasSaveData = true; } // can't scan -> err toward making a checkpoint
            catch (System.IO.IOException) { hasSaveData = true; }
            if (!hasSaveData) return true;

            string checkpointPath = BackupNaming.MakeUniquePath(Path.Combine(backupDir, $"(Pre-Restore) {DateTime.Now:yyMMddHHmmss}.zip"));
            string tempZip = checkpointPath + ".savedrake.tmp";
            try
            {
                // Sweep any orphaned temp files first (same as BackupOperation); our own tempZip doesn't exist yet.
                try { foreach (string stale in Directory.GetFiles(backupDir, "*.savedrake.tmp")) File.Delete(stale); } catch { }
                using (Ionic.Zip.ZipFile zip = new Ionic.Zip.ZipFile())
                {
                    zip.AddDirectory(liveDir);
                    zip.AddEntry(Manifest.ManifestEntryName, System.Text.Encoding.UTF8.GetBytes(Manifest.BuildBackupManifest(liveDir)));
                    zip.Comment = "SamMorrison9800";
                    zip.Save(tempZip);
                }
                // Verify-on-create (P1): don't trust a checkpoint we can't prove is restorable (CRC + manifest).
                if (!Manifest.VerifyZipRestorable(tempZip, out _) || !Manifest.VerifyZipAgainstManifest(tempZip, out _))
                {
                    try { File.Delete(tempZip); } catch { }
                    return false;
                }
                File.Move(tempZip, checkpointPath); // atomically publish the completed checkpoint
                return true;
            }
            catch (Exception)
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                return false;
            }
        }

        public static bool Rollback(string liveDir, string rollbackDir, bool stagingStarted)
        {
            try
            {
                // If T4 began, liveDir holds disposable STAGED files (all originals are already in rollbackDir) — clear
                // them so the originals can move back without File.Move name collisions. If T4 never began (including a
                // partial T3), liveDir still holds un-moved-aside originals, so we must NOT empty it.
                if (stagingStarted) EmptyDir(liveDir);
                // rollbackDir holds ONLY the user's originals; move every one back. Works for a fully moved-aside set and
                // for a partial T3 (the remainder still in liveDir are disjoint by name, so no collision).
                if (Directory.Exists(rollbackDir)) MoveDirContents(rollbackDir, liveDir);
                return true;
            }
            catch (Exception) { return false; } // never throw out of a catch; dialog will name rollbackDir for manual recovery
        }

        public static bool ValidateBackup(string filePath, out string reason)
        {
            reason = null;
            try
            {
                using (Ionic.Zip.ZipFile zip = Ionic.Zip.ZipFile.Read(filePath))
                {
                    if (zip.Count == 0) { reason = "The backup file contains no files."; return false; }
                    foreach (Ionic.Zip.ZipEntry entry in zip.Entries)
                    {
                        if (entry.IsDirectory) continue;
                        if (SaveScan.IsRealSaveEntry(entry.FileName)) return true; // GetFileName() inside ignores any win64_save\ prefix
                    }
                    reason = "The backup does not contain Dragon's Dogma 2 save data (no data*.bin / system.bin).";
                    return false;
                }
            }
            catch (Ionic.Zip.ZipException ze) { reason = "The backup file is not a valid zip: " + ze.Message; return false; }
            catch (Exception ex)              { reason = "The backup file could not be read: " + ex.Message; return false; }
        }

        public static void ExtractZipToStaging(string filePath, string stagingDir)
        {
            string stagingRoot = Path.GetFullPath(stagingDir);
            if (!stagingRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                stagingRoot += Path.DirectorySeparatorChar;

            using (Ionic.Zip.ZipFile zip = Ionic.Zip.ZipFile.Read(filePath))
            {
                foreach (Ionic.Zip.ZipEntry entry in zip.Entries)
                {
                    // zip-slip guard — ported from the HARDENED updater/UpdaterForm.cs:361-391 (GetFullPath both
                    // sides + trailing separator + OrdinalIgnoreCase). Runs on directory entries too (before the
                    // IsDirectory skip), because '..' can appear in a directory entry name.
                    string rel = entry.FileName.Replace('/', Path.DirectorySeparatorChar);
                    string destFull = Path.GetFullPath(Path.Combine(stagingRoot, rel));
                    if (!destFull.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                        throw new System.IO.IOException(
                            "Backup contains an unsafe path entry: '" + entry.FileName + "'. Restore aborted.");
                    if (entry.IsDirectory) continue;
                    // Skip Savedrake's own integrity manifest — it lives inside the zip for verification but must NOT
                    // be restored into the live save folder (it is metadata, not a game save file).
                    if (Manifest.IsManifestEntry(entry.FileName)) continue;
                    // Use ONLY this DotNetZip overload (same engine the old ExtractAll used). NOT OpenReader/stream-copy,
                    // NOT ExtractToFile (System.IO.Compression only). Ionic recreates parent subdirs automatically.
                    entry.Extract(stagingDir, Ionic.Zip.ExtractExistingFileAction.OverwriteSilently);
                }
            }
        }

        public static bool DetectNestedPrefix(string stagingDir)
        {
            return Directory.GetFiles(stagingDir).Length == 0
                && Directory.GetDirectories(stagingDir).Length == 1
                && string.Equals(new DirectoryInfo(Directory.GetDirectories(stagingDir)[0]).Name,
                                 "win64_save", StringComparison.OrdinalIgnoreCase);
        }

        public static void FlattenNestedLayout(string stagingDir)
        {
            // Peel every nested win64_save\ wrapper (handles single AND doubly-nested R3 layouts). Each pass renames
            // the sole win64_save husk to a unique name BEFORE lifting its contents up, so an inner win64_save can
            // move to the root without colliding with the husk it came from. Terminates: each pass strictly reduces
            // nesting depth. A no-op when staging is already a flat root layout.
            while (DetectNestedPrefix(stagingDir))
            {
                string nested = Directory.GetDirectories(stagingDir)[0];          // the sole win64_save husk
                string husk = Path.Combine(stagingDir, "._savedrake_husk_" + Guid.NewGuid().ToString("N"));
                Directory.Move(nested, husk);
                MoveDirContents(husk, stagingDir);
                Directory.Delete(husk, false);
            }
        }

        public static bool VerifyStagedDir(string stagingDir)
        {
            string[] all = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories);
            return all.Length > 0 && all.Any(p => SaveScan.IsRealSaveEntry(Path.GetFileName(p))); // System.Linq present (line 12)
        }

        public static string CreateSiblingTempDir(string liveDir, string tag)
        {
            // Parent-of-live keeps temp on the SAME volume → every File.Move/Directory.Move is a near-atomic rename.
            // For a real DD2 path (...\2054970\remote\win64_save) the parent 'remote' always exists. %TEMP% is a
            // last-resort fallback only when GetParent is null (drive-root edge).
            string root = Directory.GetParent(liveDir.TrimEnd(Path.DirectorySeparatorChar))?.FullName
                          ?? Path.GetTempPath();
            string dir = Path.Combine(root, "._" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void MoveDirContents(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string f in Directory.GetFiles(sourceDir))
                File.Move(f, Path.Combine(destDir, Path.GetFileName(f)));
            foreach (string d in Directory.GetDirectories(sourceDir))            // moves subdirs too (fixes the old
                Directory.Move(d, Path.Combine(destDir, new DirectoryInfo(d).Name)); // top-level-only MoveFilesToRecycleBin gap)
        }

        public static void EmptyDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            ClearReadOnlyRecursive(dir); // so a ReadOnly partial file can't block deletion
            foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
            foreach (string d in Directory.GetDirectories(dir)) Directory.Delete(d, true);
        }

        public static void ClearReadOnlyRecursive(string root)
        {
            DirectoryInfo dirInfo = new DirectoryInfo(root);
            if (!dirInfo.Exists) return;
            // Best-effort like the loops below: a transient lock on the root must NOT throw out of here, or a T5
            // call could roll back an already-committed restore. Stripping the root's ReadOnly bit is non-essential
            // (it doesn't block writes to files inside it) — swallow and continue.
            try { if ((dirInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                      dirInfo.Attributes &= ~FileAttributes.ReadOnly; }
            catch (UnauthorizedAccessException) { }
            catch (System.IO.IOException) { }

            // The recursive enumerations below were the foreach collection expressions, OUTSIDE the per-item
            // try/catch — and GetFiles/GetDirectories(AllDirectories) eagerly walk the whole tree, so a locked or
            // permission-denied subdir threw straight out of this "best-effort" method. At the T5 call site that
            // throw unwound into the rollback path and reverted an ALREADY-COMMITTED restore. Get the lists inside
            // try/catch (empty on failure) so enumeration can never throw out of here.
            FileInfo[] files;
            try { files = dirInfo.GetFiles("*", SearchOption.AllDirectories); }
            catch (UnauthorizedAccessException) { files = new FileInfo[0]; }
            catch (System.IO.IOException) { files = new FileInfo[0]; }
            foreach (FileInfo fi in files)
            {
                try { if ((fi.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                          fi.Attributes &= ~FileAttributes.ReadOnly; }
                catch (UnauthorizedAccessException) { }
                catch (System.IO.IOException) { }
            }

            DirectoryInfo[] dirs;
            try { dirs = dirInfo.GetDirectories("*", SearchOption.AllDirectories); }
            catch (UnauthorizedAccessException) { dirs = new DirectoryInfo[0]; }
            catch (System.IO.IOException) { dirs = new DirectoryInfo[0]; }
            foreach (DirectoryInfo di in dirs)
            {
                try { if ((di.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                          di.Attributes &= ~FileAttributes.ReadOnly; }
                catch (UnauthorizedAccessException) { }
                catch (System.IO.IOException) { }
            }
        }

        public static void TryDeleteDir(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try { ClearReadOnlyRecursive(dir); Directory.Delete(dir, true); }
            catch (Exception) { } // best-effort temp cleanup; at worst leaves a hidden ._savedrake_* husk
        }
    }
}

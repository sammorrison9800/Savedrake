using System;
using System.IO;
using System.Linq;

namespace Savedrake
{
    // UI-agnostic backup orchestration for the WPF migration (Phase 4a). Transcribes the WinForms BackupOperation
    // flow VERBATIM: the input guards, the no-save-data guard (R6), the same==same guard, the disk-space preflight,
    // the atomic temp-zip build (AddDirectory + in-zip integrity manifest + hidden comment), the two-layer
    // verify-on-create, and the atomic File.Move publish. Every MessageBox.Show is routed through IDialogService and
    // every Status.Text write through IStatusSink, with the SAME title/message/status text. All non-UI work uses the
    // already-extracted Core helpers (DiskPreflight.*, Manifest.*, BackupNaming.*) — the same ones Main forwards to.
    //
    // Deliberately NOT modelled (these were UI-only / autobackup-cleanup concerns in BackupOperation, and are CALLER
    // responsibilities in the WPF app — see the report):
    //   * checkbox_auto.Checked = false on a guard failure (turning the auto-backup toggle off in the UI),
    //   * the "non-default save folder" warning (directorywarningShown + PromptForFolderSelection),
    //   * the "backup location does not exist -> create it?" prompt (the WPF app validates BackupDir up front;
    //     this service requires it to exist, mirroring the post-prompt state),
    //   * the operation lock (_operationInProgress) — a caller concern, as in RestoreService,
    //   * success/failure SOUND playback (PlaySoundFromResource / PlaySoundFromResource2),
    //   * LoadBackupHistory() list refresh, the _lastAutoBackupFingerprint baseline advance, and the
    //     post-autobackup CleanUpOldAutobackups() retention pass (_cleanupMenuItem-gated).
    public static class BackupService
    {
        public static BackupResult Backup(BackupRequest req, IDialogService dialog, IStatusSink status)
        {
            // Check if the source directory is not empty and the directory exists.
            if (string.IsNullOrWhiteSpace(req.LiveSaveDir) || !Directory.Exists(req.LiveSaveDir))
            {
                dialog.Error("Error", "Please select a valid Savegame location first.");
                return new BackupResult { Ok = false, Message = "Please select a valid Savegame location first." };
            }

            // Check if the backup directory is not empty.
            if (string.IsNullOrWhiteSpace(req.BackupDir))
            {
                dialog.Error("Error", "Please select a Backup location.");
                return new BackupResult { Ok = false, Message = "Please select a Backup location." };
            }

            // Check if the directory is empty.
            DirectoryInfo directoryInfo = new DirectoryInfo(req.LiveSaveDir);
            if (directoryInfo.GetFileSystemInfos().Length == 0)
            {
                dialog.Error("Error", "The selected Savegame location is empty.");
                return new BackupResult { Ok = false, Message = "The selected Savegame location is empty." };
            }

            // R6: a folder can be non-empty yet hold no DD2 save data (wrong folder, or leftover files). Refuse to
            // create a useless/empty-looking backup — require at least one data*.bin / system.bin (searched
            // recursively; .Any() short-circuits on the first match, so the common case is fast). If the recursive
            // scan can't complete (e.g. a permission-denied/locked subfolder on a non-default path), fail OPEN:
            // proceed rather than block a legitimate backup — and never let it throw out of here.
            bool hasSaveData = true;
            try
            {
                hasSaveData = Directory
                    .EnumerateFiles(req.LiveSaveDir, "*", SearchOption.AllDirectories)
                    .Any(p => SaveScan.IsRealSaveEntry(Path.GetFileName(p)));
            }
            catch (UnauthorizedAccessException) { }
            catch (System.IO.IOException) { }
            if (!hasSaveData)
            {
                dialog.Error("Error",
                    "The selected Savegame location has no Dragon's Dogma 2 save data " +
                    "(no data*.bin / system.bin), so there is nothing to back up.");
                return new BackupResult { Ok = false, Message = "The selected Savegame location has no Dragon's Dogma 2 save data." };
            }

            // Check if the source and destination directories are not the same.
            if (req.LiveSaveDir.Equals(req.BackupDir, StringComparison.OrdinalIgnoreCase))
            {
                dialog.Error("Error", "The Savegame and Backup locations cannot be the same.");
                return new BackupResult { Ok = false, Message = "The Savegame and Backup locations cannot be the same." };
            }

            // The "backup location does not exist -> create it?" prompt is a CALLER concern (see header). Require the
            // backup folder to exist here, mirroring the post-prompt state of the original flow.
            if (!Directory.Exists(req.BackupDir))
            {
                dialog.Error("Error", "Please select a Backup location.");
                return new BackupResult { Ok = false, Message = "Please select a Backup location." };
            }

            string backupFileName;
            if (req.RandomName)
            {
                backupFileName = Path.Combine(req.BackupDir, GenerateBackupFileName(req.BackupDir, req.IsAutoBackup));
            }
            else
            {
                string autoPrefix = req.IsAutoBackup ? "auto" : "";
                // Timestamp names are only second-resolution, so two backups in the same second would collide.
                // The random-name branch already dedups (GenerateBackupFileName); make this branch unique too so a
                // second same-second backup can't silently overwrite the first.
                backupFileName = BackupNaming.MakeUniquePath(Path.Combine(req.BackupDir, $"{autoPrefix}backup_{DateTime.Now:yyMMddHHmmss}.zip"));
            }

            // Disk-space preflight: refuse before writing if the backup volume can't hold the data (+ headroom). A
            // disk-full mid-write only leaves a temp file (cleaned up below), but checking first avoids the wasted
            // work and a confusing partial failure.
            long sourceSize = DiskPreflight.GetDirectorySize(req.LiveSaveDir);
            if (!DiskPreflight.HasFreeSpaceFor(req.BackupDir, sourceSize, out string backupSpaceReason))
            {
                status.Set("Backup skipped: low disk space.");
                dialog.Warn("Low Disk Space", "Cannot create the backup: " + backupSpaceReason + ".");
                return new BackupResult { Ok = false, Message = "Cannot create the backup: " + backupSpaceReason + "." };
            }

            // Build the zip into a temp file in the SAME directory, then publish it with an atomic rename. A
            // failure mid-Save (disk full, a source save file locked/removed) then leaves only the temp file
            // (cleaned up below) — never a partial/corrupt .zip at the real backup path, and never an existing
            // good backup overwritten by a truncated stub.
            string tempZip = backupFileName + ".savedrake.tmp";
            try
            {
                // Sweep any orphaned temp files left by a previous backup that was hard-killed mid-write (a graceful
                // failure cleans up via the catch below; a power-loss/kill can't). They're invisible to the *.zip
                // listing/counter but still consume disk, so reclaim them here. Subsumes deleting our own tempZip.
                try { foreach (string stale in Directory.GetFiles(req.BackupDir, "*.savedrake.tmp")) File.Delete(stale); } catch { }
                using (Ionic.Zip.ZipFile zip = new Ionic.Zip.ZipFile())
                {
                    status.Set("Backup started... Please wait.");
                    zip.AddDirectory(req.LiveSaveDir); // Add the directory to the zip
                    // Integrity manifest (P1 layer 2): record every source file's path/length/SHA-256 inside the zip
                    // so we can prove on create (and re-check later) that the backup is complete and uncorrupted.
                    zip.AddEntry(Manifest.ManifestEntryName, System.Text.Encoding.UTF8.GetBytes(Manifest.BuildBackupManifest(req.LiveSaveDir)));
                    zip.Comment = "SamMorrison9800"; // This is the hidden comment
                    zip.Save(tempZip); // Save to the temp file first
                }
                // Verify-on-create: a backup that fails verification must never be published as if it were good. Reject
                // it here (delete the temp, throw into the catch below) so the user is told now, while their live saves
                // are untouched, instead of discovering it only at restore time. Layer 1 = CRC test-extract of every
                // entry; layer 2 = every file present with the manifest's recorded length + SHA-256.
                if (!Manifest.VerifyZipRestorable(tempZip, out string verifyReason) || !Manifest.VerifyZipAgainstManifest(tempZip, out verifyReason))
                {
                    try { File.Delete(tempZip); } catch { }
                    throw new System.IO.IOException("the backup failed verification after writing (" + verifyReason + ")");
                }
                File.Move(tempZip, backupFileName); // atomically publish the completed backup

                string okMsg = req.IsAutoBackup ? $"Autobackup created at {DateTime.Now.ToString("hh:mm:ss tt")}." : "Backup created successfully.";
                status.Set(okMsg);
                Log.Info("Backup created: " + Path.GetFileName(backupFileName) + (req.IsAutoBackup ? " (auto)" : ""));
                return new BackupResult { Ok = true, CreatedPath = backupFileName, Message = okMsg };
            }
            catch (Exception ex)
            {
                Log.Error("Backup failed", ex);
                // Clean up the partial temp file so a failed backup leaves nothing behind.
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                status.Set("Backup failed.");
                dialog.Error("Error", $"An error occurred while creating the backup: {ex.Message}");
                return new BackupResult { Ok = false, Message = $"An error occurred while creating the backup: {ex.Message}" };
            }
        }

        // Transcribed from Main.GenerateBackupFileName (the "randomly generated" name format). Lives in Core now so a
        // backup with the random-name format selected does not depend on the WinForms textbox2 instance state; the
        // backup directory is passed in. Same word list, same (Auto) prefix, same File.Exists dedup counter.
        private static string GenerateBackupFileName(string backupDir, bool isAutoBackup)
        {
            // Use a random combination of words for the file name
            string[] words = { "Bitterblack", "Everfall", "Cassardis", "Cyclops", "Dragonforged", "Chimera", "Gransys", "Sorcerer", "Strider", "Mage", "Warrior", "Mystic", "Knight", "Ranger", "Assassin", "Archer", "Magic", "Bluemoon", "Soren", "Dragonsbane", "Salomet", "Quina", "Mercedes", "Julien", "Selene", "Feste", "Daimon", "Ur-Dragon", "Golem", "Harpy", "Saurian", "Ogre", "Lich", "Wight", "Cockatrice", "Manticore", "Goblin", "Hobgoblin", "Bandit", "Phantom", "Specter", "Wraith", "Skeleton", "Zombie", "Hellhound", "Chimera", "Griffin", "Naga", "Lamia", "Medusa", "Basilisk", "Wyrm", "Wyvern", "Drake", "Dark Bishop", "Eliminator", "Gazer", "Death", "Maneater", "Giant", "Undead", "Cursed", "Abyssal", "Lure", "Brine", "Riftstone", "Portcrystal", "Wakestone", "Godsbane", "Airtight", "Flask", "Liquid", "Vim", "Ferrystone", "Conqueror", "Periapts" };
            Random rnd = new Random();

            // Apply the (Auto) prefix if isAutoBackup is true
            string autoPrefix = isAutoBackup ? "(Auto) " : "";

            // Generate the random file name
            string fileName = $"{autoPrefix}{words[rnd.Next(words.Length)]} {words[rnd.Next(words.Length)]}.zip";

            // Check if the file already exists and append a number if necessary
            int counter = 1;
            string fullPath = Path.Combine(backupDir, fileName);
            while (File.Exists(fullPath))
            {
                // Ensure the counter is added after the (Auto) prefix
                fileName = $"{autoPrefix}{Path.GetFileNameWithoutExtension(fileName).Replace($" {counter - 1}", "")} {counter++}.zip";
                fullPath = Path.Combine(backupDir, fileName);
            }

            return fileName;
        }
    }
}

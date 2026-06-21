using System;
using System.IO;

namespace Savedrake
{
    // UI-agnostic restore orchestration for the WPF migration (Phase 4a). Transcribes the WinForms restore flow
    // VERBATIM: the prompt/guard sequence from Main.button_res_Click, then the destructive swap from
    // Main.RestoreTransactional + Main.HandleRestoreFailure. Every MessageBox.Show is routed through IDialogService
    // and every Status.Text write through IStatusSink, with the SAME title/message/status text. All file work uses
    // the already-extracted RestoreEngine.* primitives — the same ones Main forwards to.
    //
    // SAFETY-CRITICAL: a bug here can delete a user's only save. The stagingStarted / rollbackOk bookkeeping and the
    // ORDERED catch blocks are the data-loss safety and are preserved exactly. CALLER concerns deliberately NOT
    // included (they were UI-only in button_res_Click): the operation lock (_operationInProgress), the save-watcher
    // suppression (_suppressSaveWatcher), and the post-success list refresh / undo-button / fingerprint-baseline.
    public static class RestoreService
    {
        public static RestoreResult Restore(RestoreRequest req, IDialogService dialog, IStatusSink status)
        {
            // ----- prompt/guard sequence (transcribed from button_res_Click) -----

            // Validate the live + backup folders exist (textbox1 / textbox2 validity checks).
            if (string.IsNullOrWhiteSpace(req.LiveSaveDir) || !Directory.Exists(req.LiveSaveDir))
            {
                dialog.Error("Invalid Directory", "Please provide a valid Savegame location.");
                return new RestoreResult { Cancelled = true, Message = "Please provide a valid Savegame location." };
            }

            if (string.IsNullOrWhiteSpace(req.BackupDir) || !Directory.Exists(req.BackupDir))
            {
                dialog.Error("Invalid Directory", "Please provide a valid Backup file location.");
                return new RestoreResult { Cancelled = true, Message = "Please provide a valid Backup file location." };
            }

            // Validate the backup is readable BEFORE touching the live saves, so a
            // corrupt or missing zip can't strand the user with no save game.
            if (string.IsNullOrWhiteSpace(req.BackupZipPath) || !File.Exists(req.BackupZipPath))
            {
                dialog.Error("Restore Failed", "The selected Backup file no longer exists on disk.");
                return new RestoreResult { Cancelled = true, Message = "The selected Backup file no longer exists on disk." };
            }

            string filePath = req.BackupZipPath;
            string liveDir = req.LiveSaveDir;

            // STEP 1 — game-running guard (R2). The caller takes the FRESH synchronous read (CheckGameRunningStatus)
            // and passes it as req.GameRunning; do NOT use a cached value.
            if (req.GameRunning)
            {
                dialog.Warn("Game Running", "Please quit Dragon's Dogma 2 before restoring.");
                return new RestoreResult { Cancelled = true, Message = "Please quit Dragon's Dogma 2 before restoring." };
            }

            // STEP 2 — Steam Cloud warning (run4 C). Declining costs nothing; no live file touched yet.
            if (!dialog.Confirm(
                    "Exit Steam Before Restoring",
                    "Before restoring, fully EXIT Steam (or disable Dragon's Dogma 2 Cloud Saves in " +
                    "Steam > Properties). Otherwise Steam may re-upload your OLD save and overwrite this restore." +
                    "\n\nSavedrake snapshots your current save first, so you can undo this from File > Undo last restore." +
                    "\n\nContinue with the restore?"))
            {
                status.Set("Restore cancelled.");
                return new RestoreResult { Cancelled = true, Message = "Restore cancelled." };
            }

            // STEP 3 — validate the backup BEFORE touching live saves (R1).
            if (!RestoreEngine.ValidateBackup(filePath, out string reason))
            {
                dialog.Error("Restore Failed", reason + "\n\nYour current save files have not been touched.");
                return new RestoreResult { Cancelled = true, Message = reason };
            }

            // STEP 3a — re-verify a manifest-bearing backup against its recorded hashes before touching live saves
            // (P1). Catches a backup that has bit-rotted on disk since it was created. Legacy backups without a
            // manifest are unaffected and restore as before.
            if (Manifest.RestoreBlockedByManifest(filePath, out string integrityReason))
            {
                dialog.Error("Restore Failed",
                    "This backup failed its integrity check (" + integrityReason + ").\n\n" +
                    "Your current save files have not been touched.");
                return new RestoreResult { Cancelled = true, Message = "This backup failed its integrity check (" + integrityReason + ")." };
            }

            // STEP 3c — disk-space preflight: the restore extracts the backup into a staging folder on the save
            // volume before swapping it in. Refuse up front if that volume can't hold it (+ headroom), with the
            // live saves untouched, instead of failing partway through the extraction.
            long restoreNeeded = DiskPreflight.GetZipUncompressedSize(filePath);
            if (!DiskPreflight.HasFreeSpaceFor(liveDir, restoreNeeded, out string restoreSpaceReason))
            {
                dialog.Warn("Low Disk Space",
                    "Cannot restore: " + restoreSpaceReason + ".\n\nYour current save files have not been touched.");
                return new RestoreResult { Cancelled = true, Message = "Cannot restore: " + restoreSpaceReason + "." };
            }

            // STEP 3b — pre-restore safety checkpoint (P4). RestoreTransactional deletes the current live save
            // on success, so snapshot it first into a "(Pre-Restore)" backup the user can roll back to. If the
            // snapshot can't be made, let the user decide rather than silently proceeding without a safety net.
            // SKIPPED when req.SuppressPreRestoreCheckpoint is set (the "Load into game" path already preserved the
            // outgoing save as a (Pre-Load) in its own folder, so a checkpoint here would be a misfiled duplicate). This
            // is a user-Undo convenience only — the transactional rollback below uses a separate sibling rollback dir
            // and is unaffected either way.
            if (!req.SuppressPreRestoreCheckpoint)
            {
                status.Set("Creating pre-restore checkpoint...");
                if (!RestoreEngine.CreatePreRestoreCheckpoint(liveDir, req.BackupDir))
                {
                    if (!dialog.Confirm(
                            "Pre-Restore Checkpoint Failed",
                            "Savedrake could not create a safety snapshot of your current save before restoring.\n\n" +
                            "If you continue, your current save will be replaced and its current state will be lost.\n\n" +
                            "Restore anyway?"))
                    {
                        status.Set("Restore cancelled.");
                        return new RestoreResult { Cancelled = true, Message = "Restore cancelled." };
                    }
                }
            }

            // STEP 4 — delegate ALL destructive work (staging, swap, rollback, cleanup) to the transaction.
            bool ok = RestoreTransactional(filePath, liveDir, dialog, status);
            return new RestoreResult
            {
                Ok = ok,
                Cancelled = false,
                Message = ok ? "Restore successful." : "Restore failed."
            };
        }

        // ===== Transactional restore (Bucket 2 — kills R1 / R2 / R3) =====
        // Invariant: at every instant the user's saves exist intact in exactly one place — liveDir or rollbackDir.
        // Transcribed verbatim from Main.RestoreTransactional; Status.Text -> status.Set, MessageBox -> dialog.
        private static bool RestoreTransactional(string filePath, string liveDir, IDialogService dialog, IStatusSink status)
        {
            status.Set("Restore started... Please wait.");
            Log.Info("Restore started from: " + Path.GetFileName(filePath));
            string stagingDir  = RestoreEngine.CreateSiblingTempDir(liveDir, "savedrake_stage");
            string rollbackDir = RestoreEngine.CreateSiblingTempDir(liveDir, "savedrake_rollback");
            bool stagingStarted = false; // true once T4 begins: live then holds disposable STAGED content, not originals
            bool rollbackOk = true;      // success leaves true (old saves now stale); a failed recovery sets it false
            try
            {
                RestoreEngine.ExtractZipToStaging(filePath, stagingDir);   // T1 stage + zip-slip guard
                RestoreEngine.FlattenNestedLayout(stagingDir);            // T1b flatten nested win64_save wrapper(s) (R3)
                if (!RestoreEngine.VerifyStagedDir(stagingDir))           // T2 verify on disk
                    throw new System.IO.IOException(
                        "The backup extracted but does not contain Dragon's Dogma 2 save data. Restore aborted.");

                RestoreEngine.MoveDirContents(liveDir, rollbackDir);      // T3 move live aside (same-volume rename)
                stagingStarted = true;                                    // originals are all in rollbackDir now
                RestoreEngine.MoveDirContents(stagingDir, liveDir);       // T4 move staged into live
                // T5 strip ReadOnly (R2). The restore is ALREADY committed here (staged saves are live), so this
                // cosmetic post-step must never throw into the catch below and trigger a rollback of a good
                // restore. Best-effort guard (ClearReadOnlyRecursive is itself non-throwing now too).
                try { RestoreEngine.ClearReadOnlyRecursive(liveDir); } catch { } // T5 strip ReadOnly (R2)

                status.Set("Restore successful.");                        // T6 commit
                Log.Info("Restore successful from: " + Path.GetFileName(filePath));
                dialog.Info("Information", "Restore successful.");
                return true;
            }
            // CS0160: order is load-bearing. ZipException : Exception, UnauthorizedAccessException : SystemException,
            // IOException : SystemException — none derives from another, so the first three are order-free; Exception MUST be last.
            catch (Ionic.Zip.ZipException ze)       { rollbackOk = HandleRestoreFailure(ze,  rollbackDir, liveDir, stagingStarted, dialog, status); return false; }
            catch (UnauthorizedAccessException uae) { rollbackOk = HandleRestoreFailure(uae, rollbackDir, liveDir, stagingStarted, dialog, status); return false; }
            catch (System.IO.IOException ioEx)      { rollbackOk = HandleRestoreFailure(ioEx, rollbackDir, liveDir, stagingStarted, dialog, status); return false; }
            catch (Exception ex)                    { rollbackOk = HandleRestoreFailure(ex,  rollbackDir, liveDir, stagingStarted, dialog, status); return false; }
            finally
            {
                RestoreEngine.TryDeleteDir(stagingDir);
                // Delete the set-aside originals ONLY when provably safe. Success leaves rollbackOk==true (live now
                // holds the committed restore, so the old copy is stale); a FULLY recovered failure also leaves it
                // true with rollbackDir already emptied. When recovery did NOT fully succeed (rollbackOk==false),
                // rollbackDir may hold the user's ONLY intact copy — preserve it (the dialog named it for recovery).
                if (rollbackOk) RestoreEngine.TryDeleteDir(rollbackDir);
            }
        }

        private static bool HandleRestoreFailure(Exception ex, string rollbackDir, string liveDir, bool stagingStarted, IDialogService dialog, IStatusSink status)
        {
            bool recovered = RestoreEngine.Rollback(liveDir, rollbackDir, stagingStarted);
            Log.Error("Restore failed (recovered=" + recovered + ")", ex);
            status.Set(recovered
                ? "Restore failed. Your previous save files were restored."
                : "Restore failed. See the dialog to recover your saves.");
            string tail = recovered
                ? "\n\nYour previous save files have been restored."
                : "\n\nWARNING: automatic recovery did not fully complete, but your ORIGINAL save files were NOT deleted." +
                  " Some may already be back in your save folder:\n" + liveDir +
                  "\nand the rest are preserved here:\n" + rollbackDir +
                  "\nMove any files from the second folder into the first to finish recovering.";
            dialog.Error("Restore Failed", ex.Message + tail);
            return recovered; // tells RestoreTransactional whether rollbackDir is empty (safe to delete) or holds the only copy
        }
    }
}

namespace Savedrake
{
    // UI-agnostic service seams for the WinForms -> WPF migration (Phase 4a). The orchestration services
    // (RestoreService / BackupService) transcribe the WinForms restore + backup flows VERBATIM (same logic,
    // sequence, and dialog/status text), with every direct MessageBox.Show / Status.Text call replaced by a
    // call through one of these interfaces. The WPF app supplies real implementations (a dialog host + a status
    // bar binding); the headless harness supplies stubs. SAFETY-CRITICAL: the services drive a destructive
    // restore — do not reorder the guards or change the text.

    // Modal user interaction the flows need. Confirm returns true for the "Yes"/affirmative choice (the WinForms
    // originals used MessageBox YesNo and tested == DialogResult.Yes). Info/Warn/Error are one-way notifications
    // mapping to the Information/Warning/Error MessageBox icons the originals used.
    public interface IDialogService
    {
        bool Confirm(string title, string message);
        void Info(string title, string message);
        void Warn(string title, string message);
        void Error(string title, string message);
    }

    // The status-line sink. Each Set(...) replaces a `Status.Text = ...` write in the original flow, verbatim.
    public interface IStatusSink
    {
        void Set(string text);
    }

    // Inputs for a restore. The caller resolves these from its UI/state before calling RestoreService.Restore:
    //   BackupZipPath = the selected backup file (textbox2 + selected list item in the WinForms app),
    //   LiveSaveDir   = the live save folder (textbox1), BackupDir = the backup folder (textbox2),
    //   GameRunning   = a FRESH game-running read taken by the caller (the WinForms guard called
    //                   CheckGameRunningStatus() synchronously, NOT the cached isGameRunning field).
    public sealed class RestoreRequest
    {
        public string BackupZipPath;
        public string LiveSaveDir;
        public string BackupDir;
        public bool GameRunning;
    }

    // Outcome of a restore. Ok = the swap committed. Cancelled = a guard stopped before any destructive work
    // (inputs invalid, game running, user declined, validation/integrity/space/checkpoint gate). Message mirrors
    // the status text. When Ok==false && Cancelled==false the swap was attempted and failed (recovery ran).
    public sealed class RestoreResult
    {
        public bool Ok;
        public bool Cancelled;
        public string Message;
    }

    // Inputs for a backup. LiveSaveDir = the save folder to back up (textbox1), BackupDir = where the zip is
    // written (textbox2), IsAutoBackup = whether this is a timer-driven autobackup (controls the file-name prefix
    // and the autobackup status text), RandomName = whether the "randomly generated" name format menu item is
    // checked (chooses GenerateBackupFileName vs the timestamped name). Pieces of BackupOperation that are purely
    // autobackup-cleanup / retention-limit concerns are CALLER responsibilities and are intentionally not modelled
    // here (see BackupService for the list).
    public sealed class BackupRequest
    {
        public string LiveSaveDir;
        public string BackupDir;
        public bool IsAutoBackup;
        public bool RandomName;
    }

    // Outcome of a backup. Ok = a verified backup zip was published. CreatedPath = its full path on success.
    // Message mirrors the status text.
    public sealed class BackupResult
    {
        public bool Ok;
        public string CreatedPath;
        public string Message;
    }
}

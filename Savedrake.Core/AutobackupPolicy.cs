namespace Savedrake
{
    // The single decision one autobackup attempt resolves to. The mechanism (timers, the save watcher, WMI game
    // events, the Dispatcher) lives in the UI layer; this enum is what that machinery asks for and acts on.
    public enum AutobackupAction
    {
        DoBackup,             // take a backup now
        SkipNoChange,         // save folder is byte-identical to the last backup — nothing to capture
        SkipNotReady,         // fingerprint unavailable (folder missing/locked/mid-write) — fail closed, retry later
        PauseGameNotRunning,  // DD2 is no longer running — pause the timer and the save watcher
        LimitReached,         // the autobackup limit is hit and auto-cleanup is off — stop and tell the user
        InvalidLimit          // the configured limit is not a valid integer — tell the user
    }

    // The change-aware autobackup decision, lifted out of the WinForms RunChangeAwareAutobackup / OnGameStatusChanged
    // so it is one pure, fully testable function with no UI, no I/O, and no timers. The caller gathers the live inputs
    // (a fresh game-running read, the count on disk, the current vs last fingerprint) and acts on the returned action.
    //
    // Order matters and mirrors the shipped app exactly:
    //   1. game not running         -> PauseGameNotRunning   (checked first; never back up stale saves)
    //   2. limit field not an int   -> InvalidLimit
    //   3. limit reached, no cleanup-> LimitReached          (cleanup on => keep going, cleanup enforces the cap)
    //   4. change gate (unless bypassed at game-start, where the first backup of a session always fires):
    //        - no fingerprint       -> SkipNotReady          (fail CLOSED: a null fp never counts as "unchanged")
    //        - fingerprint unchanged-> SkipNoChange          (a null baseline never skips — first eligible attempt)
    //   5. otherwise                -> DoBackup
    public static class AutobackupPolicy
    {
        public static AutobackupAction Decide(
            bool gameRunning,
            bool maxBackupsValid,
            int backupCount,
            int maxBackups,
            bool cleanupEnabled,
            string currentFingerprint,
            string lastFingerprint,
            bool bypassChangeGate)
        {
            if (!gameRunning) return AutobackupAction.PauseGameNotRunning;
            if (!maxBackupsValid) return AutobackupAction.InvalidLimit;

            // Cleanup on -> keep backing up (cleanup enforces the limit); off -> original stop-at-limit behaviour.
            if (!(cleanupEnabled || backupCount < maxBackups)) return AutobackupAction.LimitReached;

            // The immediate backup at game-start is NOT routed through the change gate (it always fires, then the
            // next attempt establishes the baseline). The interval timer and the save watcher always gate.
            if (!bypassChangeGate)
            {
                if (currentFingerprint == null) return AutobackupAction.SkipNotReady;
                if (lastFingerprint != null && currentFingerprint == lastFingerprint) return AutobackupAction.SkipNoChange;
            }

            return AutobackupAction.DoBackup;
        }
    }
}

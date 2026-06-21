using System;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;

namespace Savedrake.App
{
    // The configuration + effects the AutobackupController reads from. Implemented by MainViewModel so the controller
    // stays free of MVVM/XAML details and can be reasoned about (and, in principle, faked) on its own.
    internal interface IAutobackupHost
    {
        string SaveDir { get; }
        string BackupDir { get; }
        bool AutobackupEnabled { get; }
        TimeSpan AutobackupInterval { get; }   // already validated to the >= 5 min floor by the host
        bool IntervalValid { get; }
        int MaxAutobackups { get; }
        bool MaxAutobackupsValid { get; }
        bool CleanupEnabled { get; }
        bool RecycleEnabled { get; }
        bool BackupOnSaveEnabled { get; }
        string CountFilePath { get; }
        string IntervalDisplay { get; }        // the interval as the user typed it, for status text ("30 minutes")
        bool IsOperationInProgress { get; }    // a manual backup/restore is mid-flight

        IDialogService Dialog { get; }
        IStatusSink Status { get; }

        bool PerformAutobackup();   // take one autobackup now (BackupService, IsAutoBackup=true); true on success
        void OnBackupsChanged();    // the backup set changed -> refresh the list
        void ForceDisableAutobackup(); // turn the enable toggle off (limit reached / DD2 missing)
    }

    // The WPF autobackup engine. The shipped WinForms app ran the same feature on a System.Timers.Timer (marshalled
    // to the UI via SynchronizingObject), a FileSystemWatcher + a WinForms quiesce timer, and a WMI
    // ManagementEventWatcher on Steam's "Running" registry value. This re-expresses that machinery for WPF:
    //   * the interval + quiesce timers are DispatcherTimers, which fire on the UI thread directly (no marshalling),
    //   * the FileSystemWatcher and WMI events fire on background threads and are marshalled onto the UI thread via
    //     the Dispatcher (BeginInvoke, never a blocking Invoke, so a background thread can't deadlock against Dispose).
    // The DECISION is AutobackupPolicy.Decide in Core; everything here is the mechanism that gathers the live inputs,
    // asks the policy, and carries out the answer. The re-entrancy guard is preserved because a failure dialog inside
    // a backup pumps the Dispatcher queue, so a queued tick / watcher event can re-enter.
    internal sealed class AutobackupController : IDisposable
    {
        private readonly IAutobackupHost _host;
        private readonly Dispatcher _dispatcher;

        private DispatcherTimer _intervalTimer;
        private DispatcherTimer _quiesceTimer;       // debounces a burst of save writes into one backup
        private FileSystemWatcher _saveWatcher;
        private ManagementEventWatcher _gameWatcher;

        private bool _autoBackupInProgress;          // re-entrancy guard (UI thread only)
        private bool _running;                        // DD2 is currently running and the engine is active
        private string _lastFingerprint;             // change-aware baseline
        private volatile bool _suppressSaveWatcher;  // set by the host around a restore so we ignore our own writes
        private bool _noGame;                         // the WMI watcher could not be created (DD2 not a Steam app here)
        private bool _disposed;

        public AutobackupController(IAutobackupHost host)
        {
            _host = host;
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        // True when DD2 is not present as a Steam app on this machine (the WMI registry watcher failed to start). The
        // host uses this to refuse enabling autobackup with a clear message rather than silently doing nothing.
        public bool NoGame => _noGame;

        // The host sets this true for the duration of a manual restore (which writes into the save folder) so the
        // save watcher does not treat the restore's own writes as a fresh save and back them up.
        public bool SuppressSaveWatcher { get => _suppressSaveWatcher; set => _suppressSaveWatcher = value; }

        // Begin watching DD2's running state. Call once at startup.
        public void Start() => InitializeGameWatcher();

        // ----- entry points the host calls when the user changes configuration -----

        // The master enable toggle flipped, or the save/backup folder or the interval changed while enabled.
        // Re-evaluates the whole feature from the live state.
        public void OnEnableOrConfigChanged()
        {
            if (_host.AutobackupEnabled)
            {
                OnGameStatusChanged(GameDetect.IsDd2Running());
            }
            else
            {
                StopIntervalTimer();
                StopSaveWatcher();
                // Drop the baseline so a later re-enable re-baselines cleanly against whatever the save folder is then.
                _lastFingerprint = null;
            }
        }

        // The "back up the moment the game saves" option toggled. Start/stop just the watcher (the interval timer,
        // the fallback, is unaffected).
        public void OnBackupOnSaveChanged()
        {
            if (_host.AutobackupEnabled && _host.BackupOnSaveEnabled && GameDetect.IsDd2Running())
                StartSaveWatcher();
            else
                StopSaveWatcher();
        }

        // The interval text changed while autobackup is on. If DD2 is running (the timer is live), re-arm it with the
        // new interval without taking an immediate backup. A no-op when paused (the timer re-reads the interval when
        // the game next starts).
        public void ApplyIntervalChange()
        {
            if (_host.AutobackupEnabled && _running) StartIntervalTimer();
        }

        // The user just took a manual backup: advance the baseline so the next autobackup attempt does not re-capture
        // an unchanged save (matches the WinForms baseline advance inside BackupOperation, which ran for manual backups
        // too). Harmless when autobackup is off.
        public void NotifyExternalBackup()
        {
            try { _lastFingerprint = Fingerprint.ComputeSaveFingerprint(_host.SaveDir); } catch { }
        }

        // ----- game-state watcher (WMI on Steam's "Running" registry value) -----

        private void InitializeGameWatcher()
        {
            try
            {
                WindowsIdentity currentUser = WindowsIdentity.GetCurrent();
                string keyPath = @"Software\Valve\Steam\Apps\" + GameDetect.Dd2SteamAppId;
                var query = new WqlEventQuery(string.Format(
                    "SELECT * FROM RegistryValueChangeEvent WHERE Hive='HKEY_USERS' AND KeyPath='{0}\\\\{1}' AND ValueName='Running'",
                    currentUser.User.Value, keyPath.Replace("\\", "\\\\")));
                _gameWatcher = new ManagementEventWatcher(query);
                _gameWatcher.EventArrived += OnGameRegistryChanged;
                _gameWatcher.Start();
            }
            catch
            {
                _noGame = true; // DD2 not a Steam app here / WMI unavailable — autobackup can't function
            }
        }

        private void OnGameRegistryChanged(object sender, EventArrivedEventArgs e)
        {
            // WMI delivers this on a worker thread. Marshal onto the UI thread with BeginInvoke (not Invoke) so the
            // WMI thread never blocks on the UI thread — a blocking Invoke racing Dispose() could deadlock.
            if (_disposed) return;
            try
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed) return;
                    OnGameStatusChanged(GameDetect.IsDd2Running());
                }));
            }
            catch (Exception) { /* dispatcher shutting down */ }
        }

        // ----- the game-state transition (mirror of WinForms OnGameStatusChanged) -----

        private void OnGameStatusChanged(bool running)
        {
            if (!_host.AutobackupEnabled)
            {
                _running = false;
                StopIntervalTimer();
                StopSaveWatcher();
                return;
            }

            _running = running;
            if (running)
            {
                StartIntervalTimer();
                StartSaveWatcher(); // also capture instantly on each save, if the user opted in

                // Null the baseline at game-start so the first backup of the session always fires (it is taken with
                // the change gate bypassed); the next attempt then establishes the real baseline.
                _lastFingerprint = null;
                RunChangeAwareAutobackup(bypassChangeGate: true);

                if (_host.AutobackupEnabled) // still on (the immediate backup may have hit the limit and disabled it)
                    _host.Status.Set("Game running. Autobackup began every " + _host.IntervalDisplay + ".");
            }
            else
            {
                StopIntervalTimer();
                StopSaveWatcher();
                _host.Status.Set("Game not running. Autobackup paused.");
            }
        }

        // ----- one change-aware attempt (mirror of RunChangeAwareAutobackup) -----

        private void RunChangeAwareAutobackup(bool bypassChangeGate = false)
        {
            if (_autoBackupInProgress) return;          // re-entrancy guard
            if (_host.IsOperationInProgress) return;    // a manual backup/restore is mid-flight
            _autoBackupInProgress = true;
            try
            {
                bool running = GameDetect.IsDd2Running();
                int count = AutobackupCountStore.Read(_host.CountFilePath);
                string currentFp = bypassChangeGate ? null : Fingerprint.ComputeSaveFingerprint(_host.SaveDir);

                AutobackupAction action = AutobackupPolicy.Decide(
                    running,
                    _host.MaxAutobackupsValid,
                    count,
                    _host.MaxAutobackups,
                    _host.CleanupEnabled,
                    currentFp,
                    _lastFingerprint,
                    bypassChangeGate);

                switch (action)
                {
                    case AutobackupAction.PauseGameNotRunning:
                        StopIntervalTimer();
                        StopSaveWatcher();
                        _host.Status.Set("Game not running. Autobackup paused.");
                        break;

                    case AutobackupAction.InvalidLimit:
                        _host.Dialog.Error("Autobackup", "Please enter a valid whole number for the autobackup limit.");
                        break;

                    case AutobackupAction.LimitReached:
                        StopIntervalTimer(); // stop BEFORE the modal so no further tick is armed
                        StopSaveWatcher();
                        _host.ForceDisableAutobackup();
                        _host.Dialog.Info("Autobackup",
                            "The maximum number of " + _host.MaxAutobackups + " autobackups has been reached. " +
                            "To keep going, raise \"Keep at most\" in the Autobackup panel.");
                        break;

                    case AutobackupAction.SkipNotReady:
                        _host.Status.Set("Autobackup: save folder not ready, will retry.");
                        break;

                    case AutobackupAction.SkipNoChange:
                        _host.Status.Set("Autobackup: no save changes since the last backup.");
                        break;

                    case AutobackupAction.DoBackup:
                        if (_host.PerformAutobackup())
                        {
                            // Advance the baseline from the current save (matches the WinForms baseline advance inside
                            // BackupOperation), then thin old autobackups if the user enabled cleanup.
                            _lastFingerprint = Fingerprint.ComputeSaveFingerprint(_host.SaveDir);
                            if (_host.CleanupEnabled)
                            {
                                int removed = AutobackupCleanup.Run(_host.BackupDir, _host.MaxAutobackups, _host.RecycleEnabled, DateTime.UtcNow.Ticks);
                                if (removed > 0)
                                    _host.Status.Set(removed == 1 ? "Removed 1 old autobackup." : ("Removed " + removed + " old autobackups."));
                            }
                            _host.OnBackupsChanged();
                        }
                        break;
                }
            }
            finally
            {
                _autoBackupInProgress = false;
            }
        }

        // ----- interval timer (the fallback trigger) -----

        private void StartIntervalTimer()
        {
            if (!_host.IntervalValid) { StopIntervalTimer(); return; }
            if (_intervalTimer == null)
            {
                _intervalTimer = new DispatcherTimer();
                _intervalTimer.Tick += (s, e) => RunChangeAwareAutobackup();
            }
            _intervalTimer.Interval = _host.AutobackupInterval;
            _intervalTimer.Start();
        }

        private void StopIntervalTimer()
        {
            try { _intervalTimer?.Stop(); } catch { }
        }

        // ----- save-folder watcher (instant capture) -----

        private void StartSaveWatcher()
        {
            if (_disposed) return; // never resurrect the watcher after shutdown
            if (!_host.BackupOnSaveEnabled) return;
            string dir = _host.SaveDir;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            try
            {
                StopSaveWatcher(); // ensure a single clean instance (also tears down any previous quiesce timer)
                _quiesceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) }; // wait for writes to settle
                _quiesceTimer.Tick += OnQuiesceElapsed;
                _saveWatcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024
                };
                _saveWatcher.Changed += OnSaveChanged;
                _saveWatcher.Created += OnSaveChanged;
                _saveWatcher.Deleted += OnSaveChanged;
                _saveWatcher.Renamed += OnSaveChanged;
                _saveWatcher.Error += OnSaveWatcherError;
                _saveWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not start the save watcher (the interval timer remains the fallback): " + ex.Message);
                StopSaveWatcher();
            }
        }

        private void StopSaveWatcher()
        {
            if (_quiesceTimer != null)
            {
                try { _quiesceTimer.Stop(); } catch { }
                try { _quiesceTimer.Tick -= OnQuiesceElapsed; } catch { }
                _quiesceTimer = null;
            }
            if (_saveWatcher != null)
            {
                try { _saveWatcher.EnableRaisingEvents = false; } catch { }
                try
                {
                    _saveWatcher.Changed -= OnSaveChanged;
                    _saveWatcher.Created -= OnSaveChanged;
                    _saveWatcher.Deleted -= OnSaveChanged;
                    _saveWatcher.Renamed -= OnSaveChanged;
                    _saveWatcher.Error -= OnSaveWatcherError;
                }
                catch { }
                try { _saveWatcher.Dispose(); } catch { }
                _saveWatcher = null;
            }
        }

        // Watcher events fire on a ThreadPool thread. Ignore our own restore writes; otherwise marshal to the UI
        // thread and (re)start the debounce, so a burst of writes collapses into one backup once the save has settled.
        private void OnSaveChanged(object sender, FileSystemEventArgs e)
        {
            if (_disposed || _suppressSaveWatcher) return;
            try { _dispatcher.BeginInvoke(new Action(RestartQuiesce)); } catch (Exception) { }
        }

        private void RestartQuiesce()
        {
            // A marshalled event can land after StopSaveWatcher nulled the timer (shutdown / game exit / toggle off).
            if (_disposed || _quiesceTimer == null) return;
            try { _quiesceTimer.Stop(); _quiesceTimer.Start(); } catch (Exception) { }
        }

        private void OnQuiesceElapsed(object sender, EventArgs e)
        {
            if (_disposed || _quiesceTimer == null) return;
            try { _quiesceTimer.Stop(); } catch (Exception) { return; }
            if (_suppressSaveWatcher) return;
            if (!_host.AutobackupEnabled) return;
            // A backup or restore is mid-flight: wait another quiet window rather than overlapping it.
            if (_host.IsOperationInProgress) { try { _quiesceTimer.Start(); } catch (Exception) { } return; }
            RunChangeAwareAutobackup();
        }

        // A watcher Error (e.g. InternalBufferOverflow) means events may have been dropped. Re-arm the watcher; the
        // interval timer covers anything missed in the meantime.
        private void OnSaveWatcherError(object sender, ErrorEventArgs e)
        {
            if (_disposed) return;
            Log.Warn("Save watcher error, re-arming: " + (e.GetException() != null ? e.GetException().Message : "unknown"));
            try { _dispatcher.BeginInvoke(new Action(() => { if (_disposed) return; StopSaveWatcher(); StartSaveWatcher(); })); } catch (Exception) { }
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                if (_gameWatcher != null)
                {
                    _gameWatcher.EventArrived -= OnGameRegistryChanged;
                    _gameWatcher.Stop();
                    _gameWatcher.Dispose();
                    _gameWatcher = null;
                }
            }
            catch { }
            StopIntervalTimer();
            StopSaveWatcher();
        }
    }
}

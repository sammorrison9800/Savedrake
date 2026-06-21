using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Savedrake.App.ViewModels
{
    // One row in the Backups list. Mirrors the WinForms LoadBackupHistory columns (file name, friendly time,
    // integrity status) plus the pin state, with the full path kept for Restore/Delete. Status is observable so
    // "Validate all" can upgrade each row's integrity in place (Protected/Legacy -> Validated/Legacy/Corrupt/Missing)
    // without rebuilding the list; the other fields are set once at creation (rename/pin rebuild the list).
    public partial class BackupRow : ObservableObject
    {
        public string FileName { get; set; }
        public string FriendlyTime { get; set; }
        public bool IsPinned { get; set; }
        public string FullPath { get; set; }

        [ObservableProperty]
        private string status;   // "Protected"/"Legacy" at load; "Validated"/"Corrupt"/"Missing" after Validate all
    }

    // The main workflow view model. Drives the Folders + Autobackup + Backups cards and routes the Backup / Restore /
    // Delete actions through the UI-agnostic Core services (BackupService / RestoreService). The autobackup engine
    // (Phase 5) lives in AutobackupController; this view model is its IAutobackupHost — it supplies the live config
    // and the effects (take a backup, refresh the list, flip the toggle). MVVM via CommunityToolkit.Mvvm.
    public partial class MainViewModel : ObservableObject, IAutobackupHost, IDisposable
    {
        private readonly IDialogService _dialog;
        private readonly IStatusSink _status;
        private readonly AutobackupController _autobackup;
        private readonly SoundFeedback _sound = new SoundFeedback();

        private bool _loaded;               // false during initial settings load so property hooks don't react
        private bool _inEnableChange;       // re-entrancy guard while the enable toggle is being processed/reverted
        private bool _isOperationInProgress; // a manual backup/restore (or an autobackup) is mid-flight

        // ----- Folders -----

        [ObservableProperty]
        private string saveDir;

        [ObservableProperty]
        private string backupDir;

        // ----- Autobackup config (bound to the Autobackup card) -----

        // Master enable. ConfigEditable is the inverse, so the folder/interval/limit inputs lock while it is on.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ConfigEditable))]
        private bool autobackupEnabled;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IntervalValid))]
        private string intervalText = "30 minutes";

        [ObservableProperty]
        private string maxBackupsText = "10";

        [ObservableProperty]
        private bool cleanupEnabled;

        [ObservableProperty]
        private bool recycleEnabled;

        [ObservableProperty]
        private bool backupOnSaveEnabled;

        // When on, minimizing hides the window to the system tray instead of the taskbar (persisted as CheckboxTray).
        [ObservableProperty]
        private bool minimizeToTray;

        // Backup file-name format: randomly generated (default) vs time-stamped (persisted as BackupFileName1/2).
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UseTimestampName))]
        private bool useRandomName = true;

        public bool UseTimestampName => !UseRandomName;

        // Last window size, restored on next launch (persisted as WindowWidth/WindowHeight). 0 = use the default.
        public int WindowWidth { get; private set; }
        public int WindowHeight { get; private set; }

        // Preset intervals offered in the editable interval box (the box also accepts free text like "12 minutes").
        public ObservableCollection<string> IntervalOptions { get; } =
            new ObservableCollection<string> { "5 minutes", "15 minutes", "30 minutes", "1 hour", "2 hours" };

        public bool ConfigEditable => !AutobackupEnabled;

        // ----- Backups list -----

        public ObservableCollection<BackupRow> Backups { get; } = new ObservableCollection<BackupRow>();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PinMenuLabel))]
        private BackupRow selectedBackup;

        // Dynamic context-menu label: "Unpin" when the selected backup is pinned, otherwise "Pin".
        public string PinMenuLabel => (SelectedBackup != null && SelectedBackup.IsPinned) ? "Unpin" : "Pin";

        [ObservableProperty]
        private string statusText = "Ready.";

        [ObservableProperty]
        private string backupCount;

        public MainViewModel()
        {
            _dialog = new WpfDialogService();
            _status = new StatusSink(this);
            _autobackup = new AutobackupController(this);

            LoadSettings();
            RefreshBackups();
            _loaded = true;
            // The WMI watcher and any saved-on autobackup re-engage are deferred to Activate(), which the window calls
            // once it exists — so a limit/invalid dialog from the immediate game-start backup has a real owner window
            // and the WMI watcher can't fire a status change mid-construction.
        }

        // Called by the window once it is loaded. Starts watching DD2's running state and re-engages a saved-on
        // autobackup. Kept off the constructor so any modal it raises owns the (now-real) window.
        public void Activate()
        {
            _autobackup.Start();
            EngageAutobackupIfEnabled();
        }

        // ================= IAutobackupHost (live config + effects the controller reads) =================

        public TimeSpan AutobackupInterval =>
            IntervalParser.TryParse(IntervalText, out TimeSpan ts) ? ts : TimeSpan.Zero;

        public bool IntervalValid =>
            IntervalParser.TryParse(IntervalText, out TimeSpan ts) && ts >= TimeSpan.FromMinutes(5);

        public int MaxAutobackups => int.TryParse(MaxBackupsText, out int n) ? n : 0;

        // A valid limit is a positive whole number. A zero/negative limit would let autobackup engage and then
        // immediately self-disable with a nonsensical "maximum number of 0 autobackups reached" message.
        public bool MaxAutobackupsValid => int.TryParse(MaxBackupsText, out int n) && n >= 1;

        public string CountFilePath => Path.Combine(AppDataDir, "count_of_autobackups.txt");

        public string IntervalDisplay => string.IsNullOrWhiteSpace(IntervalText) ? "the set interval" : IntervalText.Trim();

        public bool IsOperationInProgress => _isOperationInProgress;

        public IDialogService Dialog => _dialog;

        public IStatusSink Status => _status;

        // Take one autobackup now. Holds the operation lock for its duration so a manual click or another trigger
        // can't overlap it (the controller's re-entrancy guard covers the in-thread case; this covers the lock the
        // manual commands check).
        public bool PerformAutobackup()
        {
            bool prev = _isOperationInProgress;
            _isOperationInProgress = true;
            try
            {
                BackupResult r = BackupService.Backup(
                    new BackupRequest { LiveSaveDir = SaveDir, BackupDir = BackupDir, IsAutoBackup = true, RandomName = UseRandomName },
                    _dialog, _status);
                if (r.Ok) _sound.Success(); else _sound.Error();
                return r.Ok;
            }
            finally { _isOperationInProgress = prev; }
        }

        public void OnBackupsChanged() => RefreshBackups();

        // Turn the enable toggle off without re-running the enable logic (the controller has already stopped the
        // timers when it calls this — limit reached / DD2 missing). Just flip the UI flag and persist.
        public void ForceDisableAutobackup()
        {
            _inEnableChange = true;
            try { AutobackupEnabled = false; }
            finally { _inEnableChange = false; }
            SaveSettings();
        }

        // ================= settings (shared savedrake_settings.xml, best-effort) =================

        private static string AppDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Savedrake");

        private static string SettingsFilePath => Path.Combine(AppDataDir, "savedrake_settings.xml");
        private static string VersionFilePath => Path.Combine(AppDataDir, "version.txt");
        private static string UpdaterXmlPath => Path.Combine(AppDataDir, "savedrake-updater.xml");

        // Read the folders + autobackup settings from the WinForms settings file if present. Read-only, dependency-free
        // (a small hand-rolled XML read), and never throws — missing/garbled settings just leave defaults in place.
        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return;
                var doc = System.Xml.Linq.XDocument.Load(SettingsFilePath);
                var root = doc.Root;
                if (root == null) return;

                string t1 = (string)root.Element("Textbox1");
                string t2 = (string)root.Element("Textbox2");
                if (!string.IsNullOrWhiteSpace(t1)) SaveDir = t1;
                if (!string.IsNullOrWhiteSpace(t2)) BackupDir = t2;

                string limit = (string)root.Element("AutoBackupLimit");
                if (!string.IsNullOrWhiteSpace(limit)) MaxBackupsText = limit;

                // Interval: reuse the WinForms ComboboxList + ComboboxSelectedIndex shape if present.
                var listEl = root.Element("ComboboxList");
                if (listEl != null)
                {
                    var items = listEl.Elements("Item").Select(e => (string)e).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                    int idx = ParseIntOr(root.Element("ComboboxSelectedIndex"), 0);
                    if (items.Count > 0 && idx >= 0 && idx < items.Count) IntervalText = items[idx];
                }

                CleanupEnabled = ParseBool(root.Element("AutoCleanupOldBackups"));
                RecycleEnabled = CleanupEnabled && ParseBool(root.Element("RemovedToRecycleBin"));
                BackupOnSaveEnabled = ParseBool(root.Element("BackupOnSaveChange"));
                MinimizeToTray = ParseBool(root.Element("CheckboxTray"));
                UseRandomName = !ParseBool(root.Element("BackupFileName2")); // BackupFileName2 = time-stamped; default random
                WindowWidth = ParseIntOr(root.Element("WindowWidth"), 0);
                WindowHeight = ParseIntOr(root.Element("WindowHeight"), 0);

                // Backup hotkey (nested <Hotkey> block + CheckboxHot), shared with the WinForms shape.
                var hk = root.Element("Hotkey");
                if (hk != null)
                {
                    HotkeyCtrl = ParseBool(hk.Element("ControlPressed"));
                    HotkeyShift = ParseBool(hk.Element("ShiftPressed"));
                    HotkeyAlt = ParseBool(hk.Element("AltPressed"));
                    HotkeyVk = KeyNameToVk((string)hk.Element("MainKey"));
                    HotkeyDisplay = BuildHotkeyDisplay();
                }
                HotkeyEnabled = ParseBool(root.Element("CheckboxHot")) && HotkeyVk != 0;

                AutobackupEnabled = ParseBool(root.Element("CheckboxAuto"));
            }
            catch { /* best-effort: defaults stand */ }
        }

        private static bool ParseBool(System.Xml.Linq.XElement el)
            => el != null && bool.TryParse(el.Value, out bool b) && b;

        private static int ParseIntOr(System.Xml.Linq.XElement el, int fallback)
            => (el != null && int.TryParse(el.Value, out int n)) ? n : fallback;

        // Persist the folders + autobackup settings into the same element shape the WinForms app uses, preserving any
        // other elements already present (window size, theme, hotkey, ...). Best-effort: a write failure never breaks
        // the workflow.
        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                System.Xml.Linq.XDocument doc;
                if (File.Exists(SettingsFilePath))
                {
                    try { doc = System.Xml.Linq.XDocument.Load(SettingsFilePath); }
                    catch { doc = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("AppSettings")); }
                }
                else
                {
                    doc = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("AppSettings"));
                }

                var root = doc.Root ?? new System.Xml.Linq.XElement("AppSettings");
                if (doc.Root == null) doc.Add(root);

                SetElement(root, "Textbox1", SaveDir ?? "");
                SetElement(root, "Textbox2", BackupDir ?? "");
                SetElement(root, "AutoBackupLimit", MaxBackupsText ?? "");
                SetElement(root, "CheckboxAuto", AutobackupEnabled.ToString());
                SetElement(root, "AutoCleanupOldBackups", CleanupEnabled.ToString());
                SetElement(root, "RemovedToRecycleBin", RecycleEnabled.ToString());
                SetElement(root, "BackupOnSaveChange", BackupOnSaveEnabled.ToString());
                SetElement(root, "CheckboxTray", MinimizeToTray.ToString());
                SetElement(root, "BackupFileName1", UseRandomName.ToString());
                SetElement(root, "BackupFileName2", (!UseRandomName).ToString());
                if (WindowWidth > 0) SetElement(root, "WindowWidth", WindowWidth.ToString());
                if (WindowHeight > 0) SetElement(root, "WindowHeight", WindowHeight.ToString());

                // Backup hotkey (nested <Hotkey> block + CheckboxHot + the display string in Textbox3).
                var hk = root.Element("Hotkey");
                if (hk == null) { hk = new System.Xml.Linq.XElement("Hotkey"); root.Add(hk); }
                SetElement(hk, "MainKey", VkToKeyName(HotkeyVk));
                SetElement(hk, "ControlPressed", HotkeyCtrl.ToString());
                SetElement(hk, "ShiftPressed", HotkeyShift.ToString());
                SetElement(hk, "AltPressed", HotkeyAlt.ToString());
                SetElement(root, "CheckboxHot", HotkeyEnabled.ToString());
                SetElement(root, "Textbox3", HotkeyDisplay ?? "");

                SetIntervalElements(root);

                doc.Save(SettingsFilePath);
            }
            catch { /* best-effort persistence */ }
        }

        // Write the chosen interval back in the WinForms ComboboxList + ComboboxSelectedIndex shape so the two apps
        // stay file-compatible. The list is the current preset set (with the chosen value ensured present).
        private void SetIntervalElements(System.Xml.Linq.XElement root)
        {
            var items = IntervalOptions.ToList();
            if (!string.IsNullOrWhiteSpace(IntervalText) && !items.Contains(IntervalText)) items.Insert(0, IntervalText);
            int idx = Math.Max(0, items.IndexOf(IntervalText ?? ""));

            var listEl = root.Element("ComboboxList");
            if (listEl == null) { listEl = new System.Xml.Linq.XElement("ComboboxList"); root.Add(listEl); }
            listEl.RemoveNodes();
            foreach (string s in items) listEl.Add(new System.Xml.Linq.XElement("Item", s));
            SetElement(root, "ComboboxSelectedIndex", idx.ToString());
        }

        private static void SetElement(System.Xml.Linq.XElement root, string name, string value)
        {
            var el = root.Element(name);
            if (el == null) root.Add(new System.Xml.Linq.XElement(name, value));
            else el.Value = value;
        }

        // ================= autobackup enable / config hooks =================

        // Re-engage a saved-on autobackup at startup, quietly (no modal prompts): if DD2 is missing or the folders /
        // interval / limit are not valid, just leave it off.
        private void EngageAutobackupIfEnabled()
        {
            if (!AutobackupEnabled) return;
            if (_autobackup.NoGame || !ValidateAutobackupDirectories(silent: true))
            {
                // Saved-on autobackup can't engage (DD2 missing or the folders/interval/limit are no longer valid).
                // Turn it off WITHOUT re-running the enable hooks, and persist so the on-disk CheckboxAuto matches
                // the UI (otherwise the shared settings file would keep claiming autobackup is on).
                _inEnableChange = true;
                try { AutobackupEnabled = false; }
                finally { _inEnableChange = false; }
                SaveSettings();
                return;
            }
            _autobackup.OnEnableOrConfigChanged();
        }

        partial void OnAutobackupEnabledChanged(bool value)
        {
            if (!_loaded || _inEnableChange) return;
            _inEnableChange = true;
            try
            {
                if (value)
                {
                    if (_autobackup.NoGame)
                    {
                        _dialog.Warn("Autobackup Unavailable",
                            "Dragon's Dogma 2 appears to be missing from your Steam library. Autobackup works with " +
                            "the Steam version of the game and can't run without it.");
                        AutobackupEnabled = false;
                        return;
                    }
                    if (!ValidateAutobackupDirectories())
                    {
                        AutobackupEnabled = false;
                        return;
                    }
                    _autobackup.OnEnableOrConfigChanged();
                }
                else
                {
                    _autobackup.OnEnableOrConfigChanged();
                    _status.Set("Autobackup disabled.");
                }
                SaveSettings();
            }
            finally { _inEnableChange = false; }
        }

        partial void OnIntervalTextChanged(string value)
        {
            if (!_loaded) return;
            SaveSettings();
            _autobackup.ApplyIntervalChange();
        }

        partial void OnMaxBackupsTextChanged(string value)
        {
            if (!_loaded) return;
            SaveSettings();
        }

        partial void OnCleanupEnabledChanged(bool value)
        {
            if (!_loaded) return;
            if (!value && RecycleEnabled) RecycleEnabled = false; // recycle only applies when cleanup is on
            SaveSettings();
        }

        partial void OnRecycleEnabledChanged(bool value)
        {
            if (!_loaded) return;
            SaveSettings();
        }

        partial void OnBackupOnSaveEnabledChanged(bool value)
        {
            if (!_loaded) return;
            SaveSettings();
            _autobackup.OnBackupOnSaveChanged();
        }

        partial void OnMinimizeToTrayChanged(bool value)
        {
            if (!_loaded) return;
            SaveSettings();
        }

        // Directory + interval + limit validity for enabling autobackup. silent = startup re-engage (no prompts).
        private bool ValidateAutobackupDirectories(bool silent = false)
        {
            if (string.IsNullOrWhiteSpace(SaveDir) || !Directory.Exists(SaveDir))
            {
                if (!silent) _dialog.Error("Error", "Please select a valid Savegame location first.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(BackupDir))
            {
                if (!silent) _dialog.Error("Error", "Please select a Backup location.");
                return false;
            }
            if (SaveDir.Equals(BackupDir, StringComparison.OrdinalIgnoreCase))
            {
                if (!silent) _dialog.Error("Error", "The Savegame and Backup locations cannot be the same.");
                return false;
            }
            if (!Directory.Exists(BackupDir))
            {
                if (silent) return false;
                if (!_dialog.Confirm("Create Directory", "The backup location does not exist. Create it?")) return false;
                try { Directory.CreateDirectory(BackupDir); }
                catch (Exception ex) { _dialog.Error("Error", "Could not create the backup location: " + ex.Message); return false; }
            }
            if (!IntervalValid)
            {
                if (!silent) _dialog.Error("Error", "Please choose an autobackup interval of at least 5 minutes.");
                return false;
            }
            if (!MaxAutobackupsValid)
            {
                if (!silent) _dialog.Error("Error", "Please enter a valid whole number for the autobackup limit.");
                return false;
            }
            return true;
        }

        // ================= backup list =================

        // Rebuild the Backups list from the *.zip files in BackupDir, newest first, exactly like the WinForms
        // LoadBackupHistory: open each zip, "Protected" if it carries a manifest else "Legacy", friendly creation
        // time, pin state from the file name. Unreadable files are skipped silently (no per-file dialogs).
        public void RefreshBackups()
        {
            Backups.Clear();

            string dir = BackupDir;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                BackupCount = "0";
                SelectedBackup = null;
                // No valid backup folder -> the on-disk autobackup count is zero. Keep the count file honest so a
                // later re-point doesn't inherit a stale count.
                AutobackupCountStore.Write(CountFilePath, 0);
                return;
            }

            string[] zipFiles;
            try { zipFiles = Directory.GetFiles(dir, "*.zip"); }
            catch { zipFiles = new string[0]; }

            Array.Sort(zipFiles, (x, y) => File.GetCreationTime(y).CompareTo(File.GetCreationTime(x)));

            foreach (string path in zipFiles)
            {
                try
                {
                    bool hasManifest;
                    using (var zip = Ionic.Zip.ZipFile.Read(path))
                    {
                        hasManifest = zip.Entries.Any(e => Manifest.IsManifestEntry(e.FileName));
                    }

                    string name = Path.GetFileName(path);
                    Backups.Add(new BackupRow
                    {
                        FileName = name,
                        FriendlyTime = TimeText.Friendly(File.GetCreationTime(path)),
                        Status = hasManifest ? "Protected" : "Legacy",
                        IsPinned = Pinning.IsPinnedBackup(name),
                        FullPath = path
                    });
                }
                catch (Ionic.Zip.ZipException) { /* not a readable zip — skip */ }
                catch (Exception) { /* locked / in use — skip silently */ }
            }

            BackupCount = Backups.Count.ToString();

            // Recompute and persist the autobackup count (non-pinned "(Auto)"/"auto" zips) so the "Keep at most N"
            // limit is enforced against the real files on disk. This is the writer the autobackup engine reads back;
            // without it the limit never triggers. Mirrors WinForms LoadBackupHistory.
            AutobackupCountStore.RecomputeAndWrite(dir, CountFilePath);
        }

        // ================= folder pickers =================

        [RelayCommand]
        private void BrowseSave()
        {
            string picked = PickFolder(SaveDir);
            if (picked == null) return;
            if (!string.IsNullOrWhiteSpace(BackupDir) && string.Equals(picked, BackupDir, StringComparison.OrdinalIgnoreCase))
            {
                _dialog.Error("Save location", "Your save folder can't be the same as your backup folder.");
                return;
            }
            SaveDir = picked;
            SaveSettings();
            RefreshBackups();
        }

        [RelayCommand]
        private void BrowseBackup()
        {
            string picked = PickFolder(BackupDir);
            if (picked == null) return;
            // Validate against the save folder + surface the cloud / same-drive / inside-save advisory (Core).
            if (!string.IsNullOrWhiteSpace(SaveDir))
            {
                if (string.Equals(picked, SaveDir, StringComparison.OrdinalIgnoreCase))
                {
                    _dialog.Error("Backup location", "The backup folder can't be the same as your save folder.");
                    return;
                }
                string warning = SaveScan.BackupLocationWarning(SaveDir, picked);
                if (warning != null && !_dialog.Confirm("Backup location", warning + "\n\nUse this folder anyway?"))
                    return;
            }
            BackupDir = picked;
            SaveSettings();
            RefreshBackups();
        }

        private static string PickFolder(string current)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                    dlg.SelectedPath = current;
                return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        // ================= workflow actions =================

        [RelayCommand]
        private void Backup()
        {
            if (_isOperationInProgress) { _status.Set("Please wait, another operation is already running."); return; }
            _isOperationInProgress = true;
            try
            {
                BackupResult r = BackupService.Backup(
                    new BackupRequest { LiveSaveDir = SaveDir, BackupDir = BackupDir, IsAutoBackup = false, RandomName = UseRandomName },
                    _dialog, _status);
                if (r.Ok) _sound.Success(); else _sound.Error();
            }
            finally { _isOperationInProgress = false; }

            _autobackup.NotifyExternalBackup(); // advance the change-aware baseline so autobackup doesn't re-capture
            RefreshBackups();
        }

        [RelayCommand]
        private void Restore()
        {
            if (SelectedBackup == null)
            {
                _dialog.Warn("Restore", "Please select a backup to restore first.");
                return;
            }
            DoRestore(SelectedBackup.FullPath);
        }

        // The shared restore path (used by Restore and Undo-last-restore): runs the full transactional restore against
        // a specific backup zip, holding the operation lock + suppressing the save watcher, and advances the
        // change-aware baseline on success.
        private void DoRestore(string backupZipPath)
        {
            if (_isOperationInProgress) { _status.Set("Please wait, another operation is already running."); return; }

            RestoreResult result = null;
            _isOperationInProgress = true;
            _autobackup.SuppressSaveWatcher = true; // the restore writes into the save folder — don't auto-back that up
            try
            {
                result = RestoreService.Restore(
                    new RestoreRequest
                    {
                        BackupZipPath = backupZipPath,
                        LiveSaveDir = SaveDir,
                        BackupDir = BackupDir,
                        GameRunning = GameDetect.IsDd2Running()
                    },
                    _dialog, _status);
            }
            finally
            {
                _isOperationInProgress = false;
                _autobackup.SuppressSaveWatcher = false;
            }
            // On a SUCCESSFUL restore, advance the change-aware baseline to the now-restored save (mirrors the WinForms
            // reference, which set _lastAutoBackupFingerprint after a successful RestoreTransactional). SuppressSaveWatcher
            // is cleared in the finally above, so a trailing FileSystemWatcher event from the restore's own writes can
            // still arrive and, ~4s later, run the change-aware gate; with the baseline now equal to the restored
            // content that gate resolves to SkipNoChange instead of capturing a redundant autobackup. Gated on Ok so the
            // baseline only moves when the save actually changed (on failure the restore rolled back to the original).
            if (result != null && result.Ok) _autobackup.NotifyExternalRestore();
            RefreshBackups();
        }

        // ----- File / Help menu commands -----

        // Roll the live save back to the automatic snapshot taken just before the last restore. Re-runs the full
        // restore flow against that checkpoint (so the undo is itself snapshotted, and the Steam-Cloud guidance shows).
        [RelayCommand]
        private void UndoLastRestore()
        {
            if (string.IsNullOrWhiteSpace(BackupDir) || !Directory.Exists(BackupDir))
            {
                _dialog.Info("Undo last restore", "Please set your backup folder first.");
                return;
            }
            string checkpoint = SaveScan.FindLatestPreRestoreCheckpoint(BackupDir);
            if (checkpoint == null)
            {
                _dialog.Info("Nothing to undo",
                    "There is no snapshot to undo. Savedrake automatically saves a snapshot of your current save each " +
                    "time you restore a backup, so an undo only becomes available after a restore.");
                return;
            }
            string when = Path.GetFileNameWithoutExtension(checkpoint).Replace("(Pre-Restore)", "").Trim();
            if (!_dialog.Confirm("Undo last restore",
                    "This rolls your save back to the snapshot Savedrake took just before your last restore" +
                    (when.Length > 0 ? " (" + when + ")" : "") + ".\n\n" +
                    "Your current save is snapshotted first, so you can redo.\n\nUndo the last restore?"))
                return;
            DoRestore(checkpoint);
        }

        // Auto-find the DD2 save folder via Steam and offer to set it. Only changes the folder on a Yes.
        [RelayCommand]
        private void DetectSaveFolder()
        {
            if (!ConfigEditable)
            {
                _dialog.Info("Detect save folder", "Turn off autobackup before changing the save folder.");
                return;
            }
            List<string> found;
            try { found = SaveScan.FindDd2SaveFolders(); } catch { found = new List<string>(); }
            if (found.Count == 0)
            {
                _dialog.Info("Detect save folder",
                    "Savedrake could not find a Dragon's Dogma 2 save folder automatically. Make sure Steam is installed " +
                    "and you have run the game at least once, or set the folder with Browse.");
                return;
            }
            string best = found[0];
            string extra = found.Count > 1
                ? "\n\n(" + (found.Count - 1) + " other Steam profile" + (found.Count > 2 ? "s were" : " was") +
                  " also found; this is the most recently used.)"
                : "";
            if (_dialog.Confirm("Detect save folder", "Found your Dragon's Dogma 2 saves here:\n\n" + best + extra + "\n\nUse this folder?"))
            {
                SaveDir = best;
                SaveSettings();
                RefreshBackups();
                _status.Set("Save game folder set automatically.");
            }
        }

        [RelayCommand]
        private void OpenFaq()
        {
            if (_dialog.Confirm("Open FAQ", "This will open the FAQ page on Nexusmods in your browser. Continue?"))
                OpenUrl("https://www.nexusmods.com/dragonsdogma2/mods/772/?tab=posts");
        }

        [RelayCommand]
        private void OpenAbout()
        {
            if (_dialog.Confirm("Open About", "This will open Savedrake's Nexusmods page in your browser. Continue?"))
                OpenUrl("https://www.nexusmods.com/dragonsdogma2/mods/772?tab=description");
        }

        private void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(url); }
            catch (Exception ex) { _dialog.Error("Open Link", "Could not open the link: " + ex.Message); }
        }

        [RelayCommand]
        private void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(Log.Directory());
                System.Diagnostics.Process.Start("explorer.exe", Log.Directory());
            }
            catch (Exception ex) { _dialog.Error("Open log folder", "Could not open the log folder: " + ex.Message); }
        }

        [RelayCommand]
        private void Exit() => System.Windows.Application.Current?.Shutdown();

        // ----- backup hotkey (state lives here + persists; the window does the Win32 RegisterHotKey) -----

        public bool HotkeyCtrl { get; private set; }
        public bool HotkeyShift { get; private set; }
        public bool HotkeyAlt { get; private set; }
        public int HotkeyVk { get; private set; }
        public bool HotkeyEnabled { get; private set; }
        public string HotkeyDisplay { get; private set; }

        public void SetHotkey(bool ctrl, bool shift, bool alt, int vk, string display)
        {
            HotkeyCtrl = ctrl; HotkeyShift = shift; HotkeyAlt = alt; HotkeyVk = vk;
            HotkeyDisplay = display; HotkeyEnabled = true;
            SaveSettings();
            _status.Set("Backup hotkey set to " + display + ".");
        }

        public void ClearHotkey()
        {
            HotkeyCtrl = HotkeyShift = HotkeyAlt = false; HotkeyVk = 0;
            HotkeyDisplay = ""; HotkeyEnabled = false;
            SaveSettings();
            _status.Set("Backup hotkey cleared.");
        }

        private static int KeyNameToVk(string name)
            => !string.IsNullOrWhiteSpace(name) && System.Enum.TryParse(name, out System.Windows.Input.Key k)
               ? System.Windows.Input.KeyInterop.VirtualKeyFromKey(k) : 0;

        private static string VkToKeyName(int vk)
            => vk != 0 ? System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk).ToString() : "";

        private string BuildHotkeyDisplay()
        {
            if (HotkeyVk == 0) return "";
            var sb = new System.Text.StringBuilder();
            if (HotkeyCtrl) sb.Append("Ctrl + ");
            if (HotkeyShift) sb.Append("Shift + ");
            if (HotkeyAlt) sb.Append("Alt + ");
            sb.Append(VkToKeyName(HotkeyVk));
            return sb.ToString();
        }

        // ----- backup name format / window size / reset -----

        partial void OnUseRandomNameChanged(bool value) { if (_loaded) SaveSettings(); }

        [RelayCommand] private void NameFormatRandom() => UseRandomName = true;
        [RelayCommand] private void NameFormatTimestamp() => UseRandomName = false;

        // Persist the window size (called by the window on close so the next launch restores it).
        public void SaveWindowSize(int width, int height)
        {
            if (width > 0) WindowWidth = width;
            if (height > 0) WindowHeight = height;
            SaveSettings();
        }

        // Reset: stop the autobackup engine and delete the persisted state files; returns true if all were removed.
        // The caller then disposes the tray and Environment.Exit(0)s so nothing re-creates them. Disposing the
        // controller (rather than toggling AutobackupEnabled) avoids a SaveSettings that would re-create the file we
        // just deleted.
        public bool ResetState()
        {
            try { _autobackup?.Dispose(); } catch { }
            bool allDeleted = true;
            foreach (string path in new[] { SettingsFilePath, CountFilePath, VersionFilePath, UpdaterXmlPath })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { allDeleted = false; }
            }
            return allDeleted;
        }

        // Send the selected backup(s) to the Recycle Bin (recoverable), supporting multi-select. The selection is
        // passed from the ListView so the toolbar button, the context menu, and the Delete key all act on the same set.
        [RelayCommand]
        private void Delete(System.Collections.IList selected)
        {
            // Snapshot the selection now (the live SelectedItems collection mutates as the list rebuilds below).
            List<BackupRow> rows = (selected != null ? selected.Cast<BackupRow>()
                                    : (SelectedBackup != null ? new[] { SelectedBackup } : Enumerable.Empty<BackupRow>()))
                                   .Where(r => r != null).ToList();
            if (rows.Count == 0) { _dialog.Warn("Delete", "Please select one or more backups to delete first."); return; }
            if (_isOperationInProgress) { _status.Set("Please wait, another operation is already running."); return; }

            // Hold the operation lock across the whole delete, including the confirm dialog. The modal pumps the
            // Dispatcher queue, so without the lock a queued autobackup tick could rebuild the list mid-delete.
            _isOperationInProgress = true;
            try
            {
                string plural = rows.Count > 1 ? "s" : "";
                if (!_dialog.Confirm("Delete Backup" + plural,
                        "Send the selected backup" + plural + " to the Recycle Bin?\n\n" +
                        string.Join("\n", rows.Select(r => r.FileName))))
                    return;

                int deleted = 0;
                foreach (BackupRow row in rows)
                {
                    try
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(row.FullPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _dialog.Error("Delete Failed", "Could not delete " + row.FileName + ": " + ex.Message);
                    }
                }
                _status.Set(deleted == 1 ? "Backup sent to the Recycle Bin." : (deleted + " backups sent to the Recycle Bin."));
            }
            finally { _isOperationInProgress = false; }
            RefreshBackups();
        }

        // Rename the selected backup on disk (keeps the .zip extension; renaming a pinned backup drops the pin, matching
        // the shipped app). Single-select only.
        [RelayCommand]
        private void Rename()
        {
            BackupRow row = SelectedBackup;
            if (row == null) { _dialog.Warn("Rename", "Please select one backup to rename first."); return; }
            if (_isOperationInProgress) { _status.Set("Please wait, another operation is already running."); return; }

            string ext = Path.GetExtension(row.FileName);
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter a new name for this backup:", "Rename Backup", Path.GetFileNameWithoutExtension(row.FileName));
            if (string.IsNullOrWhiteSpace(input)) return; // cancelled / empty

            string newName = Path.GetFileNameWithoutExtension(input.Trim()) + ext;
            if (string.Equals(newName, row.FileName, StringComparison.Ordinal)) return;

            _isOperationInProgress = true;
            try
            {
                string target = BackupNaming.MakeUniquePath(Path.Combine(Path.GetDirectoryName(row.FullPath), newName));
                File.Move(row.FullPath, target);
                _status.Set("Backup renamed.");
            }
            catch (Exception ex) { _dialog.Error("Rename Failed", "Could not rename the backup: " + ex.Message); }
            finally { _isOperationInProgress = false; }
            RefreshBackups();
        }

        // Toggle the selected backup's pinned state (a " [PINNED]" filename token). Pinned backups are protected from
        // automatic cleanup and excluded from the autobackup limit. Single-select only.
        [RelayCommand]
        private void Pin()
        {
            BackupRow row = SelectedBackup;
            if (row == null) { _dialog.Warn("Pin backup", "Select one backup to pin or unpin."); return; }
            if (_isOperationInProgress) { _status.Set("Please wait, another operation is already running."); return; }

            _isOperationInProgress = true;
            try
            {
                if (!File.Exists(row.FullPath)) return;
                bool pinned = Pinning.IsPinnedBackup(Path.GetFileName(row.FullPath));
                string target = pinned ? Pinning.UnpinnedPath(row.FullPath) : Pinning.PinnedPath(row.FullPath);
                if (!string.Equals(target, row.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    target = BackupNaming.MakeUniquePath(target); // never clobber an existing backup
                    File.Move(row.FullPath, target);
                }
                _status.Set(pinned ? "Backup unpinned." : "Backup pinned. It won't be removed by automatic cleanup.");
            }
            catch (Exception ex) { _dialog.Error("Pin backup", "Could not change the pinned state: " + ex.Message); }
            finally { _isOperationInProgress = false; }
            RefreshBackups();
        }

        // Full integrity check of every backup: each row's Status is upgraded in place to Validated / Legacy / Corrupt
        // / Missing (the pill recolours live), with a tally and a warning if any failed.
        [RelayCommand]
        private void ValidateAll()
        {
            if (Backups.Count == 0) { _dialog.Info("Validate Backups", "There are no backups to validate."); return; }

            _status.Set("Validating backups...");
            int validated = 0, legacy = 0, failed = 0;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                foreach (BackupRow row in Backups)
                {
                    string state = File.Exists(row.FullPath) ? Manifest.ClassifyBackupFully(row.FullPath) : "Missing";
                    row.Status = state;
                    if (state == "Validated") validated++;
                    else if (state == "Legacy") legacy++;
                    else failed++;
                }
            }
            finally { System.Windows.Input.Mouse.OverrideCursor = null; }

            _status.Set("Validated " + Backups.Count + " backups: " + validated + " OK, " + legacy + " legacy, " + failed + " failed.");
            if (failed > 0)
                _dialog.Warn("Validation", failed + " backup(s) failed validation and may not be restorable.");
        }

        // Open the backup zip in the default handler (double-click on a row).
        [RelayCommand]
        private void OpenBackup(BackupRow row)
        {
            row = row ?? SelectedBackup;
            if (row == null || string.IsNullOrEmpty(row.FullPath)) return;
            try { System.Diagnostics.Process.Start(row.FullPath); }
            catch (Exception ex) { _dialog.Error("Open Backup", "Could not open the backup: " + ex.Message); }
        }

        public void Dispose() => _autobackup?.Dispose();

        // ================= WPF service implementations =================

        // Modal dialogs via System.Windows.MessageBox. Confirm uses YesNo (Yes == affirmative, matching the WinForms
        // DialogResult.Yes convention the Core services were transcribed against).
        private sealed class WpfDialogService : IDialogService
        {
            public bool Confirm(string title, string message)
                => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

            public void Info(string title, string message)
                => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

            public void Warn(string title, string message)
                => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

            public void Error(string title, string message)
                => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Routes each Core Status.Text write into the bound StatusText property, marshalled to the UI thread.
        private sealed class StatusSink : IStatusSink
        {
            private readonly MainViewModel _vm;
            public StatusSink(MainViewModel vm) { _vm = vm; }

            public void Set(string text)
            {
                Dispatcher d = Application.Current?.Dispatcher;
                if (d != null && !d.CheckAccess())
                    d.Invoke(() => _vm.StatusText = text);
                else
                    _vm.StatusText = text;
            }
        }
    }
}

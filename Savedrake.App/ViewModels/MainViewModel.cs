using System;
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
    // integrity status) plus the pin state, with the full path kept for Restore/Delete. Plain DTO — the list is
    // rebuilt wholesale by RefreshBackups, so no per-row change notification is needed.
    public sealed class BackupRow
    {
        public string FileName { get; set; }
        public string FriendlyTime { get; set; }
        public string Status { get; set; }   // "Protected" (has manifest) or "Legacy"
        public bool IsPinned { get; set; }
        public string FullPath { get; set; }
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

        // Preset intervals offered in the editable interval box (the box also accepts free text like "12 minutes").
        public ObservableCollection<string> IntervalOptions { get; } =
            new ObservableCollection<string> { "5 minutes", "15 minutes", "30 minutes", "1 hour", "2 hours" };

        public bool ConfigEditable => !AutobackupEnabled;

        // ----- Backups list -----

        public ObservableCollection<BackupRow> Backups { get; } = new ObservableCollection<BackupRow>();

        [ObservableProperty]
        private BackupRow selectedBackup;

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
                    new BackupRequest { LiveSaveDir = SaveDir, BackupDir = BackupDir, IsAutoBackup = true, RandomName = true },
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
            SaveDir = picked;
            SaveSettings();
            RefreshBackups();
        }

        [RelayCommand]
        private void BrowseBackup()
        {
            string picked = PickFolder(BackupDir);
            if (picked == null) return;
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
                    new BackupRequest { LiveSaveDir = SaveDir, BackupDir = BackupDir, IsAutoBackup = false, RandomName = true },
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
            if (_isOperationInProgress) { _status.Set("Please wait, another operation is already running."); return; }

            _isOperationInProgress = true;
            _autobackup.SuppressSaveWatcher = true; // the restore writes into the save folder — don't auto-back that up
            try
            {
                RestoreService.Restore(
                    new RestoreRequest
                    {
                        BackupZipPath = SelectedBackup.FullPath,
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
            // Re-baseline the change-aware fingerprint to the now-restored save so the next autobackup tick doesn't
            // immediately re-capture it (and any trailing watcher event for the restore's own writes finds no change).
            _autobackup.NotifyExternalBackup();
            RefreshBackups();
        }

        [RelayCommand]
        private void Delete()
        {
            if (SelectedBackup == null)
            {
                _dialog.Warn("Delete", "Please select a backup to delete first.");
                return;
            }
            if (_isOperationInProgress) { _status.Set("Please wait, another operation is already running."); return; }

            BackupRow row = SelectedBackup;

            // Hold the operation lock across the whole delete, including the confirm dialog. The modal pumps the
            // Dispatcher queue, so without the lock a queued autobackup tick could fire and rebuild the list (nulling
            // SelectedBackup) mid-delete.
            _isOperationInProgress = true;
            try
            {
                if (!_dialog.Confirm("Delete Backup", "Delete this backup permanently?\n\n" + row.FileName))
                    return;
                try
                {
                    File.Delete(row.FullPath);
                    _status.Set("Backup deleted.");
                }
                catch (Exception ex)
                {
                    _dialog.Error("Delete Failed", "Could not delete the backup: " + ex.Message);
                }
            }
            finally { _isOperationInProgress = false; }
            RefreshBackups();
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

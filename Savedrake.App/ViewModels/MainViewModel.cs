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
        private bool _setupComplete;        // persisted "user finished first-run setup"; see NeedsSetup

        // ----- Folders -----

        [ObservableProperty]
        private string saveDir;

        [ObservableProperty]
        private string backupDir;

        // The active character: the subfolder of BackupDir whose backups are shown and where new backups land. DD2 has
        // one save slot, so a "character" is a named backup history. Persisted; defaults to "Default".
        [ObservableProperty]
        private string activeCharacter = CharacterFolder.Default;

        // IsConfigured/NeedsSetup also depend on Directory.Exists, which can change while the path string does not
        // (folder deleted in Explorer, drive disconnected). During first-run that window is narrow; these hooks
        // re-raise on path-string change, which is sufficient for the welcome panel to update live. BackupDir also
        // feeds the per-character routing, so it re-raises those computed paths too.
        partial void OnSaveDirChanged(string value)
        { OnPropertyChanged(nameof(NeedsSetup)); OnPropertyChanged(nameof(IsConfigured)); }

        partial void OnBackupDirChanged(string value)
        {
            OnPropertyChanged(nameof(NeedsSetup)); OnPropertyChanged(nameof(IsConfigured));
            OnPropertyChanged(nameof(EffectiveBackupDir)); OnPropertyChanged(nameof(CountFilePath));
            OnPropertyChanged(nameof(BackupsTitle));
        }

        partial void OnActiveCharacterChanged(string value)
        {
            OnPropertyChanged(nameof(EffectiveBackupDir)); OnPropertyChanged(nameof(CountFilePath));
            OnPropertyChanged(nameof(BackupsTitle));
        }

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

        // Take one final backup when DD2 closes (the safest capture moment — the game has released the save file).
        // Default on: it is the trigger every leading manager uses by default, and it is change-gated so it never
        // duplicates a state already captured during the session.
        [ObservableProperty]
        private bool backupOnGameCloseEnabled = true;

        // ----- Collapsible config sections -----
        // The Folders and Autobackup cards fold to just their title so the Backups list (the thing you actually work
        // with) can fill the window. Default expanded so a first run shows everything; the choice is persisted. The
        // caret string flips between "open" (▾) and "closed" (▸) for the clickable header.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FoldersCaret))]
        private bool foldersExpanded = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AutobackupSectionCaret))]
        private bool autobackupSectionExpanded = true;

        public string FoldersCaret => FoldersExpanded ? "▾" : "▸";
        public string AutobackupSectionCaret => AutobackupSectionExpanded ? "▾" : "▸";

        // When on, minimizing hides the window to the system tray instead of the taskbar (persisted as CheckboxTray).
        [ObservableProperty]
        private bool minimizeToTray;

        // Backup file-name format: randomly generated (default) vs time-stamped (persisted as BackupFileName1/2).
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UseTimestampName))]
        private bool useRandomName = true;

        public bool UseTimestampName => !UseRandomName;

        // Light vs dark theme (persisted as ThemeMode; default dark). ThemeMenuLabel drives the toggle's caption.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ThemeMenuLabel))]
        private bool isLightTheme;

        public string ThemeMenuLabel => IsLightTheme ? "Use dark theme" : "Use light theme";

        public void SetTheme(bool light) { IsLightTheme = light; SaveSettings(); }

        // Last window size, restored on next launch (persisted as WindowWidth/WindowHeight). 0 = use the default.
        public int WindowWidth { get; private set; }
        public int WindowHeight { get; private set; }

        // Preset intervals offered in the editable interval box (the box also accepts free text like "12 minutes").
        public ObservableCollection<string> IntervalOptions { get; } =
            new ObservableCollection<string> { "5 minutes", "15 minutes", "30 minutes", "1 hour", "2 hours" };

        public bool ConfigEditable => !AutobackupEnabled;

        // Both folders point at real, existing directories. This is "ready to use".
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(SaveDir) && Directory.Exists(SaveDir) &&
            !string.IsNullOrWhiteSpace(BackupDir) && Directory.Exists(BackupDir);

        // First-run welcome shows only when setup was never completed AND folders aren't already usable.
        // An existing user (folders already in savedrake_settings.xml) is IsConfigured == true, so this is false for
        // them on the very first launch of this build, before the flag is ever written.
        public bool NeedsSetup => !_setupComplete && !IsConfigured;

        // The shipped version shown by the wordmark, from the assembly version (single source of truth, drives the
        // update check too) — so a version bump updates the UI automatically instead of via a hardcoded string.
        public string AppVersion
        {
            get
            {
                Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v != null ? v.Major + "." + v.Minor + "." + v.Build : "1.4.0";
            }
        }

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
            // One-time, non-destructive migration to the folder-per-character layout: move any backups loose directly
            // under the Backups folder into a "Default" character. Move-only, idempotent, resumable; a no-op once done
            // or on first run (no folder yet). Runs before the first RefreshBackups so the list is already per-character.
            var legacyCount = Path.Combine(AppDataDir, "count_of_autobackups.txt");
            var migration = CharacterMigration.MigrateLooseToDefault(BackupDir, legacyCount);
            if (migration.Ran) { ActiveCharacter = CharacterFolder.Default; SaveSettings(); }
            EnsureEffectiveDirExists();
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

        // BackupDir is the parent folder the user picked; EffectiveBackupDir is the ACTIVE CHARACTER's subfolder, which
        // is where that character's backups actually live. Every Core call routes through EffectiveBackupDir so each
        // character keeps its own history. The empty-guard returns BackupDir unchanged on first run so every existing
        // string.IsNullOrWhiteSpace(BackupDir) check keeps firing exactly as before.
        public string EffectiveBackupDir =>
            string.IsNullOrWhiteSpace(BackupDir)
                ? BackupDir
                : Path.Combine(BackupDir, CharacterFolder.SafeName(ActiveCharacter));

        // The autobackup count cache lives inside the active character's folder (per-character retention). On first run
        // (no BackupDir yet) it falls back to AppData, which is harmless since nothing reads it until a backup runs.
        public string CountFilePath =>
            string.IsNullOrWhiteSpace(BackupDir)
                ? Path.Combine(AppDataDir, "count_of_autobackups.txt")
                : Path.Combine(EffectiveBackupDir, "count_of_autobackups.txt");

        // The Backups card title shows which character you are managing.
        public string BackupsTitle => "Backups — " + CharacterFolder.SafeName(ActiveCharacter);

        // Make sure the active character's backup folder exists before a backup/restore writes into it. Returns false
        // (never throws) if there is no parent yet or it cannot be created; callers decide how to surface that (a modal
        // for a manual action, a status line for the autobackup engine) so a valid parent is never mistaken for "no
        // backup location selected" by BackupService.
        private bool EnsureEffectiveDirExists()
        {
            if (string.IsNullOrWhiteSpace(BackupDir)) return false;
            try { Directory.CreateDirectory(EffectiveBackupDir); return true; }
            catch { return false; }
        }

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
                if (!EnsureEffectiveDirExists()) { _status.Set("Autobackup paused: backup folder unavailable."); return false; }
                BackupResult r = BackupService.Backup(
                    new BackupRequest { LiveSaveDir = SaveDir, BackupDir = EffectiveBackupDir, IsAutoBackup = true, RandomName = UseRandomName },
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

                // Active character: clamp an absent/garbled value to "Default" (IsValidName null-guards first).
                var acEl = root.Element("ActiveCharacter");
                string ac = acEl == null ? null : (string)acEl;
                ActiveCharacter = CharacterFolder.IsValidName(ac) ? ac.Trim() : CharacterFolder.Default;

                _setupComplete = ParseBool(root.Element("SetupComplete"));
                // Self-heal: an existing user with folders already set but no flag is treated as already set up.
                // NOTE: this intentionally checks the path STRINGS only, not Directory.Exists. It covers the existing
                // user whose backup drive is disconnected at launch (folders set, dir missing -> IsConfigured == false);
                // the string-only flag keeps them out of first-run. Do NOT make this existence-based or that user gets
                // re-onboarded.
                if (!_setupComplete &&
                    !string.IsNullOrWhiteSpace(SaveDir) && !string.IsNullOrWhiteSpace(BackupDir))
                    _setupComplete = true;

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
                BackupOnGameCloseEnabled = ParseBoolOr(root.Element("BackupOnGameClose"), true);
                MinimizeToTray = ParseBool(root.Element("CheckboxTray"));
                UseRandomName = !ParseBool(root.Element("BackupFileName2")); // BackupFileName2 = time-stamped; default random
                IsLightTheme = string.Equals((string)root.Element("ThemeMode"), "Light", StringComparison.OrdinalIgnoreCase);
                WindowWidth = ParseIntOr(root.Element("WindowWidth"), 0);
                WindowHeight = ParseIntOr(root.Element("WindowHeight"), 0);
                FoldersExpanded = ParseBoolOr(root.Element("FoldersExpanded"), true);
                AutobackupSectionExpanded = ParseBoolOr(root.Element("AutobackupSectionExpanded"), true);

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

        // Like ParseBool but returns the given default when the element is missing or unparseable (so a setting that
        // defaults to true, e.g. a card starting expanded, is not forced false just by being absent on first run).
        private static bool ParseBoolOr(System.Xml.Linq.XElement el, bool fallback)
            => (el != null && bool.TryParse(el.Value, out bool b)) ? b : fallback;

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
                SetElement(root, "ActiveCharacter", string.IsNullOrWhiteSpace(ActiveCharacter) ? CharacterFolder.Default : ActiveCharacter);
                SetElement(root, "AutoBackupLimit", MaxBackupsText ?? "");
                SetElement(root, "CheckboxAuto", AutobackupEnabled.ToString());
                SetElement(root, "AutoCleanupOldBackups", CleanupEnabled.ToString());
                SetElement(root, "RemovedToRecycleBin", RecycleEnabled.ToString());
                SetElement(root, "BackupOnSaveChange", BackupOnSaveEnabled.ToString());
                SetElement(root, "BackupOnGameClose", BackupOnGameCloseEnabled.ToString());
                SetElement(root, "CheckboxTray", MinimizeToTray.ToString());
                SetElement(root, "BackupFileName1", UseRandomName.ToString());
                SetElement(root, "BackupFileName2", (!UseRandomName).ToString());
                SetElement(root, "ThemeMode", IsLightTheme ? "Light" : "Dark");
                if (WindowWidth > 0) SetElement(root, "WindowWidth", WindowWidth.ToString());
                if (WindowHeight > 0) SetElement(root, "WindowHeight", WindowHeight.ToString());
                SetElement(root, "FoldersExpanded", FoldersExpanded.ToString());
                SetElement(root, "AutobackupSectionExpanded", AutobackupSectionExpanded.ToString());
                SetElement(root, "SetupComplete", _setupComplete.ToString());

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
            // Turning automatic cleanup ON enables automatic removal of old autobackups, so ask first (parity with the
            // shipped app). The revert re-enters with value=false, which persists and skips this confirm.
            if (value && !_dialog.Confirm("Automatically clean up old autobackups",
                    "Savedrake will keep your recent autobackups and a spread of older ones, then remove the extra " +
                    "older autobackups so they don't pile up. Your manual backups and pinned backups are never " +
                    "removed.\n\nTurn this on?"))
            {
                CleanupEnabled = false;
                return;
            }
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

        // No live controller call: this only changes what happens the next time DD2 closes, so persisting is enough.
        partial void OnBackupOnGameCloseEnabledChanged(bool value)
        {
            if (!_loaded) return;
            SaveSettings();
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
            // The active character's subfolder is where autobackups actually land; make sure it exists too.
            if (!EnsureEffectiveDirExists())
            {
                if (!silent) _dialog.Error("Error", "Could not create this character's backup folder under the backup location.");
                return false;
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

            string dir = EffectiveBackupDir;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                BackupCount = "0";
                SelectedBackup = null;
                // No valid (character) backup folder yet -> nothing to count. AutobackupCountStore.Read already returns
                // 0 for a missing count file, so there is nothing to write here (and the dir may not exist yet).
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
            // without it the limit never triggers. Mirrors WinForms LoadBackupHistory. Ensure the character folder
            // exists first so the per-character cache actually lands.
            EnsureEffectiveDirExists();
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
            // Advisory (parity): warn when the chosen folder isn't the default DD2 save layout. Not blocking — the
            // backup itself still guards on real save data.
            string p = picked.TrimEnd('\\', '/');
            if (!p.EndsWith(@"\2054970\remote\win64_save", StringComparison.OrdinalIgnoreCase) &&
                !_dialog.Confirm("Non-Default Path Selected",
                    "The selected folder is not the default Dragon's Dogma 2 save folder (see Help). Use this folder anyway?"))
                return;
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
            BackupResult result = null;
            _isOperationInProgress = true;
            try
            {
                if (!EnsureEffectiveDirExists())
                {
                    _dialog.Error("Backup", "Savedrake could not create this character's backup folder. Check the Backups location.");
                }
                else
                {
                    result = BackupService.Backup(
                        new BackupRequest { LiveSaveDir = SaveDir, BackupDir = EffectiveBackupDir, IsAutoBackup = false, RandomName = UseRandomName },
                        _dialog, _status);
                    if (result.Ok) _sound.Success(); else _sound.Error();
                }
            }
            finally { _isOperationInProgress = false; }

            // Advance the change-aware baseline ONLY on a successful backup (mirrors the WinForms reference, which set
            // _lastAutoBackupFingerprint after the atomic publish, and matches the restore path). Advancing it on a
            // FAILED manual backup would make the next autobackup tick skip the un-backed-up save, leaving recent
            // progress unprotected until the next in-game save.
            if (result != null && result.Ok) _autobackup.NotifyExternalBackup();
            RefreshBackups();
        }

        [RelayCommand]
        private void Restore(System.Collections.IList selected)
        {
            // Restore is single-backup; with multi-select on, refuse rather than silently restoring the anchor.
            if (selected != null && selected.Count > 1)
            {
                _dialog.Warn("Restore", "Please select only one backup to restore at a time.");
                return;
            }
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
                if (!EnsureEffectiveDirExists())
                {
                    _dialog.Error("Restore", "Savedrake could not access this character's backup folder.");
                }
                else
                {
                    result = RestoreService.Restore(
                        new RestoreRequest
                        {
                            BackupZipPath = backupZipPath,
                            LiveSaveDir = SaveDir,
                            BackupDir = EffectiveBackupDir,
                            GameRunning = GameDetect.IsDd2Running()
                        },
                        _dialog, _status);
                }
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
            if (string.IsNullOrWhiteSpace(EffectiveBackupDir) || !Directory.Exists(EffectiveBackupDir))
            {
                _dialog.Info("Undo last restore", "Please set your backup folder first.");
                return;
            }
            string checkpoint = SaveScan.FindLatestPreRestoreCheckpoint(EffectiveBackupDir);
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

        // Case-insensitive so a WinForms-saved hotkey whose Keys enum name differs only in casing from the WPF Key
        // enum (e.g. "Oemplus" -> Key.OemPlus, "Oemcomma" -> Key.OemComma) still round-trips on the first WPF launch.
        private static int KeyNameToVk(string name)
            => !string.IsNullOrWhiteSpace(name) && System.Enum.TryParse(name, true, out System.Windows.Input.Key k)
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

        // Fold / unfold the two config cards (persisted so the choice sticks across launches).
        [RelayCommand] private void ToggleFolders() { FoldersExpanded = !FoldersExpanded; if (_loaded) SaveSettings(); }
        [RelayCommand] private void ToggleAutobackupSection() { AutobackupSectionExpanded = !AutobackupSectionExpanded; if (_loaded) SaveSettings(); }

        // ----- first-run setup (the welcome panel that replaces the Folders card on first launch) -----

        // The user set both folders in the welcome panel: mark setup done so the normal UI takes over and the panel
        // never returns. Guarded so it can't complete half-configured.
        [RelayCommand]
        private void CompleteSetup()
        {
            if (!IsConfigured)
            {
                _dialog.Info("Almost there", "Please set both your save folder and your backup folder first.");
                return;
            }
            _setupComplete = true;
            SaveSettings();
            OnPropertyChanged(nameof(NeedsSetup));
            _status.Set("Setup complete. You're ready to back up your saves.");
        }

        // "I'll do this later": dismiss the welcome panel without requiring both folders. IsConfigured stays false, so
        // the Autobackup/Backups cards remain disabled until the user sets folders via the normal Folders card's Browse
        // buttons.
        [RelayCommand]
        private void SkipSetup()
        {
            _setupComplete = true;
            SaveSettings();
            OnPropertyChanged(nameof(NeedsSetup));
            _status.Set("You can set your folders any time from the Folders card.");
        }

        // ----- characters (per-character backup folders) -----

        // Switch which character's backups are shown and where new backups land. NON-DESTRUCTIVE: it never touches the
        // live DD2 save. Creates the character's folder if needed, re-baselines the autobackup engine, and refreshes.
        [RelayCommand]
        private void SwitchCharacter(string name)
        {
            if (!CharacterFolder.IsValidName(name)) return;
            string safe = name.Trim();
            if (string.Equals(safe, ActiveCharacter, StringComparison.OrdinalIgnoreCase)) return;
            ActiveCharacter = safe;
            SaveSettings();
            if (!EnsureEffectiveDirExists())
            {
                _dialog.Error("Characters", "Savedrake could not create this character's backup folder.");
                return;
            }
            _autobackup.OnActiveCharacterChanged();
            RefreshBackups();
            _status.Set("Now managing " + safe + "'s backups.");
        }

        // Prompt for a new character name and switch to it (its folder is created on first use).
        [RelayCommand]
        private void NewCharacter()
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Name this character (for example, your in-game character's name). " +
                "Savedrake keeps each character's backups in its own folder.",
                "New character", "");
            if (string.IsNullOrWhiteSpace(input)) return;   // cancelled
            if (!CharacterFolder.IsValidName(input))
            {
                _dialog.Error("New character",
                    "That name can't be used for a folder. Try letters, numbers, spaces, and dashes (up to 40 characters).");
                return;
            }
            SwitchCharacter(input.Trim());
        }

        // Rename the active character (non-destructive: moves its folder, so all of its backups move with it).
        [RelayCommand]
        private void RenameCharacter()
        {
            if (string.IsNullOrWhiteSpace(BackupDir))
            {
                _dialog.Info("Rename character", "Please set your backup folder first.");
                return;
            }
            string current = CharacterFolder.SafeName(ActiveCharacter);
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Rename character \"" + current + "\". Its backups move with it.", "Rename character", current);
            if (string.IsNullOrWhiteSpace(input)) return;                                    // cancelled
            string target = input.Trim();
            if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase)) return;  // unchanged
            if (!CharacterFolder.IsValidName(target))
            {
                _dialog.Error("Rename character",
                    "That name can't be used for a folder. Try letters, numbers, spaces, and dashes (up to 40 characters).");
                return;
            }
            string from = Path.Combine(BackupDir, current);
            string to = Path.Combine(BackupDir, target);
            if (Directory.Exists(to))
            {
                _dialog.Error("Rename character", "A character named \"" + target + "\" already exists.");
                return;
            }
            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to);
                else Directory.CreateDirectory(to);   // current character has no folder yet (never backed up): adopt the new name
            }
            catch (Exception ex)
            {
                _dialog.Error("Rename character", "Could not rename this character: " + ex.Message);
                return;
            }
            ActiveCharacter = target;
            SaveSettings();
            _autobackup.OnActiveCharacterChanged();
            RefreshBackups();
            _status.Set("Renamed to " + target + ".");
        }

        // The characters available to switch to: each subfolder of the backup location, plus the active one (so a
        // just-created, still-empty character still shows). Count is "*.zip files" (includes checkpoints/manual zips).
        public IReadOnlyList<(string Name, int FileCount)> EnumerateCharacters()
        {
            var list = new List<(string Name, int FileCount)>();
            if (!string.IsNullOrWhiteSpace(BackupDir) && Directory.Exists(BackupDir))
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(BackupDir); } catch { dirs = new string[0]; }
                foreach (string d in dirs)
                {
                    string nm = Path.GetFileName(d);
                    // Skip a subfolder whose name we couldn't switch to anyway (e.g. an externally-created folder
                    // longer than the 40-char limit), so the menu never shows a row that does nothing when clicked.
                    if (!CharacterFolder.IsValidName(nm)) continue;
                    int n = 0; try { n = Directory.GetFiles(d, "*.zip").Length; } catch { }
                    list.Add((nm, n));
                }
            }
            if (!list.Any(x => string.Equals(x.Name, ActiveCharacter, StringComparison.OrdinalIgnoreCase)))
                list.Insert(0, (ActiveCharacter, 0));
            return list;
        }

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

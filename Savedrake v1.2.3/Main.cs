//using Ionic.Zip;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Newtonsoft.Json.Linq;
using Shell32;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Media;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using System.Xml.Serialization;



namespace Savedrake
{
    public partial class Main : Form
    {
        //Check only one instance is running using Mutex
        // Add a static mutex field
        private static Mutex mutex = new Mutex(true, "890f37b1-d5e4-4375-8790-d94bc4dced9f");


        //Loading bool
        private bool isLoading = false;

        //Autobackup feature related
        #region
        private System.Timers.Timer autobackupTimer; //(Autobackup feature)
        private bool _autoBackupInProgress; // R7a: re-entrancy guard for OnAutobackupTimerElapsed (UI-thread only)
        private bool _operationInProgress;  // operation lock: a backup or restore is mid-flight (UI-thread only)
        private bool isAutoBackupEnabled = false; //(Autobackup feature)
        private ManagementEventWatcher _watcher; //(Autobackup feature)
        private bool isGameRunning; //(Autobackup feature)
        // Change-aware autobackup (PR1): content fingerprint of the save folder at the last successful backup.
        // UI-thread-only (set/read only from the marshaled timer tick, BackupOperation, restore, and the checkbox
        // handler). null = no baseline yet, so the next eligible backup always fires.
        private string _lastAutoBackupFingerprint;
        // Change-aware autobackup, part 2 (clean up old backups): two opt-in toggles under Files > Settings, OFF by
        // default. _cleanupMenuItem on = after each autobackup, keep recent backups + a spread of older ones and remove
        // the extra old autobackups. _recycleMenuItem on = send removed ones to the Recycle Bin instead of deleting.
        private ToolStripMenuItem _cleanupMenuItem;
        private ToolStripMenuItem _recycleMenuItem;
        // Change-aware autobackup, part 4 (back up the moment the game saves): a FileSystemWatcher on the save folder
        // triggers a backup shortly after the game writes a save, instead of waiting for the interval timer. Off by
        // default (_backupOnSaveMenuItem). _quiesceTimer debounces a burst of write events; _suppressSaveWatcher is set
        // while WE write the save folder (restore) so our own writes never trigger a backup. The interval timer stays
        // on as a fallback, so a missed/overflowed watcher event is still caught.
        private ToolStripMenuItem _backupOnSaveMenuItem;
        private ToolStripMenuItem _themeMenuItem; // light/dark toggle
        private FileSystemWatcher _saveWatcher;
        private System.Windows.Forms.Timer _quiesceTimer;
        private volatile bool _suppressSaveWatcher;
        #endregion

        //Hotkey related
        #region
        private string ConvertKeyToString(Keys key)
        {
            switch (key)
            {
                case Keys.D0: return "0";
                case Keys.D1: return "1";
                case Keys.D2: return "2";
                case Keys.D3: return "3";
                case Keys.D4: return "4";
                case Keys.D5: return "5";
                case Keys.D6: return "6";
                case Keys.D7: return "7";
                case Keys.D8: return "8";
                case Keys.D9: return "9";
                case Keys.Oem1: return ";";
                case Keys.Oemplus: return "=";
                case Keys.Oemcomma: return ",";
                case Keys.OemMinus: return "-";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Oemtilde: return "`";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemPipe: return "\\";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemQuotes: return "'";
                case Keys.OemBackslash: return "\\";
                // Add cases for any other special keys you want to handle
                default: return key.ToString();
            }
        }

        private MessageWindow msgWindow;
        // Windows API function to register a hotkey
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        // Windows API function to unregister a hotkey
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // Constants for modifier keys
        private const int MOD_NONE = 0x0000;
        private const int MOD_ALT = 0x0001;
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_SHIFT = 0x0004;
        private const int MOD_WIN = 0x0008;

        // Unique id for the hotkey
        private int hotkeyId = 1;

        // Windows API functions for setting a low-level keyboard hook
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private static LowLevelKeyboardProc _proc;
        private static IntPtr _hookID = IntPtr.Zero;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private readonly object _syncLock = new object();
        private volatile bool _isRecordingHotkey = false;
        private volatile bool _controlPressed = false;
        private volatile bool _shiftPressed = false;
        private volatile bool _altPressed = false;
        private volatile Keys _currentMainKey = Keys.None;

        #endregion

        //tray
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        //Undo
        // Class-level variable to store the paths of deleted files
        List<string> deletedFiles = new List<string>();

        public Main()
        {
            // Attempt to acquire the mutex
            if (!mutex.WaitOne(TimeSpan.Zero, true))
            {
                // If the mutex is already acquired, it means another instance is running
                MessageBox.Show("Another instance of the application is already running.", "Instance Running", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                Environment.Exit(1); // Exit the application
            }



            InitializeComponent();

            // Move any pre-existing settings / autobackup counter from the legacy
            // working-directory location into %APPDATA%\Savedrake before anything
            // (LoadSettings, the autobackup timer) reads or writes them.
            MigrateLegacyStateFiles();

            this.Resize += new System.EventHandler(this.Main_Resize); //System Tray and listView Comumn alignment


            InitializeRegistryWatcher(); //(Autobackup feature)
            InitializeAutobackupTimer(); //(Autobackup feature)

            InitializeHotkey(); //Hotkey

            //combox_auto validation watcher event handler (Autobackup feature)
            this.combobox_auto.Validating += new System.ComponentModel.CancelEventHandler(combobox_auto_Validating);

            #region listview
            //list view contextMenu related
            ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
            ToolStripMenuItem renameMenultem = new ToolStripMenuItem("Rename");
            ToolStripMenuItem deleteMenuItem = new ToolStripMenuItem("Delete");
            ToolStripMenuItem pinMenuItem = new ToolStripMenuItem("Pin backup");
            ToolStripMenuItem validateAllMenuItem = new ToolStripMenuItem("Validate all backups");
            contextMenuStrip.Items.Add(renameMenultem);
            contextMenuStrip.Items.Add(deleteMenuItem);
            contextMenuStrip.Items.Add(pinMenuItem);
            contextMenuStrip.Items.Add(validateAllMenuItem);
            renameMenultem.Click += RenameMenultem_Click;
            deleteMenuItem.Click += DeleteMenuItem_Click;
            pinMenuItem.Click += PinMenuItem_Click;
            validateAllMenuItem.Click += (s, e) => ValidateAllBackups();
            // Pinning (part 3): the menu label reflects the selected backup's state, and Pin is only available for a
            // single selection.
            contextMenuStrip.Opening += (s, e) =>
            {
                bool one = listView.SelectedItems.Count == 1;
                pinMenuItem.Enabled = one;
                pinMenuItem.Text = (one && IsPinnedBackup(Path.GetFileName(listView.SelectedItems[0].Tag.ToString()))) ? "Unpin backup" : "Pin backup";
            };
            listView.ContextMenuStrip = contextMenuStrip;

            //listView related
            listView.ColumnClick += new ColumnClickEventHandler(listView_ColumnClick);
            listView.MouseDoubleClick += listView_MouseDoubleClick;
            listView.MouseClick += listView_MouseClick;
            listView.AfterLabelEdit += listView_AfterLabelEdit;
            listView.ContextMenuStrip = contextMenuStrip;
            listView.KeyDown += ListView_KeyDown;
            // Theme: owner-draw the backup list so it matches the dark/light palette.
            listView.DrawColumnHeader += listView_DrawColumnHeader;
            listView.DrawItem += listView_DrawItem;
            listView.DrawSubItem += listView_DrawSubItem;

            // Integrity column (P1): per-backup state. On load it shows "Protected"/"Legacy" (cheap manifest-presence
            // check); the right-click "Validate all backups" action runs the full hash check and marks each row
            // "Validated"/"Legacy"/"Corrupt".
            if (listView.Columns.Count < 3)
                listView.Columns.Add("Integrity");
            listViewColumnResize();

            #endregion

            // Help > Open log folder (P2): quick access to the diagnostic logs in %APPDATA%\Savedrake\Logs.
            ToolStripMenuItem openLogFolderMenuItem = new ToolStripMenuItem("Open log folder");
            openLogFolderMenuItem.Click += (s, e) =>
            {
                try { System.IO.Directory.CreateDirectory(Log.Directory()); System.Diagnostics.Process.Start("explorer.exe", Log.Directory()); }
                catch (Exception ex) { Log.Error("Could not open log folder", ex); }
            };
            helpToolStripMenuItem.DropDownItems.Add(openLogFolderMenuItem);

            // File menu: "Undo last restore" (QoL) — roll the live save back to the automatic pre-restore snapshot.
            ToolStripMenuItem undoRestoreMenuItem = new ToolStripMenuItem("Undo last restore");
            undoRestoreMenuItem.Click += (s, e) => UndoLastRestore();
            fileToolStripMenuItem.DropDownItems.Add(undoRestoreMenuItem);

            // File menu: "Detect save folder" (QoL) — auto-find the DD2 save folder via Steam.
            ToolStripMenuItem detectMenuItem = new ToolStripMenuItem("Detect save folder");
            detectMenuItem.Click += (s, e) => DetectAndOfferSaveFolder(true);
            fileToolStripMenuItem.DropDownItems.Add(detectMenuItem);

            // File menu: light/dark theme toggle.
            _themeMenuItem = new ToolStripMenuItem();
            _themeMenuItem.Click += (s, e) => ToggleTheme();
            fileToolStripMenuItem.DropDownItems.Add(_themeMenuItem);

            // Files > Settings: opt-in "clean up old backups" toggles (change-aware autobackup, part 2). OFF by default,
            // so existing behavior (autobackup stops at the limit) is unchanged until the user opts in. Turning it on
            // asks for confirmation because it enables automatic removal of old autobackups.
            _cleanupMenuItem = new ToolStripMenuItem("Automatically clean up old autobackups");
            _recycleMenuItem = new ToolStripMenuItem("Send removed backups to the Recycle Bin") { CheckOnClick = true, Enabled = false };
            _cleanupMenuItem.Click += (s, e) =>
            {
                if (!_cleanupMenuItem.Checked)
                {
                    DialogResult r = MessageBox.Show(
                        "Savedrake will keep all your recent autobackups and a spread of older ones, then remove the " +
                        "extra older autobackups so they don't pile up. Your manual backups and pinned backups are never removed.\n\n" +
                        "Turn this on?",
                        "Automatically clean up old autobackups", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes) return;
                    _cleanupMenuItem.Checked = true;
                }
                else _cleanupMenuItem.Checked = false;
                _recycleMenuItem.Enabled = _cleanupMenuItem.Checked;
                if (!_cleanupMenuItem.Checked) _recycleMenuItem.Checked = false;
            };
            ssettingsToolStripMenuItem.DropDownItems.Add(_cleanupMenuItem);
            ssettingsToolStripMenuItem.DropDownItems.Add(_recycleMenuItem);

            // Files > Settings: opt-in "back up the moment the game saves" (change-aware autobackup, part 4). OFF by
            // default. When on (and autobackup is enabled and the game is running) a FileSystemWatcher captures a backup
            // shortly after each real save instead of waiting for the interval timer; the timer stays on as a fallback.
            _backupOnSaveMenuItem = new ToolStripMenuItem("Back up the moment the game saves") { CheckOnClick = true };
            _backupOnSaveMenuItem.CheckedChanged += (s, e) =>
            {
                if (_backupOnSaveMenuItem.Checked && checkbox_auto.Checked && isGameRunning) StartSaveWatcher();
                else StopSaveWatcher();
            };
            ssettingsToolStripMenuItem.DropDownItems.Add(_backupOnSaveMenuItem);

            //tray
            #region
            // Initialize the NotifyIcon component
            trayIcon = new NotifyIcon();
            trayIcon.Icon = this.Icon; // Set the icon
            trayIcon.Visible = false; // Hide the icon initially
            trayIcon.DoubleClick += TrayIcon_DoubleClick; // Event handler for double-clicking the icon
            trayIcon.Text = "Savedrake v1.3.0";
            // Initialize the ContextMenuStrip
            trayMenu = new ContextMenuStrip();
            ToolStripMenuItem showItem = new ToolStripMenuItem("Show");
            showItem.Click += Show_Click; // Make sure the method name matches the event handler
            trayMenu.Items.Add(showItem);

            ToolStripMenuItem quitItem = new ToolStripMenuItem("Quit");
            quitItem.Click += Quit_Click; // Make sure the method name matches the event handler
            trayMenu.Items.Add(quitItem);

            // Assign the ContextMenuStrip to the NotifyIcon
            trayIcon.ContextMenuStrip = trayMenu;
            #endregion


            //Combobox
            this.combobox_auto.Leave += new System.EventHandler(this.combobox_auto_Leave);
            this.combobox_auto.KeyDown += new KeyEventHandler(combobox_auto_KeyDown);

            this.toolStripTextBox2.KeyDown += new KeyEventHandler(toolStripTextBox2_Keydown);

            //Close
            this.FormClosing += new FormClosingEventHandler(Main_FormClosing);

            //Backup file name related
            //this.Load += new System.EventHandler(this.Main_Load);

            // Set CheckOnClick to true for both menu items
            this.randomlyGeneratedToolStripMenuItem.CheckOnClick = true;
            this.timeStampedToolStripMenuItem.CheckOnClick = true;

            // Subscribe to the CheckedChanged event for both menu items
            this.randomlyGeneratedToolStripMenuItem.CheckedChanged += new System.EventHandler(this.randomlyGeneratedToolStripMenuItem_CheckedChanged);
            this.timeStampedToolStripMenuItem.CheckedChanged += new System.EventHandler(this.timeStampedToolStripMenuItem_CheckedChanged);


            //Assign a value to isGameRunning here, after all other initializations. (Autobackup feature)
            isGameRunning = CheckGameRunningStatus();
            OnGameStatusChanged(isGameRunning);

        }

        private bool directorywarningShown = false; //trackking the directory warning or the session

        //Save and Load
        #region
        [XmlRoot("AppSettings")]
        public class AppSettings
        {
            [XmlElement("WindowWidth")]
            public int WindowWidth { get; set; }

            [XmlElement("WindowHeight")]
            public int WindowHeight { get; set; }

            [XmlElement("Textbox1")]
            public string Textbox1 { get; set; }

            [XmlElement("Textbox2")]
            public string Textbox2 { get; set; }

            [XmlElement("AutoBackupLimit")]
            public string AutoBackupLimit { get; set; }

            [XmlElement("CheckboxAuto")]
            public bool CheckboxAuto { get; set; }

            [XmlArray("ComboboxList")]
            [XmlArrayItem("Item")]
            public List<string> ComboboxList { get; set; }

            private int _comboboxSelectedIndex;
            [XmlElement("ComboboxSelectedIndex")]
            public int ComboboxSelectedIndex
            {
                get { return _comboboxSelectedIndex; }
                set
                {
                    // Validate to ensure -1 is not accepted
                    _comboboxSelectedIndex = (value >= 0) ? value : 0; // Set to 0 or another valid default index
                }
            }

            [XmlElement("CheckboxTray")]
            public bool CheckboxTray { get; set; }

            [XmlElement("CheckboxHot")]
            public bool CheckboxHot { get; set; }

            [XmlElement("Textbox3")]
            public string Textbox3 { get; set; }

            [XmlElement("BackupFileName1")]
            public bool BackupFileName1 { get; set; }

            [XmlElement("BackupFileName2")]
            public bool BackupFileName2 { get; set; }

            [XmlElement("AutoCleanupOldBackups")]
            public bool AutoCleanupOldBackups { get; set; }

            [XmlElement("RemovedToRecycleBin")]
            public bool RemovedToRecycleBin { get; set; }

            [XmlElement("BackupOnSaveChange")]
            public bool BackupOnSaveChange { get; set; }

            [XmlElement("ThemeMode")]
            public string ThemeMode { get; set; }



            // Include HotkeySettings property
            [XmlElement("Hotkey")]
            public HotkeySettings Hotkey { get; set; }
        }

        public class HotkeySettings
        {
            [XmlElement("MainKey")]
            public Keys MainKey { get; set; }

            [XmlElement("ControlPressed")]
            public bool ControlPressed { get; set; }

            [XmlElement("ShiftPressed")]
            public bool ShiftPressed { get; set; }

            [XmlElement("AltPressed")]
            public bool AltPressed { get; set; }

            [XmlElement("IsRecording")]
            public bool IsRecording { get; set; }
        }

       
        // --- Per-user state file locations -------------------------------------
        // savedrake_settings.xml and the autobackup counter used to be written
        // with bare relative paths, landing them in the process's current
        // working directory (often the install folder, sometimes elsewhere, and
        // typically unwritable under Program Files). They now live under
        // %APPDATA%\Savedrake. MigrateLegacyStateFiles() copies any pre-existing
        // file forward on first run so upgrading users keep their settings.
        private static readonly string AppDataDir = CreateAppDataDir();

        private static string SettingsFilePath => Path.Combine(AppDataDir, "savedrake_settings.xml");
        private static string AutoBackupCountFilePath => Path.Combine(AppDataDir, "count_of_autobackups.txt");
        // run4: version.txt and savedrake-updater.xml used to live in the working dir (often Program Files,
        // read-only) — moved to %APPDATA%\Savedrake alongside the settings/count files. The updater
        // (updater/UpdaterForm.cs) reads them from the SAME path; keep these two in lockstep with it.
        private static string VersionFilePath => Path.Combine(AppDataDir, "version.txt");
        private static string UpdaterXmlPath => Path.Combine(AppDataDir, "savedrake-updater.xml");

        private static string CreateAppDataDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Savedrake");
            Directory.CreateDirectory(dir); // no-op if it already exists
            return dir;
        }

        private static void MigrateLegacyStateFiles()
        {
            MigrateLegacyFile("savedrake_settings.xml", SettingsFilePath);
            MigrateLegacyFile("count_of_autobackups.txt", AutoBackupCountFilePath);
            MigrateLegacyFile("version.txt", VersionFilePath);
            MigrateLegacyFile("savedrake-updater.xml", UpdaterXmlPath);
        }

        // Copies a legacy file from the old working-directory location to its new
        // %APPDATA% home if one exists and we have not already migrated. Copy (not
        // move) so a read-only source directory cannot fail the migration and the
        // original is left untouched as a fallback. Best-effort: never throws.
        private static void MigrateLegacyFile(string legacyFileName, string newPath)
        {
            if (File.Exists(newPath))
            {
                return;
            }

            foreach (string dir in new[] { Environment.CurrentDirectory, Application.StartupPath })
            {
                try
                {
                    string legacyPath = Path.Combine(dir, legacyFileName);
                    if (File.Exists(legacyPath))
                    {
                        File.Copy(legacyPath, newPath);
                        return;
                    }
                }
                catch
                {
                    // Ignore and try the next candidate location / fall through to defaults.
                }
            }
        }

        // Best-effort delete of any legacy working-dir copies of a relocated state file, mirroring the
        // locations MigrateLegacyFile reads from. Used by the reset path so a "restore defaults" isn't
        // silently undone by the updater's transition fallback reading a surviving old copy.
        private static void DeleteLegacyCopies(string legacyFileName)
        {
            foreach (string dir in new[] { Environment.CurrentDirectory, Application.StartupPath })
            {
                try
                {
                    string legacyPath = Path.Combine(dir, legacyFileName);
                    if (File.Exists(legacyPath))
                    {
                        File.Delete(legacyPath);
                    }
                }
                catch
                {
                    // Best-effort cleanup; ignore failures (locked / missing / permission).
                }
            }
        }

        private void SaveSettings()
        {
            var settings = new AppSettings
            {
                // Save combobox_auto information first
                ComboboxList = combobox_auto.Items.Cast<string>().ToList(),
                ComboboxSelectedIndex = combobox_auto.SelectedIndex,

                // Save the rest of the settings
                WindowWidth = this.Size.Width,
                WindowHeight = this.Size.Height,
            

                Textbox1 = textbox1.Text,
                Textbox2 = textbox2.Text,

                AutoBackupLimit = toolStripTextBox2.Text,

                CheckboxAuto = checkbox_auto.Checked,
                CheckboxTray = checkbox_tray.Checked,
                CheckboxHot = checkbox_hot.Checked,
                Textbox3 = textbox3.Text,
                BackupFileName1 = randomlyGeneratedToolStripMenuItem.Checked,
                BackupFileName2 = timeStampedToolStripMenuItem.Checked,

                AutoCleanupOldBackups = _cleanupMenuItem != null && _cleanupMenuItem.Checked,
                RemovedToRecycleBin = _recycleMenuItem != null && _recycleMenuItem.Checked,
                BackupOnSaveChange = _backupOnSaveMenuItem != null && _backupOnSaveMenuItem.Checked,
                ThemeMode = Theme.Current == Theme.Mode.Light ? "Light" : "Dark",
                
                // Save the hotkey settings
                Hotkey = new HotkeySettings
                {
                    MainKey = _currentMainKey,
                    ControlPressed = _controlPressed,
                    ShiftPressed = _shiftPressed,
                    AltPressed = _altPressed,
                    IsRecording = _isRecordingHotkey
                }

            };

            

            // Serialize to a temp file in the same dir, then atomically swap it into place. StreamWriter opens
            // the target with FileMode.Create (truncates to 0 first), so writing straight to SettingsFilePath
            // meant a crash / full disk mid-serialize left the only settings copy empty or half-written — and
            // SaveSettings runs from Main_FormClosing, a realistic force-kill window. With temp+replace, the
            // previous good settings survive any failed write.
            var serializer = new XmlSerializer(typeof(AppSettings));
            string tempPath = SettingsFilePath + ".tmp";
            using (var writer = new StreamWriter(tempPath))
            {
                serializer.Serialize(writer, settings);
            }
            try
            {
                if (File.Exists(SettingsFilePath))
                    File.Replace(tempPath, SettingsFilePath, null); // atomic on NTFS; same volume (%APPDATA%)
                else
                    File.Move(tempPath, SettingsFilePath);           // first-ever write
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } // don't leave an orphan temp
                throw; // preserve the original propagation (Main_FormClosing reports the save error)
            }
        }



        private void LoadSettings()
        {
            if (!File.Exists(SettingsFilePath))
            {
                return;
            }

            AppSettings settings;
            try
            {
                var serializer = new XmlSerializer(typeof(AppSettings));
                using (var reader = new StreamReader(SettingsFilePath))
                {
                    settings = (AppSettings)serializer.Deserialize(reader);
                }
            }
            catch (Exception ex)
            {
                // A corrupt or partially-written settings XML used to throw out
                // of the form constructor, which prevented the app from ever
                // starting. Warn the user, leave the file in place so they can
                // recover it manually, and fall through to defaults.
                MessageBox.Show(
                    $"Could not load saved settings (file may be corrupt). Defaults will be used.\n\n{ex.Message}",
                    "Settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (settings == null)
            {
                return;
            }

            //Loading Filenaming convention before
            try
            {
                randomlyGeneratedToolStripMenuItem.Checked = settings.BackupFileName1;
                timeStampedToolStripMenuItem.Checked = settings.BackupFileName2;
            }
            catch { }

            // Load combobox_auto information first
            combobox_auto.Items.Clear();
            // An older/partial settings file can deserialize with ComboboxList == null;
            // guard so it doesn't NRE outside the load try/catch and escape into Main_Load.
            if (settings.ComboboxList != null)
            {
                combobox_auto.Items.AddRange(settings.ComboboxList.ToArray());
            }
            // Only set a selection when there is something to select, and clamp the saved
            // index to range, so an empty list / stale index can't throw ArgumentOutOfRange.
            if (combobox_auto.Items.Count > 0)
            {
                int savedIndex = settings.ComboboxSelectedIndex;
                combobox_auto.SelectedIndex = (savedIndex >= 0 && savedIndex < combobox_auto.Items.Count) ? savedIndex : 0;
            }

            // Load the rest of the settings
            this.Size = new Size(settings.WindowWidth, settings.WindowHeight);
            textbox1.Text = settings.Textbox1;
            textbox2.Text = settings.Textbox2;

            toolStripTextBox2.Text = settings.AutoBackupLimit;
            // Unsubscribe the event to prevent it from firing
            //checkbox_auto.CheckedChanged -= checkbox_auto_CheckedChanged;
            checkbox_auto.Checked = settings.CheckboxAuto;
            //checkbox_auto.CheckedChanged += checkbox_auto_CheckedChanged;

            checkbox_tray.Checked = settings.CheckboxTray;
            checkbox_hot.Checked = settings.CheckboxHot;
            textbox3.Text = settings.Textbox3 ?? " "; // Use null-coalescing operator for simplicity

            // Change-aware autobackup, part 2: restore the "clean up old backups" toggles. Recycle is only meaningful
            // (and only enabled) while cleanup is on. Setting .Checked here does not fire the user-confirmation dialog,
            // which lives in the Click handler, not CheckedChanged.
            if (_cleanupMenuItem != null)
            {
                _cleanupMenuItem.Checked = settings.AutoCleanupOldBackups;
                _recycleMenuItem.Checked = settings.AutoCleanupOldBackups && settings.RemovedToRecycleBin;
                _recycleMenuItem.Enabled = _cleanupMenuItem.Checked;
            }
            if (_backupOnSaveMenuItem != null) _backupOnSaveMenuItem.Checked = settings.BackupOnSaveChange;
            Theme.Current = string.Equals(settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase) ? Theme.Mode.Light : Theme.Mode.Dark;



            // Load the hotkey settings
            try
            {
                _currentMainKey = settings.Hotkey.MainKey;
                _controlPressed = settings.Hotkey.ControlPressed;
                _shiftPressed = settings.Hotkey.ShiftPressed;
                _altPressed = settings.Hotkey.AltPressed;
                _isRecordingHotkey = settings.Hotkey.IsRecording;
            }
            catch { }

            // A persisted "recording" flag is a stale transient — never resume recording on load (R4). But if a
            // real combo was captured before the app closed, restore it as the bound hotkey instead of dropping it.
            _isRecordingHotkey = false;
            if (_currentMainKey != Keys.None)
            {
                RegisterHotKeyFunction(false); // restore the saved binding silently (validates key + modifier)
            }
        }
        #endregion

        #region Autobackup feature
        private bool noGame = false;
        private void InitializeRegistryWatcher()
        {
            try
            {
                var currentUser = WindowsIdentity.GetCurrent();
                string keyPath = @"Software\Valve\Steam\Apps\2054970";
                string valueName = "Running";
                var query = new WqlEventQuery(string.Format(
                "SELECT * FROM RegistryValueChangeEvent WHERE Hive='HKEY_USERS' AND KeyPath='{0}\\\\{1}' AND ValueName='{2}'",
                currentUser.User.Value, keyPath.Replace("\\", "\\\\"), valueName));


                _watcher = new ManagementEventWatcher(query);
                _watcher.EventArrived += new EventArrivedEventHandler(KeyValueChanged);
                _watcher.Start();
            }
            catch
            {
                noGame = true;
                //Environment.Exit(1);
            }

        }


        private void KeyValueChanged(object sender, EventArrivedEventArgs e)
        {
            // WMI delivers this on a worker thread; OnGameStatusChanged touches UI controls
            // (Status.Text, checkbox_auto, MessageBox), so marshal onto the UI thread.
            // BeginInvoke (not Invoke) so the WMI thread never blocks on the UI thread — that
            // blocking Invoke combined with FormClosing's _watcher.Stop() could deadlock.
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    isGameRunning = CheckGameRunningStatus();
                    OnGameStatusChanged(isGameRunning);
                }));
            }
            catch (ObjectDisposedException) { } // most-derived first (derives from InvalidOperationException)
            catch (InvalidOperationException) { } // handle destroyed between the guard above and BeginInvoke (form closing)
        }


        private bool CheckGameRunningStatus()
        {
            string keyPath = @"Software\Valve\Steam\Apps\2054970";
            string valueName = "Running";
            using (RegistryKey myKey = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                if (myKey != null)
                {
                    object runningValue = myKey.GetValue(valueName);
                    if (runningValue != null && runningValue is int)
                    {
                        return Convert.ToInt32(runningValue) == 1;
                    }
                }
            }
            return false;
        }

        private bool ValidateDirectories()
        {
            // Check if the Savegame location is valid
            if (string.IsNullOrWhiteSpace(textbox1.Text) || !Directory.Exists(textbox1.Text))
            {
                if (checkbox_auto.Checked)
                {
                    checkbox_auto.Checked = false;
                }
                MessageBox.Show("Please select a valid Savegame location first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // Check only in the selected folder, not subdirectories, for specific files
            if (!textbox1.Text.EndsWith(@"\2054970\remote\win64_save") && !textbox1.Text.EndsWith(@"\2054970\remote\win64_save\"))
            {
                if (!directorywarningShown && !isLoading)
                {
                    var result = MessageBox.Show("The selected folder is not the default directory for Dragon's Dogma 2 save files (see Help). Are you sure you want to continue with this folder?", "Non-Default Path Selected", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    directorywarningShown = true;
                    if (result == DialogResult.No)
                    {
                        PromptForFolderSelection(); // Prompt again
                        return false;
                    }
                }
            }

            // Check if the Backup location is not empty
            if (string.IsNullOrWhiteSpace(textbox2.Text))
            {
                if (checkbox_auto.Checked)
                {
                    checkbox_auto.Checked = false;
                }
                MessageBox.Show("Please select a Backup location.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // Check if the source and destination directories are not the same
            if (textbox1.Text.Equals(textbox2.Text, StringComparison.OrdinalIgnoreCase))
            {
                if (checkbox_auto.Checked)
                {
                    checkbox_auto.Checked = false;
                }
                MessageBox.Show("The Savegame and Backup locations cannot be the same.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // Check if the backup directory exists, if not, prompt to create it
            if (!Directory.Exists(textbox2.Text))
            {
                DialogResult dialogResult = MessageBox.Show("The backup location does not exist. Would you like to create it?", "Create Directory", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dialogResult == DialogResult.Yes)
                {
                    Directory.CreateDirectory(textbox2.Text);
                    MessageBox.Show("Backup location created successfully.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    return false; // Exit the method if the user does not want to create the directory
                }
            }

            return true; // All checks passed
        }

        private void AddProcessedTextToComboBox()
        {
            string originalText = combobox_auto.Text;
            string processedText = ProcessText(originalText);

            // Add processed text if it's not already in the ComboBox items
            if (!combobox_auto.Items.Contains(processedText))
            {
                UpdateComboBox(() => combobox_auto.Items.Add(processedText));
            }

            // Remove duplicates from the ComboBox
            HashSet<string> uniqueItems = new HashSet<string>(combobox_auto.Items.Cast<string>());

            // Clear and add unique items to the ComboBox
            UpdateComboBox(() =>
            {
                combobox_auto.Items.Clear();
                foreach (var item in uniqueItems)
                {
                    combobox_auto.Items.Add(item);
                }
            });

            // Set the ComboBox text to the processed text
            combobox_auto.Text = processedText;
            combobox_auto.SelectedIndex = combobox_auto.Items.IndexOf(processedText);

            SortComboBoxItems();
        }

        // Single source of truth for interpreting an autobackup interval string, shared by validation,
        // sorting, and timer setup so they can never disagree. Locale-robust by design:
        //  - case-insensitive + CultureInvariant unit matching (no Turkish-I / casing surprises),
        //  - tolerant grammar: optional surrounding whitespace, no space required before the unit, and
        //    common synonyms/abbreviations ("min"/"mins"/"minute(s)", "hr"/"hrs"/"hour(s)"),
        //  - the number is parsed with InvariantCulture over ASCII [0-9] only, so a non-US Windows
        //    locale (different digit grouping / digit script) can't make int parsing throw or misread.
        // Returns false (rather than throwing) for anything unrecognized or out of TimeSpan range.
        // Interval parsing moved to Savedrake.Core.IntervalParser (WPF migration, Phase 1). Thin forwarders keep the
        // existing call sites (ParseToTimeSpan / ProcessText / the combobox validation) unchanged.
        private static bool TryParseInterval(string input, out TimeSpan interval) => IntervalParser.TryParse(input, out interval);
        private static string CanonicalizeInterval(string input) => IntervalParser.Canonicalize(input);

        private TimeSpan ParseToTimeSpan(string s)
        {
            // Unrecognized items sort first (TimeSpan.Zero), matching the previous default-case behavior —
            // but without throwing on malformed strings the way the old int.Parse did.
            return TryParseInterval(s, out TimeSpan interval) ? interval : TimeSpan.Zero;
        }

        private void SortComboBoxItems()
        {
            // Capture the current selection (or typed text) BEFORE the rebuild. Items.Clear()+AddRange below
            // resets SelectedIndex to -1 (so SelectedItem becomes null) while Text persists — which made
            // SetAutoBackupInterval report "please select an interval" even though one was shown (R5).
            string current = combobox_auto.SelectedItem != null
                ? combobox_auto.SelectedItem.ToString()
                : combobox_auto.Text;

            List<string> sortedIntervals = combobox_auto.Items.Cast<string>()
                .Select(s => new
                {
                    OriginalString = s,
                    TimeSpan = ParseToTimeSpan(s)
                })
                .OrderBy(x => x.TimeSpan)
                .Select(x => x.OriginalString)
                .ToList();

            UpdateComboBox(() =>
            {
                combobox_auto.Items.Clear();
                combobox_auto.Items.AddRange(sortedIntervals.ToArray());

                // Restore the selection so SelectedItem is non-null again; fall back to the text so the shown
                // value is never blanked. (No re-entrancy: once selected, Text matches an item, so the
                // SelectedIndexChanged handler won't re-add/re-sort.)
                int idx = string.IsNullOrEmpty(current) ? -1 : combobox_auto.Items.IndexOf(current);
                if (idx >= 0)
                    combobox_auto.SelectedIndex = idx;
                else if (!string.IsNullOrEmpty(current))
                    combobox_auto.Text = current;
            });
        }

        

        private string ProcessText(string text)
        {
            // Collapse case/abbreviation/spacing variants of a recognized interval onto one canonical spelling
            // ("5min"/"5 Minutes" -> "5 minutes", "1 Hr" -> "1 hour"). Without this, the broadened input
            // grammar accepted by combobox_auto_Validating would let those forms be added as permanent
            // duplicate ComboBox items (persisted via ComboboxList). Units are preserved; non-interval text
            // falls through to the legacy word-level normalization below.
            if (IntervalParser.IsInterval(text))
                return CanonicalizeInterval(text);

            // Replace whole word "min" with "minutes"
            text = Regex.Replace(text, @"\bmin\b", "minutes");

            // Replace "mins" with "minutes"
            text = Regex.Replace(text, @"\bmins\b", "minutes");

            // Use Regex to find numbers and determine whether to use "hour" or "hours"
            var matches = Regex.Matches(text, @"\d+");
            foreach (Match match in matches)
            {
                if (int.TryParse(match.Value, out int number))
                {
                    if (number > 1)
                    {
                        text = Regex.Replace(text, @"\bhour\b", "hours");
                    }
                    else
                    {
                        text = Regex.Replace(text, @"\bhours\b", "hour");
                    }
                }
            }

            return text;
        }

        private void combobox_auto_Leave(object sender, EventArgs e)
        {
            // Trigger the Validating event
            combobox_auto_Validating(combobox_auto, new System.ComponentModel.CancelEventArgs());

            // Add the text to the items of the ComboBox if it's not already present
            if (!combobox_auto.Items.Contains(combobox_auto.Text))
            {
                AddProcessedTextToComboBox();
                combobox_auto.SelectedIndex = combobox_auto.Items.IndexOf(combobox_auto.Text);
                MessageBox.Show($"Custom interval {combobox_auto.Items[combobox_auto.SelectedIndex].ToString()} added.");
            }
            // Handle auto backup based on checkbox state
            else if (checkbox_auto.Checked)
            {
                // No-op while autobackup is active: the interval is reapplied via SetAutoBackupInterval and the
                // status line is owned by the autobackup flow, so don't overwrite it here.
            }
            else
            {
                Status.Text = $"Autobackup interval set to {combobox_auto.Text}.";
            }

            // Focus the form
            //this.Focus();
        }

        private void combobox_auto_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                // Trigger the Validating event
                combobox_auto_Validating(combobox_auto, new System.ComponentModel.CancelEventArgs());

                // Add the text to the items of the ComboBox if it's not already present
                if (!combobox_auto.Items.Contains(combobox_auto.Text))
                {
                    AddProcessedTextToComboBox();
                    combobox_auto.SelectedIndex = combobox_auto.Items.IndexOf(combobox_auto.Text);
                    MessageBox.Show($"Custom interval {combobox_auto.Items[combobox_auto.SelectedIndex].ToString()} added.");
                }

                // Handle auto backup based on checkbox state
                if (!checkbox_auto.Checked)
                {
                    Status.Text = $"Autobackup interval set to {combobox_auto.Text}.";
                }
                

                // Suppress the default error sound and handle the Enter key
                e.SuppressKeyPress = true;
                e.Handled = true;

                // Focus the form
                //this.Focus();
            }
        }

        private void combobox_auto_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Assuming AddProcessedTextToComboBox() adds the text to combobox_auto.Items
            if (!combobox_auto.Items.Contains(combobox_auto.Text))
            {
                AddProcessedTextToComboBox();
            }

            // Update the selected index without changing the checkbox state
            combobox_auto.SelectedIndex = combobox_auto.Items.IndexOf(combobox_auto.Text);

            // If checkbox_auto is checked, restart the autobackup
            if (!checkbox_auto.Checked)
            {
                Status.Text = $"Autobackup interval set to {combobox_auto.Text}.";
            }
            
        }

        private void UpdateComboBox(Action action)
        {
            if (combobox_auto.InvokeRequired)
            {
                combobox_auto.Invoke(new MethodInvoker(action));
            }
            else
            {
                action();
            }
        }

        private void checkbox_auto_CheckedChanged(object sender, EventArgs e)
        {
            if (checkbox_auto.Checked) { combobox_auto.SelectedIndex = combobox_auto.Items.IndexOf(combobox_auto.Text);}
            

            


            if (noGame && checkbox_auto.Checked)
            {
                MessageBox.Show("Dragon's Dogma 2 appears to be missing from your Steam library. The autobackup feature is designed to work with the Steam version of the game and cannot function without it.", "Autobackup Feature Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                checkbox_auto.Checked = false;
                checkbox_auto.Enabled = false;
                combobox_auto.Enabled = false;
                return;
            }





            isGameRunning = CheckGameRunningStatus();
            OnGameStatusChanged(isGameRunning);





            if (checkbox_auto.Checked)
            {
                if (ValidateDirectories())
                {
                    textbox1.Enabled = false;
                    textbox2.Enabled = false;
                    combobox_auto.Enabled = false;
                    Button_br_1.Enabled = false;
                    Button_br_2.Enabled = false;
                    Button_br_1.BackColor = Theme.P.PanelAlt;
                    Button_br_2.BackColor = Theme.P.PanelAlt;
                }
            }
            else
            {
                textbox1.Enabled = true;
                textbox2.Enabled = true;
                combobox_auto.Enabled = true;
                Button_br_1.Enabled = true;
                Button_br_2.Enabled = true;
                Button_br_1.BackColor = Theme.P.Panel;
                Button_br_2.BackColor = Theme.P.Panel;
                Status.Text = $"Autobackup disabled";
                // Change-aware autobackup (PR1): drop the baseline when autobackup is turned off so a later re-enable
                // re-baselines cleanly against whatever the save folder is then (Hook C also nulls it at game-start).
                _lastAutoBackupFingerprint = null;
            }

            isAutoBackupEnabled = checkbox_auto.Checked;

        }
        protected virtual void OnGameStatusChanged(bool isGameRunning)
        {
            // If the auto backup checkbox is checked.
            if (checkbox_auto.Checked)
            {
                if (isGameRunning)
                {
                    SetAutoBackupInterval();
                    autobackupTimer.Start();
                    StartSaveWatcher(); // part 4: also capture instantly on each save, if the user opted in

                    //Count's before backing up in the beginning.
                    string countFilePath = AutoBackupCountFilePath;

                    // Read the current backup count from the file
                    int backupCount = 0;
                    if (File.Exists(countFilePath))
                    {
                        int.TryParse(File.ReadAllText(countFilePath), out backupCount);
                    }

                    // Parse the integer value from toolStripTextBox1
                    if (int.TryParse(toolStripTextBox2.Text, out int maxBackups))
                    {
                        // Clean-up on -> keep backing up (cleanup enforces the limit); off -> original stop-at-limit.
                        if ((_cleanupMenuItem != null && _cleanupMenuItem.Checked) || backupCount < maxBackups)
                        {
                            // Change-aware autobackup (PR1): null the baseline at game-start so this first backup of
                            // the session always fires (it is NOT routed through the change-aware gate) and Hook B then
                            // establishes the real baseline from the current save. Also covers a save-folder change
                            // made while autobackup was off (the textboxes are only editable when it is off).
                            _lastAutoBackupFingerprint = null;
                            BackupOperation(true); // Perform the backup operation
                            // No stale increment: LoadBackupHistory (via BackupOperation) already wrote the accurate
                            // count from the files on disk; a failed backup must not advance the limit.
                        }
                        else
                        {
                            // Notify the user that the maximum number of backups has been reached
                            this.Invoke((MethodInvoker)delegate {
                                MessageBox.Show($"The maximum number of {maxBackups} autobackups has been reached. To change the limit go to: \"Files > Settings > Limit Autobackups\" and enter the new limit.", "Autobackup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            });
                            autobackupTimer.Stop(); // Stop the timer as the limit has been reached
                            checkbox_auto.Checked = false;
                        }
                    }
                    else
                    {
                        // Notify the user that the value in toolStripTextBox1 is not a valid integer
                        MessageBox.Show("Please enter a valid integer in the autobackup limit field.", "Autobackup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    //NotifyUser("Game running. Autobackup will start now."); // Game is running, start autobackup.
                    Status.Text = $"Game running. Autobackup began every {combobox_auto.Text}.";
                }
                else
                {
                    autobackupTimer.Stop();
                    StopSaveWatcher();
                    Status.Text = $"Game not running. Autobackup paused.";
                    //NotifyUser("Game not running. Autobackup will start when the game starts."); // Game is not running, wait to start autobackup.
                }
            }
            else
            {
                autobackupTimer.Stop(); // Auto backup checkbox is not checked, stop autobackup.
                StopSaveWatcher();
                                        // No message is needed here as per your requirement.
            }
        }


        private void InitializeAutobackupTimer()
        {
            if (autobackupTimer == null)
            {
                autobackupTimer = new System.Timers.Timer();
                // R7a: marshal Elapsed onto the UI thread. Without this it fires on a ThreadPool thread, where the
                // callback touches UI controls (Status, checkbox_auto, the backup itself) cross-thread and any
                // exception is silently swallowed by System.Timers.Timer (so a failed autobackup goes unnoticed).
                autobackupTimer.SynchronizingObject = this;
                autobackupTimer.Elapsed += OnAutobackupTimerElapsed;
                autobackupTimer.AutoReset = true;
                SetAutoBackupInterval();
            }

        }

        private void SetAutoBackupInterval()
        {
            combobox_auto_Validating(combobox_auto, new System.ComponentModel.CancelEventArgs());
            // Drive the timer from the same parser that validated the input, so the milliseconds always
            // match what the user saw — and so units/abbreviations stay consistent across the app. Using a
            // TimeSpan avoids the old int*int milliseconds computation that could overflow on large values.
            if (combobox_auto.SelectedItem != null
                && TryParseInterval(combobox_auto.SelectedItem.ToString(), out TimeSpan interval)
                && interval >= TimeSpan.FromMinutes(5))
            {
                autobackupTimer.Interval = interval.TotalMilliseconds;
            }
            else
            {
                autobackupTimer?.Stop();
                MessageBox.Show("Please select an interval for autobackup.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                checkbox_auto.Checked = false;
            }
        }


        private void OnAutobackupTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            // The interval timer is now one of two triggers for a change-aware autobackup (the other is the save
            // watcher, part 4). Both funnel through RunChangeAwareAutobackup, which carries the re-entrancy guard.
            RunChangeAwareAutobackup();
        }

        // One change-aware autobackup attempt, shared by the interval timer and the save watcher (part 4). Must run on
        // the UI thread (the timer marshals via SynchronizingObject; the watcher via BeginInvoke). The re-entrancy guard
        // (R7a) keeps overlapping triggers safe: a modal dialog below pumps the message queue, and a timer tick and a
        // settled watcher event can arrive together — the second call finds the fingerprint unchanged and skips.
        private void RunChangeAwareAutobackup()
        {
            if (_autoBackupInProgress) return;
            _autoBackupInProgress = true;
            try
            {
                // Re-check game state every time (fresh read, not the cached isGameRunning); if DD2 is no longer
                // running, sync the flag and pause autobackup AND the save watcher rather than backing up stale saves.
                if (!CheckGameRunningStatus())
                {
                    isGameRunning = false;
                    autobackupTimer.Stop();
                    StopSaveWatcher();
                    Status.Text = "Game not running. Autobackup paused.";
                    return;
                }

                // Define the path for the count file
                string countFilePath = AutoBackupCountFilePath;

                // Read the current backup count from the file
                int backupCount = 0;
                if (File.Exists(countFilePath))
                {
                    int.TryParse(File.ReadAllText(countFilePath), out backupCount);
                }

                // Parse the integer value from toolStripTextBox1
                if (int.TryParse(toolStripTextBox2.Text, out int maxBackups))
                {
                    // When "clean up old autobackups" is on, autobackup keeps running (cleanup enforces the limit);
                    // otherwise the original behavior stops autobackup once the limit is reached.
                    if ((_cleanupMenuItem != null && _cleanupMenuItem.Checked) || backupCount < maxBackups)
                    {
                        // Change-aware gate (PR1): skip when the save folder is unchanged since the last backup. A null
                        // fingerprint (folder missing/locked/mid-write/no save data) fails CLOSED: skip and retry. A
                        // null baseline (first eligible attempt) never skips. Skipping consumes no count, writes no file.
                        string currentFp = ComputeSaveFingerprint(textbox1.Text);
                        if (currentFp == null)
                        {
                            Status.Text = "Autobackup: save folder not ready, will retry.";
                        }
                        else if (_lastAutoBackupFingerprint != null && currentFp == _lastAutoBackupFingerprint)
                        {
                            Status.Text = "Autobackup: no save changes since last backup.";
                        }
                        else
                        {
                            BackupOperation(true); // Perform the backup operation
                            // BackupOperation -> LoadBackupHistory recomputes the count from the files on disk, so no
                            // stale local increment here (which previously advanced the limit even on a failed backup).
                        }
                    }
                    else
                    {
                        autobackupTimer.Stop(); // limit reached — stop BEFORE the modal so no further tick is armed
                        StopSaveWatcher();
                        checkbox_auto.Checked = false;
                        MessageBox.Show($"The maximum number of {maxBackups} autobackups has been reached. To change the limit go to: \"Files > Settings > Limit Autobackups\" and enter the new limit.", "Autobackup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                else
                {
                    // Notify the user that the value in toolStripTextBox1 is not a valid integer
                    MessageBox.Show("Please enter a valid integer in the autobackup limit field.", "Autobackup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                _autoBackupInProgress = false;
            }
        }

        // ---- Part 4: save-folder watcher (instant capture). No-op unless the user opted in. The interval timer stays
        // on as the fallback, so a missed or overflowed watcher event is still caught on the next tick. ----

        private void StartSaveWatcher()
        {
            if (_backupOnSaveMenuItem == null || !_backupOnSaveMenuItem.Checked) return;
            if (string.IsNullOrWhiteSpace(textbox1.Text) || !Directory.Exists(textbox1.Text)) return;
            try
            {
                StopSaveWatcher(); // ensure a single clean instance (also disposes any previous quiesce timer)
                _quiesceTimer = new System.Windows.Forms.Timer { Interval = 4000 }; // debounce: wait for writes to settle
                _quiesceTimer.Tick += OnQuiesceElapsed;
                _saveWatcher = new FileSystemWatcher(textbox1.Text)
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
            // Fully tear the debounce timer down (stop + unsubscribe + dispose + null) so repeated start/stop cycles
            // don't accumulate timers or duplicate Tick handlers, and so a queued callback sees null rather than a
            // disposed object.
            if (_quiesceTimer != null)
            {
                try { _quiesceTimer.Stop(); } catch { }
                try { _quiesceTimer.Tick -= OnQuiesceElapsed; } catch { }
                try { _quiesceTimer.Dispose(); } catch { }
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

        // Watcher events fire on a ThreadPool thread. Ignore our own restore writes; otherwise marshal to the UI thread
        // and (re)start the debounce, so a burst of writes collapses into one backup once the save has settled.
        private void OnSaveChanged(object sender, FileSystemEventArgs e)
        {
            if (_suppressSaveWatcher) return;
            try { if (IsHandleCreated) BeginInvoke((MethodInvoker)RestartQuiesce); } catch { }
        }

        private void RestartQuiesce()
        {
            // A watcher event marshaled here can land after StopSaveWatcher disposed+nulled the timer (shutdown / game
            // exit / toggle off). Null-check covers the nulled case; try/catch covers a queued message racing dispose.
            if (_quiesceTimer == null) return;
            try { _quiesceTimer.Stop(); _quiesceTimer.Start(); } catch (ObjectDisposedException) { }
        }

        private void OnQuiesceElapsed(object sender, EventArgs e)
        {
            if (_quiesceTimer == null) return;
            try { _quiesceTimer.Stop(); } catch (ObjectDisposedException) { return; }
            if (_suppressSaveWatcher) return;
            if (!checkbox_auto.Checked) return;
            // A backup or restore is mid-flight: wait another quiet window rather than overlapping it.
            if (_operationInProgress) { try { _quiesceTimer.Start(); } catch (ObjectDisposedException) { } return; }
            RunChangeAwareAutobackup();
        }

        // A watcher Error (e.g. InternalBufferOverflow) means events may have been dropped. Re-arm the watcher; the
        // interval timer covers anything missed in the meantime.
        private void OnSaveWatcherError(object sender, ErrorEventArgs e)
        {
            Log.Warn("Save watcher error, re-arming: " + (e.GetException() != null ? e.GetException().Message : "unknown"));
            try { if (IsHandleCreated) BeginInvoke((MethodInvoker)(() => { StopSaveWatcher(); StartSaveWatcher(); })); } catch { }
        }

        private void combobox_auto_Validating(object sender, System.ComponentModel.CancelEventArgs e)
        {
            string input = combobox_auto.Text;

            // Accepts e.g. "12 minutes", "1 hour", "2 hours" — and now, case-insensitively and with flexible
            // spacing, "12 Minutes", "5min", "1 Hr", "2 hrs". TryParseInterval is the same parser used to
            // drive the timer and sort the list, so what validates here is exactly what those will accept.
            if (!TryParseInterval(input, out TimeSpan interval))
            {
                MessageBox.Show("Please enter the time in the correct format (e.g., '12 minutes', '1 hour', '2 hours').", "Invalid Format", MessageBoxButtons.OK, MessageBoxIcon.Error);
                e.Cancel = true; // Prevents focus from changing
                if (combobox_auto.Items.Count > 0) combobox_auto.SelectedIndex = 0; // guard: empty list -> no reset (else ArgumentOutOfRangeException)
                return;
            }

            // Enforce the 5-minute floor uniformly, whether the interval was given in minutes or hours.
            if (interval < TimeSpan.FromMinutes(5))
            {
                MessageBox.Show("The time interval cannot be less than 5 minutes.", "Invalid Time", MessageBoxButtons.OK, MessageBoxIcon.Error);
                e.Cancel = true; // Prevents focus from changing
                if (combobox_auto.Items.Count > 0) combobox_auto.SelectedIndex = 0; // guard: empty list -> no reset (else ArgumentOutOfRangeException)
            }
        }

        

        #endregion

        #region Browse and Open Buttons

        private void Button_br_1_Click(object sender, EventArgs e)
        {
            PromptForFolderSelection();
        }

        // ---- Auto-detect the DD2 save folder (QoL) ----
        // Dd2AppId moved to Savedrake.Core.SaveScan during the WPF migration (Phase 0).

        // The Steam install root from the registry, or a common default. null if not found.
        private static string GetSteamRoot()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string p = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p)) { p = p.Replace('/', '\\'); if (Directory.Exists(p)) return p; }
                }
            }
            catch { }
            try
            {
                string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
                if (Directory.Exists(def)) return def;
            }
            catch { }
            return null;
        }

        // Existing DD2 save folders under a Steam root. Moved to Savedrake.Core.SaveScan (with Dd2AppId + DirLastWriteUtc)
        // during the WPF migration (Phase 0); thin forwarder keeps the existing call sites unchanged.
        private static List<string> FindDd2SaveFoldersUnder(string steamRoot) => SaveScan.FindDd2SaveFoldersUnder(steamRoot);

        private static List<string> FindDd2SaveFolders()
        {
            string root = GetSteamRoot();
            return root != null ? FindDd2SaveFoldersUnder(root) : new List<string>();
        }

        // Offer the detected DD2 save folder. 'manual' = the user clicked Detect (so we tell them when nothing is found);
        // on first run we stay quiet if there is nothing to suggest. Only SETS the folder on a Yes (never silently).
        private void DetectAndOfferSaveFolder(bool manual)
        {
            List<string> found;
            try { found = FindDd2SaveFolders(); } catch { found = new List<string>(); }
            if (found.Count == 0)
            {
                if (manual)
                    MessageBox.Show("Savedrake could not find a Dragon's Dogma 2 save folder automatically. Make sure Steam " +
                        "is installed and you have run the game at least once, or set the folder with Browse.",
                        "Detect save folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string best = found[0];
            string extra = found.Count > 1
                ? "\n\n(" + (found.Count - 1) + " other Steam profile" + (found.Count > 2 ? "s were" : " was") + " also found; this is the most recently used.)"
                : "";
            DialogResult r = MessageBox.Show(
                "Found your Dragon's Dogma 2 saves here:\n\n" + best + extra + "\n\nUse this folder?",
                "Detect save folder", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                textbox1.Text = best;
                Status.Text = "Save game folder set automatically.";
            }
        }

        // QoL: a human-friendly relative time for the backup list ("just now", "5 min ago", "2 hours ago", "yesterday",
        // "3 days ago", else an absolute date). DISPLAY ONLY — the list still sorts by the FileInfo in each row's Tag
        // (ListViewItemDateComparer), so changing this text never affects ordering.
        private static string FriendlyTime(DateTime when) => TimeText.Friendly(when); // moved to Savedrake.Core.TimeText

        // QoL: a one-line caution about a chosen backup folder, or null if it's fine. Advisory, not blocking. Flags a
        // backup folder inside the save folder, in a cloud-synced folder (OneDrive/Dropbox/Google Drive — sync churn),
        // or on the same drive as the saves (one disk failure loses both). Pure/static so the harness can test it.
        private static string BackupLocationWarning(string saveDir, string backupDir) => SaveScan.BackupLocationWarning(saveDir, backupDir); // moved to Savedrake.Core.SaveScan

        // ---- UI theme (dark/light) ----
        private void UpdateThemeMenuText()
        {
            if (_themeMenuItem != null)
                _themeMenuItem.Text = Theme.Current == Theme.Mode.Dark ? "Use light theme" : "Use dark theme";
        }

        private void ToggleTheme()
        {
            Theme.Current = Theme.Current == Theme.Mode.Dark ? Theme.Mode.Light : Theme.Mode.Dark;
            Theme.Apply(this);
            // Re-apply the card/button look that Theme.Style can't infer (outlined Restore, red Delete, card-bg labels).
            if (_variantBBuilt) { ApplyVariantBOverrides(); ApplyListScrollTheme(); Invalidate(true); }
            UpdateThemeMenuText();
            listView.Refresh();
            try { SaveSettings(); } catch { }
        }

        private void listView_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var bg = new SolidBrush(Theme.P.TitleBar)) e.Graphics.FillRectangle(bg, e.Bounds);
            Rectangle r = e.Bounds; r.X += 6; r.Width -= 6;
            TextRenderer.DrawText(e.Graphics, e.Header.Text, listView.Font, r, Theme.P.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (var pen = new Pen(Theme.P.Border)) e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
        }

        private void listView_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            // Details owner-draw: every cell is painted in DrawSubItem; suppress the default item render (which would
            // otherwise draw the row first with the cached SubItem colours, causing flicker and a stray light row).
            e.DrawDefault = false;
        }

        private void listView_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            Color back = e.Item.Selected ? Theme.P.Sel : (e.ItemIndex % 2 == 0 ? Theme.P.Panel : Theme.P.PanelAlt);
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            // Subtle row separator along the bottom (matches the mockup's clean list).
            using (var sep = new Pen(Theme.P.RowSep))
                e.Graphics.DrawLine(sep, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            float s = listView.DeviceDpi / 96f;
            int pad = (int)(12 * s);
            bool pinned = IsPinnedBackup(e.Item.Text);
            Rectangle r = e.Bounds; r.X += pad; r.Width -= pad * 2;

            if (e.ColumnIndex == 0)
            {
                // Leading dot (gold for pinned) + the backup name (gold if pinned, cream otherwise).
                int dot = (int)(6 * s), dy = e.Bounds.Top + (e.Bounds.Height - dot) / 2;
                using (var db = new SolidBrush(pinned ? Theme.P.Pinned : Theme.P.TextSecondary))
                    e.Graphics.FillEllipse(db, e.Bounds.X + pad, dy, dot, dot);
                Rectangle nr = r; nr.X += (int)(16 * s); nr.Width -= (int)(16 * s);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, listView.Font, nr, pinned ? Theme.P.Pinned : Theme.P.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else if (e.ColumnIndex == 1)
            {
                // Friendly time, right-aligned and muted.
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, listView.Font, r, Theme.P.TextSecondary,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                // Status tag, right-aligned. Pinned backups read "pinned" (gold); otherwise the integrity status in its
                // theme colour (green "protected"/"validated", red "corrupt"/"missing").
                string tag = pinned ? "pinned" : (e.SubItem.Text ?? string.Empty).ToLowerInvariant();
                Color c = pinned ? Theme.P.Pinned : StatusColor(e.SubItem.Text);
                TextRenderer.DrawText(e.Graphics, tag, listView.Font, r, c,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private static Color StatusColor(string s)
        {
            if (s == "Validated" || s == "Protected") return Theme.P.Success;
            if (s == "Corrupt" || s == "Missing") return Theme.P.Danger;
            return Theme.P.TextSecondary;
        }

        private void PromptForFolderSelection()
        {
            using (var dialog = new CommonOpenFileDialog())
            {
                dialog.IsFolderPicker = true;
                if (dialog.ShowDialog() == CommonFileDialogResult.Ok && !string.IsNullOrWhiteSpace(dialog.FileName))
                {
                    string folder = dialog.FileName;
                    // Ensure the selected path is not the same as textbox2's path
                    if (folder.Equals(textbox2.Text, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("The Savegame and Backup locations cannot be the same.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        PromptForFolderSelection(); // Prompt again
                    }
                    else if (folder.EndsWith(@"\2054970\remote\win64_save") || folder.EndsWith(@"\2054970\remote\win64_save\"))
                    {
                        textbox1.Text = folder; // Set the selected folder path to textbox1
                    }
                    else
                    {
                        if (!isLoading)
                        {
                            
                            if (!directorywarningShown)
                            {
                                var result = MessageBox.Show("The selected folder is not the default directory for Dragon's Dogma 2 save files (see Help > FAQ). Are you sure you want to continue with this folder?", "Non-Default Path Selected", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                                directorywarningShown = true;
                                if (result == DialogResult.No)
                                {
                                    PromptForFolderSelection(); // Prompt again
                                }
                                else
                                {
                                    textbox1.Text = folder; // User confirmed the selection
                                    // Create a new DirectoryInfo object
                                    DirectoryInfo directoryInfo = new DirectoryInfo(textbox1.Text);

                                    // Check if the directory is empty
                                    if (directoryInfo.GetFileSystemInfos().Length == 0)
                                    {
                                        MessageBox.Show("The selected Savegame location is empty.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                        PromptForFolderSelection();
                                    }
                                }
                            }
                            else 
                            {
                                textbox1.Text = folder; // User confirmed the selection
                                // Create a new DirectoryInfo object
                                DirectoryInfo directoryInfo = new DirectoryInfo(textbox1.Text);

                                // Check if the directory is empty
                                if (directoryInfo.GetFileSystemInfos().Length == 0)
                                {
                                    MessageBox.Show("The selected Savegame location is empty.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                    PromptForFolderSelection();
                                }
                            }
                            
                        }
                    }
                }
            }
        }

        private void Button_br_2_Click(object sender, EventArgs e)
        {
            // Check if the savegame location is not set
            if (string.IsNullOrWhiteSpace(textbox1.Text))
            {
                MessageBox.Show("Please select the Savegame location first.", "Savegame Location Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return; // Exit the method
            }

            using (var dialog = new CommonOpenFileDialog())
            {
                dialog.IsFolderPicker = true;
                dialog.InitialDirectory = textbox2.Text; // Set the initial directory

                if (dialog.ShowDialog() == CommonFileDialogResult.Ok && !string.IsNullOrWhiteSpace(dialog.FileName))
                {
                    string selectedPath = dialog.FileName;

                    // Ensure the selected path is not the same as textbox1's path
                    if (selectedPath.Equals(textbox1.Text, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("The Backup location cannot be the same as the Savegame location.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Error);

                    }
                    // Check if the selected path is a subdirectory of textbox1's path
                    else if (IsSubdirectoryOf(selectedPath, textbox1.Text))
                    {
                        MessageBox.Show("The Backup location cannot be a subdirectory of the Savegame location.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Error);

                    }
                    else if (selectedPath.EndsWith(@"\2054970\remote\win64_save") || selectedPath.EndsWith(@"\2054970\remote\win64_save\"))
                    {
                        MessageBox.Show("The selected folder cannot be used as the Backup location as it appears to be the default savegame location for Dragon's Dogma 2.", "Invalid Selection", MessageBoxButtons.OK, MessageBoxIcon.Error);

                    }
                    else
                    {
                        textbox2.Text = selectedPath; // Set the selected folder path to textbox2
                        // QoL: advise (don't block) if the chosen folder is cloud-synced or on the same drive as saves.
                        string locWarn = BackupLocationWarning(textbox1.Text, selectedPath);
                        if (locWarn != null)
                            MessageBox.Show(locWarn, "Backup location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            LoadBackupHistory();
        }

        private bool IsSubdirectoryOf(string selectedPath, string potentialBasePath)
        {
            var selectedDirectoryInfo = new DirectoryInfo(selectedPath).FullName;
            var baseDirectoryInfo = new DirectoryInfo(potentialBasePath).FullName;

            if (!baseDirectoryInfo.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                baseDirectoryInfo += Path.DirectorySeparatorChar;
            }

            return selectedDirectoryInfo.StartsWith(baseDirectoryInfo, StringComparison.OrdinalIgnoreCase);
        }

        private void Button_op_1_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(textbox1.Text) && Directory.Exists(textbox1.Text))
            {
                System.Diagnostics.Process.Start(textbox1.Text);
            }
            else
            {
                MessageBox.Show("The directory path is invalid.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Button_op_2_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(textbox2.Text) && Directory.Exists(textbox2.Text))
            {
                System.Diagnostics.Process.Start(textbox2.Text);
            }
            else
            {
                MessageBox.Show("The directory path is invalid.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        #endregion

        #region Main Resize listView and tray icon
        private void Main_Resize(object sender, EventArgs e)
        {
            listViewColumnResize();

            // Check if the form is minimized and the checkbox is checked
            if (this.WindowState == FormWindowState.Minimized && checkbox_tray.Checked)
            {


                // Hide the form from the taskbar
                //this.ShowInTaskbar = false;
                trayIcon.Visible = checkbox_tray.Checked;
                this.Hide();


                // Show a balloon tip if needed
                trayIcon.ShowBalloonTip(500, "Application Minimized", "Savedrake is now minimized to the system tray.", ToolTipIcon.Info);
            }
            else
            {
                return;
            }
        }

        private void listViewColumnResize()
        {
            if (listView.Columns.Count >= 3)
            {
                // Name (flex) | friendly time (right) | status tag (right). The mockup gives the name the room and keeps
                // the time + tag as narrow right-aligned columns. Size against ClientSize (excludes any vertical
                // scrollbar) so the columns never overflow into a horizontal scrollbar.
                float s = listView.DeviceDpi / 96f;
                int tag = (int)(110 * s), time = (int)(150 * s);
                int avail = listView.ClientSize.Width;
                int name = Math.Max((int)(80 * s), avail - tag - time);
                listView.Columns[0].Width = name;
                listView.Columns[1].Width = time;
                listView.Columns[2].Width = tag;
                return;
            }
            listView.Columns[0].Width = listView.Width / 2;
            // Set the width of the second column (Column 1) to be 50% of the ListView's width
            listView.Columns[1].Width = listView.Width / 2;
            // ... Set other columns as needed

            // Set the last column to fill the remaining space
            if (listView.Columns.Count > 0)
            {
                listView.Columns[listView.Columns.Count - 1].Width = -2;
            }
        }
        #endregion

        //Backup Zip Operations //Undetected
        #region Backup
        private string backupFileName;
        private void BackupOperation(bool isAutoBackup = false)
        {
            // Check if the source directory textbox is not empty and the directory exists
            if (string.IsNullOrWhiteSpace(textbox1.Text) || !Directory.Exists(textbox1.Text))
            {
                if (checkbox_auto.Checked)
                {
                    checkbox_auto.Checked = false;
                }
                MessageBox.Show("Please select a valid Savegame location first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Check only in the selected folder, not subdirectories, for specific files
            if (textbox1.Text.EndsWith(@"\2054970\remote\win64_save") || textbox1.Text.EndsWith(@"\2054970\remote\win64_save\"))
            {


            }
            else
            {
                if (!directorywarningShown)
                {
                    var result = MessageBox.Show("The selected folder is not the default directory for Dragon's Dogma 2 save files (see Help > FAQ). Are you sure you want to continue with this folder?", "Non-Default Path Selected", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    directorywarningShown = true;
                    if (result == DialogResult.No)
                    {
                        PromptForFolderSelection(); // Prompt again
                    }
                }
            }

            // Check if the backup directory textbox is not empty
            if (string.IsNullOrWhiteSpace(textbox2.Text))
            {
                if (checkbox_auto.Checked)
                {
                    checkbox_auto.Checked = false;
                }
                MessageBox.Show("Please select a Backup location.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                return;
            }

            // Create a new DirectoryInfo object
            DirectoryInfo directoryInfo = new DirectoryInfo(textbox1.Text);

            // Check if the directory is empty
            if (directoryInfo.GetFileSystemInfos().Length == 0)
            {
                MessageBox.Show("The selected Savegame location is empty.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // R6: a folder can be non-empty yet hold no DD2 save data (wrong folder, or leftover files). Refuse to
            // create a useless/empty-looking backup — require at least one data*.bin / system.bin (searched
            // recursively; .Any() short-circuits on the first match, so the common case is fast). If the recursive
            // scan can't complete (e.g. a permission-denied/locked subfolder on a non-default path), fail OPEN:
            // proceed rather than block a legitimate backup — and never let it throw out of here (an uncaught
            // throw would crash a manual backup or silently kill an auto-backup on the timer thread).
            bool hasSaveData = true;
            try
            {
                hasSaveData = Directory
                    .EnumerateFiles(textbox1.Text, "*", SearchOption.AllDirectories)
                    .Any(p => IsRealSaveEntry(Path.GetFileName(p)));
            }
            catch (UnauthorizedAccessException) { }
            catch (System.IO.IOException) { }
            if (!hasSaveData)
            {
                if (checkbox_auto.Checked)
                {
                    checkbox_auto.Checked = false;
                }
                MessageBox.Show("The selected Savegame location has no Dragon's Dogma 2 save data " +
                    "(no data*.bin / system.bin), so there is nothing to back up.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Check if the source and destination directories are not the same
            if (textbox1.Text.Equals(textbox2.Text, StringComparison.OrdinalIgnoreCase))
            {
                if (checkbox_auto.Checked)
                {
                    checkbox_auto.Checked = false;
                }
                MessageBox.Show("The Savegame and Backup locations cannot be the same.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                return;
            }

            // Check if the backup directory exists, if not, prompt to create it
            if (!Directory.Exists(textbox2.Text))
            {
                // No sound here: a missing-but-creatable folder is a prompt, not a failure. The error sound is
                // reserved for an actual backup failure (the catch below); success plays its own sound once the
                // backup completes. (Previously this chimed error.wav, which — now that sound is no longer
                // hotkey-gated — produced a misleading error-then-success double sound on a successful backup.)
                DialogResult dialogResult = MessageBox.Show("The backup location does not exist. Would you like to create it?", "Create Directory", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dialogResult == DialogResult.Yes)
                {
                    Directory.CreateDirectory(textbox2.Text);
                    MessageBox.Show("Backup location created successfully.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    return; // Exit the method if the user does not want to create the directory
                }
            }

            if (randomlyGeneratedToolStripMenuItem.Checked)
            {
                backupFileName = Path.Combine(textbox2.Text, GenerateBackupFileName(isAutoBackup));
            }
            else
            {
                string autoPrefix = isAutoBackup ? "auto" : "";
                // Timestamp names are only second-resolution, so two backups in the same second would collide.
                // The random-name branch already dedups (GenerateBackupFileName); make this branch unique too so a
                // second same-second backup can't silently overwrite the first.
                backupFileName = MakeUniquePath(Path.Combine(textbox2.Text, $"{autoPrefix}backup_{DateTime.Now:yyMMddHHmmss}.zip"));
            }

            // Disk-space preflight: refuse before writing if the backup volume can't hold the data (+ headroom). A
            // disk-full mid-write only leaves a temp file (cleaned up below), but checking first avoids the wasted
            // work and a confusing partial failure.
            long sourceSize = GetDirectorySize(textbox1.Text);
            if (!HasFreeSpaceFor(textbox2.Text, sourceSize, out string backupSpaceReason))
            {
                if (checkbox_auto.Checked) checkbox_auto.Checked = false;
                Status.Text = "Backup skipped: low disk space.";
                MessageBox.Show("Cannot create the backup: " + backupSpaceReason + ".", "Low Disk Space", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Operation lock: the UI thread serializes operations, but a modal dialog pumps the message queue, so a
            // queued autobackup tick or click could otherwise re-enter while a backup/restore is mid-flight.
            if (_operationInProgress) { Status.Text = "Please wait, another operation is already running."; return; }

            // Build the zip into a temp file in the SAME directory, then publish it with an atomic rename. A
            // failure mid-Save (disk full, a source save file locked/removed) then leaves only the temp file
            // (cleaned up below) — never a partial/corrupt .zip at the real backup path, and never an existing
            // good backup overwritten by a truncated stub.
            string tempZip = backupFileName + ".savedrake.tmp";
            _operationInProgress = true;
            try
            {
                // Sweep any orphaned temp files left by a previous backup that was hard-killed mid-write (a graceful
                // failure cleans up via the catch below; a power-loss/kill can't). They're invisible to the *.zip
                // listing/counter but still consume disk, so reclaim them here. Subsumes deleting our own tempZip.
                try { foreach (string stale in Directory.GetFiles(textbox2.Text, "*.savedrake.tmp")) File.Delete(stale); } catch { }
                using (Ionic.Zip.ZipFile zip = new Ionic.Zip.ZipFile())
                {
                    Status.Text = "Backup started... Please wait.";
                    zip.AddDirectory(textbox1.Text); // Add the directory to the zip
                    // Integrity manifest (P1 layer 2): record every source file's path/length/SHA-256 inside the zip
                    // so we can prove on create (and re-check later) that the backup is complete and uncorrupted.
                    zip.AddEntry(ManifestEntryName, System.Text.Encoding.UTF8.GetBytes(BuildBackupManifest(textbox1.Text)));
                    zip.Comment = "SamMorrison9800"; // This is the hidden comment
                    zip.Save(tempZip); // Save to the temp file first
                }
                // Verify-on-create: a backup that fails verification must never be published as if it were good. Reject
                // it here (delete the temp, throw into the catch below) so the user is told now, while their live saves
                // are untouched, instead of discovering it only at restore time. Layer 1 = CRC test-extract of every
                // entry; layer 2 = every file present with the manifest's recorded length + SHA-256.
                if (!VerifyZipRestorable(tempZip, out string verifyReason) || !VerifyZipAgainstManifest(tempZip, out verifyReason))
                {
                    try { File.Delete(tempZip); } catch { }
                    throw new System.IO.IOException("the backup failed verification after writing (" + verifyReason + ")");
                }
                File.Move(tempZip, backupFileName); // atomically publish the completed backup

                // Update the status
                LoadBackupHistory();
                // Change-aware autobackup (PR1): refresh the content baseline from the just-published save so the next
                // autobackup tick skips it when nothing changed. Set unconditionally (manual OR auto) so a manual
                // backup also suppresses an immediately-following redundant autobackup. Placed inside the try after the
                // atomic File.Move publish, so a backup that failed verification (and threw to the catch) never
                // advances the baseline.
                _lastAutoBackupFingerprint = ComputeSaveFingerprint(textbox1.Text);
                Status.Text = isAutoBackup ? $"Autobackup created at {DateTime.Now.ToString("hh:mm:ss tt")}." : "Backup created successfully.";
                PlaySoundFromResource();
                Log.Info("Backup created: " + Path.GetFileName(backupFileName) + (isAutoBackup ? " (auto)" : ""));
                // Change-aware autobackup, part 2: after a successful AUTObackup, if the user opted in, keep recent
                // backups + a spread of older ones and remove the extra old autobackups.
                if (isAutoBackup && _cleanupMenuItem != null && _cleanupMenuItem.Checked) CleanUpOldAutobackups();
            }
            catch (Exception ex)
            {
                Log.Error("Backup failed", ex);
                // Clean up the partial temp file so a failed backup leaves nothing behind.
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                // If an error occurs, show an error message
                PlaySoundFromResource2();
                if (checkbox_auto.Checked)
                {
                    checkbox_auto.Checked = false;
                }
                Status.Text = "Backup failed.";
                MessageBox.Show($"An error occurred while creating the backup: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _operationInProgress = false; }
        }

        // Returns fullPath if it is free, otherwise the first "name_N.ext" variant that does not exist.
        private static string MakeUniquePath(string fullPath) => BackupNaming.MakeUniquePath(fullPath); // moved to Savedrake.Core.BackupNaming

        // Disk-space preflight helpers moved to Savedrake.Core.DiskPreflight during the WPF migration (Phase 0);
        // thin forwarders keep the existing call sites unchanged.
        private static long GetDirectorySize(string dir) => DiskPreflight.GetDirectorySize(dir);

        private static long GetZipUncompressedSize(string zipPath) => DiskPreflight.GetZipUncompressedSize(zipPath);

        private static bool HasFreeSpaceFor(string targetDir, long requiredBytes, out string reason) => DiskPreflight.HasFreeSpaceFor(targetDir, requiredBytes, out reason);

        // Backup integrity verification, layer 1 (P1): prove a freshly written archive is actually restorable before
        // we publish or trust it. DotNetZip's IsZipFile(testExtract:true) opens the zip, reads its directory, and
        // expands EVERY entry while checking CRCs, so truncation, bit-rot, or a half-written/locked source file is
        // caught at creation time instead of only when the user finally needs the backup. Returns false (with a
        // reason) on any failure. Static + file-path-only so the headless harness can test it directly.
        private static bool VerifyZipRestorable(string zipPath, out string reason) => Manifest.VerifyZipRestorable(zipPath, out reason);

        // Backup-integrity / manifest helpers moved to Savedrake.Core.Manifest during the WPF migration (Phase 0).
        // ManifestEntryName + IsManifestEntry stay reachable from Main (LoadBackupHistory / backup + restore flow)
        // via a const alias + thin forwarder so call sites are unchanged.
        private const string ManifestEntryName = Manifest.ManifestEntryName;

        private static bool IsManifestEntry(string entryFileName) => Manifest.IsManifestEntry(entryFileName);

        private static string BuildBackupManifest(string sourceDir) => Manifest.BuildBackupManifest(sourceDir);

        // Change-aware autobackup (PR1): a stable content fingerprint of the save folder, so the autobackup timer can
        // skip a tick when nothing changed instead of writing a redundant identical backup that eats into the user's
        // limit. Reuses BuildBackupManifest (the same per-file path/length/SHA-256 a backup records), then hashes only
        // the content fields, so it is immune to the manifest's volatile createdUtc/tool stamps. Returns null (never
        // throws) when the folder is missing/locked/unreadable or holds no real save data; callers treat null as
        // "not safely comparable" and SKIP (fail-closed) rather than zip a folder they cannot fully read.
        private static string ComputeSaveFingerprint(string saveDir) => Fingerprint.ComputeSaveFingerprint(saveDir); // moved to Savedrake.Core.Fingerprint

        private static string StableManifestHash(string manifestJson) => Manifest.StableManifestHash(manifestJson); // moved to Savedrake.Core.Manifest

        // Change-aware autobackup, part 2 (tiered retention): pure selection of which autobackups to THIN (delete),
        // given the UTC tick timestamps of the THINNABLE autobackups only (the caller excludes manual backups, pinned
        // ones, the (Pre-Restore) checkpoint, and corrupt backups, which are never thinned). Tiered time-bucket policy
        // (Borg/restic style): keep EVERY backup in the last hour, then the NEWEST per bucket as buckets widen — one
        // per 30 min to 6h, per hour to 24h, per day to 7d, per week beyond. After bucketing, if more than maxKeep
        // survive (maxKeep <= 0 means no cap), the OLDEST survivors are thinned too until maxKeep remain. Deterministic,
        // idempotent (re-running at the same 'now' on the survivors thins nothing more), and independent of input order.
        // Returns the indices INTO candidateTicksUtc of the entries to delete.
        private static int[] SelectAutobackupsToThin(long[] candidateTicksUtc, long nowTicksUtc, int maxKeep) => RetentionPolicy.SelectAutobackupsToThin(candidateTicksUtc, nowTicksUtc, maxKeep); // moved to Savedrake.Core.RetentionPolicy

        // Change-aware autobackup, part 2: after a successful autobackup, keep recent backups and a spread of older
        // ones and remove the extra OLD autobackups (chosen by SelectAutobackupsToThin). Only autobackups are eligible;
        // manual backups and the "(Pre-Restore)" checkpoint (different name prefixes) are never removed, and a corrupt
        // backup is skipped so a possibly-recoverable archive is never auto-deleted. Removed backups go to the Recycle
        // Bin when the user chose that, otherwise they are deleted. UI-thread only; best-effort (errors are logged,
        // never thrown onto the timer).
        private void CleanUpOldAutobackups()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(textbox2.Text) || !Directory.Exists(textbox2.Text)) return;

                // Eligible = autobackups only ("(Auto)"/"auto" prefix). Manual backups (no prefix) and the
                // "(Pre-Restore)" checkpoint are excluded here, so they are never removed.
                var files = new List<string>();
                var ticks = new List<long>();
                foreach (string file in Directory.GetFiles(textbox2.Text, "*.zip"))
                {
                    string name = Path.GetFileName(file);
                    if (!(name.StartsWith("(Auto)") || name.StartsWith("auto"))) continue;
                    if (IsPinnedBackup(name)) continue; // pinned backups (part 3) are never removed
                    files.Add(file);
                    ticks.Add(File.GetLastWriteTimeUtc(file).Ticks);
                }
                if (files.Count == 0) return;

                int maxKeep = (int.TryParse(toolStripTextBox2.Text, out int m) && m > 0) ? m : 0;
                int[] toRemove = SelectAutobackupsToThin(ticks.ToArray(), DateTime.UtcNow.Ticks, maxKeep);

                bool toRecycle = _recycleMenuItem != null && _recycleMenuItem.Checked;
                int removed = 0;
                foreach (int i in toRemove)
                {
                    try
                    {
                        if (ClassifyBackupFully(files[i]) == "Corrupt") continue; // never auto-remove a corrupt backup
                        if (toRecycle)
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(files[i],
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        else
                            File.Delete(files[i]);
                        removed++;
                    }
                    catch (Exception ex) { Log.Warn("Could not remove old autobackup " + Path.GetFileName(files[i]) + ": " + ex.Message); }
                }

                if (removed > 0)
                {
                    LoadBackupHistory(); // refresh the list and recompute the autobackup count from disk
                    Status.Text = removed == 1 ? "Removed 1 old autobackup." : ("Removed " + removed + " old autobackups.");
                    Log.Info("Auto-cleanup removed " + removed + " old autobackup(s)" + (toRecycle ? " (to Recycle Bin)" : ""));
                }
            }
            catch (Exception ex) { Log.Warn("Auto-cleanup of old autobackups failed: " + ex.Message); }
        }

        // Change-aware autobackup, part 3 (pinning): a pinned backup is protected from automatic cleanup and is not
        // counted toward the autobackup limit. Pins are marked by a " [PINNED]" token in the file name (not a sidecar
        // or index) so they survive copy/move, are visible in Explorer and the backup list, and need no extra storage.
        // Renaming a pinned file outside Savedrake to drop the token simply unpins it.
        // PinTag + the three pin helpers moved to Savedrake.Core.Pinning during the WPF migration (Phase 0);
        // thin forwarders keep the existing call sites unchanged.
        internal const string PinTag = Pinning.PinTag;

        private static bool IsPinnedBackup(string fileName) => Pinning.IsPinnedBackup(fileName);

        private static string PinnedPath(string path) => Pinning.PinnedPath(path);

        private static string UnpinnedPath(string path) => Pinning.UnpinnedPath(path);

        // Backup integrity verification, layer 2 (P1): confirm the freshly written archive contains every file the
        // manifest declares, each with the recorded length + SHA-256. Catches a backup missing whole files (e.g. a zip
        // truncated at an entry boundary, which the layer-1 CRC test-extract can miss) and per-file bit-rot. Returns
        // false (with a reason) on any missing/mismatched file or a missing/garbled manifest.
        private static bool VerifyZipAgainstManifest(string zipPath, out string reason) => Manifest.VerifyZipAgainstManifest(zipPath, out reason);

        private static bool HasManifest(string zipPath) => Manifest.HasManifest(zipPath);

        private static bool RestoreBlockedByManifest(string zipPath, out string reason) => Manifest.RestoreBlockedByManifest(zipPath, out reason);

        private static string ClassifyBackupFully(string zipPath) => Manifest.ClassifyBackupFully(zipPath);


        // Helper method to generate a unique backup file name
        private string GenerateBackupFileName(bool isAutoBackup)
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
            string fullPath = Path.Combine(textbox2.Text, fileName);
            while (File.Exists(fullPath))
            {
                // Ensure the counter is added after the (Auto) prefix
                fileName = $"{autoPrefix}{Path.GetFileNameWithoutExtension(fileName).Replace($" {counter - 1}", "")} {counter++}.zip";
                fullPath = Path.Combine(textbox2.Text, fileName);
            }

            return fileName;
        }

        private void button_backup_Click(object sender, EventArgs e)
        {
            BackupOperation();
        }
        #endregion

        //Restore Zip Operations //Undetected
        #region Restore Operation

        // Pre-restore safety checkpoint (P4). RestoreTransactional moves the current live save aside and DELETES it
        // on success (Undo is disabled afterwards), so restoring backup A would otherwise discard the player's
        // current state B with no way back. Snapshot the live save into the backup folder first, under a distinct
        // "(Pre-Restore) " name so it is visible in the history list, is NOT counted as an autobackup (LoadBackupHistory
        // only counts "(Auto)"/"auto" prefixes), and is never auto-pruned (nothing prunes). Mirrors the atomic
        // temp+rename write used by BackupOperation. UI-free (takes explicit dirs) so the headless harness can test it.
        // Returns true on success OR when there is nothing to snapshot; false on failure so the caller can let the
        // user decide whether to restore without a safety net.
        // Moved to Savedrake.Core.RestoreEngine (WPF Phase 0). Thin instance forwarder keeps the original signature
        // so RestoreTransactional + this.X(...) call sites are untouched.
        private bool CreatePreRestoreCheckpoint(string liveDir, string backupDir) => RestoreEngine.CreatePreRestoreCheckpoint(liveDir, backupDir);

        // Undo-restore (QoL): the newest "(Pre-Restore)" checkpoint in the backup folder — the automatic snapshot
        // Savedrake takes of the live save just before each restore. Returns null if there is none, or the folder is
        // unreadable. Static + folder-parameterised so the headless harness can test it.
        private static string FindLatestPreRestoreCheckpoint(string backupDir) => SaveScan.FindLatestPreRestoreCheckpoint(backupDir); // moved to Savedrake.Core.SaveScan

        // Undo-restore (QoL): roll the live save back to the snapshot taken just before the last restore. Instead of
        // duplicating the (high-stakes) restore flow, this selects that checkpoint in the list and invokes the normal
        // restore, so it inherits the game-running guard, the Steam Cloud warning, validation, AND a fresh pre-restore
        // snapshot of the CURRENT state (so the undo is itself undoable / redoable).
        private void UndoLastRestore()
        {
            if (string.IsNullOrWhiteSpace(textbox2.Text) || !Directory.Exists(textbox2.Text))
            {
                MessageBox.Show("Please set your backup folder first.", "Undo last restore", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            LoadBackupHistory(); // make sure the list matches what's on disk before we pick from it
            string checkpoint = FindLatestPreRestoreCheckpoint(textbox2.Text);
            if (checkpoint == null)
            {
                MessageBox.Show("There is no snapshot to undo. Savedrake automatically saves a snapshot of your current " +
                    "save each time you restore a backup, so an undo only becomes available after a restore.",
                    "Nothing to undo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string when = Path.GetFileNameWithoutExtension(checkpoint).Replace("(Pre-Restore)", "").Trim();
            DialogResult r = MessageBox.Show(
                "This rolls your save back to the snapshot Savedrake took just before your last restore" +
                (when.Length > 0 ? " (" + when + ")" : "") + ".\n\n" +
                "Your current save is snapshotted first, so you can redo. You'll be reminded about Steam next.\n\n" +
                "Undo the last restore?",
                "Undo last restore", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            string fileName = Path.GetFileName(checkpoint);
            ListViewItem target = null;
            foreach (ListViewItem it in listView.Items)
                if (string.Equals(it.Text, fileName, StringComparison.OrdinalIgnoreCase)) { target = it; break; }
            if (target == null)
            {
                MessageBox.Show("Could not find the snapshot in the backup list.", "Undo last restore", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            listView.SelectedItems.Clear();
            target.Selected = true;
            target.Focused = true;
            button_res_Click(this, EventArgs.Empty); // reuse the full restore flow (cloud warning, checkpoint, transaction)
        }

        private void button_res_Click(object sender, EventArgs e)
        {
            // Check if the textboxes are not empty and contain valid paths
            if (string.IsNullOrWhiteSpace(textbox1.Text) || !Directory.Exists(textbox1.Text))
            {
                MessageBox.Show("Please provide a valid Savegame location.", "Invalid Directory", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(textbox2.Text) || !Directory.Exists(textbox2.Text))
            {
                MessageBox.Show("Please provide a valid Backup file location.", "Invalid Directory", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Check if exactly one item is selected in the ListView
            if (listView.SelectedItems.Count == 1)
            {
                // Get the selected file name
                string fileName = listView.SelectedItems[0].Text;

                // Combine the source directory with the file name to get the full file path
                string filePath = Path.Combine(textbox2.Text, fileName);

                // Validate the backup is readable BEFORE touching the live saves, so a
                // corrupt or missing zip can't strand the user with no save game.
                if (!File.Exists(filePath))
                {
                    MessageBox.Show("The selected Backup file no longer exists on disk.", "Restore Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    LoadBackupHistory();
                    return;
                }
                // STEP 1 — game-running guard (R2). Fresh synchronous read; do NOT use the cached isGameRunning field.
                if (CheckGameRunningStatus())
                {
                    MessageBox.Show("Please quit Dragon's Dogma 2 before restoring.", "Game Running",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // STEP 2 — Steam Cloud warning (run4 C). Declining costs nothing; no live file touched yet.
                DialogResult cloud = MessageBox.Show(
                    "Before restoring, fully EXIT Steam (or disable Dragon's Dogma 2 Cloud Saves in " +
                    "Steam > Properties). Otherwise Steam may re-upload your OLD save and overwrite this restore." +
                    "\n\nSavedrake snapshots your current save first, so you can undo this from File > Undo last restore." +
                    "\n\nContinue with the restore?",
                    "Exit Steam Before Restoring", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (cloud != DialogResult.Yes) { Status.Text = "Restore cancelled."; return; }

                // STEP 3 — validate the backup BEFORE touching live saves (R1).
                if (!ValidateBackup(filePath, out string reason))
                {
                    MessageBox.Show(reason + "\n\nYour current save files have not been touched.",
                        "Restore Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // STEP 3a — re-verify a manifest-bearing backup against its recorded hashes before touching live saves
                // (P1). Catches a backup that has bit-rotted on disk since it was created. Legacy backups without a
                // manifest are unaffected and restore as before.
                if (RestoreBlockedByManifest(filePath, out string integrityReason))
                {
                    MessageBox.Show("This backup failed its integrity check (" + integrityReason + ").\n\n" +
                        "Your current save files have not been touched.",
                        "Restore Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // STEP 3c — disk-space preflight: the restore extracts the backup into a staging folder on the save
                // volume before swapping it in. Refuse up front if that volume can't hold it (+ headroom), with the
                // live saves untouched, instead of failing partway through the extraction.
                long restoreNeeded = GetZipUncompressedSize(filePath);
                if (!HasFreeSpaceFor(textbox1.Text, restoreNeeded, out string restoreSpaceReason))
                {
                    MessageBox.Show("Cannot restore: " + restoreSpaceReason + ".\n\nYour current save files have not been touched.",
                        "Low Disk Space", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Operation lock: don't begin destructive restore work while a backup/restore is already running.
                if (_operationInProgress)
                {
                    MessageBox.Show("Please wait, another operation is already running.", "Busy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _operationInProgress = true;
                try
                {
                    // STEP 3b — pre-restore safety checkpoint (P4). RestoreTransactional deletes the current live save
                    // on success, so snapshot it first into a "(Pre-Restore)" backup the user can roll back to. If the
                    // snapshot can't be made, let the user decide rather than silently proceeding without a safety net.
                    Status.Text = "Creating pre-restore checkpoint...";
                    if (!CreatePreRestoreCheckpoint(textbox1.Text, textbox2.Text))
                    {
                        DialogResult proceed = MessageBox.Show(
                            "Savedrake could not create a safety snapshot of your current save before restoring.\n\n" +
                            "If you continue, your current save will be replaced and its current state will be lost.\n\n" +
                            "Restore anyway?",
                            "Pre-Restore Checkpoint Failed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (proceed != DialogResult.Yes) { Status.Text = "Restore cancelled."; return; }
                    }

                    // STEP 4 — delegate ALL destructive work (staging, swap, rollback, cleanup) to the transaction.
                    // Part 4: suppress the save watcher around the restore writes so our own writes don't trigger a backup
                    // (the _operationInProgress guard and the post-restore fingerprint baseline are independent backstops).
                    bool ok;
                    _suppressSaveWatcher = true;
                    try { ok = RestoreTransactional(filePath, textbox1.Text); }
                    finally { _suppressSaveWatcher = false; }

                    // STEP 5 — post-success UI. Undo is DISABLED on success: old saves went to a temp dir, not the
                    // recycle bin, so a recycle-bin Undo would silently no-op. Honest = disable it.
                    if (ok)
                    {
                        deletedFiles.Clear();
                        UpdateUndoButtonState();
                        LoadBackupHistory();
                        SortComboBoxItems();
                        listView.Sort();
                        // Change-aware autobackup (PR1): the live save now equals the just-restored backup. Set the
                        // baseline to it so the next autobackup tick sees no change and does NOT redundantly re-back-up
                        // the restored save (it is already captured by the backup it came from plus the (Pre-Restore)
                        // checkpoint). The next real in-game save changes the fingerprint and triggers a fresh backup.
                        _lastAutoBackupFingerprint = ComputeSaveFingerprint(textbox1.Text);
                    }
                }
                finally { _operationInProgress = false; }
            }
            else if (listView.SelectedItems.Count > 1)
            {
                MessageBox.Show("Please select only one Backup file at a time.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                MessageBox.Show("Please select a Backup file from the list first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ===== Transactional restore (Bucket 2 — kills R1 / R2 / R3) =====
        // Invariant: at every instant the user's saves exist intact in exactly one place — liveDir or rollbackDir.
        private bool RestoreTransactional(string filePath, string liveDir)
        {
            Status.Text = "Restore started... Please wait.";
            Log.Info("Restore started from: " + Path.GetFileName(filePath));
            string stagingDir  = CreateSiblingTempDir(liveDir, "savedrake_stage");
            string rollbackDir = CreateSiblingTempDir(liveDir, "savedrake_rollback");
            bool stagingStarted = false; // true once T4 begins: live then holds disposable STAGED content, not originals
            bool rollbackOk = true;      // success leaves true (old saves now stale); a failed recovery sets it false
            try
            {
                ExtractZipToStaging(filePath, stagingDir);                 // T1 stage + zip-slip guard
                FlattenNestedLayout(stagingDir);                          // T1b flatten nested win64_save wrapper(s) (R3)
                if (!VerifyStagedDir(stagingDir))                          // T2 verify on disk
                    throw new System.IO.IOException(
                        "The backup extracted but does not contain Dragon's Dogma 2 save data. Restore aborted.");

                MoveDirContents(liveDir, rollbackDir);                     // T3 move live aside (same-volume rename)
                stagingStarted = true;                                     // originals are all in rollbackDir now
                MoveDirContents(stagingDir, liveDir);                      // T4 move staged into live
                // T5 strip ReadOnly (R2). The restore is ALREADY committed here (staged saves are live), so this
                // cosmetic post-step must never throw into the catch below and trigger a rollback of a good
                // restore. Best-effort guard (ClearReadOnlyRecursive is itself non-throwing now too).
                try { ClearReadOnlyRecursive(liveDir); } catch { }        // T5 strip ReadOnly (R2)

                Status.Text = "Restore successful.";                       // T6 commit
                Log.Info("Restore successful from: " + Path.GetFileName(filePath));
                MessageBox.Show("Restore successful.", "Information",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            // CS0160: order is load-bearing. ZipException : Exception, UnauthorizedAccessException : SystemException,
            // IOException : SystemException — none derives from another, so the first three are order-free; Exception MUST be last.
            catch (Ionic.Zip.ZipException ze)       { rollbackOk = HandleRestoreFailure(ze,  rollbackDir, liveDir, stagingStarted); return false; }
            catch (UnauthorizedAccessException uae) { rollbackOk = HandleRestoreFailure(uae, rollbackDir, liveDir, stagingStarted); return false; }
            catch (System.IO.IOException ioEx)      { rollbackOk = HandleRestoreFailure(ioEx, rollbackDir, liveDir, stagingStarted); return false; }
            catch (Exception ex)                    { rollbackOk = HandleRestoreFailure(ex,  rollbackDir, liveDir, stagingStarted); return false; }
            finally
            {
                TryDeleteDir(stagingDir);
                // Delete the set-aside originals ONLY when provably safe. Success leaves rollbackOk==true (live now
                // holds the committed restore, so the old copy is stale); a FULLY recovered failure also leaves it
                // true with rollbackDir already emptied. When recovery did NOT fully succeed (rollbackOk==false),
                // rollbackDir may hold the user's ONLY intact copy — preserve it (the dialog named it for recovery).
                if (rollbackOk) TryDeleteDir(rollbackDir);
            }
        }

        private bool HandleRestoreFailure(Exception ex, string rollbackDir, string liveDir, bool stagingStarted)
        {
            bool recovered = Rollback(liveDir, rollbackDir, stagingStarted);
            Log.Error("Restore failed (recovered=" + recovered + ")", ex);
            Status.Text = recovered
                ? "Restore failed. Your previous save files were restored."
                : "Restore failed. See the dialog to recover your saves.";
            string tail = recovered
                ? "\n\nYour previous save files have been restored."
                : "\n\nWARNING: automatic recovery did not fully complete, but your ORIGINAL save files were NOT deleted." +
                  " Some may already be back in your save folder:\n" + liveDir +
                  "\nand the rest are preserved here:\n" + rollbackDir +
                  "\nMove any files from the second folder into the first to finish recovering.";
            MessageBox.Show(ex.Message + tail, "Restore Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return recovered; // tells RestoreTransactional whether rollbackDir is empty (safe to delete) or holds the only copy
        }

        // Moved to Savedrake.Core.RestoreEngine (WPF Phase 0). Thin forwarders below keep the ORIGINAL signatures and
        // instance/static-ness so RestoreTransactional + every other Main call site is untouched.
        private bool Rollback(string liveDir, string rollbackDir, bool stagingStarted) => RestoreEngine.Rollback(liveDir, rollbackDir, stagingStarted);

        private bool ValidateBackup(string filePath, out string reason) => RestoreEngine.ValidateBackup(filePath, out reason);

        private static bool IsRealSaveEntry(string entryFileName) => SaveScan.IsRealSaveEntry(entryFileName); // moved to Savedrake.Core.SaveScan

        private void ExtractZipToStaging(string filePath, string stagingDir) => RestoreEngine.ExtractZipToStaging(filePath, stagingDir);

        private static bool DetectNestedPrefix(string stagingDir) => RestoreEngine.DetectNestedPrefix(stagingDir);

        private static void FlattenNestedLayout(string stagingDir) => RestoreEngine.FlattenNestedLayout(stagingDir);

        private static bool VerifyStagedDir(string stagingDir) => RestoreEngine.VerifyStagedDir(stagingDir);

        private static string CreateSiblingTempDir(string liveDir, string tag) => RestoreEngine.CreateSiblingTempDir(liveDir, tag);

        private static void MoveDirContents(string sourceDir, string destDir) => RestoreEngine.MoveDirContents(sourceDir, destDir);

        private static void EmptyDir(string dir) => RestoreEngine.EmptyDir(dir);

        private static void ClearReadOnlyRecursive(string root) => RestoreEngine.ClearReadOnlyRecursive(root);

        private static void TryDeleteDir(string dir) => RestoreEngine.TryDeleteDir(dir);

        #endregion

        //Undetected
        #region View bakups on listView / LoadBackupHistory
        private void LoadBackupHistory()
        {
            // Define the path for the count file
            string countFilePath = AutoBackupCountFilePath;
            // Initialize the backup count
            int backupCount = 0;

            // Check if the count file exists
            if (File.Exists(countFilePath))
            {
                // Read the count from the file
                int.TryParse(File.ReadAllText(countFilePath), out backupCount);
            }

            // Check if the textbox2 path is empty or invalid
            if (string.IsNullOrWhiteSpace(textbox2.Text) || !Directory.Exists(textbox2.Text))
            {
                // If the path is empty or invalid, do nothing and return
                return;
            }

            listView.Items.Clear(); // Clear existing items

            // Load backup zips from the backup directory (.zip only — avoids opening unrelated files as zips).
            string[] zipFiles = Directory.GetFiles(textbox2.Text, "*.zip");

            // Sort the files by creation date, newest first
            Array.Sort(zipFiles, (x, y) => File.GetCreationTime(y).CompareTo(File.GetCreationTime(x)));

            foreach (string zipFilePath in zipFiles)
            {
                // Create a FileInfo object for each zip file
                FileInfo fileInfo = new FileInfo(zipFilePath);

                try
                {
                    using (Ionic.Zip.ZipFile zip = Ionic.Zip.ZipFile.Read(zipFilePath))
                    {
                        // R6: list every readable backup zip. The "SamMorrison9800" comment is treated as a
                        // provenance tag, not a hard filter — DotNetZip 1.16 can't store a comment on a 0-entry
                        // zip, so empty backups used to be invisible (and so couldn't be seen or deleted).
                        // Cheap integrity hint (P1): "Protected" if the backup carries an integrity manifest, else
                        // "Legacy". Reuses the zip already open here (no extra read). The full hash check that yields
                        // "Validated"/"Corrupt" runs only on demand via the "Validate all backups" right-click action,
                        // because hashing every backup on every refresh would be slow.
                        bool hasManifest = zip.Entries.Any(en => IsManifestEntry(en.FileName));
                        ListViewItem item = new ListViewItem(new[] { fileInfo.Name, FriendlyTime(fileInfo.CreationTime),
                            hasManifest ? "Protected" : "Legacy" });
                        item.UseItemStyleForSubItems = false;
                        // Integrity-column colour is applied by the owner-draw renderer (StatusColor), so it follows the theme.
                        item.Tag = fileInfo; // Store the FileInfo object in the Tag property
                        listView.Items.Add(item);
                        listView.Sort();
                    }
                }
                catch (Ionic.Zip.ZipException) // not a readable zip — skip it
                {
                }
                catch (Exception)
                {
                    // A file we can't open as a zip (e.g. locked / in use). Skip it silently rather than popping a
                    // modal dialog per file — LoadBackupHistory runs on startup and after every backup/restore/delete,
                    // so a folder with several such files would otherwise produce a storm of blocking dialogs.
                }
            }

            // Set the ListViewItemSorter property to an instance of the custom comparer
            listView.ListViewItemSorter = new ListViewItemDateComparer(1, SortOrder.Descending);

            // Sort the ListView
            listView.Sort();
            listView.Refresh();

            // Filter the zip files to include only those with "(Auto)" or "auto" in the name
            string[] autoBackupFiles = zipFiles.Where(file => (Path.GetFileName(file).StartsWith("(Auto)") || Path.GetFileName(file).StartsWith("auto")) && !IsPinnedBackup(Path.GetFileName(file))).ToArray();

            // Set the backup count to the number of autobackup files found
            backupCount = autoBackupFiles.Length;

            // Write the new count back to the file
            File.WriteAllText(countFilePath, backupCount.ToString());

            // Reflect the on-disk backup count in the Folders card's count box (mockup shows it beside the backup path).
            if (_countBackup != null) _countBackup.Text = listView.Items.Count.ToString();
        }

        // Right-click "Validate all backups" action (P1): run the FULL integrity check on every listed backup and mark
        // each row in the Integrity column — green "Validated", gray "Legacy", red "Corrupt"/"Missing". This hashes
        // every backup, so it can take a moment on large folders; the cheap "Protected"/"Legacy" hint is what shows on
        // load. Read-only: it never modifies, deletes, or touches a backup.
        private void ValidateAllBackups()
        {
            if (listView.Items.Count == 0)
            {
                MessageBox.Show("There are no backups to validate.", "Validate Backups", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Cursor previous = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            Status.Text = "Validating backups...";
            int validated = 0, legacy = 0, failed = 0;
            try
            {
                foreach (ListViewItem item in listView.Items)
                {
                    FileInfo fi = item.Tag as FileInfo;
                    string path = fi != null ? fi.FullName : Path.Combine(textbox2.Text, item.Text);
                    string state = File.Exists(path) ? ClassifyBackupFully(path) : "Missing";
                    while (item.SubItems.Count < 3) item.SubItems.Add(string.Empty);
                    item.UseItemStyleForSubItems = false;
                    item.SubItems[2].Text = state;
                    // Colour comes from the owner-draw renderer (StatusColor), theme-aware; just tally here.
                    if (state == "Validated") validated++;
                    else if (state == "Legacy") legacy++;
                    else failed++;
                }
            }
            finally { Cursor.Current = previous; }
            listView.Refresh();
            Status.Text = $"Validated {listView.Items.Count} backups: {validated} OK, {legacy} legacy, {failed} failed.";
            if (failed > 0)
                MessageBox.Show(failed + " backup(s) failed validation and may not be restorable. They are marked in red in the Integrity column.",
                    "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        #endregion

        //Hotkey
        #region Hotkey
        private void InitializeHotkey()
        {
            // Keep the delegate rooted so it can't be GC'd while a hook is live, but do NOT install the global
            // low-level keyboard hook at startup (R4). The hook is installed ONLY while actively recording a
            // hotkey and removed the instant recording ends — see InstallRecordingHook / RemoveRecordingHook.
            _proc = HookCallback;

            msgWindow = new MessageWindow();
            msgWindow.HotkeyPressed += MsgWindow_HotkeyPressed;
        }

        // R4: the WH_KEYBOARD_LL hook lives ONLY during hotkey recording. Both helpers run on the UI thread and
        // are idempotent, so _hookID is a single source of truth (no second SetHook overwriting/leaking the first).
        private void InstallRecordingHook()
        {
            if (_hookID == IntPtr.Zero)
                _hookID = SetHook(_proc);
        }

        private void RemoveRecordingHook()
        {
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        private void MsgWindow_HotkeyPressed(object sender, EventArgs e)
        {
            // Trigger a manual backup; the feedback sound now plays for all backup paths (see PlayBackupSound).
            BackupOperation(false);
        }

        private string GetHotkeyString()
        {
            lock (_syncLock)
            {
                StringBuilder hotkeyBuilder = new StringBuilder();
                if (_controlPressed) hotkeyBuilder.Append("Ctrl + ");
                if (_shiftPressed) hotkeyBuilder.Append("Shift + ");
                if (_altPressed) hotkeyBuilder.Append("Alt + ");
                hotkeyBuilder.Append(ConvertKeyToString(_currentMainKey)); // Call the new method here
                return hotkeyBuilder.ToString();
            }
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                Keys key = (Keys)vkCode;

                if (_isRecordingHotkey)
                {
                    // Esc cancels recording. R4: do NOT swallow it — fall through to CallNextHookEx so Esc still
                    // reaches whatever app the user is in. BeginInvoke keeps the hook callback fast and non-blocking.
                    if (key == Keys.Escape)
                    {
                        lock (_syncLock)
                        {
                            _isRecordingHotkey = false;
                            RemoveRecordingHook();
                            this.BeginInvoke((MethodInvoker)delegate
                            {
                                checkbox_hot.Checked = false;
                                Status.Text = "Hotkey recording cancelled.";
                                MessageBox.Show("Hotkey recording cancelled.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            });
                        }
                        return CallNextHookEx(_hookID, nCode, wParam, lParam);
                    }
                    // Enter finishes recording. Registration is validated (real key + a modifier); on an invalid
                    // combo we stay in recording so the user can fix it. R4: Enter is NOT swallowed either.
                    else if (key == Keys.Enter)
                    {
                        lock (_syncLock)
                        {
                            if (RegisterHotKeyFunction(true))
                            {
                                _isRecordingHotkey = false;
                                RemoveRecordingHook();
                                string hotkeyString = GetHotkeyString();
                                this.BeginInvoke((MethodInvoker)delegate
                                {
                                    textbox3.Text = textbox3.Text.Replace("(Enter to finish\\Esc to cancle)", "");
                                    Status.Text = "Hotkey recorded.";
                                    MessageBox.Show($"Hotkey set to: {hotkeyString}", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                });
                            }
                            else
                            {
                                this.BeginInvoke((MethodInvoker)delegate
                                {
                                    Status.Text = "Hold Ctrl/Shift/Alt and press a key, then Enter.";
                                });
                            }
                        }
                        return CallNextHookEx(_hookID, nCode, wParam, lParam);
                    }

                    // Check for modifier keys
                    _controlPressed = (Control.ModifierKeys & Keys.Control) != 0;
                    _shiftPressed = (Control.ModifierKeys & Keys.Shift) != 0;
                    _altPressed = (Control.ModifierKeys & Keys.Alt) != 0;

                    // hotkey fixed
                    if (key != Keys.ControlKey && key != Keys.ShiftKey && key != Keys.Menu)
                    {
                        lock (_syncLock)
                        {
                            // If any modifiers are pressed or no modifiers are pressed, set the current main key
                            if (_controlPressed || _shiftPressed || _altPressed || (!_controlPressed && !_shiftPressed && !_altPressed))
                            {
                                _currentMainKey = key;
                                UpdateHotkeyDisplay();
                            }
                        }
                    }
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }


        private void UpdateHotkeyDisplay()
        {
            string hotkeyString = GetHotkeyString();


            // Update the textbox on the UI thread
            this.Invoke((MethodInvoker)delegate
            {
                //textbox3.ForeColor = Color.OrangeRed;
                textbox3.Text = $"{hotkeyString}\n" +
                $"(Enter to finish\\Esc to cancle)";
            });
        }


        // Event handler for checkbox_hot
        private void checkbox_hot_CheckedChanged(object sender, EventArgs e)
        {
            if (checkbox_hot.Checked)
            {
                // Recording is a user action — never auto-start it while restoring saved UI state on load.
                if (isLoading) return;

                // Start fresh: forget any half-entered combo and drop the previous binding so it can't fire
                // while the user presses keys for a new one. The hook goes live ONLY now, during recording (R4).
                lock (_syncLock)
                {
                    _currentMainKey = Keys.None;
                    _controlPressed = _shiftPressed = _altPressed = false;
                }
                UnregisterHotKey(msgWindow.Handle, hotkeyId);
                _isRecordingHotkey = true;
                InstallRecordingHook();
                textbox3.Text = "Press your keys \n" +
                $"(Enter to finish\\Esc to cancle)";
                Status.Text = "Recording Hotkey...";
            }
            else
            {
                _isRecordingHotkey = false;
                RemoveRecordingHook(); // unhook the instant the toggle goes off (R4)
                ResetHotkey();
                textbox3.Text = " ";
                if (!isLoading) Status.Text = "Hotkey disabled.";
            }
        }
        private void ResetHotkey()
        {
            _isRecordingHotkey = false;
            _currentMainKey = Keys.None;
            _controlPressed = _shiftPressed = _altPressed = false;
            UnregisterHotKey(msgWindow.Handle, hotkeyId);
            if (!isLoading) Status.Text = "Hotkey reset.";
        }

        // Registers the current combo as a global hotkey; returns true on success. R4: refuses an empty key
        // (vk==0 / Keys.None) or a bare key with no modifier — either would fail to register or hijack a plain
        // key (e.g. a gameplay key) system-wide. `announce` gates user-facing dialogs (false on silent load).
        private bool RegisterHotKeyFunction(bool announce)
        {
            lock (_syncLock)
            {
                uint modifiers = (uint)((_controlPressed ? MOD_CONTROL : 0) |
                                        (_shiftPressed ? MOD_SHIFT : 0) |
                                        (_altPressed ? MOD_ALT : 0));

                if (_currentMainKey == Keys.None || modifiers == 0)
                {
                    if (announce)
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            MessageBox.Show("Pick a key together with at least one modifier (Ctrl, Shift, or Alt).",
                                "Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        });
                    return false;
                }

                UnregisterHotKey(msgWindow.Handle, hotkeyId); // replace any prior binding before re-registering
                if (!RegisterHotKey(msgWindow.Handle, hotkeyId, modifiers, (uint)_currentMainKey))
                {
                    if (announce)
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            MessageBox.Show("Failed to register the hotkey.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        });
                    return false;
                }
                return true;
            }
        }

        // (Removed a dead WndProc override that handled WM_HOTKEY: the global hotkey is registered against
        // msgWindow.Handle, so Windows posts WM_HOTKEY to MessageWindow.WndProc — the Main form never received it,
        // making that branch unreachable. The hotkey path runs via MsgWindow_HotkeyPressed.)
        #endregion

        //tray
        #region
        private void TrayIcon_DoubleClick(object sender, EventArgs e)
        {
            // Restore the window


            // Show the form in the taskbar
            //this.ShowInTaskbar = true;
            this.Show();
            this.WindowState = FormWindowState.Normal;
            // Hide the tray icon
            trayIcon.Visible = false;

            // Uncheck the checkbox_tray
            //checkbox_tray.Checked = false;
        }

        private void Show_Click(object sender, EventArgs e)
        {


            // Show the form in the taskbar
            //this.ShowInTaskbar = true;
            this.Show();
            this.WindowState = FormWindowState.Normal;
            // Hide the tray icon
            trayIcon.Visible = false;

            // Hide the tray icon if the checkbox is unchecked
            //trayIcon.Visible = checkbox_tray.Checked;
        }

        private void Quit_Click(object sender, EventArgs e)
        {
            
            trayIcon.Visible = false;
            this.Opacity = 0.0;
            this.Show();
            this.WindowState = FormWindowState.Normal;
            
            Application.Exit();

        }
        #endregion

        //Undo
        #region
        // Method to record the file path before deleting


        private void RecordDeletion(string filePath, bool isNewDel)
        {
            // If a new action is initiated, clear the existing list
            if (isNewDel)
            {
                deletedFiles.Clear();
            }

            // Add the file path to the list
            deletedFiles.Add(filePath);
        }
        // Method to update the undo button's enabled state

        private void UpdateUndoButtonState()
        {
            // Enable the button if there are files in the deletedFiles list, otherwise disable it
            button_undo.Enabled = deletedFiles.Count > 0;

            // Theme-aware background (enabled = panel, disabled = dimmer panel).
            button_undo.BackColor = button_undo.Enabled ? Theme.P.Panel : Theme.P.PanelAlt;

            // The on-form Undo button is hidden in the variant-B layout; keep its File-menu twin in sync.
            if (_undoDeleteMenuItem != null) _undoDeleteMenuItem.Enabled = deletedFiles.Count > 0;
        }


        // Method to restore the deleted files
        private void RestoreDeletedFiles()
        {
            // Shell32 hands back COM objects (RCWs) that must be released, or their native shell handles linger
            // until a GC happens to finalize them. Release each one (the per-item FolderItem every loop, and the
            // Shell/Folder/FolderItems at the end) via Marshal.ReleaseComObject in a finally.
            Shell32.Shell shell = null;
            Folder recycleBin = null;
            FolderItems items = null;
            try
            {
                shell = new Shell32.Shell();
                recycleBin = shell.NameSpace(10);
                items = recycleBin.Items();

                foreach (string filePath in deletedFiles)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        FolderItem fi = items.Item(i);
                        try
                        {
                            string fileName = recycleBin.GetDetailsOf(fi, 0);
                            if (Path.GetExtension(fileName) == "")
                            {
                                fileName += Path.GetExtension(fi.Path); // Necessary for systems with hidden file extensions
                            }
                            string filePathInBin = recycleBin.GetDetailsOf(fi, 1);
                            string fileOriginalPath = Path.Combine(filePathInBin, fileName);
                            if (filePath == fileOriginalPath)
                            {
                                // Get the creation date of the file
                                string fileCreationDate = recycleBin.GetDetailsOf(fi, 4);

                                // Show file path and creation date
                                Console.WriteLine($"Restoring: {fileOriginalPath} (Created: {fileCreationDate})");

                                // Check if the file already exists at the original location
                                if (File.Exists(fileOriginalPath))
                                {
                                    // Replace the file at the original location
                                    File.Delete(fileOriginalPath);
                                }

                                // Move the file from the Recycle Bin to the original location
                                File.Move(fi.Path, fileOriginalPath);
                                break;
                            }
                        }
                        finally
                        {
                            if (fi != null) Marshal.ReleaseComObject(fi);
                        }
                    }
                }

                // Reset the record
                deletedFiles.Clear();
            }
            finally
            {
                if (items != null) Marshal.ReleaseComObject(items);
                if (recycleBin != null) Marshal.ReleaseComObject(recycleBin);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        private void button_undo_Click(object sender, EventArgs e)
        {
            // Check if there are files to restore
            if (deletedFiles.Count > 0)
            {
                // Create a message detailing the files to be restored with their creation dates
                string message = "The following file(s) will be restored from the Recycle Bin:\n";
                foreach (string filePath in deletedFiles)
                {
                    // Get the creation date of the file
                    DateTime creationDate = File.GetCreationTime(filePath);
                    // Append the file path and creation date to the message
                    message += $"{filePath} (Created: {creationDate})\n";
                }

                // Check for files that will be replaced
                List<string> filesToBeReplaced = deletedFiles.Where(File.Exists).Select(filePath =>
                {
                    // Get the creation date of the file
                    DateTime creationDate = File.GetCreationTime(filePath);
                    // Return the file path and creation date
                    return $"{filePath} (Created: {creationDate})";
                }).ToList();

                if (filesToBeReplaced.Count > 0)
                {
                    message += "\n\nThe following file(s) will be replaced:\n" +
                               string.Join("\n", filesToBeReplaced);
                }

                // Show the confirmation dialog
                var confirmResult = MessageBox.Show(message + "\n\nDo you want to proceed with the undo operation?",
                                                    "Confirm Undo",
                                                    MessageBoxButtons.YesNo,
                                                    MessageBoxIcon.Question);

                // If the user confirms, proceed with the restoration
                if (confirmResult == DialogResult.Yes)
                {
                    RestoreDeletedFiles();
                    // Clear the deletedFiles list after restoring
                    deletedFiles.Clear();
                    // Update the undo button state
                    UpdateUndoButtonState();
                    LoadBackupHistory();
                    SortComboBoxItems(); //Must
                    listView.Sort();
                    Status.Text = "Undo successful.";
                }

            }
            else
            {
                if (!isLoading)
                {
                    MessageBox.Show("There are no files to restore.", "Undo Not Available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

            }
        }

        #endregion

        //Sort listView //Undetected
        #region Sorting listView
        // Event handler for sorting the ListView
        private int sortColumn = -1;
        private SortOrder sortOrder = SortOrder.None;

        private void listView_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            // Determine if the clicked column is already the column being sorted.
            if (e.Column == sortColumn)
            {
                // Reverse the current sort direction.
                if (sortOrder == SortOrder.Ascending)
                    sortOrder = SortOrder.Descending;
                else
                    sortOrder = SortOrder.Ascending;
            }
            else
            {
                // Set the column number that is to be sorted; default to ascending.
                sortColumn = e.Column;
                sortOrder = SortOrder.Ascending;
            }

            // Perform the sort with these new sort options.
            listView.ListViewItemSorter = new ListViewItemDateComparer(e.Column, sortOrder);
            listView.Sort();
        }
        #endregion

        //Listview mouse clicks //Undetected
        #region
        private void listView_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // Capture FocusedItem once and guard for null — a double-click in empty list space (or below the last
            // row) leaves FocusedItem null. The old code dereferenced it in the try (NRE) AND AGAIN in the catch
            // (a second NRE thrown from inside the catch, which is unhandled → crash dialog).
            ListViewItem focused = listView.FocusedItem;
            if (focused == null || !focused.Bounds.Contains(e.Location)) return;

            try
            {
                // The full path of the file is stored in the Tag property
                string filePath = focused.Tag.ToString();
                System.Diagnostics.Process.Start(filePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("An error occurred while opening the file: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LoadBackupHistory();
                SortComboBoxItems();
            }
        }

        private void listView_MouseClick(object sender, MouseEventArgs e)
        {
            // No manual context-menu Show here: the working menu (built in the constructor with wired Rename/Delete
            // handlers) is assigned to listView.ContextMenuStrip and is shown AUTOMATICALLY by WinForms on
            // right-click. The previous contextMenuStrip.Show(...) popped the SEPARATE designer-field menu whose
            // Rename/Delete have no handlers — a dead, non-functional duplicate — so it has been removed.
        }
        #endregion

        //Listview rename //Undetected
        #region
        private void RenameMenultem_Click(object sender, EventArgs e)
        {
            // Logic to handle the rename action
            if (listView.SelectedItems.Count == 1)
            {
                listView.SelectedItems[0].BeginEdit();
            }
        }

        // Pinning (part 3): toggle the selected backup's pinned state by renaming it to add/remove the " [PINNED]"
        // token. A pinned backup is protected from automatic cleanup and is not counted toward the autobackup limit.
        private void PinMenuItem_Click(object sender, EventArgs e)
        {
            if (listView.SelectedItems.Count != 1)
            {
                MessageBox.Show("Select one backup to pin or unpin.", "Pin backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string path;
            try { path = listView.SelectedItems[0].Tag.ToString(); } catch { return; }
            try
            {
                if (!File.Exists(path)) { LoadBackupHistory(); return; }
                bool pinned = IsPinnedBackup(Path.GetFileName(path));
                string target = pinned ? UnpinnedPath(path) : PinnedPath(path);
                if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                {
                    target = MakeUniquePath(target); // never clobber an existing backup
                    File.Move(path, target);
                }
                LoadBackupHistory();
                listView.Sort();
                Status.Text = pinned ? "Backup unpinned." : "Backup pinned. It won't be removed by automatic cleanup.";
            }
            catch (Exception ex)
            {
                Log.Warn("Pin/unpin failed: " + ex.Message);
                MessageBox.Show("Could not change the pinned state: " + ex.Message, "Pin backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void listView_AfterLabelEdit(object sender, LabelEditEventArgs e)
        {
            if (e.Label != null)
            {
                // Get the original file name and extension
                string originalFileName = listView.Items[e.Item].Text;
                string originalExtension = Path.GetExtension(originalFileName);

                // Get the new file name without changing the extension
                string newFileNameWithoutExtension = Path.GetFileNameWithoutExtension(e.Label);
                string newFileName = newFileNameWithoutExtension + originalExtension;

                // Now proceed to rename the file with the new name but original extension
                string oldFilePath = ((FileInfo)listView.Items[e.Item].Tag).FullName;
                string newFilePath = Path.Combine(Path.GetDirectoryName(oldFilePath), newFileName);

                try
                {
                    File.Move(oldFilePath, newFilePath);
                    // Update the Tag and Text properties with the new file info
                    listView.Items[e.Item].Tag = new FileInfo(newFilePath);
                    listView.Items[e.Item].Text = newFileName;


                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error renaming the file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    e.CancelEdit = true; // Cancel the label edit if there's an error
                }

            }

        }
        #endregion

        //Listview Delete //Undetected
        #region
        private void DeleteMenuItem_Click(object sender, EventArgs e)
        {
            // Check if at least one item is selected in the ListView
            if (listView.SelectedItems.Count > 0)
            {
                // Confirm deletion
                var confirmResult = MessageBox.Show("Are you sure you want to send the selected file(s) to the Recycle Bin?\n" + string.Join("\n", listView.SelectedItems.Cast<ListViewItem>().Select(item => item.Text)),
                                                    "Confirm Delete",
                                                    MessageBoxButtons.YesNo,
                                                    MessageBoxIcon.Question);

                if (confirmResult == DialogResult.Yes)
                {
                    // Snapshot the selected file paths before mutating the ListView.
                    // LoadBackupHistory() clears listView.Items, so iterating
                    // SelectedItems directly and refreshing inside the loop would
                    // throw or skip files on multi-select delete.
                    List<string> filesToDelete = listView.SelectedItems
                        .Cast<ListViewItem>()
                        .Select(item => Path.Combine(textbox2.Text, item.Text))
                        .ToList();

                    bool isNewDel = true;
                    try
                    {
                        foreach (string filePath in filesToDelete)
                        {
                            RecordDeletion(filePath, isNewDel);
                            isNewDel = false;

                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(filePath,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        }

                        LoadBackupHistory();
                        listView.Sort();
                        Status.Text = "Backup(s) deleted sucessfully.";
                        UpdateUndoButtonState();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("An error occurred while deleting the file(s): " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show("Please select at least one Backup file from the list to delete.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        #endregion

        //Listview Keydown //Undetected
        #region
        private void ListView_KeyDown(object sender, KeyEventArgs e)
        {
            // Check if the 'Del' key is pressed and items are selected
            if (e.KeyCode == Keys.Delete && listView.SelectedItems.Count > 0)
            {
                DeleteMenuItem_Click(sender, e);
            }
            // Check if the 'F2' key is pressed and exactly one item is selected
            else if (e.KeyCode == Keys.F2 && listView.SelectedItems.Count == 1)
            {
                RenameMenultem_Click(sender, e);
            }
            // Check if 'Ctrl' is held down and 'A' is pressed
            else if (e.Control && e.KeyCode == Keys.A)
            {
                // Check if at least one item is already selected
                if (listView.SelectedItems.Count > 0)
                {
                    // Select all items in the ListView
                    foreach (ListViewItem item in listView.Items)
                    {
                        item.Selected = true;
                    }
                }
                // Prevent the default 'Ctrl + A' behavior (e.g., text box select all)
                e.Handled = true;
            }
        }



        #endregion


        //Update
        #region

        private async Task ExecuteUpdateProcess()
        {
            bool updateAvailable = await CheckForUpdatesAsync();
            if (updateAvailable)
            {
                // Resolve the updater next to THIS exe (install dir), not the process working directory — a bare
                // relative name fails to launch when the app was started with a different CWD (e.g. a shortcut
                // whose "Start in" differs), even though Savedrake-Updater.exe sits right beside Savedrake.exe.
                try { Process.Start(Path.Combine(Application.StartupPath, "Savedrake-Updater.exe")); }
                catch { MessageBox.Show("A new update is available. But Savedrake-Updater.exe could not be started. Make sure the file is present within Savedrake directory.", "Savedrake-Updater.exe Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
            else if (!isLoading)
            {
                // Manual check (the startup check stays silent via isLoading): give feedback either way.
                if (IsAPIError)
                    MessageBox.Show("Could not check for updates. Please check your internet connection and try again.", "Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    MessageBox.Show("Your Savedrake is up to date.", "No Updates Available", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private async Task<bool> CheckForUpdatesAsync()
        {
            if (TryParseVersion(GetCurrentVersion(), out Version currentVersion) &&
                TryParseVersion(await GetLatestVersionFromGit("sammorrison9800", "Savedrake"), out Version latestVersion))
            {
                return latestVersion > currentVersion;
            }
            return false;
        }

        private string GetCurrentVersion()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version.ToString();
        }

        private bool TryParseVersion(string versionString, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(versionString))
            {
                return false;
            }

            // GitHub release tags are conventionally prefixed with 'v' (e.g. "v1.2.5").
            // Strip it so a future v-prefixed tag doesn't silently break update checks.
            if (versionString.Length > 1 && (versionString[0] == 'v' || versionString[0] == 'V'))
            {
                versionString = versionString.Substring(1);
            }

            string[] versionParts = versionString.Split('.');
            if (versionParts.Length < 2 || versionParts.Length > 4)
            {
                return false;
            }

            foreach (string part in versionParts)
            {
                if (!int.TryParse(part, out int _))
                {
                    return false;
                }
            }

            try
            {
                version = new Version(versionString);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private bool IsAPIError = false;
        private async Task<string> GetLatestVersionFromGit(string owner, string repo)
        {
            IsAPIError = false; // reset per check, so a previous failure doesn't stick across re-checks
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15); // a stalled connection must not hang the ~100s default
                    client.DefaultRequestHeaders.Add("User-Agent", "Savedrake Update Checker");

                    using (HttpResponseMessage response = await client.GetAsync(apiUrl))
                    {
                        response.EnsureSuccessStatusCode();

                        string responseBody = await response.Content.ReadAsStringAsync();
                        JObject json = JObject.Parse(responseBody);

                        // Null-safe: a 200 whose JSON lacks tag_name returns null (no NRE) and is treated as
                        // "no version found" -> "up to date" rather than an alarming error.
                        return json.Value<string>("tag_name");
                    }
                }
            }
            catch (HttpRequestException)
            {
                IsAPIError = true; // offline / DNS / refused
                return null;
            }
            catch (Exception)
            {
                // Timeout (TaskCanceledException), JSON parse error, or any other transient failure: treat like a
                // connectivity error. Do NOT pop a modal here — that fired even during the SILENT startup check;
                // ExecuteUpdateProcess reports it only on the manual path (isLoading == false).
                IsAPIError = true;
                return null;
            }
        }



        #endregion
        private void button_ref_Click(object sender, EventArgs e)
        {
            LoadBackupHistory();
            combobox_auto.SelectedIndex = combobox_auto.Items.IndexOf(combobox_auto.Text);

            SortComboBoxItems();
            listView.Sort();
            foreach (ListViewItem item in listView.Items)
            {
                item.ToolTipText = "Right-click to rename/delete files.";
            }
            

        }

        //Form Loading and Closing
        #region
        private async void Main_Load(object sender, EventArgs e)
        {
            isLoading = true;
            LoadSettings();
            // First run: if no save folder is configured yet, offer to auto-detect the DD2 save folder via Steam.
            if (string.IsNullOrWhiteSpace(textbox1.Text)) DetectAndOfferSaveFolder(false);
            // Apply the saved UI theme (default dark) before the list populates so it owner-draws themed.
            Theme.Apply(this);
            UpdateThemeMenuText();
            // Build the warm-dark "variant B" card layout: reparents the controls above into cards, adds the branded
            // header + surfaced autobackup settings, and restyles the list/buttons. Runs after the theme + settings load
            // so it moves live, populated controls; it re-applies the theme to the new cards itself.
            BuildVariantBLayout();
            //randomlyGeneratedToolStripMenuItem.Checked = true;
            LoadBackupHistory();

            foreach (ListViewItem item in listView.Items)
            {
                item.ToolTipText = "Right-click to rename/delete files.";
            }


            CreateVersionText();
            listViewColumnResize();
            UpdateUndoButtonState();

            bool checkBox1Value = false; // Default value if the XML file doesn't exist

            try
            {
                // Attempt to load the checkBox values from the XML file
                XElement root = XElement.Load(UpdaterXmlPath);
                checkBox1Value = bool.Parse(root.Element("CheckBox1").Value);
            }
            catch (System.IO.FileNotFoundException)
            {
                // Log the error or inform the user that the settings file doesn't exist
                // For example: Console.WriteLine("Settings file not found. Proceeding with defaults.");
                //await ExecuteUpdateProcess();
            }
            catch (Exception)
            {
                // Handle other potential exceptions here
                // For example: Console.WriteLine($"An error occurred: {ex.Message}");
            }

            // Conditionally run the update process based on checkBox1's value
            if (!checkBox1Value)
            {
                await ExecuteUpdateProcess();
            }

            

            isLoading = false;
            //this.Focus();
        }

        private void CreateVersionText()
        {
            string version = GetCurrentVersion();
            if (TryParseVersion(version, out Version parsedVersion))
            {
                try
                {
                    File.WriteAllText(VersionFilePath, parsedVersion.ToString());
                }
                catch
                {
                    // Best-effort: under the asInvoker manifest the working dir (e.g. Program
                    // Files) can be read-only. A throw here would escape into the async-void
                    // Main_Load and crash startup; version.txt is rewritten on the next run.
                }
            }
        }

        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            // R7a: stop the autobackup timer FIRST, before any teardown. It now marshals Elapsed onto this form
            // (SynchronizingObject), so a tick firing while the handle is being destroyed would otherwise throw.
            autobackupTimer?.Stop();

            try
            {
                SaveSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error in Saving Settings: {ex.Message}");
                // Optionally set e.Cancel = true; to prevent the form from closing if saving settings is critical
            }

            // If SaveSettings is successful, or you want to proceed regardless
            RemoveRecordingHook(); // R4: drop the keyboard hook on close (e.g. if closed mid-recording)
            UnregisterHotKey(msgWindow.Handle, hotkeyId);
            msgWindow.DestroyHandle(); // Clean up the message-only window

            autobackupTimer?.Dispose(); // R7a: the timer was previously never disposed
            StopSaveWatcher(); // part 4: stop, unsubscribe, dispose the save watcher + the quiesce timer (nulls both)

            if (_watcher != null)
            {
                _watcher.Stop();
                _watcher.Dispose();
            }

            // trayIcon is created with `new NotifyIcon()` (not registered with components),
            // so it is never auto-disposed; without this its tray icon can linger as a
            // "ghost" until the user hovers over it.
            if (trayIcon != null)
            {
                trayIcon.Dispose();
            }
        }


        #endregion


        //Backup file name related
        #region
        // CheckedChanged event for randomlyGeneratedToolStripMenuItem
        private void randomlyGeneratedToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            // If this item is checked, uncheck the other and disable it
            if (randomlyGeneratedToolStripMenuItem.Checked)
            {
                randomlyGeneratedToolStripMenuItem.Enabled = false;
                timeStampedToolStripMenuItem.Checked = false;
                timeStampedToolStripMenuItem.Enabled = true;
                if (!isLoading)
                {
                    MessageBox.Show("Backup file names will now be Randomly Generated.", "Backup Name Format", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

            }

        }

        private void timeStampedToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            // If this item is checked, uncheck the other and disable it
            if (timeStampedToolStripMenuItem.Checked)
            {
                timeStampedToolStripMenuItem.Enabled = false;
                randomlyGeneratedToolStripMenuItem.Checked = false;
                randomlyGeneratedToolStripMenuItem.Enabled = true;
                if (!isLoading)
                {
                    MessageBox.Show("Backup file names will now be Time Stamped.", "Backup Name Format", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

            }

        }
        #endregion

        //Sounds
        // Success/error feedback now plays for EVERY backup (manual button, hotkey, and autobackup), not just
        // hotkey-triggered ones — the old _isUsingHotkey gate has been removed.
        public void PlaySoundFromResource()
        {
            PlayBackupSound("success.wav");
        }
        public void PlaySoundFromResource2()
        {
            PlayBackupSound("error.wav");
        }

        // One reusable SoundPlayer rather than a fresh (undisposed) instance per backup. Play() with SND_ASYNC
        // replaces whatever it was playing, so swapping SoundLocation and reusing is correct and leak-free.
        private SoundPlayer _backupSoundPlayer;

        // Plays a short feedback .wav that ships next to the executable (Content/CopyToOutputDirectory).
        // Resolved against the install dir (Application.StartupPath) rather than the current working directory,
        // so it works no matter how the app was launched, including from a read-only Program Files install.
        // Best-effort: a missing file or any audio/IO error must never disrupt or fail a backup.
        private void PlayBackupSound(string wavFileName)
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, wavFileName);
                if (!File.Exists(path))
                    return;
                if (_backupSoundPlayer == null)
                    _backupSoundPlayer = new SoundPlayer();
                _backupSoundPlayer.SoundLocation = path;
                _backupSoundPlayer.Play(); // async (SND_ASYNC); does not block the UI thread
            }
            catch
            {
                // Sound is non-essential; swallow any audio/IO failure.
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private async void checkForUpdateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Run the in-app version check (shows "up to date" / "couldn't check", and launches the updater only
            // when an update actually exists) instead of blindly launching the updater every time. isLoading is
            // false here, so ExecuteUpdateProcess is allowed to show its result.
            await ExecuteUpdateProcess();
        }

        private void fAQToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DialogResult dialogResult = MessageBox.Show("This will open the FAQ page on Nexusmods in your default web browser. Do you want to proceed?", "Open FAQ", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dialogResult == DialogResult.Yes)
            {
                Process.Start("https://www.nexusmods.com/dragonsdogma2/mods/772/?tab=posts");
            }
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DialogResult dialogResult = MessageBox.Show("This will open Savedrake's Nexusmods page in your default web browser. Do you want to proceed?", "Open About", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dialogResult == DialogResult.Yes)
            {
                Process.Start("https://www.nexusmods.com/dragonsdogma2/mods/772?tab=description");
            }
        }

        private void restoreDefaultToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DialogResult dialogResult = MessageBox.Show("This will reset all settings to default. Do you want to proceed?", "Reset Settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dialogResult == DialogResult.Yes)
            {
                // Stop autobackup first so a timer tick can't re-create count_of_autobackups.txt between the
                // delete below and the exit (e.g. while the confirmation modal is up with the game running).
                autobackupTimer?.Stop();

                // Delete each %APPDATA% state file independently and track failures, so one locked file neither
                // skips the others nor lets us falsely report success.
                bool allDeleted = true;
                foreach (string path in new[] { SettingsFilePath, UpdaterXmlPath, VersionFilePath, AutoBackupCountFilePath })
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch { allDeleted = false; }
                }
                // Also clear any legacy working-dir copies left behind by the COPY-based migration, so the reset
                // can't be silently undone by a fallback that still reads the old file. This now covers ALL FOUR
                // migrated state files — previously only the updater xml + version were cleared, so a reset was
                // silently reverted for settings + the autobackup count on the next launch.
                DeleteLegacyCopies("savedrake_settings.xml");
                DeleteLegacyCopies("count_of_autobackups.txt");
                DeleteLegacyCopies("savedrake-updater.xml");
                DeleteLegacyCopies("version.txt");

                textbox1.Text = null;
                textbox2.Text = null;
                checkbox_auto.Checked = false;
                checkbox_hot.Checked = false;
                checkbox_tray.Checked = false;
                listView.Items.Clear();

                if (!allDeleted)
                {
                    // Be honest: at least one file couldn't be removed (in use / permission), so a restart would
                    // bring the old settings back. Don't claim success or exit.
                    MessageBox.Show("Some settings files could not be deleted (they may be in use). Close Savedrake and delete them from %APPDATA%\\Savedrake manually, or try again.", "Reset Incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                MessageBox.Show("Reset successful. Please restart Savedrake.", "Reset Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
                // Dispose the tray icon before exiting so it doesn't ghost in the notification area. We keep
                // Environment.Exit (NOT Application.Exit) on purpose: Application.Exit would fire Main_FormClosing
                // -> SaveSettings and re-create the settings file we just deleted, undoing the reset.
                try { trayIcon?.Dispose(); } catch { }
                Environment.Exit(0);
            }
        }


        private void toolStripTextBox2_Keydown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (int.TryParse(toolStripTextBox2.Text, out int result))
                {

                    if (!isLoading)
                    {
                        MessageBox.Show($"The maximum number of autobackups set to {toolStripTextBox2.Text}.", "Limit Autobackup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }

                }
                else
                {
                    MessageBox.Show("Error: Please enter an integer value.", "Wrong Input", MessageBoxButtons.OK, MessageBoxIcon.Error); // Prompt the user to enter an integer
                    toolStripTextBox2.Text = "800";
                }
            }
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // Release the mutex when the form is closed
            if (mutex != null)
            {
                mutex.ReleaseMutex();
            }
        }


    }
}

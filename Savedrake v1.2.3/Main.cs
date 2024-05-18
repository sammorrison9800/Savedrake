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
        private bool isAutoBackupEnabled = false; //(Autobackup feature)
        private ManagementEventWatcher _watcher; //(Autobackup feature)
        private bool isGameRunning; //(Autobackup feature)
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

        private bool _isUsingHotkey = false;
        private readonly object _syncLock = new object();
        private volatile bool _EnterPressed = false;
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
            contextMenuStrip.Items.Add(renameMenultem);
            contextMenuStrip.Items.Add(deleteMenuItem);
            renameMenultem.Click += RenameMenultem_Click;
            deleteMenuItem.Click += DeleteMenuItem_Click;
            listView.ContextMenuStrip = contextMenuStrip;

            //listView related
            listView.ColumnClick += new ColumnClickEventHandler(listView_ColumnClick);
            listView.MouseDoubleClick += listView_MouseDoubleClick;
            listView.MouseClick += listView_MouseClick;
            listView.AfterLabelEdit += listView_AfterLabelEdit;
            listView.AfterLabelEdit += new LabelEditEventHandler(listView_AfterLabelEdit);
            listView.ContextMenuStrip = contextMenuStrip;
            listView.KeyDown += ListView_KeyDown;
            listView.ItemSelectionChanged += ListView_ItemSelectionChanged;

            #endregion

            //tray
            #region
            // Initialize the NotifyIcon component
            trayIcon = new NotifyIcon();
            trayIcon.Icon = this.Icon; // Set the icon
            trayIcon.Visible = false; // Hide the icon initially
            trayIcon.DoubleClick += TrayIcon_DoubleClick; // Event handler for double-clicking the icon
            trayIcon.Text = "Savedrake v1.2.4";
            this.Resize += new System.EventHandler(this.Main_Resize);
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
            this.combobox_auto.Validating += new System.ComponentModel.CancelEventHandler(combobox_auto_Validating);

            //ToolStripTextBox2 Autobackup Limnit
            //this.toolStripTextBox2.Leave += new EventHandler(toolStripTextBox2_Leave);
            //this.toolStripTextBox2.LostFocus += new EventHandler(toolStripTextBox2_Leave);
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

            

            var serializer = new XmlSerializer(typeof(AppSettings));
            using (var writer = new StreamWriter("savedrake_settings.xml"))
            {
                serializer.Serialize(writer, settings);
            }
        }



        private void LoadSettings()
        {
            if (File.Exists("savedrake_settings.xml"))
            {
                //wasLoaded = true;
                var serializer = new XmlSerializer(typeof(AppSettings));
                using (var reader = new StreamReader("savedrake_settings.xml"))
                {
                    var settings = (AppSettings)serializer.Deserialize(reader);

                    //Loading Filenaming convention before
                    try
                    {
                        randomlyGeneratedToolStripMenuItem.Checked = settings.BackupFileName1;
                        timeStampedToolStripMenuItem.Checked = settings.BackupFileName2;
                    }
                    catch { }

                    // Load combobox_auto information first
                    combobox_auto.Items.Clear();
                    combobox_auto.Items.AddRange(settings.ComboboxList.ToArray());
                    //combobox_auto.SelectedIndex = settings.ComboboxSelectedIndex >= 0 ? settings.ComboboxSelectedIndex : 0;
                    combobox_auto.SelectedIndex = settings.ComboboxSelectedIndex;

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

                    // Handle hotkey continuation or re-registration
                    if (_isRecordingHotkey)
                    {
                        _isRecordingHotkey = true;
                        //wasLoaded = true;
                        checkbox_hot.Checked = false;
                    }
                    else if (_currentMainKey != Keys.None)
                    {
                        RegisterHotKeyFunction();
                    }
                }
            }
        }
        #endregion

        //Undetected by Bkav
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
            isGameRunning = CheckGameRunningStatus();
            OnGameStatusChanged(isGameRunning);
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

        private TimeSpan ParseToTimeSpan(string s)
        {
            int value = int.Parse(new string(s.Where(char.IsDigit).ToArray()));
            if (s.Contains("min") || s.Contains("mins") || s.Contains("minutes"))
                return TimeSpan.FromMinutes(value);
            if (s.Contains("hour") || s.Contains("hours"))
                return TimeSpan.FromHours(value);
            // Add more conditions if there are other time units like "seconds", "days", etc.
            return TimeSpan.Zero; // Default case
        }

        private void SortComboBoxItems()
        {
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
            });
        }

        

        private string ProcessText(string text)
        {
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
                //RestartAutoBackup();
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
                    Button_br_1.BackColor = System.Drawing.ColorTranslator.FromHtml("#f0f0f0");
                    Button_br_2.BackColor = System.Drawing.ColorTranslator.FromHtml("#f0f0f0");
                }
            }
            else
            {
                textbox1.Enabled = true;
                textbox2.Enabled = true;
                combobox_auto.Enabled = true;
                Button_br_1.Enabled = true;
                Button_br_2.Enabled = true;
                Button_br_1.BackColor = Color.White;
                Button_br_2.BackColor= Color.White;
                Status.Text = $"Autobackup disabled";
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

                    //Count's before backing up in the beginning.
                    string countFilePath = "count_of_autobackups.txt";

                    // Read the current backup count from the file
                    int backupCount = 0;
                    if (File.Exists(countFilePath))
                    {
                        int.TryParse(File.ReadAllText(countFilePath), out backupCount);
                    }

                    // Parse the integer value from toolStripTextBox1
                    if (int.TryParse(toolStripTextBox2.Text, out int maxBackups))
                    {
                        // Check if the current number of backups is less than the maximum allowed
                        if (backupCount < maxBackups)
                        {
                            BackupOperation(true); // Perform the backup operation
                            backupCount++; // Increment the backup count

                            // Write the updated count back to the file
                            File.WriteAllText(countFilePath, backupCount.ToString());
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
                    Status.Text = $"Game not running. Autobackup paused.";
                    //NotifyUser("Game not running. Autobackup will start when the game starts."); // Game is not running, wait to start autobackup.
                }
            }
            else
            {
                autobackupTimer.Stop(); // Auto backup checkbox is not checked, stop autobackup.
                                        // No message is needed here as per your requirement.
            }
        }


        private void InitializeAutobackupTimer()
        {
            if (autobackupTimer == null)
            {
                autobackupTimer = new System.Timers.Timer();
                autobackupTimer.Elapsed += OnAutobackupTimerElapsed;
                autobackupTimer.AutoReset = true;
                SetAutoBackupInterval();
            }

        }

        private void SetAutoBackupInterval()
        {
            combobox_auto_Validating(combobox_auto, new System.ComponentModel.CancelEventArgs());
            if (combobox_auto.SelectedItem != null)
            {
                string interval = combobox_auto.SelectedItem.ToString();
                int timeValue = 0; // Initialize timeValue to zero
                int multiplier;

                // Check if the interval is in hours or minutes
                if (interval.ToLower().Contains("hour") || interval.ToLower().Contains("hours"))
                {
                    // Extract the number of hours and convert to milliseconds
                    timeValue = int.Parse(interval.Split(' ')[0]);
                    multiplier = 60 * 60 * 1000; // 1 hour = 60 minutes = 3600 seconds = 3600000 milliseconds
                }
                else
                {
                    // Extract the number of minutes and convert to milliseconds
                    timeValue = int.Parse(interval.Split(' ')[0]);
                    multiplier = 60 * 1000; // 1 minute = 60 seconds = 60000 milliseconds
                }

                autobackupTimer.Interval = timeValue * multiplier;
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
            // Define the path for the count file
            string countFilePath = "count_of_autobackups.txt";

            // Read the current backup count from the file
            int backupCount = 0;
            if (File.Exists(countFilePath))
            {
                int.TryParse(File.ReadAllText(countFilePath), out backupCount);
            }

            // Parse the integer value from toolStripTextBox1
            if (int.TryParse(toolStripTextBox2.Text, out int maxBackups))
            {
                // Check if the current number of backups is less than the maximum allowed
                if (backupCount < maxBackups)
                {
                    BackupOperation(true); // Perform the backup operation
                    backupCount++; // Increment the backup count

                    // Write the updated count back to the file
                    File.WriteAllText(countFilePath, backupCount.ToString());
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
        }

        private void combobox_auto_Validating(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Regular expression pattern to match "x min", "x hour", or "xx hours"
            string pattern = @"^\d+\s(minutes|hour|hours)$";
            string input = combobox_auto.Text;

            // Check if the input matches the pattern
            if (!Regex.IsMatch(input, pattern))
            {
                MessageBox.Show("Please enter the time in the correct format (e.g., '12 minutes', '1 hour', '1.5 hours', '2 hours').", "Invalid Format", MessageBoxButtons.OK, MessageBoxIcon.Error);
                e.Cancel = true; // Prevents focus from changing
                combobox_auto.SelectedIndex = 0;
                return;
            }

            // Parse the number from the input
            int timeValue1 = int.Parse(Regex.Match(input, @"\d+").Value);
            int timeValue2 = int.Parse(Regex.Match(input, @"\d+").Value);

            // Convert hours to minutes
            timeValue2 *= 60;

            string timeUnit = Regex.Match(input, @"(minutes|hour|hours)").Value;

            // Check if the time is less than 5 minutes
            if ((timeUnit == "minutes") && timeValue1 < 5)
            {
                MessageBox.Show("The time interval cannot be less than 5 minutes.", "Invalid Time", MessageBoxButtons.OK, MessageBoxIcon.Error);
                e.Cancel = true; // Prevents focus from changing
                combobox_auto.SelectedIndex = 0;
            }
            else if ((timeUnit == "hour" || timeUnit == "hours") && timeValue2 < 0.0833)
            {
                MessageBox.Show("The time interval cannot be less than 5 minutes.", "Invalid Time", MessageBoxButtons.OK, MessageBoxIcon.Error);
                e.Cancel = true; // Prevents focus from changing
                combobox_auto.SelectedIndex = 0;
            }
        }

        

        #endregion

        //Undetected by Bkav
        #region Browse and Open Buttons

        private void Button_br_1_Click(object sender, EventArgs e)
        {
            PromptForFolderSelection();
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
                                    if (!Directory.EnumerateFileSystemEntries(textbox1.Text).Any())
                                    {
                                        MessageBox.Show("The selected Savegame location is empty.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                        PromptForFolderSelection();
                                    }
                                }
                            }
                            else 
                            {
                                textbox1.Text = folder; // User confirmed the selection
                                if (!Directory.EnumerateFileSystemEntries(textbox1.Text).Any())
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

        //Undetected by Bkav
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

            // Check if the source is empty
            if (!Directory.EnumerateFileSystemEntries(textbox1.Text).Any())
            {
                MessageBox.Show("The selected Savegame location is empty.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                if (_isUsingHotkey) { PlaySoundFromResource2(); }

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
                backupFileName = Path.Combine(textbox2.Text, $"{autoPrefix}backup_{DateTime.Now:yyMMddHHmmss}.zip");
            }


            try
            {
                // Create a zip file of the directory specified in textbox1
                using (Ionic.Zip.ZipFile zip = new Ionic.Zip.ZipFile())
                {
                    Status.Text = "Backup started... Please wait.";
                    zip.AddDirectory(textbox1.Text); // Add the directory to the zip
                    zip.Comment = "SamMorrison9800"; // This is the hidden comment
                    zip.Save(backupFileName); // Save the zip file
                } //hmm - def related

                // Update the ListView with the new backup entry
                ListViewItem item = new ListViewItem(new[] { Path.GetFileName(backupFileName), DateTime.Now.ToString() }); //conformed no issue HATSPATS
                //listView.Items.Add(item);
                //listView.Sort();

                // Update the status
                LoadBackupHistory();
                //listView.Sort();
                Status.Text = isAutoBackup ? $"Autobackup created at {DateTime.Now.ToString("hh:mm:ss tt")}." : "Backup created successfully."; //def related - HATSPATS
                PlaySoundFromResource(); //mustenable
            }
            catch (Exception ex)
            {
                // If an error occurs, show an error message
                PlaySoundFromResource2(); //mustenable
                if (checkbox_auto.Checked)
                {
                    checkbox_auto.Checked = false;
                }
                MessageBox.Show($"An error occurred while creating the backup: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                Environment.Exit(0);
                //HATSPATS This error is being trigerre saying combobox_auto is being assessed from nother thread it was created in 
            }
        }


        // Helper method to generate a unique backup file name
        private string GenerateBackupFileName(bool isAutoBackup)
        {
            //HATSPATS
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
        } //HATSPATS SUSPECT

        private void button_backup_Click(object sender, EventArgs e)
        {
            BackupOperation();
        }
        #endregion

        //Restore Zip Operations //Undetected
        #region Restore Operation

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

            if (isGameRunning)
            {
                MessageBox.Show("Please quit the game before restoring.", "Game Running", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            
            // Check if exactly one item is selected in the ListView
            if (listView.SelectedItems.Count == 1)
            {
                // Move all files from the directory in textbox1 to the Recycle Bin
                MoveFilesToRecycleBin(textbox1.Text);

                // Get the selected file name
                string fileName = listView.SelectedItems[0].Text;

                // Combine the source directory with the file name to get the full file path
                string filePath = Path.Combine(textbox2.Text, fileName);


                // Unzip the file to the target directory using DotNetZip
                Status.Text = "Restore started... Please wait.";
                UnzipFileWithDotNetZip(filePath, textbox1.Text);


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

        private void MoveFilesToRecycleBin(string directoryPath)
        {
            // Indicate that a new deletion action has started
            bool isNewDel = true;

            string[] files = Directory.GetFiles(directoryPath);
            foreach (string file in files)
            {
                // Record the deletion
                RecordDeletion(file, isNewDel);

                // Subsequent deletions in the loop are part of the same action
                isNewDel = false;

                // Use the FileSystem.DeleteFile method to move the file to the Recycle Bin
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                // Update the undo button state after the operation
                UpdateUndoButtonState();
            }
        }

        private void UnzipFileWithDotNetZip(string filePath, string targetDirectory)
        {
            try
            {


                using (Ionic.Zip.ZipFile zip = Ionic.Zip.ZipFile.Read(filePath))
                {
                    zip.ExtractAll(targetDirectory, Ionic.Zip.ExtractExistingFileAction.OverwriteSilently);
                }

                //RefreshWindowsExplorer();
                Status.Text = "Restore successful.";
                MessageBox.Show("Restore successful.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Ionic.Zip.ZipException ze)
            {
                MessageBox.Show($"The Backup file is either not a zip file or is corrupted: {ze.Message}", "Zip Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (System.IO.IOException ioEx)
            {
                MessageBox.Show($"An IO error occurred: {ioEx.Message}", "IO Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred while unzipping the Backup file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /*public static void RefreshWindowsExplorer()
        {
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            const int SHCNF_IDLIST = 0x0000;

            // Call the function with the specified flags to refresh the Windows Explorer
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }*/

        #endregion

        //Undetected
        #region View bakups on listView / LoadBackupHistory
        private void LoadBackupHistory()
        {
            // Define the path for the count file
            string countFilePath = "count_of_autobackups.txt";
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

            // Load all zip files from the backup directory
            string[] zipFiles = Directory.GetFiles(textbox2.Text, "*");

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
                        // Check if the zip file contains the hidden comment
                        if (zip.Comment == "SamMorrison9800")
                        {
                            // Add the zip file to the ListView, even if it's empty
                            ListViewItem item = new ListViewItem(new[] { fileInfo.Name, fileInfo.CreationTime.ToString() });
                            item.Tag = fileInfo; // Store the FileInfo object in the Tag property
                            listView.Items.Add(item);
                            listView.Sort();
                        }
                    }
                }
                catch (Ionic.Zip.ZipException) // Handle exceptions related to reading zip files
                {
                    // You might want to handle this scenario, e.g., log the error or notify the user
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"An error occurred while loading the backup: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            // Set the ListViewItemSorter property to an instance of the custom comparer
            listView.ListViewItemSorter = new ListViewItemDateComparer(1, SortOrder.Descending);

            // Sort the ListView
            listView.Sort();
            listView.Refresh();

            // Filter the zip files to include only those with "(Auto)" or "auto" in the name
            string[] autoBackupFiles = zipFiles.Where(file => Path.GetFileName(file).StartsWith("(Auto)") || Path.GetFileName(file).StartsWith("auto")).ToArray();

            // Set the backup count to the number of autobackup files found
            backupCount = autoBackupFiles.Length;

            // Write the new count back to the file
            File.WriteAllText(countFilePath, backupCount.ToString());
        }

        #endregion

        //Hotkey
        #region Hotkey
        private void InitializeHotkey()
        {
            _proc = HookCallback;
            _hookID = SetHook(_proc);

            msgWindow = new MessageWindow();
            msgWindow.HotkeyPressed += MsgWindow_HotkeyPressed;
        }

        private void MsgWindow_HotkeyPressed(object sender, EventArgs e)
        {
            // Call your desired function
            _isUsingHotkey = true;
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
                    // Check if the user pressed the Escape key to finish recording the hotkey
                    if (key == Keys.Escape)
                    {
                        lock (_syncLock)
                        {
                            _EnterPressed = true;
                            _isRecordingHotkey = false;
                            checkbox_hot.Checked = false;

                            System.Threading.Tasks.Task.Run(() =>
                            {
                                this.Invoke((MethodInvoker)delegate
                                {
                                    MessageBox.Show($"Hotkey recording cancelled.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                });
                            });

                            return (IntPtr)1; // Prevent further processing of the Escape key
                        }
                    }
                    else if (key == Keys.Enter)
                    {
                        lock (_syncLock)
                        {
                            _EnterPressed = true;
                            _isRecordingHotkey = false;
                            RegisterHotKeyFunction();
                            this.Invoke((MethodInvoker)delegate
                            {
                                textbox3.Text = textbox3.Text.Replace("(Enter to finish\\Esc to cancle)", "");
                                Status.Text = "Hotkey recorded.";
                                string hotkeyString = GetHotkeyString(); // Make sure this method is thread-safe
                                System.Threading.Tasks.Task.Run(() =>
                                {
                                    this.Invoke((MethodInvoker)delegate
                                    {
                                        MessageBox.Show($"Hotkey set to: {hotkeyString}", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                    });
                                });
                            });


                            return (IntPtr)1; // Prevent further processing of the Enter key
                        }
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
                _isRecordingHotkey = true;
                RegisterHotKeyFunction();
                //textbox3.ForeColor = Color.OrangeRed;
                textbox3.Text = "Press your keys \n" +
                $"(Enter to finish\\Esc to cancle)";
                if (!isLoading)
                {
                    Status.Text = "Recording Hotkey...";
                }
                else { Status.Text = "Ready."; }
                _hookID = SetHook(_proc);



            }
            else
            {
                UnhookWindowsHookEx(_hookID);
                // Reset the recorded keys
                //_currentMainKey = Keys.None;
                //_controlPressed = _shiftPressed = _altPressed = false;
                ResetHotkey();
                textbox3.Text = " ";
                Status.Text = "Hotkey disabled.";
            }
        }
        private void ResetHotkey()
        {
            _isRecordingHotkey = false;
            _currentMainKey = Keys.None;
            _controlPressed = _shiftPressed = _altPressed = false;
            UnregisterHotKey(msgWindow.Handle, hotkeyId);
            Status.Text = "Hotkey reset.";
        }

        // Method to register the hotkey
        private void RegisterHotKeyFunction()
        {
            lock (_syncLock)
            {
                uint modifiers = (uint)((_controlPressed ? MOD_CONTROL : 0) |
                                        (_shiftPressed ? MOD_SHIFT : 0) |
                                        (_altPressed ? MOD_ALT : 0));

                if (!RegisterHotKey(msgWindow.Handle, hotkeyId, modifiers, (uint)_currentMainKey))
                {
                    if (!_EnterPressed)
                    {
                        this.Invoke((MethodInvoker)delegate
                        {
                            MessageBox.Show("Failed to register the hotkey.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        });
                        _EnterPressed = true;
                    }
                }
            }
        }

        // Override the WndProc method to handle hotkey presses
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x0312 && _currentMainKey != Keys.None)
            {
                // The ID of the hotkey that was pressed is in m.WParam
                // Check if the pressed hotkey matches the recorded hotkey
                if ((int)m.WParam == hotkeyId)
                {
                    BackupOperation(false);
                }
            }
        }
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

            // Set the button's background color
            button_undo.BackColor = deletedFiles.Count < 0 ? System.Drawing.ColorTranslator.FromHtml("#f0f0f0") : System.Drawing.Color.Transparent;
            button_undo.BackColor = deletedFiles.Count > 0 ? Color.White : System.Drawing.Color.Transparent;
        }


        // Method to restore the deleted files
        private void RestoreDeletedFiles()
        {
            Shell32.Shell shell = new Shell32.Shell();
            Folder recycleBin = shell.NameSpace(10);
            FolderItems items = recycleBin.Items();

            foreach (string filePath in deletedFiles)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    FolderItem fi = items.Item(i);
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
            }

            // Reset the record
            deletedFiles.Clear();
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
            if (e.Button == MouseButtons.Left)
            {
                try
                {
                    if (listView.FocusedItem.Bounds.Contains(e.Location))
                    {
                        // Assuming the full path of the file is stored in the Tag property
                        string filePath = listView.FocusedItem.Tag.ToString();
                        System.Diagnostics.Process.Start(filePath);
                    }
                }
                catch (Exception ex)
                {
                    string filePath = listView.FocusedItem.Tag.ToString();
                    MessageBox.Show("An error occurred while deleting the file(s): " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    LoadBackupHistory();
                    SortComboBoxItems();
                    //    listView.Sort(); //ARMA
                }
            }
        }

        private void listView_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (listView.FocusedItem != null && listView.FocusedItem.Bounds.Contains(e.Location))
                {
                    contextMenuStrip.Show(listView, e.Location);
                }
            }
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
                    // Indicate that a new deletion action has started
                    bool isNewDel = true;
                    try
                    {
                        foreach (ListViewItem item in listView.SelectedItems)
                        {
                            // Get the full path of the selected file
                            string filePath = Path.Combine(textbox2.Text, item.Text);

                            // Record the deletion
                            RecordDeletion(filePath, isNewDel);

                            // Subsequent deletions in the loop are part of the same action
                            isNewDel = false;

                            // Move the file to the Recycle Bin
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(filePath,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                            // Optionally, remove the item from the ListView after moving it to the Recycle Bin
                            //listView.Items.Remove(item);
                            LoadBackupHistory();
                            listView.Sort();
                            Status.Text = "Backup(s) deleted sucessfully.";

                            // Update the undo button state after the operation
                            UpdateUndoButtonState();
                        }
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
        private void ListView_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            // Enable the renameMenuItem only if exactly one item is selected
            renameMenuItem.Enabled = listView.SelectedItems.Count == 1;
        }

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
                try { Process.Start("Savedrake-Updater.exe"); }
                catch { MessageBox.Show("A new update is available. But Savedrake-Updater.exe could not be started. Make sure the file is present within Savedrake directory.", "Savedrake-Updater.exe Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning); }


            }
            else
            {
                if (!IsAPIError && !isLoading)
                {
                    MessageBox.Show("Your Savedrake is up to date.", "No Updates Available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
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
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "Savedrake Update Checker");

                    HttpResponseMessage response = await client.GetAsync(apiUrl);
                    response.EnsureSuccessStatusCode();

                    string responseBody = await response.Content.ReadAsStringAsync();
                    JObject json = JObject.Parse(responseBody);

                    string tagName = json["tag_name"].ToString();
                    return tagName;
                }
            }
            catch (HttpRequestException)
            {
                // Handle HTTP-specific exception with a MessageBox
                
                IsAPIError = true;
                return null;
            }
            catch (Exception ex)
            {
                // Handle non-HTTP exceptions with a MessageBox
                MessageBox.Show($"An unexpected error occurred: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                item.ToolTipText = "Right-click to rename/delete files."; // Set "PiCKJKL" as the tooltip for each item
            }
            

        }

        //Form Loading and Closing
        #region
        private async void Main_Load(object sender, EventArgs e)
        {
            isLoading = true;
            LoadSettings();
            //randomlyGeneratedToolStripMenuItem.Checked = true;
            LoadBackupHistory();

            foreach (ListViewItem item in listView.Items)
            {
                item.ToolTipText = "Right-click to rename/delete files."; // Set "PiCKJKL" as the tooltip for each item
            }


            CreateVersionText();
            listViewColumnResize();
            UpdateUndoButtonState();

            bool checkBox1Value = false; // Default value if the XML file doesn't exist

            try
            {
                // Attempt to load the checkBox values from the XML file
                XElement root = XElement.Load("savedrake-updater.xml");
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
                File.WriteAllText("version.txt", parsedVersion.ToString());
            }
        }

        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
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
            UnregisterHotKey(msgWindow.Handle, hotkeyId);
            msgWindow.DestroyHandle(); // Clean up the message-only window

            if (_watcher != null)
            {
                _watcher.Stop();
                _watcher.Dispose();
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
        public void PlaySoundFromResource()
        {
            try
            {
                SoundPlayer player = new SoundPlayer("success.wav");
                if (_isUsingHotkey == true)
                {
                    // Load the .wav file from resources


                    // Play the .wav file
                    player.Play();
                    _isUsingHotkey = false;
                }
                return;
            }
            catch { }
        }
        public void PlaySoundFromResource2()
        {

            try
            {
                SoundPlayer player = new SoundPlayer("error.wav");
                if (_isUsingHotkey == true)
                {
                    // Load the .wav file from resources


                    // Play the .wav file
                    player.Play();
                    _isUsingHotkey = false;
                }
                return;
            }
            catch { }

        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private async Task checkForUpdateToolStripMenuItem_ClickAsync(object sender, EventArgs e)
        {
            //CallToUpdateAsync();
            await ExecuteUpdateProcess();

        }

        private void checkForUpdateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try { Process.Start("Savedrake-Updater.exe"); } catch { MessageBox.Show("Savedrake-Updater.exe could not be started. Make sure the file is present within Savedrake directory.", "Savedrake-Updater.exe Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
                try
                {
                    File.Delete("savedrake_settings.xml");
                    File.Delete("savedrake-updater.xml");
                    File.Delete("version.txt");
                    File.Delete("count_of_autobackups.txt");
                    textbox1.Text = null;
                    textbox2.Text = null;
                    checkbox_auto.Checked = false;
                    checkbox_hot.Checked = false;
                    checkbox_tray.Checked = false;
                    listView.Items.Clear();
                    
                }
                catch
                {
                    //MessageBox.Show("An error occurred: " + ex.Message);
                }
                MessageBox.Show("Reset successful. Please restart Savedrake.", "Reset Settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

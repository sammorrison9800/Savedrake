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

    // The main workflow view model (Phase 4b). Drives the Folders + Backups cards and routes the Backup / Restore /
    // Delete actions through the UI-agnostic Core services (BackupService / RestoreService) using the WPF dialog +
    // status implementations below. MVVM via CommunityToolkit.Mvvm source generators.
    public partial class MainViewModel : ObservableObject
    {
        private readonly IDialogService _dialog;
        private readonly IStatusSink _status;

        // The two folders the workflow operates on.
        [ObservableProperty]
        private string saveDir;

        [ObservableProperty]
        private string backupDir;

        // The backup list shown in the Backups card (newest first), and the current selection.
        public ObservableCollection<BackupRow> Backups { get; } = new ObservableCollection<BackupRow>();

        [ObservableProperty]
        private BackupRow selectedBackup;

        // Status-bar text (bound) and the count shown beside the Backups folder path.
        [ObservableProperty]
        private string statusText = "Ready.";

        [ObservableProperty]
        private string backupCount;

        public MainViewModel()
        {
            _dialog = new WpfDialogService();
            _status = new StatusSink(this);

            LoadSettings();
            RefreshBackups();
        }

        // ----- settings (read-only reuse of the WinForms savedrake_settings.xml, best-effort) -----

        private static string AppDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Savedrake");

        private static string SettingsFilePath => Path.Combine(AppDataDir, "savedrake_settings.xml");

        // Read SaveDir / BackupDir from the WinForms settings file's <Textbox1> / <Textbox2> if present. Read-only,
        // dependency-free (a tiny hand-rolled XML read so we don't pull in the WinForms AppSettings type). Never throws.
        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return;
                var doc = System.Xml.Linq.XDocument.Load(SettingsFilePath);
                string t1 = (string)doc.Root?.Element("Textbox1");
                string t2 = (string)doc.Root?.Element("Textbox2");
                if (!string.IsNullOrWhiteSpace(t1)) SaveDir = t1;
                if (!string.IsNullOrWhiteSpace(t2)) BackupDir = t2;
            }
            catch { /* best-effort: missing/garbled settings just leave the fields blank */ }
        }

        // Persist the two folders into the same <Textbox1>/<Textbox2> shape the WinForms app reads, preserving any
        // other elements already present. Best-effort: a write failure must never break the workflow.
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
                doc.Save(SettingsFilePath);
            }
            catch { /* best-effort persistence */ }
        }

        private static void SetElement(System.Xml.Linq.XElement root, string name, string value)
        {
            var el = root.Element(name);
            if (el == null) root.Add(new System.Xml.Linq.XElement(name, value));
            else el.Value = value;
        }

        // ----- backup list -----

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
                return;
            }

            string[] zipFiles;
            try { zipFiles = Directory.GetFiles(dir, "*.zip"); }
            catch { zipFiles = new string[0]; }

            // Newest first by creation time (same ordering as LoadBackupHistory).
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
        }

        // ----- folder pickers -----

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

        // ----- workflow actions -----

        [RelayCommand]
        private void Backup()
        {
            BackupService.Backup(
                new BackupRequest
                {
                    LiveSaveDir = SaveDir,
                    BackupDir = BackupDir,
                    IsAutoBackup = false,
                    RandomName = true
                },
                _dialog, _status);
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

            RestoreService.Restore(
                new RestoreRequest
                {
                    BackupZipPath = SelectedBackup.FullPath,
                    LiveSaveDir = SaveDir,
                    BackupDir = BackupDir,
                    GameRunning = IsGameRunning()
                },
                _dialog, _status);
            RefreshBackups();
        }

        // Best-effort game-running check: a plain process-name probe. The WinForms CheckGameRunningStatus is more
        // thorough (it also inspects window state); a process check is sufficient for the restore guard for now.
        // TODO (Phase 5): replace with the full CheckGameRunningStatus parity check.
        private static bool IsGameRunning()
        {
            try
            {
                return System.Diagnostics.Process.GetProcessesByName("DD2").Length > 0
                    || System.Diagnostics.Process.GetProcessesByName("DD2-game").Length > 0;
            }
            catch { return false; }
        }

        [RelayCommand]
        private void Delete()
        {
            if (SelectedBackup == null)
            {
                _dialog.Warn("Delete", "Please select a backup to delete first.");
                return;
            }

            BackupRow row = SelectedBackup;
            if (!_dialog.Confirm("Delete Backup",
                    "Delete this backup permanently?\n\n" + row.FileName))
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
            RefreshBackups();
        }

        // ----- WPF service implementations -----

        // Modal dialogs via System.Windows.MessageBox. Confirm uses YesNo (Yes == affirmative, matching the WinForms
        // DialogResult.Yes convention the Core services were transcribed against); Info/Warn/Error map to the
        // matching MessageBox icons.
        private sealed class WpfDialogService : IDialogService
        {
            public bool Confirm(string title, string message)
            {
                return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                       == MessageBoxResult.Yes;
            }

            public void Info(string title, string message)
                => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

            public void Warn(string title, string message)
                => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

            public void Error(string title, string message)
                => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // Routes each Core Status.Text write into the bound StatusText property, marshalled to the UI thread (a
        // backup/restore may set status from a non-UI context in later phases).
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

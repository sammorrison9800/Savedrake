using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Savedrake.App.ViewModels;

namespace Savedrake.App
{
    // The borderless themed shell window. WindowChrome (see XAML) keeps native drag / resize / Aero-Snap.
    // On Win11 we additionally ask DWM for rounded corners, immersive dark mode, and a caption color that
    // matches the espresso header bar, so the system-drawn parts blend with our theme. All best-effort.
    public partial class MainWindow : Window
    {
        // DWM window attributes (dwmapi.h).
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // BOOL: title bar / system UI dark
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33; // int: 2 == DWMWCP_ROUND
        private const int DWMWA_CAPTION_COLOR = 35;            // COLORREF: 0x00BBGGRR

        private const int DWMWCP_ROUND = 2;

        // The header/caption bar color (#241C14) as a Win32 COLORREF (0x00BBGGRR).
        // R=0x24, G=0x1C, B=0x14  ->  0x00141C24
        private const int CaptionColorRef = 0x00141C24;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new Savedrake.App.ViewModels.MainViewModel();
            // Start the autobackup engine (WMI watcher) and re-engage a saved-on autobackup only once the window is
            // loaded, so any limit/invalid dialog from the immediate game-start backup has a real owner window.
            Loaded += (s, e) => (DataContext as Savedrake.App.ViewModels.MainViewModel)?.Activate();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyDwmTheming();
        }

        // Tear down the view model on close so the autobackup engine's WMI game watcher, timers, and file-system
        // watcher are stopped and disposed rather than lingering past the window.
        protected override void OnClosed(EventArgs e)
        {
            (DataContext as IDisposable)?.Dispose();
            base.OnClosed(e);
        }

        // Ask DWM to round corners, go dark, and tint the caption to our bar color. Each call is guarded:
        // older Windows builds simply return a non-zero HRESULT, which we ignore.
        private void ApplyDwmTheming()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                int caption = CaptionColorRef;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            }
            catch (Exception ex)
            {
                try { Savedrake.Log.Error("DWM theming failed (non-fatal).", ex); }
                catch { }
            }
        }

        // Backup-list keyboard shortcuts: Delete -> recycle selected, F2 -> rename, Ctrl+A -> select all.
        private void BackupList_KeyDown(object sender, KeyEventArgs e)
        {
            if (!(DataContext is MainViewModel vm)) return;
            if (e.Key == Key.Delete)
            {
                vm.DeleteCommand.Execute(BackupList.SelectedItems);
                e.Handled = true;
            }
            else if (e.Key == Key.F2)
            {
                vm.RenameCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                BackupList.SelectAll();
                e.Handled = true;
            }
        }

        // Double-click a row to open the backup zip. Guarded so a double-click on empty space is a no-op.
        private void BackupList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MainViewModel vm && vm.SelectedBackup != null)
                vm.OpenBackupCommand.Execute(vm.SelectedBackup);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

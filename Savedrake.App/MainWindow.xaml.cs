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

        // The caption colour now comes from ThemeManager.CaptionColorRef (theme-aware).

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // ----- global backup hotkey (RegisterHotKey against this window; WM_HOTKEY -> BackupCommand) -----
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004;
        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0xB001;
        private HwndSource _hwndSource;
        private bool _hotkeyRegistered;

        // The system-tray icon (WinForms NotifyIcon; UseWindowsForms is on). Only visible while minimized-to-tray.
        private System.Windows.Forms.NotifyIcon _tray;
        private System.Drawing.Icon _trayIcon; // NotifyIcon doesn't own its Icon, so we dispose it ourselves

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new Savedrake.App.ViewModels.MainViewModel();
            // Apply the saved theme before the first paint (no flash). Brush mutation repaints any existing controls.
            ThemeManager.Apply((DataContext as Savedrake.App.ViewModels.MainViewModel)?.IsLightTheme ?? false);
            // Start the autobackup engine (WMI watcher) and re-engage a saved-on autobackup only once the window is
            // loaded, so any limit/invalid dialog from the immediate game-start backup has a real owner window. The
            // tray icon is created here too (after the window exists).
            Loaded += (s, e) =>
            {
                var vm = DataContext as Savedrake.App.ViewModels.MainViewModel;
                if (vm != null && vm.WindowWidth > 0 && vm.WindowHeight > 0)
                {
                    Width = vm.WindowWidth;
                    Height = vm.WindowHeight;
                }
                InitTray();
                vm?.Activate();
                RegisterCurrentHotkey();
                AppUpdater.WriteVersionFile();        // version-handshake file for the external updater
                _ = AppUpdater.RunStartupCheckAsync(); // silent unless a newer release exists
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyDwmTheming();
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(WndProc);
        }

        // Receives WM_HOTKEY for the registered global backup hotkey and fires a manual backup.
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                if (DataContext is Savedrake.App.ViewModels.MainViewModel vm)
                    vm.BackupCommand.Execute(null);
                handled = true;
            }
            return IntPtr.Zero;
        }

        // (Re)register the global hotkey from the view model's saved combo. Refuses a modifier-less combo.
        private void RegisterCurrentHotkey()
        {
            UnregisterCurrentHotkey();
            if (!(DataContext is Savedrake.App.ViewModels.MainViewModel vm) || !vm.HotkeyEnabled || vm.HotkeyVk == 0) return;
            IntPtr h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            uint mods = (uint)((vm.HotkeyCtrl ? MOD_CONTROL : 0) | (vm.HotkeyShift ? MOD_SHIFT : 0) | (vm.HotkeyAlt ? MOD_ALT : 0));
            if (mods == 0) return; // never grab a bare key system-wide
            _hotkeyRegistered = RegisterHotKey(h, HotkeyId, mods, (uint)vm.HotkeyVk);
        }

        private void UnregisterCurrentHotkey()
        {
            if (!_hotkeyRegistered) return;
            try { UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId); } catch { }
            _hotkeyRegistered = false;
        }

        // File > Set backup hotkey: open the recording dialog, then persist + (re)register or clear.
        private void SetHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is Savedrake.App.ViewModels.MainViewModel vm)) return;
            var dlg = new HotkeyDialog(vm.HotkeyCtrl, vm.HotkeyShift, vm.HotkeyAlt, vm.HotkeyVk, vm.HotkeyDisplay) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Outcome == HotkeyDialogResult.Save)
            {
                vm.SetHotkey(dlg.Ctrl, dlg.Shift, dlg.Alt, dlg.Vk, dlg.Display);
                RegisterCurrentHotkey();
            }
            else if (dlg.Outcome == HotkeyDialogResult.Clear)
            {
                vm.ClearHotkey();
                UnregisterCurrentHotkey();
            }
        }

        // Open the folder Settings dialog (save game + backups paths). Shares the main window's view model so the
        // path fields and Detect/Browse commands operate on the live state.
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow { Owner = this, DataContext = DataContext }.ShowDialog();
        }

        // Build the tray icon + its Show/Quit menu. Hidden until the window is minimized with "minimize to tray" on.
        private void InitTray()
        {
            _tray = new System.Windows.Forms.NotifyIcon { Text = "Savedrake", Visible = false };
            try
            {
                var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/savedrake.ico"));
                if (res != null) using (var stream = res.Stream) { _trayIcon = new System.Drawing.Icon(stream); _tray.Icon = _trayIcon; }
            }
            catch { /* no icon -> NotifyIcon just won't show; non-fatal */ }

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Show", null, (s, e) => RestoreFromTray());
            menu.Items.Add("Quit", null, (s, e) => { if (_tray != null) _tray.Visible = false; System.Windows.Application.Current?.Shutdown(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => RestoreFromTray();
        }

        // Bring the window back from the tray.
        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_tray != null) _tray.Visible = false;
        }

        // When the user minimizes and "minimize to tray" is on, hide the window and show the tray icon instead.
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized
                && _tray != null
                && DataContext is Savedrake.App.ViewModels.MainViewModel vm && vm.MinimizeToTray)
            {
                Hide();
                _tray.Visible = true;
                try { _tray.ShowBalloonTip(800, "Savedrake", "Savedrake is minimized to the system tray.", System.Windows.Forms.ToolTipIcon.Info); }
                catch { }
            }
        }

        // Tear down the tray icon and the view model on close so the autobackup engine's WMI game watcher, timers,
        // and file-system watcher are stopped and disposed rather than lingering past the window.
        protected override void OnClosed(EventArgs e)
        {
            UnregisterCurrentHotkey();
            try { _hwndSource?.RemoveHook(WndProc); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Icon = null; _tray.Dispose(); _tray = null; } } catch { }
            try { if (_trayIcon != null) { _trayIcon.Dispose(); _trayIcon = null; } } catch { }
            // Persist the window size for next launch (RestoreBounds is the normal-state size even if minimized/maximized).
            if (DataContext is Savedrake.App.ViewModels.MainViewModel vm)
            {
                try { var b = RestoreBounds; if (b.Width > 0 && b.Height > 0) vm.SaveWindowSize((int)b.Width, (int)b.Height); } catch { }
            }
            (DataContext as IDisposable)?.Dispose();
            base.OnClosed(e);
        }

        // File > Reset Savedrake: delete the persisted state and close (Environment.Exit so nothing re-creates it).
        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is Savedrake.App.ViewModels.MainViewModel vm)) return;
            if (MessageBox.Show("This will reset all Savedrake settings to default and close the app. Continue?",
                    "Reset Savedrake", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            if (!vm.ResetState())
            {
                MessageBox.Show("Some settings files could not be deleted (they may be in use). Close Savedrake and " +
                    "delete them from %APPDATA%\\Savedrake manually, or try again.",
                    "Reset Incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            MessageBox.Show("Reset successful. Savedrake will now close.", "Reset Savedrake",
                MessageBoxButton.OK, MessageBoxImage.Information);
            try { if (_tray != null) { _tray.Visible = false; _tray.Icon = null; _tray.Dispose(); _tray = null; } } catch { }
            try { if (_trayIcon != null) { _trayIcon.Dispose(); _trayIcon = null; } } catch { }
            Environment.Exit(0); // NOT Close() — that would run OnClosed -> SaveWindowSize and re-create the file
        }

        // Ask DWM to round corners, go dark, and tint the caption to our bar color. Each call is guarded:
        // older Windows builds simply return a non-zero HRESULT, which we ignore.
        private void ApplyDwmTheming()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkMode = ThemeManager.IsLight ? 0 : 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                int caption = ThemeManager.CaptionColorRef;
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

        // Help > Check for Updates: a manual check that always reports its result.
        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            await AppUpdater.RunManualCheckAsync();
        }

        // File > Use light/dark theme: swap the palette live, persist, and re-tint the DWM caption.
        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is Savedrake.App.ViewModels.MainViewModel vm)) return;
            bool light = !vm.IsLightTheme;
            ThemeManager.Apply(light);
            vm.SetTheme(light);
            ApplyDwmTheming();
            // Rebuild the backup rows so their converter-painted dot/pill/name colours re-resolve against the new
            // palette (the converters resolve concrete brush instances, so they don't follow the live brush swap).
            vm.RefreshBackups();
        }

        // Open a header button's dropdown (File / Help) as a menu anchored under the button.
        private void HeaderMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.ContextMenu != null)
            {
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                b.ContextMenu.IsOpen = true;
            }
        }

        // Build + open the character switcher: one checkable row per character (switch on click), then "New character".
        private void CharacterMenu_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(DataContext is ViewModels.MainViewModel vm)) return;
            var menu = new ContextMenu
            {
                PlacementTarget = b,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };
            foreach (var c in vm.EnumerateCharacters())
            {
                menu.Items.Add(new MenuItem
                {
                    Header = c.Name + "   (" + c.FileCount + (c.FileCount == 1 ? " file)" : " files)"),
                    IsChecked = string.Equals(c.Name, vm.ActiveCharacter, System.StringComparison.OrdinalIgnoreCase),
                    Command = vm.SwitchCharacterCommand,
                    CommandParameter = c.Name
                });
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "New character…", Command = vm.NewCharacterCommand });
            menu.Items.Add(new MenuItem { Header = "Rename current character…", Command = vm.RenameCharacterCommand });
            menu.IsOpen = true;
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

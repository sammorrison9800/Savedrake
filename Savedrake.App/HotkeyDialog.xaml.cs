using System.Text;
using System.Windows;
using System.Windows.Input;

namespace Savedrake.App
{
    public enum HotkeyDialogResult { Cancel, Save, Clear }

    // A small themed modal that records a global-backup hotkey. Recording is done with WPF's own PreviewKeyDown while
    // the dialog has focus — no global low-level keyboard hook is needed (that is only the WinForms way); the global
    // REGISTRATION (so the hotkey fires when Savedrake is in the background) is done by the owner window via
    // RegisterHotKey. Enter saves, Esc cancels; a modifier (Ctrl/Shift/Alt) is required before Save enables.
    public partial class HotkeyDialog : Window
    {
        public bool Ctrl, Shift, Alt;
        public int Vk;
        public string Display;
        public HotkeyDialogResult Outcome = HotkeyDialogResult.Cancel;

        public HotkeyDialog(bool ctrl, bool shift, bool alt, int vk, string display)
        {
            InitializeComponent();
            Ctrl = ctrl; Shift = shift; Alt = alt; Vk = vk; Display = display;
            if (vk != 0 && !string.IsNullOrEmpty(display))
            {
                ComboReadout.Text = display;
                SaveBtn.IsEnabled = (ctrl || shift || alt);
            }
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return) { if (SaveBtn.IsEnabled) Commit(); e.Handled = true; return; }
            if (e.Key == Key.Escape) { Outcome = HotkeyDialogResult.Cancel; Close(); e.Handled = true; return; }

            // Alt-combinations arrive as Key.System with the real key in SystemKey.
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Ignore a bare modifier press — wait for the actual key.
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LeftAlt || key == Key.RightAlt || key == Key.LWin || key == Key.RWin)
            {
                e.Handled = true; return;
            }

            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) { e.Handled = true; return; }

            Ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            Shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            Alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            Vk = vk;
            Display = Format(Ctrl, Shift, Alt, key);
            ComboReadout.Text = Display;
            // Require at least one modifier so a bare key can't hijack a gameplay key system-wide.
            SaveBtn.IsEnabled = (Ctrl || Shift || Alt);
            e.Handled = true;
        }

        public static string Format(bool ctrl, bool shift, bool alt, Key key)
        {
            var sb = new StringBuilder();
            if (ctrl) sb.Append("Ctrl + ");
            if (shift) sb.Append("Shift + ");
            if (alt) sb.Append("Alt + ");
            sb.Append(key.ToString());
            return sb.ToString();
        }

        private void Commit() { Outcome = HotkeyDialogResult.Save; Close(); }
        private void Save_Click(object sender, RoutedEventArgs e) => Commit();
        private void Cancel_Click(object sender, RoutedEventArgs e) { Outcome = HotkeyDialogResult.Cancel; Close(); }
        private void Clear_Click(object sender, RoutedEventArgs e) { Outcome = HotkeyDialogResult.Clear; Close(); }
    }
}

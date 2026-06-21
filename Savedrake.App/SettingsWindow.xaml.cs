using System.Windows;
using System.Windows.Input;

namespace Savedrake.App
{
    // The folder Settings dialog. The caller sets DataContext to the MainViewModel so the path fields and the
    // Detect/Browse commands bind to the same live state the main window uses. Esc or the Done button closes it; the
    // folder changes are already persisted by the Browse/Detect commands themselves.
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private void Done_Click(object sender, RoutedEventArgs e) => Close();
    }
}

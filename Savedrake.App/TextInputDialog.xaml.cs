using System.Windows;
using System.Windows.Input;

namespace Savedrake.App
{
    // Code-behind for the themed text prompt (see TextInputDialog.xaml). Returns the entered text via ResponseText when
    // the dialog closes with DialogResult == true. The caller (WpfDialogService.Prompt) maps a false/null result to a
    // cancel, matching the old InputBox contract (empty/whitespace == cancelled).
    public partial class TextInputDialog : Window
    {
        public string ResponseText { get; private set; }

        public TextInputDialog(string title, string message, string defaultValue)
        {
            InitializeComponent();
            Title = title;
            TitleText.Text = title;
            MessageText.Text = message;
            Input.Text = defaultValue ?? "";
            Loaded += (s, e) => { Input.Focus(); Input.SelectAll(); };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            ResponseText = Input.Text;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

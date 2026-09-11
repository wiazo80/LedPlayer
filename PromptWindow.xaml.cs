using System.Windows;
using System.Windows.Input;

namespace LedPlayer
{
    public partial class PromptWindow : Window
    {
        public string InputText => InputBox.Text;

        public PromptWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => InputBox.Focus();
        }

        private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;

        private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

        // Enter — сохранить, Esc — отмена
        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) DialogResult = true;
            else if (e.Key == Key.Escape) DialogResult = false;
        }
    }
}
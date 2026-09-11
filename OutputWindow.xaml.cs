using System.Windows;
using System.Windows.Input;

namespace LedPlayer
{
    public partial class OutputWindow : Window
    {
        public OutputWindow()
        {
            InitializeComponent();
        }

        // Esc — закрыть окно вывода (важно: оно может перекрыть всё!)
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            base.OnKeyDown(e);
        }
    }
}
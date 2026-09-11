using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;

namespace LedPlayer
{
    public partial class OutputSettingsWindow : Window
    {
        private readonly MainWindow _main;

        public OutputSettingsWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // Список мониторов — чтобы понимать, откуда считать координаты
            var sb = new StringBuilder();
            var screens = Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                sb.AppendLine($"Монитор {i + 1}:  {b.Width}×{b.Height}   X={b.Left}, Y={b.Top}"
                              + (screens[i].Primary ? "   (основной)" : ""));
            }
            MonitorsText.Text = sb.ToString().TrimEnd();

            var c = _main.Config;
            TbX.Text = c.OutputX.ToString();
            TbY.Text = c.OutputY.ToString();
            TbW.Text = c.OutputW.ToString();
            TbH.Text = c.OutputH.ToString();
            CbTopmost.IsChecked = c.OutputTopmost;
            CbSound.IsChecked = c.OutputSound;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TbX.Text, out int x) ||
                !int.TryParse(TbY.Text, out int y) ||
                !int.TryParse(TbW.Text, out int w) ||
                !int.TryParse(TbH.Text, out int h))
            {
                MessageBox.Show(this, "Поля X, Y, Ширина и Высота должны быть числами",
                    "Проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (w < 64 || h < 64)
            {
                MessageBox.Show(this, "Ширина и высота не могут быть меньше 64",
                    "Проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _main.ApplyOutputCustom(x, y, w, h, CbTopmost.IsChecked == true);
        }

        private void OnHideClick(object sender, RoutedEventArgs e)
        {
            _main.HideOutput();
        }

        private void OnTopmostChanged(object sender, RoutedEventArgs e)
        {
            _main.SetOutputTopmost(CbTopmost.IsChecked == true);
        }

        private void OnSoundChanged(object sender, RoutedEventArgs e)
        {
            _main.SetOutputSound(CbSound.IsChecked == true);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
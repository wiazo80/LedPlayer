using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Forms = System.Windows.Forms;

namespace LedPlayer
{
    // ─── Мостик к движку VLC ──────────────────────────────
    // Функции масштабирования есть в движке, но не в .NET-обёртке —
    // вызываем напрямую
    internal static class VlcNative
    {
        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libvlc_video_set_scale(IntPtr mp, float factor);

        [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
        public static extern void libvlc_video_set_aspect_ratio(
            IntPtr mp, [MarshalAs(UnmanagedType.LPUTF8Str)] string? ratio);

        public static IntPtr Ptr(MediaPlayer mp)
        {
            for (var t = (Type?)mp.GetType(); t != null; t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType == typeof(IntPtr))
                    {
                        var v = (IntPtr)f.GetValue(mp)!;
                        if (v != IntPtr.Zero) return v;
                    }
                }
            }
            return IntPtr.Zero;
        }
    }

    // ─── Настройки (сохраняются в config.json) ────────────
    public class Config
    {
        public string VideoFolder { get; set; } = @"C:\LedContent";
        public int Volume { get; set; } = 30;
        public bool Shuffle { get; set; } = false;

        public int CompW { get; set; } = 1248;   // размер LED-экрана (для «Композиции»)
        public int CompH { get; set; } = 576;

        public string ScaleMode { get; set; } = "fill";   // "fill" | "fit" | "stretch"

        public bool OutputVisible { get; set; } = false;
        public string OutputMode { get; set; } = "custom";
        public int OutputX { get; set; } = 100;
        public int OutputY { get; set; } = 100;
        public int OutputW { get; set; } = 640;
        public int OutputH { get; set; } = 480;
        public bool OutputTopmost { get; set; } = true;
        public bool OutputSound { get; set; } = false;
    }

    // ─── Один пункт плейлиста (обновляется «на лету») ─────
    public class PlaylistItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void OnProp(string name) =>
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(name));

        public string FullPath { get; set; } = "";
        public int Number { get; set; }
        public long SizeBytes { get; set; }

        private long _durationMs;
        public long DurationMs
        {
            get => _durationMs;
            set
            {
                if (_durationMs == value) return;
                _durationMs = value;
                OnProp(nameof(DurationMs));
                OnProp(nameof(DurationText));
            }
        }

        private uint _videoW;
        public uint VideoW
        {
            get => _videoW;
            set { if (_videoW == value) return; _videoW = value; OnProp(nameof(VideoW)); }
        }

        private uint _videoH;
        public uint VideoH
        {
            get => _videoH;
            set { if (_videoH == value) return; _videoH = value; OnProp(nameof(VideoH)); }
        }

        private int _playCount;
        public int PlayCount
        {
            get => _playCount;
            set
            {
                if (_playCount == value) return;
                _playCount = value;
                OnProp(nameof(PlayCount));
                OnProp(nameof(PlaysText));
            }
        }

        public string FileName => Path.GetFileName(FullPath);

        public string DurationText
        {
            get
            {
                if (DurationMs <= 0) return "--:--";
                var t = TimeSpan.FromMilliseconds(DurationMs);
                return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss")
                                         : t.ToString(@"mm\:ss");
            }
        }

        public string SizeText => SizeBytes > 0
            ? $"{SizeBytes / 1024.0 / 1024.0:F1} МБ" : "";

        public string PlaysText => $"показов: {PlayCount}";
    }

    public partial class MainWindow : Window
    {
        private LibVLC _libVlc = null!;
        private MediaPlayer _player = null!;      // плеер предпросмотра
        private MediaPlayer _outPlayer = null!;   // плеер окна вывода
        private OutputWindow? _outputWindow;
        private List<PlaylistItem> _playlist = new();
        private int _index;
        private Config _config = new();
        private FileSystemWatcher? _watcher;
        private Dictionary<string, int> _playCounts = new();
        private bool _isAppClosing;

        private readonly System.Windows.Threading.DispatcherTimer _resizeDebounce =
            new() { Interval = TimeSpan.FromMilliseconds(150) };

        private static readonly string[] VideoExt =
            { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".mpg", ".webm" };

        private string StatsPath => Path.Combine(AppContext.BaseDirectory, "stats.json");
        public Config Config => _config;

        public MainWindow()
        {
            InitializeComponent();
            Core.Initialize();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadConfig();
            LoadStats();
            InitPlayer();
            StartWatchFolder();
            _ = ReloadPlaylistAsync();
            StartClock();
            RestoreOutput();

            _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); ApplyScaling(); };
            PreviewHost.SizeChanged += (_, _) => { _resizeDebounce.Stop(); _resizeDebounce.Start(); };
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _isAppClosing = true;
            base.OnClosing(e);
        }

        // ─── Конфиг ────────────────────────────────────────

        private void LoadConfig()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "config.json");
                if (File.Exists(path))
                    _config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "config.json"),
                    JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ─── Плееры ────────────────────────────────────────

        private void InitPlayer()
        {
            _libVlc = new LibVLC("--no-osd", "--avcodec-hw=any");

            _player = new MediaPlayer(_libVlc) { Volume = _config.Volume };
            VideoView.MediaPlayer = _player;

            _outPlayer = new MediaPlayer(_libVlc)
            {
                Volume = _config.OutputSound ? _config.Volume : 0
            };

            VolumeSlider.Value = _config.Volume;

            switch (_config.ScaleMode)
            {
                case "fit": RbFit.IsChecked = true; break;
                case "stretch": RbStretch.IsChecked = true; break;
                default: RbFill.IsChecked = true; break;
            }

            _player.EndReached += OnEndReached;
            _player.EncounteredError += OnError;
        }

        // ─── Плейлист ──────────────────────────────────────

        private async Task ReloadPlaylistAsync()
        {
            var files = Directory.Exists(_config.VideoFolder)
                ? Directory.EnumerateFiles(_config.VideoFolder)
                    .Where(f => VideoExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            var items = new List<PlaylistItem>();
            for (int i = 0; i < files.Count; i++)
            {
                var info = new FileInfo(files[i]);
                items.Add(new PlaylistItem
                {
                    FullPath = files[i],
                    Number = i + 1,
                    SizeBytes = info.Exists ? info.Length : 0,
                    PlayCount = _playCounts.TryGetValue(files[i], out var c) ? c : 0
                });
            }

            _playlist = items;
            if (_index >= _playlist.Count) _index = 0;

            await Task.Run(() =>
            {
                foreach (var item in _playlist)
                {
                    try
                    {
                        using var media = new Media(_libVlc, new Uri(item.FullPath));
                        media.Parse(MediaParseOptions.ParseLocal);
                        item.DurationMs = media.Duration;
                    }
                    catch { }
                }
            });

            RefreshPlaylistUi();

            if (_playlist.Count > 0 && !_player.IsPlaying)
                PlayCurrent();
        }

        private void RefreshPlaylistUi()
        {
            PlaylistBox.ItemsSource = _playlist;
            if (_index >= 0 && _index < _playlist.Count)
                PlaylistBox.SelectedIndex = _index;

            StatusRight.Text = $"Папка: {_config.VideoFolder}  ·  Роликов: {_playlist.Count}";

            bool empty = _playlist.Count == 0;
            IdleText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            VideoView.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void PlayCurrent()
        {
            if (_playlist.Count == 0) return;
            if (_index >= _playlist.Count) _index = 0;

            if (_config.Shuffle)
                _index = Random.Shared.Next(_playlist.Count);

            var item = _playlist[_index];

            using var media = new Media(_libVlc, new Uri(item.FullPath));
            _player.Play(media);

            if (_outputWindow != null)
            {
                using var media2 = new Media(_libVlc, new Uri(item.FullPath));
                _outPlayer.Play(media2);
            }

            PlaylistBox.SelectedIndex = _index;
            BtnPlayPause.Content = "❚❚";
            StatusText.Text = $"Играет: {item.FileName}";
            SetOnAir(true);

            item.PlayCount++;
            _playCounts[item.FullPath] = item.PlayCount;
            SaveStats();

            // Масштаб применим и сейчас, и подхватим через секунду,
            // когда видео-движок прогреется (см. SyncVideoInfo)
            ApplyScaling();
        }

        private static string StartOption(double seconds) =>
            ":start-time=" + seconds.ToString("F2",
                System.Globalization.CultureInfo.InvariantCulture);

        // ─── Масштаб FILL / FIT / STRETCH ──────────────────
        // Масштабируем КАРТИНКУ ВНУТРИ видео-окна средствами VLC.
        // Само видео-окно не двигается и не растёт — интерфейс не перекрывается

        private void OnScaleFillChecked(object sender, RoutedEventArgs e)
            => SetScaleMode("fill");

        private void OnScaleFitChecked(object sender, RoutedEventArgs e)
            => SetScaleMode("fit");

        private void OnScaleStretchChecked(object sender, RoutedEventArgs e)
            => SetScaleMode("stretch");

        private void SetScaleMode(string mode)
        {
            _config.ScaleMode = mode;
            SaveConfig();

            var report = ApplyScaling();
            StatusText.Text = report != ""
                ? report
                : $"Масштаб: {mode.ToUpper()} — применится при запуске ролика";
        }

        private string ApplyScaling()
        {
            if (_player == null) return "";

            var report = ApplyScalingTo(_player, PreviewHost);
            if (_outputWindow != null)
                ApplyScalingTo(_outPlayer, null);

            return report;
        }

        private string ApplyScalingTo(MediaPlayer mp, FrameworkElement? host)
        {
            if (mp == null) return "";

            var ptr = VlcNative.Ptr(mp);
            if (ptr == IntPtr.Zero)
                return "Масштаб: мостик к плееру не нашёл адрес (напишите мне — поправлю)";

            // Площадка: панель превью или окно вывода
            double hw, hh;
            Visual visual;
            if (host != null)
            {
                hw = host.ActualWidth;
                hh = host.ActualHeight;
                visual = host;
            }
            else if (_outputWindow != null)
            {
                hw = _outputWindow.ActualWidth;
                hh = _outputWindow.ActualHeight;
                visual = _outputWindow;
            }
            else return "";

            if (hw < 2 || hh < 2) return "";

            // Физические пиксели (масштаб Windows 125% → ×1.25)
            double dpi = 1.0;
            try
            {
                var src = PresentationSource.FromVisual(visual);
                if (src?.CompositionTarget != null)
                    dpi = src.CompositionTarget.TransformToDevice.M11;
            }
            catch { }

            int pw = Math.Max(2, (int)Math.Round(hw * dpi));
            int ph = Math.Max(2, (int)Math.Round(hh * dpi));

            // Родное разрешение ролика (если неизвестно — считаем 16:9)
            var item = (_index >= 0 && _index < _playlist.Count) ? _playlist[_index] : null;
            double vw = item?.VideoW ?? 0;
            double vh = item?.VideoH ?? 0;
            if (vw <= 0 || vh <= 0) { vw = 16; vh = 9; }

            try
            {
                switch (_config.ScaleMode)
                {
                    case "stretch":
                        // РАСТЯНУТЬ: подменяем пропорции кадра пропорциями площадки
                        VlcNative.libvlc_video_set_aspect_ratio(ptr, $"{pw}:{ph}");
                        VlcNative.libvlc_video_set_scale(ptr, 0f);
                        return $"Масштаб: STRETCH — тянем на {pw}:{ph}";

                    case "fill":
                        // ЗАПОЛНИТЬ: зум, пока кадр не накроет площадку.
                        // Вылезающее обрезается границей видео-окна
                        VlcNative.libvlc_video_set_aspect_ratio(ptr, null);
                        var k = Math.Max(pw / vw, ph / vh);
                        VlcNative.libvlc_video_set_scale(ptr, (float)k);
                        return $"Масштаб: FILL — зум ×{k:F2}";

                    default:
                        // ВПИСАТЬ: обычное поведение VLC — ролик целиком
                        VlcNative.libvlc_video_set_aspect_ratio(ptr, null);
                        VlcNative.libvlc_video_set_scale(ptr, 0f);
                        return "Масштаб: FIT — ролик целиком";
                }
            }
            catch (Exception ex)
            {
                return $"Масштаб: ошибка мостика ({ex.GetType().Name}) — напишите мне";
            }
        }

        // ─── Кнопки ────────────────────────────────────────

        private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        {
            if (_playlist.Count == 0) return;

            if (_player.IsPlaying)
            {
                _player.Pause();
                _outPlayer.Pause();
                BtnPlayPause.Content = "▶";
                StatusText.Text = "Пауза";
                SetOnAir(false);
            }
            else
            {
                _player.Play();
                _outPlayer.Play();
                BtnPlayPause.Content = "❚❚";
                StatusText.Text = $"Играет: {Path.GetFileName(_playlist[_index].FullPath)}";
                SetOnAir(true);
            }
        }

        private void OnPrevClick(object sender, RoutedEventArgs e)
        {
            if (_playlist.Count == 0) return;
            _index = (_index - 1 + _playlist.Count) % _playlist.Count;
            PlayCurrent();
        }

        private void OnNextClick(object sender, RoutedEventArgs e)
        {
            if (_playlist.Count == 0) return;
            _index = (_index + 1) % _playlist.Count;
            PlayCurrent();
        }

        private void OnPlaylistDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (PlaylistBox.SelectedIndex >= 0)
            {
                _index = PlaylistBox.SelectedIndex;
                PlayCurrent();
            }
        }

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_player == null) return;
            _player.Volume = (int)e.NewValue;
            if (_outPlayer != null)
                _outPlayer.Volume = _config.OutputSound ? (int)e.NewValue : 0;
        }

        // ─── Меню «Окно вывода» ────────────────────────────

        private void OnOutputClick(object sender, RoutedEventArgs e)
        {
            BuildOutputMenu();
            OutputMenu.IsOpen = true;
        }

        private void BuildOutputMenu()
        {
            OutputMenuPanel.Children.Clear();

            var screens = Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                int idx = i;
                var b = screens[i].Bounds;
                OutputMenuPanel.Children.Add(MakeMenuItem(
                    $"Полный экран — монитор {i + 1}   ({b.Width}×{b.Height})",
                    () => ApplyOutputFullscreen(idx)));
            }

            OutputMenuPanel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x55)),
                Margin = new Thickness(4, 5, 4, 5)
            });

            OutputMenuPanel.Children.Add(MakeMenuItem("Настройка вывода…", () =>
            {
                var win = new OutputSettingsWindow(this) { Owner = this };
                win.ShowDialog();
            }));
        }

        private Button MakeMenuItem(string text, Action action)
        {
            var btn = new Button
            {
                Content = text,
                FontSize = 13,
                Margin = new Thickness(2, 1, 2, 1)
            };
            btn.Style = (Style)FindResource("MenuButtonStyle");
            btn.Click += (_, _) =>
            {
                OutputMenu.IsOpen = false;
                action();
            };
            return btn;
        }

        // ─── Управление окном вывода ───────────────────────

        private void EnsureOutputWindow()
        {
            if (_outputWindow != null) return;

            _outputWindow = new OutputWindow { Owner = this };
            _outputWindow.VideoView.MediaPlayer = _outPlayer;
            _outputWindow.SizeChanged += (_, _) =>
            {
                _resizeDebounce.Stop();
                _resizeDebounce.Start();
            };
            _outputWindow.Closed += (_, _) =>
            {
                _outPlayer.Stop();
                _outputWindow = null;

                if (!_isAppClosing)
                {
                    _config.OutputVisible = false;
                    SaveConfig();
                    StatusText.Text = "Окно вывода закрыто";
                }
            };
        }

        public void ApplyOutputFullscreen(int screenIndex)
        {
            var screens = Forms.Screen.AllScreens;
            if (screens.Length == 0) return;
            if (screenIndex < 0 || screenIndex >= screens.Length) screenIndex = 0;

            var b = screens[screenIndex].Bounds;

            EnsureOutputWindow();
            var w = _outputWindow!;

            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowStyle = WindowStyle.None;
            w.ResizeMode = ResizeMode.NoResize;
            w.Topmost = _config.OutputTopmost;
            w.Left = b.Left;
            w.Top = b.Top;
            w.Width = b.Width;
            w.Height = b.Height;

            if (!w.IsVisible) w.Show();

            w.WindowState = WindowState.Maximized;

            _config.OutputMode = $"fullscreen:{screenIndex}";
            _config.OutputVisible = true;
            SaveConfig();

            SyncOutputPlayback();
            StatusText.Text = $"Вывод: полный экран, монитор {screenIndex + 1}";
        }

        public void ApplyOutputCustom(int x, int y, int width, int height, bool topmost)
        {
            EnsureOutputWindow();
            var w = _outputWindow!;

            w.WindowState = WindowState.Normal;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowStyle = WindowStyle.None;
            w.ResizeMode = ResizeMode.NoResize;
            w.Topmost = topmost;
            w.Left = x;
            w.Top = y;
            w.Width = width;
            w.Height = height;

            if (!w.IsVisible) w.Show();

            _config.OutputMode = "custom";
            _config.OutputVisible = true;
            _config.OutputX = x;
            _config.OutputY = y;
            _config.OutputW = width;
            _config.OutputH = height;
            _config.OutputTopmost = topmost;
            SaveConfig();

            SyncOutputPlayback();
            StatusText.Text = $"Вывод: {width}×{height} в точке ({x}, {y})";
        }

        public void SetOutputTopmost(bool topmost)
        {
            _config.OutputTopmost = topmost;
            if (_outputWindow != null) _outputWindow.Topmost = topmost;
            SaveConfig();
        }

        public void SetOutputSound(bool sound)
        {
            _config.OutputSound = sound;
            if (_outPlayer != null)
                _outPlayer.Volume = sound ? (int)VolumeSlider.Value : 0;
            SaveConfig();
        }

        public void HideOutput()
        {
            _outputWindow?.Close();
        }

        private void SyncOutputPlayback()
        {
            if (_outputWindow == null) return;
            if (_playlist.Count == 0 || _index >= _playlist.Count) return;
            if (!_player.IsPlaying) return;

            var startSec = _player.Time / 1000.0;
            using var media = new Media(_libVlc, new Uri(_playlist[_index].FullPath));
            if (startSec > 0.5)
                media.AddOption(StartOption(startSec));
            _outPlayer.Play(media);

            ApplyScaling();
        }

        private void RestoreOutput()
        {
            if (!_config.OutputVisible) return;

            if (_config.OutputMode.StartsWith("fullscreen:"))
            {
                var n = _config.OutputMode["fullscreen:".Length..];
                ApplyOutputFullscreen(int.TryParse(n, out var idx) ? idx : 0);
            }
            else
            {
                ApplyOutputCustom(_config.OutputX, _config.OutputY,
                                  _config.OutputW, _config.OutputH, _config.OutputTopmost);
            }
        }

        // ─── Служебное ─────────────────────────────────────

        private void OnEndReached(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_playlist.Count == 0) return;
                _index = (_index + 1) % _playlist.Count;
                PlayCurrent();
            });
        }

        private void OnError(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_playlist.Count == 0) return;
                _index = (_index + 1) % _playlist.Count;
                PlayCurrent();
            });
        }

        private void StartWatchFolder()
        {
            if (!Directory.Exists(_config.VideoFolder)) return;

            _watcher = new FileSystemWatcher(_config.VideoFolder)
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };
            _watcher.Created += (_, _) => Dispatcher.BeginInvoke(() => _ = ReloadPlaylistAsync());
            _watcher.Deleted += (_, _) => Dispatcher.BeginInvoke(() => _ = ReloadPlaylistAsync());
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => _ = ReloadPlaylistAsync());
        }

        // ─── Статистика показов ────────────────────────────

        private void LoadStats()
        {
            try
            {
                if (File.Exists(StatsPath))
                    _playCounts = JsonSerializer.Deserialize<Dictionary<string, int>>(
                        File.ReadAllText(StatsPath)) ?? new Dictionary<string, int>();
            }
            catch { }
        }

        private void SaveStats()
        {
            try { File.WriteAllText(StatsPath, JsonSerializer.Serialize(_playCounts)); }
            catch { }
        }

        // ─── Часы + добор информации о ролике ──────────────

        private void StartClock()
        {
            ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += (_, _) =>
            {
                ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
                SyncVideoInfo();
            };
            timer.Start();
        }

        private void SyncVideoInfo()
        {
            if (_playlist.Count > 0 && _index < _playlist.Count)
            {
                var item = _playlist[_index];
                try
                {
                    if (item.DurationMs <= 0)
                    {
                        var len = _player.Length;
                        if (len > 0) item.DurationMs = len;
                    }

                    if (item.VideoW == 0 || item.VideoH == 0)
                    {
                        uint w = 0, h = 0;
                        _player.Size(0, ref w, ref h);
                        if (w > 0 && h > 0)
                        {
                            item.VideoW = w;
                            item.VideoH = h;
                        }
                    }
                }
                catch { }
            }

            // Тихо подхватываем масштаб: в первые секунды после запуска
            // ролика видео-движок ещё не готов, и первый вызов мог утонуть
            ApplyScaling();
        }

        private void SetOnAir(bool onAir)
        {
            var brush = onAir ? Brushes.MediumSpringGreen : Brushes.Orange;
            OnAirDot.Fill = brush;
            OnAirText.Foreground = brush;
            OnAirText.Text = onAir ? "В ЭФИРЕ" : "ПАУЗА";
        }

        private void OnCompositionClick(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Композиция — оживим на следующем шаге ;)";
        }
    }
}
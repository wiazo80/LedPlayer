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

    // ─── Настройки (config.json) ──────────────────────────
    public class Config
    {
        public string VideoFolder { get; set; } = @"C:\LedContent";
        public int Volume { get; set; } = 30;
        public bool Shuffle { get; set; } = false;

        public int CompW { get; set; } = 1248;
        public int CompH { get; set; } = 576;

        public string ScaleMode { get; set; } = "fill";

        public bool OutputVisible { get; set; } = false;
        public string OutputMode { get; set; } = "custom";
        public int OutputX { get; set; } = 100;
        public int OutputY { get; set; } = 100;
        public int OutputW { get; set; } = 640;
        public int OutputH { get; set; } = 480;
        public bool OutputTopmost { get; set; } = true;
        public bool OutputSound { get; set; } = false;
    }

    // ─── Статистика по ролику (stats.json) ────────────────
    public class StatEntry
    {
        public int Total { get; set; }
        public Dictionary<string, int> Daily { get; set; } = new(); // "2025-01-15" → 5
        public DateTime? LastPlayed { get; set; }
    }

    // ─── Лимиты конкретного ролика (playlistSettings.json) ─
    public class PlaylistSettings
    {
        public int MaxPlaysPerDay { get; set; }
        public int IntervalMinutes { get; set; }
    }

    // ─── Один пункт плейлиста ─────────────────────────────
    public class PlaylistItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public event Action? SettingsEdited;   // оператор поправил лимиты

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
            set { if (_durationMs == value) return; _durationMs = value; OnProp(nameof(DurationMs)); OnProp(nameof(DurationText)); }
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
            set { if (_playCount == value) return; _playCount = value; OnProp(nameof(PlayCount)); OnProp(nameof(PlaysText)); }
        }

        private int _playsToday;
        public int PlaysToday
        {
            get => _playsToday;
            set { if (_playsToday == value) return; _playsToday = value; OnProp(nameof(PlaysToday)); OnProp(nameof(PlaysText)); }
        }

        private int _maxPlays;
        public int MaxPlaysPerDay
        {
            get => _maxPlays;
            set { if (_maxPlays == value) return; _maxPlays = value; OnProp(nameof(MaxPlaysPerDay)); OnProp(nameof(PlaysText)); }
        }

        private int _intervalMin;
        public int IntervalMinutes
        {
            get => _intervalMin;
            set { if (_intervalMin == value) return; _intervalMin = value; OnProp(nameof(IntervalMinutes)); }
        }

        // Текст в поле «Лимит/день» (пусто = 0 = без лимита)
        public string MaxPlaysText
        {
            get => _maxPlays <= 0 ? "" : _maxPlays.ToString();
            set
            {
                var v = ParseInt(value);
                if (_maxPlays == v) { OnProp(nameof(MaxPlaysText)); return; }
                _maxPlays = v;
                OnProp(nameof(MaxPlaysText));
                OnProp(nameof(PlaysText));
                SettingsEdited?.Invoke();
            }
        }

        // Текст в поле «Интервал, мин» (пусто = 0 = без паузы)
        public string IntervalText
        {
            get => _intervalMin <= 0 ? "" : _intervalMin.ToString();
            set
            {
                var v = ParseInt(value);
                if (v > 10080) v = 10080;   // не больше недели
                if (_intervalMin == v) { OnProp(nameof(IntervalText)); return; }
                _intervalMin = v;
                OnProp(nameof(IntervalText));
                SettingsEdited?.Invoke();
            }
        }

        private static int ParseInt(string? s)
        {
            int.TryParse((s ?? "").Trim(), out var v);
            return Math.Clamp(v, 0, 9999);
        }

        private string _blockInfo = "";
        public string BlockInfo
        {
            get => _blockInfo;
            set { if (_blockInfo == value) return; _blockInfo = value; OnProp(nameof(BlockInfo)); }
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

        public string PlaysText
        {
            get
            {
                var today = MaxPlaysPerDay > 0
                    ? $" · сегодня: {PlaysToday}/{MaxPlaysPerDay}"
                    : $" · сегодня: {PlaysToday}";
                return $"показов: {PlayCount}{today}";
            }
        }
    }

    public partial class MainWindow : Window
    {
        private LibVLC _libVlc = null!;
        private MediaPlayer _player = null!;
        private MediaPlayer _outPlayer = null!;
        private OutputWindow? _outputWindow;
        private List<PlaylistItem> _playlist = new();
        private int _index;
        private Config _config = new();
        private FileSystemWatcher? _watcher;
        private Dictionary<string, StatEntry> _stats = new();
        private Dictionary<string, PlaylistSettings> _plSettings = new();
        private bool _isAppClosing;
        private bool _waiting;   // все ролики заблокированы лимитами — ждём

        private readonly System.Windows.Threading.DispatcherTimer _resizeDebounce =
            new() { Interval = TimeSpan.FromMilliseconds(150) };

        private static readonly string[] VideoExt =
            { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".mpg", ".webm" };

        private string StatsPath => Path.Combine(AppContext.BaseDirectory, "stats.json");
        private string PlSettingsPath => Path.Combine(AppContext.BaseDirectory, "playlistSettings.json");
        private static string TodayKey => DateTime.Now.ToString("yyyy-MM-dd");
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
            LoadPlSettings();
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
                _stats.TryGetValue(files[i], out var se);
                _plSettings.TryGetValue(files[i], out var st);

                var it = new PlaylistItem
                {
                    FullPath = files[i],
                    Number = i + 1,
                    SizeBytes = info.Exists ? info.Length : 0,
                    PlayCount = se?.Total ?? 0,
                    PlaysToday = se?.Daily.TryGetValue(TodayKey, out var pc) == true ? pc : 0,
                    MaxPlaysPerDay = st?.MaxPlaysPerDay ?? 0,
                    IntervalMinutes = st?.IntervalMinutes ?? 0
                };
                it.SettingsEdited += () => OnItemSettingsEdited(it);
                items.Add(it);
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

            if (_playlist.Count > 0 && !_player.IsPlaying && !_waiting)
                PlayCurrent();
        }

        private void RefreshPlaylistUi()
        {
            PlaylistBox.ItemsSource = _playlist;
            if (_index >= 0 && _index < _playlist.Count)
                PlaylistBox.SelectedIndex = _index;

            StatusRight.Text = $"Папка: {_config.VideoFolder}  ·  Роликов: {_playlist.Count}";

            bool empty = _playlist.Count == 0;
            IdleText.Text = "Нет сигнала";
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

            VideoView.Visibility = Visibility.Visible;
            IdleText.Visibility = Visibility.Collapsed;

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

            RecordPlay(item);
            ApplyScaling();
        }

        private static string StartOption(double seconds) =>
            ":start-time=" + seconds.ToString("F2",
                System.Globalization.CultureInfo.InvariantCulture);

        // ─── Правила показов: лимиты и интервалы ───────────

        // Можно ли играть ролик сейчас?
        private bool IsPlayable(PlaylistItem it)
        {
            if (!_stats.TryGetValue(it.FullPath, out var s)) return true;

            // Дневной лимит исчерпан?
            if (it.MaxPlaysPerDay > 0 &&
                s.Daily.TryGetValue(TodayKey, out var today) &&
                today >= it.MaxPlaysPerDay)
                return false;

            // Интервал с последнего показа ещё не прошёл?
            if (it.IntervalMinutes > 0 && s.LastPlayed is DateTime lp &&
                DateTime.Now - lp < TimeSpan.FromMinutes(it.IntervalMinutes))
                return false;

            return true;
        }

        // Следующий РАЗРЕШЁННЫЙ ролик по кругу (или -1, если все заблокированы)
        private int FindNextPlayable(int fromExclusive)
        {
            int n = _playlist.Count;
            if (n == 0) return -1;

            for (int k = 1; k <= n; k++)
            {
                var i = (fromExclusive + k) % n;
                if (IsPlayable(_playlist[i])) return i;
            }
            return -1;
        }

        // Автопрокрутка: идём к следующему разрешённому.
        // Если таковых нет — входим в режим ожидания
        private void AdvanceAuto()
        {
            if (_playlist.Count == 0) return;

            var next = FindNextPlayable(_index);
            if (next < 0)
            {
                StartWaiting();
                return;
            }
            _index = next;
            PlayCurrent();
        }

        private void StartWaiting()
        {
            _waiting = true;
            _player.Stop();
            _outPlayer.Stop();
            SetOnAir(false);
            BtnPlayPause.Content = "▶";
            VideoView.Visibility = Visibility.Collapsed;
            UpdateWaitingUi();
        }

        private void UpdateWaitingUi()
        {
            var t = NextUnlockTime();
            IdleText.Text = t.HasValue
                ? $"ПАУЗА — следующий ролик в {t:HH:mm}"
                : "ПАУЗА — лимиты на сегодня исчерпаны";
            IdleText.Visibility = Visibility.Visible;
            StatusText.Text = "Все ролики заблокированы лимитами — ожидание…";
        }

        // Когда разблокируется ближайший ролик
        private DateTime? NextUnlockTime()
        {
            DateTime? best = null;
            foreach (var it in _playlist)
            {
                if (!_stats.TryGetValue(it.FullPath, out var s)) continue;

                if (it.IntervalMinutes > 0 && s.LastPlayed is DateTime lp)
                {
                    var until = lp.AddMinutes(it.IntervalMinutes);
                    if (until > DateTime.Now && (best == null || until < best)) best = until;
                }

                if (it.MaxPlaysPerDay > 0 &&
                    s.Daily.TryGetValue(TodayKey, out var c) && c >= it.MaxPlaysPerDay)
                {
                    var midnight = DateTime.Today.AddDays(1);   // обнулится в полночь
                    if (best == null || midnight < best) best = midnight;
                }
            }
            return best;
        }

        // Оранжевые пометки «почему пропущен» в списке
        private void UpdateBlockInfos()
        {
            foreach (var it in _playlist)
            {
                var info = "";
                _stats.TryGetValue(it.FullPath, out var s);

                if (it.MaxPlaysPerDay > 0 &&
                    s?.Daily.TryGetValue(TodayKey, out var today) == true &&
                    today >= it.MaxPlaysPerDay)
                {
                    info = "⏳ лимит на сегодня";
                }
                else if (it.IntervalMinutes > 0 && s?.LastPlayed is DateTime lp)
                {
                    var until = lp.AddMinutes(it.IntervalMinutes);
                    if (until > DateTime.Now) info = $"⏳ до {until:HH:mm}";
                }

                it.BlockInfo = info;
            }
        }

        // Засчитать показ: всего, за сегодня, время последнего показа
        private void RecordPlay(PlaylistItem item)
        {
            if (!_stats.TryGetValue(item.FullPath, out var e))
            {
                e = new StatEntry();
                _stats[item.FullPath] = e;
            }

            e.Total++;
            e.Daily[TodayKey] = e.Daily.TryGetValue(TodayKey, out var c) ? c + 1 : 1;
            e.LastPlayed = DateTime.Now;

            item.PlayCount = e.Total;
            item.PlaysToday = e.Daily[TodayKey];
            SaveStats();
        }

        // Оператор поправил лимиты в списке — сохранить
        private void OnItemSettingsEdited(PlaylistItem item)
        {
            _plSettings[item.FullPath] = new PlaylistSettings
            {
                MaxPlaysPerDay = item.MaxPlaysPerDay,
                IntervalMinutes = item.IntervalMinutes
            };
            SavePlSettings();
            UpdateBlockInfos();
            StatusText.Text =
                $"«{item.FileName}»: лимит {item.MaxPlaysPerDay}/день, " +
                $"интервал {item.IntervalMinutes} мин — сохранено";
        }

        // ─── Масштаб FILL / FIT / STRETCH ──────────────────

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

            var item = (_index >= 0 && _index < _playlist.Count) ? _playlist[_index] : null;
            double vw = item?.VideoW ?? 0;
            double vh = item?.VideoH ?? 0;
            if (vw <= 0 || vh <= 0) { vw = 16; vh = 9; }

            try
            {
                switch (_config.ScaleMode)
                {
                    case "stretch":
                        VlcNative.libvlc_video_set_aspect_ratio(ptr, $"{pw}:{ph}");
                        VlcNative.libvlc_video_set_scale(ptr, 0f);
                        return $"Масштаб: STRETCH — тянем на {pw}:{ph}";

                    case "fill":
                        VlcNative.libvlc_video_set_aspect_ratio(ptr, null);
                        var k = Math.Max(pw / vw, ph / vh);
                        VlcNative.libvlc_video_set_scale(ptr, (float)k);
                        return $"Масштаб: FILL — зум ×{k:F2}";

                    default:
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

            // Вывести из ожидания и играть первый доступный
            if (_waiting)
            {
                _waiting = false;
                var next = FindNextPlayable(_index);
                if (next >= 0) _index = next;
                PlayCurrent();
                return;
            }

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

        // ◀◀ и ▶▶ — принудительно, лимиты не смотрим (оператор всегда прав)
        private void OnPrevClick(object sender, RoutedEventArgs e)
        {
            if (_playlist.Count == 0) return;
            _waiting = false;
            _index = (_index - 1 + _playlist.Count) % _playlist.Count;
            PlayCurrent();
        }

        private void OnNextClick(object sender, RoutedEventArgs e)
        {
            if (_playlist.Count == 0) return;
            _waiting = false;
            _index = (_index + 1) % _playlist.Count;
            PlayCurrent();
        }

        // Двойной клик по ролику — запустить именно его.
        // Двойной клик по полям лимитов — это редактирование, не запуск
        private void OnPlaylistDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            var onTextBox = false;
            while (src != null)
            {
                if (src is System.Windows.Controls.TextBox) { onTextBox = true; break; }
                if (src is ListBox || src is ListBoxItem) break;
                DependencyObject? parent = null;
                try { parent = VisualTreeHelper.GetParent(src); }
                catch { break; }
                src = parent;
            }
            if (onTextBox) return;

            if (PlaylistBox.SelectedIndex >= 0)
            {
                _waiting = false;
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
            => Dispatcher.BeginInvoke(AdvanceAuto);

        private void OnError(object? sender, EventArgs e)
            => Dispatcher.BeginInvoke(AdvanceAuto);

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

        // ─── Статистика (stats.json) ───────────────────────

        private void LoadStats()
        {
            _stats = new Dictionary<string, StatEntry>();
            try
            {
                if (!File.Exists(StatsPath)) return;
                var json = File.ReadAllText(StatsPath);
                try
                {
                    _stats = JsonSerializer.Deserialize<Dictionary<string, StatEntry>>(json)
                             ?? new Dictionary<string, StatEntry>();
                }
                catch
                {
                    // старый формат: путь → общее число показов
                    var old = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (old != null)
                        foreach (var kv in old)
                            _stats[kv.Key] = new StatEntry { Total = kv.Value };
                }
            }
            catch { }
        }

        private void SaveStats()
        {
            try
            {
                // подрезаем историю старше 60 дней, файл не растёт вечно
                var cutoff = DateTime.Today.AddDays(-60).ToString("yyyy-MM-dd");
                foreach (var e in _stats.Values)
                {
                    var stale = e.Daily.Keys
                        .Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList();
                    foreach (var k in stale) e.Daily.Remove(k);
                }
                File.WriteAllText(StatsPath, JsonSerializer.Serialize(_stats));
            }
            catch { }
        }

        // ─── Лимиты роликов (playlistSettings.json) ────────

        private void LoadPlSettings()
        {
            try
            {
                if (File.Exists(PlSettingsPath))
                    _plSettings = JsonSerializer.Deserialize<Dictionary<string, PlaylistSettings>>(
                        File.ReadAllText(PlSettingsPath))
                        ?? new Dictionary<string, PlaylistSettings>();
            }
            catch { _plSettings = new Dictionary<string, PlaylistSettings>(); }
        }

        private void SavePlSettings()
        {
            try { File.WriteAllText(PlSettingsPath, JsonSerializer.Serialize(_plSettings)); }
            catch { }
        }

        // ─── Часы: ожидание, пометки, добор инфо о ролике ───

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
            // Режим ожидания: как только ролик разблокируется — продолжаем сами
            if (_waiting)
            {
                var next = FindNextPlayable(_index);
                if (next >= 0)
                {
                    _waiting = false;
                    _index = next;
                    PlayCurrent();
                }
                else UpdateWaitingUi();
            }

            if (!_waiting && _playlist.Count > 0 && _index < _playlist.Count)
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
                        if (w > 0 && h > 0) { item.VideoW = w; item.VideoH = h; }
                    }
                }
                catch { }
            }

            UpdateBlockInfos();
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
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        public bool AutoStart { get; set; } = false;          // старт эфира при запуске программы
        public string PlaylistMode { get; set; } = "folder";  // "folder" | "list"
        public string ActiveComposition { get; set; } = "";

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
        public Dictionary<string, int> Daily { get; set; } = new();
        public DateTime? LastPlayed { get; set; }
    }

    // ─── Лимиты конкретного ролика (playlistSettings.json) ─
    public class PlaylistSettings
    {
        public int MaxPlaysPerDay { get; set; }
        public int IntervalMinutes { get; set; }
        public int ImageSeconds { get; set; } = 10;
    }

    // ─── Композиция (comps.json): снимок плейлиста ────────
    public class CompItem
    {
        public string Path { get; set; } = "";
        public int Max { get; set; }
        public int Int { get; set; }
        public int Sec { get; set; } = 10;
    }

    public class CompSnapshot
    {
        public int W { get; set; } = 1248;
        public int H { get; set; } = 576;
        public List<CompItem> Items { get; set; } = new();
    }

    // ─── Один пункт плейлиста ─────────────────────────────
    public class PlaylistItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public event Action? SettingsEdited;

        private void OnProp(string name) =>
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(name));

        public string FullPath { get; set; } = "";
        public int Number { get; set; }
        public long SizeBytes { get; set; }

        public bool IsImage { get; set; }

        public string TypeBadge => IsImage ? "🖼" : "";

        public System.Windows.Visibility ImageSecsVisible => IsImage
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        private bool _fileMissing;
        public bool FileMissing
        {
            get => _fileMissing;
            set
            {
                if (_fileMissing == value) return;
                _fileMissing = value;
                OnProp(nameof(FileMissing));
                OnProp(nameof(MissingText));
            }
        }

        public string MissingText => FileMissing ? "файл отсутствует" : "";

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

        private int _imageSecs = 10;
        public int ImageSeconds
        {
            get => _imageSecs;
            set { if (_imageSecs == value) return; _imageSecs = value; OnProp(nameof(ImageSeconds)); }
        }

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

        public string IntervalText
        {
            get => _intervalMin <= 0 ? "" : _intervalMin.ToString();
            set
            {
                var v = ParseInt(value);
                if (v > 10080) v = 10080;
                if (_intervalMin == v) { OnProp(nameof(IntervalText)); return; }
                _intervalMin = v;
                OnProp(nameof(IntervalText));
                SettingsEdited?.Invoke();
            }
        }

        public string ImageSecsText
        {
            get => _imageSecs.ToString();
            set
            {
                var v = ParseInt(value);
                if (v < 1) v = 1;
                if (v > 3600) v = 3600;
                if (_imageSecs == v) { OnProp(nameof(ImageSecsText)); return; }
                _imageSecs = v;
                OnProp(nameof(ImageSecsText));
                OnProp(nameof(DurationText));
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
                if (IsImage)
                {
                    var t = TimeSpan.FromSeconds(Math.Max(1, ImageSeconds));
                    return $"{t.Seconds} c";
                }
                if (DurationMs <= 0) return "--:--";
                var tv = TimeSpan.FromMilliseconds(DurationMs);
                return tv.TotalHours >= 1 ? tv.ToString(@"hh\:mm\:ss")
                                          : tv.ToString(@"mm\:ss");
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
        private ObservableCollection<PlaylistItem> _playlist = new();
        private int _index;
        private Config _config = new();
        private Dictionary<string, StatEntry> _stats = new();
        private Dictionary<string, PlaylistSettings> _plSettings = new();
        private Dictionary<string, CompSnapshot> _comps = new();
        private string _activeComp = "";
        private bool _isAppClosing;
        private bool _waiting;
        private bool _previewOnly;
        private bool _autoStartDone;

        private DateTime _imgStartedAt;
        private double _imgElapsedBase;

        private List<string> _manualFiles = new();
        private List<string> _manualOrder = new();

        private readonly System.Windows.Threading.DispatcherTimer _resizeDebounce =
            new() { Interval = TimeSpan.FromMilliseconds(150) };

        private static readonly string[] VideoExt =
            { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".mpg", ".webm" };
        private static readonly string[] ImageExt =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".webp" };

        private string StatsPath => Path.Combine(AppContext.BaseDirectory, "stats.json");
        private string PlSettingsPath => Path.Combine(AppContext.BaseDirectory, "playlistSettings.json");
        private string ManualListPath => Path.Combine(AppContext.BaseDirectory, "manualList.json");
        private string CompsPath => Path.Combine(AppContext.BaseDirectory, "comps.json");
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
            LoadManualList();
            LoadComps();
            _activeComp = _config.ActiveComposition;
            InitPlayer();
            _ = ReloadPlaylistAsync();
            StartClock();
            StartProgressTimer();
            RestoreOutput();
            UpdateAutoStartButton();

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

        private bool IsMediaFile(string f)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            return VideoExt.Contains(ext) || ImageExt.Contains(ext);
        }

        // Режим "folder": всё из папки + добавленное вручную.
        // Режим "list" (композиция): ТОЛЬКО список — что в папке не в списке, не играет.
        // Файлы, которых нет на диске, НЕ выкидываем — показываем сноской
        private List<string> CollectFiles()
        {
            List<string> all;
            if (_config.PlaylistMode == "list")
            {
                all = _manualFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
            {
                var fromFolder = Directory.Exists(_config.VideoFolder)
                    ? Directory.EnumerateFiles(_config.VideoFolder)
                        .Where(IsMediaFile)
                        .ToList()
                    : new List<string>();

                all = _manualFiles.Concat(fromFolder)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var ordered = new List<string>();
            foreach (var p in _manualOrder)
            {
                var hit = all.FirstOrDefault(a =>
                    string.Equals(a, p, StringComparison.OrdinalIgnoreCase));
                if (hit != null) { ordered.Add(hit); all.Remove(hit); }
            }
            ordered.AddRange(all.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            return ordered;
        }

        private async Task ReloadPlaylistAsync()
        {
            var files = CollectFiles();

            var items = new List<PlaylistItem>();
            for (int i = 0; i < files.Count; i++)
            {
                var info = new FileInfo(files[i]);
                _stats.TryGetValue(files[i], out var se);
                _plSettings.TryGetValue(files[i], out var st);

                var ext = Path.GetExtension(files[i]).ToLowerInvariant();
                var it = new PlaylistItem
                {
                    FullPath = files[i],
                    Number = i + 1,
                    SizeBytes = info.Exists ? info.Length : 0,
                    IsImage = ImageExt.Contains(ext),
                    FileMissing = !info.Exists,
                    PlayCount = se?.Total ?? 0,
                    PlaysToday = se?.Daily.TryGetValue(TodayKey, out var pc) == true ? pc : 0,
                    MaxPlaysPerDay = st?.MaxPlaysPerDay ?? 0,
                    IntervalMinutes = st?.IntervalMinutes ?? 0,
                    ImageSeconds = st?.ImageSeconds ?? 10
                };
                it.SettingsEdited += () => OnItemSettingsEdited(it);
                items.Add(it);
            }

            _playlist = new ObservableCollection<PlaylistItem>(items);
            if (_index >= _playlist.Count) _index = 0;

            await Task.Run(() =>
            {
                foreach (var item in _playlist.Where(x => !x.IsImage && !x.FileMissing))
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
            UpdateIdleState();

            // Автостарт: один раз после загрузки плейлиста
            if (_config.AutoStart && !_autoStartDone && _playlist.Count > 0)
            {
                _autoStartDone = true;
                var next = FindNextPlayable(_index);
                if (next >= 0) { _index = next; PlayCurrent(); }
                else StartWaiting();
                return;
            }

            if (!_config.AutoStart && !_player.IsPlaying && !_waiting)
            {
                StatusText.Text = _playlist.Count > 0
                    ? "Готов — нажмите ▶ для запуска"
                    : "Готов — плейлист пуст";
            }
        }

        private void RefreshPlaylistUi()
        {
            PlaylistBox.ItemsSource = _playlist;
            if (_index >= 0 && _index < _playlist.Count)
                PlaylistBox.SelectedIndex = _index;

            var miss = _playlist.Count(x => x.FileMissing);
            var mode = _config.PlaylistMode == "list"
                ? $"  ·  список: «{_activeComp}»"
                : "";
            StatusRight.Text = $"Роликов: {_playlist.Count}" +
                (miss > 0 ? $"  ·  отсутствуют: {miss}" : "") + mode;
        }

        private void UpdateIdleState()
        {
            if (_waiting)
            {
                VideoView.Visibility = Visibility.Collapsed;
                IdleText.Text = NextUnlockTime() is DateTime t
                    ? $"ПАУЗА — следующий ролик в {t:HH:mm}"
                    : "ПАУЗА — лимиты на сегодня исчерпаны";
                IdleText.Visibility = Visibility.Visible;
                return;
            }

            if (_playlist.Count == 0)
            {
                VideoView.Visibility = Visibility.Collapsed;
                IdleText.Text = "Нет сигнала";
                IdleText.Visibility = Visibility.Visible;
                return;
            }

            if (!_player.IsPlaying)
            {
                VideoView.Visibility = Visibility.Collapsed;
                IdleText.Text = "Готов — ▶ запуск";
                IdleText.Visibility = Visibility.Visible;
            }
        }

        private void PlayCurrent()
        {
            if (_playlist.Count == 0) return;
            if (_index >= _playlist.Count) _index = 0;

            var item = _playlist[_index];

            if (item.FileMissing)
            {
                StatusText.Text = $"Файл отсутствует: {item.FileName}";
                return;
            }

            var playable = IsPlayable(item);
            _previewOnly = !playable;

            using var media = new Media(_libVlc, new Uri(item.FullPath));
            if (item.IsImage)
            {
                media.AddOption($":image-duration={Math.Max(1, item.ImageSeconds)}");
                _imgElapsedBase = 0;
                _imgStartedAt = DateTime.Now;
            }
            _player.Play(media);

            if (_previewOnly)
            {
                _outPlayer.Stop();
                HideOutputVideo();
                SetAirLimit();
                StatusText.Text = $"ЛИМИТ — «{item.FileName}» только в предпросмотре";
            }
            else
            {
                if (_outputWindow != null)
                {
                    using var media2 = new Media(_libVlc, new Uri(item.FullPath));
                    if (item.IsImage)
                        media2.AddOption($":image-duration={Math.Max(1, item.ImageSeconds)}");
                    _outPlayer.Play(media2);
                    ShowOutputVideo();
                }

                RecordPlay(item);
                SetOnAir(true);
                StatusText.Text = $"Играет: {item.FileName}";
            }

            VideoView.Visibility = Visibility.Visible;
            IdleText.Visibility = Visibility.Collapsed;

            PlaylistBox.SelectedIndex = _index;
            BtnPlayPause.Content = "❚❚";

            ApplyScaling();
        }

        private void ShowOutputVideo()
        {
            if (_outputWindow != null)
                _outputWindow.VideoView.Visibility = Visibility.Visible;
        }

        private void HideOutputVideo()
        {
            if (_outputWindow != null)
                _outputWindow.VideoView.Visibility = Visibility.Collapsed;
        }

        private static string StartOption(double seconds) =>
            ":start-time=" + seconds.ToString("F2",
                System.Globalization.CultureInfo.InvariantCulture);

        // ─── Правила показов ───────────────────────────────

        private bool IsPlayable(PlaylistItem it)
        {
            if (it.FileMissing) return false;

            if (!_stats.TryGetValue(it.FullPath, out var s)) return true;

            if (it.MaxPlaysPerDay > 0 &&
                s.Daily.TryGetValue(TodayKey, out var today) &&
                today >= it.MaxPlaysPerDay)
                return false;

            if (it.IntervalMinutes > 0 && s.LastPlayed is DateTime lp &&
                DateTime.Now - lp < TimeSpan.FromMinutes(it.IntervalMinutes))
                return false;

            return true;
        }

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
            _previewOnly = false;
            _player.Stop();
            _outPlayer.Stop();
            HideOutputVideo();
            SetOnAir(false);
            BtnPlayPause.Content = "▶";
            UpdateIdleState();
            StatusText.Text = "Все ролики заблокированы лимитами — ожидание…";
        }

        private DateTime? NextUnlockTime()
        {
            DateTime? best = null;
            foreach (var it in _playlist)
            {
                if (it.FileMissing) continue;
                if (!_stats.TryGetValue(it.FullPath, out var s)) continue;

                if (it.IntervalMinutes > 0 && s.LastPlayed is DateTime lp)
                {
                    var until = lp.AddMinutes(it.IntervalMinutes);
                    if (until > DateTime.Now && (best == null || until < best)) best = until;
                }

                if (it.MaxPlaysPerDay > 0 &&
                    s.Daily.TryGetValue(TodayKey, out var c) && c >= it.MaxPlaysPerDay)
                {
                    var midnight = DateTime.Today.AddDays(1);
                    if (best == null || midnight < best) best = midnight;
                }
            }
            return best;
        }

        private void UpdateBlockInfos()
        {
            foreach (var it in _playlist)
            {
                var info = "";
                if (it.FileMissing)
                {
                    it.BlockInfo = "";
                    continue;
                }

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

        private void OnItemSettingsEdited(PlaylistItem item)
        {
            _plSettings[item.FullPath] = new PlaylistSettings
            {
                MaxPlaysPerDay = item.MaxPlaysPerDay,
                IntervalMinutes = item.IntervalMinutes,
                ImageSeconds = item.ImageSeconds
            };
            SavePlSettings();
            UpdateBlockInfos();
            StatusText.Text =
                $"«{item.FileName}»: лимит {item.MaxPlaysPerDay}/день, " +
                $"интервал {item.IntervalMinutes} мин — сохранено";
        }

        // ─── Кнопки плейлиста ──────────────────────────────

        private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(_config.VideoFolder))
                    Directory.CreateDirectory(_config.VideoFolder);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = _config.VideoFolder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть папку: " + ex.Message,
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            _ = ReloadPlaylistAsync();
            StatusText.Text = "Плейлист обновлён";
        }

        private void OnAddFilesClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Добавить файлы в плейлист",
                Filter = "Видео и картинки|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.mpg;*.webm;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tiff;*.webp|" +
                         "Все файлы|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog(this) != true) return;

            foreach (var f in dlg.FileNames)
                if (!_manualFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                    _manualFiles.Add(f);

            SaveManualList();
            StatusText.Text = $"Добавлено файлов: {dlg.FileNames.Length}";
            _ = ReloadPlaylistAsync();
        }

        private void OnAddFolderClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Forms.FolderBrowserDialog
            {
                ShowNewFolderButton = false,
                Description = "Добавить в плейлист видео и картинки из папки (включая подпапки)"
            };
            if (dlg.ShowDialog() != Forms.DialogResult.OK) return;

            var added = 0;
            try
            {
                var files = Directory.EnumerateFiles(dlg.SelectedPath, "*.*", SearchOption.AllDirectories)
                    .Where(IsMediaFile);
                foreach (var f in files)
                {
                    if (!_manualFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                    {
                        _manualFiles.Add(f);
                        added++;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось прочитать папку: " + ex.Message,
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            SaveManualList();
            StatusText.Text = added > 0
                ? $"Из папки добавлено файлов: {added}"
                : "В папке не нашлось новых видео или картинок";
            _ = ReloadPlaylistAsync();
        }

        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            var sel = PlaylistBox.SelectedItem as PlaylistItem;
            if (sel == null)
            {
                StatusText.Text = "Сначала выберите ролик в списке";
                return;
            }

            var idx = _playlist.IndexOf(sel);
            var name = sel.FileName;

            _manualFiles.RemoveAll(f =>
                string.Equals(f, sel.FullPath, StringComparison.OrdinalIgnoreCase));
            _manualOrder.RemoveAll(f =>
                string.Equals(f, sel.FullPath, StringComparison.OrdinalIgnoreCase));
            SaveManualList();

            var inFolder = Directory.Exists(_config.VideoFolder) &&
                sel.FullPath.StartsWith(_config.VideoFolder, StringComparison.OrdinalIgnoreCase);

            _ = ReloadPlaylistAsync().ContinueWith(_ =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (inFolder && _config.PlaylistMode == "folder")
                        StatusText.Text =
                            $"«{name}» убран, но файл лежит в папке {_config.VideoFolder} — " +
                            "в режиме «вся папка» он вернётся";
                    else
                        StatusText.Text = $"«{name}» убран из плейлиста";

                    if (idx < _playlist.Count) _index = idx;
                    else if (_playlist.Count > 0) _index = 0;
                }));
        }

        private void OnMoveUpClick(object sender, RoutedEventArgs e)
        {
            var sel = PlaylistBox.SelectedItem as PlaylistItem;
            if (sel == null) { StatusText.Text = "Сначала выберите ролик в списке"; return; }
            MoveItem(sel, -1);
        }

        private void OnMoveDownClick(object sender, RoutedEventArgs e)
        {
            var sel = PlaylistBox.SelectedItem as PlaylistItem;
            if (sel == null) { StatusText.Text = "Сначала выберите ролик в списке"; return; }
            MoveItem(sel, +1);
        }

        private void MoveItem(PlaylistItem sel, int dir)
        {
            int i = _playlist.IndexOf(sel);
            int j = i + dir;
            if (i < 0 || j < 0 || j >= _playlist.Count) return;

            _playlist.Move(i, j);
            _manualOrder = _playlist.Select(x => x.FullPath).ToList();
            SaveManualList();

            for (int k = 0; k < _playlist.Count; k++) _playlist[k].Number = k + 1;

            if (_index == i) _index = j;
            else if (_index == j) _index = i;

            PlaylistBox.SelectedIndex = j;
            StatusText.Text = $"Порядок: «{sel.FileName}» " + (dir < 0 ? "выше" : "ниже");
        }

        // Enter/Esc в полях плейлиста
        private void OnPlaylistBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.TextBox tb)
            {
                var expr = tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);

                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    expr?.UpdateSource();
                    System.Windows.Input.Keyboard.Focus(PlaylistBox);
                    e.Handled = true;
                }
                else if (e.Key == System.Windows.Input.Key.Escape)
                {
                    expr?.UpdateTarget();
                    System.Windows.Input.Keyboard.Focus(PlaylistBox);
                    e.Handled = true;
                }
            }
        }

        // ─── Автостарт ─────────────────────────────────────

        private void OnAutoStartClick(object sender, RoutedEventArgs e)
        {
            _config.AutoStart = !_config.AutoStart;
            SaveConfig();
            UpdateAutoStartButton();

            StatusText.Text = _config.AutoStart
                ? "Автостарт ВКЛЮЧЁН — эфир начнётся сам при следующем запуске программы"
                : "Автостарт ВЫКЛЮЧЕН — при запуске программы ничего не играет";
        }

        private void UpdateAutoStartButton()
        {
            if (_config.AutoStart)
            {
                BtnAutoStart.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x5B));
                BtnAutoStart.Content = "Автостарт: ВКЛ";
            }
            else
            {
                BtnAutoStart.Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x77, 0x2C));
                BtnAutoStart.Content = "Автостарт: ВЫКЛ";
            }
            BtnAutoStart.Foreground = Brushes.White;
        }

        // ─── Меню «Композиция» ─────────────────────────────

        private void OnCompositionClick(object sender, RoutedEventArgs e)
        {
            BuildCompositionMenu();
            CompositionMenu.IsOpen = true;
        }

        private void BuildCompositionMenu()
        {
            CompositionMenuPanel.Children.Clear();

            var modeText = _config.PlaylistMode == "list" && _activeComp != ""
                ? $"Режим: точный список — «{_activeComp}»"
                : "Режим: вся папка + добавленное вручную";
            CompositionMenuPanel.Children.Add(new TextBlock
            {
                Text = modeText,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xB0)),
                FontSize = 11,
                Margin = new Thickness(6, 4, 0, 4)
            });

            if (_config.PlaylistMode == "list")
                CompositionMenuPanel.Children.Add(
                    MakeMenuItem("⟲  Вернуться к режиму «вся папка»", SwitchToFolderMode));

            AddMenuSeparator();

            CompositionMenuPanel.Children.Add(MakeMenuItem(
                "💾  Сохранить текущий плейлист как композицию…", OnSaveCompositionMenu));

            AddMenuSeparator();

            CompositionMenuPanel.Children.Add(new TextBlock
            {
                Text = "СОХРАНЁННЫЕ КОМПОЗИЦИИ",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xB0)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(6, 2, 0, 4)
            });

            if (_comps.Count == 0)
            {
                CompositionMenuPanel.Children.Add(new TextBlock
                {
                    Text = "(пока ничего не сохранено)",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x88)),
                    FontSize = 12,
                    Margin = new Thickness(6, 2, 0, 4)
                });
            }

            foreach (var kv in _comps.OrderBy(x => x.Key))
            {
                var name = kv.Key;
                var row = new Grid();
                row.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = GridLength.Auto });

                var title = $"▶  {name}   ({kv.Value.Items.Count} рол.)" +
                            (name == _activeComp ? "   ✓ активна" : "");
                var applyBtn = MakeMenuItem(title, () => ApplyComposition(name));
                row.Children.Add(applyBtn);

                var delBtn = new Button
                {
                    Content = "✕",
                    FontSize = 13,
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(2, 1, 2, 1),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x90, 0x90))
                };
                delBtn.Style = (Style)FindResource("MenuButtonStyle");
                delBtn.Click += (_, _) => { CompositionMenu.IsOpen = false; DeleteComposition(name); };
                Grid.SetColumn(delBtn, 1);
                row.Children.Add(delBtn);

                CompositionMenuPanel.Children.Add(row);
            }
        }

        private void AddMenuSeparator()
        {
            CompositionMenuPanel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x55)),
                Margin = new Thickness(4, 5, 4, 5)
            });
        }

        private void OnSaveCompositionMenu()
        {
            if (_playlist.Count == 0)
            {
                StatusText.Text = "Плейлист пуст — нечего сохранять";
                return;
            }

            var prompt = new PromptWindow { Owner = this };
            if (prompt.ShowDialog() == true)
            {
                var name = (prompt.InputText ?? "").Trim();
                if (name == "")
                {
                    StatusText.Text = "Имя пустое — композиция не сохранена";
                    return;
                }
                SaveComposition(name);
            }
        }

        private void SaveComposition(string name)
        {
            var snap = new CompSnapshot
            {
                W = _config.CompW,
                H = _config.CompH,
                Items = _playlist.Select(it => new CompItem
                {
                    Path = it.FullPath,
                    Max = it.MaxPlaysPerDay,
                    Int = it.IntervalMinutes,
                    Sec = it.ImageSeconds
                }).ToList()
            };

            _comps[name] = snap;
            SaveComps();

            _activeComp = name;
            _config.ActiveComposition = name;
            _config.PlaylistMode = "list";
            _manualFiles = snap.Items.Select(i => i.Path).ToList();
            _manualOrder = _manualFiles.ToList();
            SaveConfig();
            SaveManualList();
            RefreshPlaylistUi();

            StatusText.Text =
                $"Композиция «{name}» сохранена ({snap.Items.Count} роликов) и включена";
        }

        private void ApplyComposition(string name)
        {
            if (!_comps.TryGetValue(name, out var comp)) return;

            _config.PlaylistMode = "list";
            _config.CompW = comp.W;
            _config.CompH = comp.H;
            _config.ActiveComposition = name;
            _manualFiles = comp.Items.Select(i => i.Path).ToList();
            _manualOrder = _manualFiles.ToList();
            foreach (var i in comp.Items)
                _plSettings[i.Path] = new PlaylistSettings
                {
                    MaxPlaysPerDay = i.Max,
                    IntervalMinutes = i.Int,
                    ImageSeconds = i.Sec
                };
            _activeComp = name;

            SaveConfig();
            SavePlSettings();
            SaveManualList();

            _index = 0;
            _ = ReloadPlaylistAsync();
            StatusText.Text = $"Композиция «{name}» включена ({comp.Items.Count} роликов)";
        }

        private void DeleteComposition(string name)
        {
            if (MessageBox.Show(this, $"Удалить композицию «{name}»?", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _comps.Remove(name);
            SaveComps();

            if (_activeComp == name)
            {
                _activeComp = "";
                _config.ActiveComposition = "";
                _config.PlaylistMode = "folder";
                SaveConfig();
                _ = ReloadPlaylistAsync();
            }

            StatusText.Text = $"Композиция «{name}» удалена";
        }

        private void SwitchToFolderMode()
        {
            _config.PlaylistMode = "folder";
            _config.ActiveComposition = "";
            _activeComp = "";
            SaveConfig();
            _ = ReloadPlaylistAsync();
            StatusText.Text = "Режим: вся папка + добавленное вручную";
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

        // ─── Транспорт ─────────────────────────────────────

        private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        {
            if (_playlist.Count == 0) return;

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
                if (!_previewOnly) _outPlayer.Pause();

                if (_playlist[_index].IsImage)
                    _imgElapsedBase += (DateTime.Now - _imgStartedAt).TotalSeconds;

                BtnPlayPause.Content = "▶";
                StatusText.Text = _previewOnly ? "Пауза (тест)" : "Пауза";
                SetOnAir(false);
            }
            else if (_player.Time > 0)
            {
                if (_playlist[_index].IsImage) _imgStartedAt = DateTime.Now;
                _player.Play();
                if (!_previewOnly) _outPlayer.Play();
                BtnPlayPause.Content = "❚❚";
                if (_previewOnly) SetAirLimit();
                else SetOnAir(true);
                StatusText.Text = _previewOnly
                    ? "ЛИМИТ — продолжение теста в предпросмотре"
                    : $"Играет: {Path.GetFileName(_playlist[_index].FullPath)}";
            }
            else
            {
                if (IsPlayable(_playlist[_index]))
                {
                    PlayCurrent();
                }
                else
                {
                    var next = FindNextPlayable(_index);
                    if (next >= 0) _index = next;
                    PlayCurrent();
                }
            }
        }

        // ■ — полная остановка: ролик сбрасывается в начало
        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            if (_playlist.Count == 0) return;

            _player.Stop();
            _outPlayer.Stop();

            _waiting = false;
            _previewOnly = false;
            _imgElapsedBase = 0;

            HideOutputVideo();
            BtnPlayPause.Content = "▶";
            SetAirIdle();

            VideoView.Visibility = Visibility.Collapsed;
            IdleText.Text = "Готов — ▶ запуск";
            IdleText.Visibility = Visibility.Visible;

            PlayBar.Value = 0;
            TimeNow.Text = "00:00";
            TimeTotal.Text = "00:00";

            StatusText.Text = "Стоп";
        }

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
                CompositionMenu.IsOpen = false;
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

            if (!_player.IsPlaying || _previewOnly)
            {
                _outPlayer.Stop();
                HideOutputVideo();
                ApplyScaling();
                return;
            }

            if (_playlist.Count == 0 || _index >= _playlist.Count) return;

            var item = _playlist[_index];
            var startSec = _player.Time / 1000.0;
            using var media = new Media(_libVlc, new Uri(item.FullPath));
            if (item.IsImage)
                media.AddOption($":image-duration={Math.Max(1, item.ImageSeconds)}");
            else if (startSec > 0.5)
                media.AddOption(StartOption(startSec));
            _outPlayer.Play(media);
            ShowOutputVideo();

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

        // ─── Лимиты (playlistSettings.json) ────────────────

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

        // ─── Ручной список и порядок (manualList.json) ─────

        private void LoadManualList()
        {
            try
            {
                if (File.Exists(ManualListPath))
                {
                    var doc = JsonSerializer.Deserialize<ManualListDoc>(
                        File.ReadAllText(ManualListPath));
                    if (doc != null)
                    {
                        _manualFiles = doc.Files ?? new List<string>();
                        _manualOrder = doc.Order ?? new List<string>();
                    }
                }
            }
            catch { }
        }

        private void SaveManualList()
        {
            try
            {
                File.WriteAllText(ManualListPath, JsonSerializer.Serialize(
                    new ManualListDoc { Files = _manualFiles, Order = _manualOrder },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private class ManualListDoc
        {
            public List<string> Files { get; set; } = new();
            public List<string> Order { get; set; } = new();
        }

        // ─── Композиции (comps.json) ───────────────────────

        private void LoadComps()
        {
            try
            {
                if (File.Exists(CompsPath))
                    _comps = JsonSerializer.Deserialize<Dictionary<string, CompSnapshot>>(
                        File.ReadAllText(CompsPath))
                        ?? new Dictionary<string, CompSnapshot>();
            }
            catch { _comps = new Dictionary<string, CompSnapshot>(); }
        }

        private void SaveComps()
        {
            try
            {
                File.WriteAllText(CompsPath,
                    JsonSerializer.Serialize(_comps, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ─── Часы ──────────────────────────────────────────

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
            if (_waiting)
            {
                var next = FindNextPlayable(_index);
                if (next >= 0)
                {
                    _waiting = false;
                    _index = next;
                    PlayCurrent();
                }
                else UpdateIdleState();
            }

            if (!_waiting && _playlist.Count > 0 && _index < _playlist.Count)
            {
                var item = _playlist[_index];
                try
                {
                    if (!item.IsImage && item.DurationMs <= 0)
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

        // ─── Прогресс-бар ──────────────────────────────────

        private void StartProgressTimer()
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            timer.Tick += (_, _) => UpdateProgress();
            timer.Start();
        }

        private void UpdateProgress()
        {
            if (_playlist.Count == 0 || _index >= _playlist.Count ||
                (!_player.IsPlaying && _player.Time <= 0))
            {
                PlayBar.Value = 0;
                return;
            }

            var item = _playlist[_index];
            double curSec = 0, totalSec = 0;

            if (item.IsImage)
            {
                totalSec = Math.Max(1, item.ImageSeconds);
                var elapsed = _imgElapsedBase;
                if (_player.IsPlaying)
                    elapsed += (DateTime.Now - _imgStartedAt).TotalSeconds;
                curSec = elapsed;
            }
            else
            {
                try
                {
                    totalSec = _player.Length / 1000.0;
                    if (totalSec <= 0) totalSec = item.DurationMs / 1000.0;
                    curSec = _player.Time / 1000.0;
                }
                catch { }
            }

            var ratio = totalSec > 0 ? Math.Clamp(curSec / totalSec, 0, 1) : 0;

            PlayBar.Value = ratio * 100.0;
            TimeNow.Text = FmtTime(curSec);
            TimeTotal.Text = FmtTime(totalSec);
        }

        private static string FmtTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
        }

        // ─── Индикатор эфира ───────────────────────────────

        private void SetOnAir(bool onAir)
        {
            var brush = onAir ? Brushes.MediumSpringGreen : Brushes.Orange;
            OnAirDot.Fill = brush;
            OnAirText.Foreground = brush;
            OnAirText.Text = onAir ? "В ЭФИРЕ" : "ПАУЗА";
        }

        private void SetAirLimit()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0xE0, 0xC0, 0x60));
            OnAirDot.Fill = brush;
            OnAirText.Foreground = brush;
            OnAirText.Text = "ЛИМИТ";
        }

        private void SetAirIdle()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x78));
            OnAirDot.Fill = brush;
            OnAirText.Foreground = brush;
            OnAirText.Text = "ОЖИДАНИЕ";
        }
    }
}
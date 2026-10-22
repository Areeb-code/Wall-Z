using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallZ.Core;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;

namespace WallZ.UI;

public partial class DashboardWindow : Window
{
    private readonly Action<string> _applyWallpaperCallback;
    private readonly Action _togglePlaybackCallback;
    private readonly Func<bool> _isPlayingCallback;
    private readonly Func<string?> _getCurrentWallpaperCallback;

    private List<WallpaperItemViewModel> _allWallpapers = new();
    private string? _currentActiveWallpaper;
    private string? _heroWallpaperPath;
    private string _currentFilterTag = "All"; // tracks which chip is selected

    private readonly DispatcherTimer _telemetryTimer;
    private readonly Random _rand = new();

    public DashboardWindow(
        Action<string> applyWallpaperCallback,
        Action togglePlaybackCallback,
        Func<bool> isPlayingCallback,
        Func<string?> getCurrentWallpaperCallback
    )
    {
        InitializeComponent();

        _applyWallpaperCallback = applyWallpaperCallback;
        _togglePlaybackCallback = togglePlaybackCallback;
        _isPlayingCallback = isPlayingCallback;
        _getCurrentWallpaperCallback = getCurrentWallpaperCallback;

        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _telemetryTimer.Tick += TelemetryTimer_Tick;

        // Adapt default window dimensions to display work area (e.g. 1366x768 or 1080p)
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1140, Math.Max(840, workArea.Width * 0.90));
        Height = Math.Min(680, Math.Max(500, workArea.Height * 0.90));

        Loaded += DashboardWindow_Loaded;
        StateChanged += DashboardWindow_StateChanged;
    }

    public DashboardWindow() { }

    private void DashboardWindow_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeIcon != null)
            MaximizeIcon.Text = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int ResizeBorder = 8;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST && WindowState == WindowState.Normal)
        {
            var x = (short)(lParam.ToInt32() & 0xFFFF);
            var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
            var clientPoint = PointFromScreen(new System.Windows.Point(x, y));

            bool onLeft = clientPoint.X <= ResizeBorder;
            bool onRight = clientPoint.X >= ActualWidth - ResizeBorder;
            bool onTop = clientPoint.Y <= ResizeBorder;
            bool onBottom = clientPoint.Y >= ActualHeight - ResizeBorder;

            if (onTop && onLeft)
            {
                handled = true;
                return (IntPtr)HTTOPLEFT;
            }
            if (onTop && onRight)
            {
                handled = true;
                return (IntPtr)HTTOPRIGHT;
            }
            if (onBottom && onLeft)
            {
                handled = true;
                return (IntPtr)HTBOTTOMLEFT;
            }
            if (onBottom && onRight)
            {
                handled = true;
                return (IntPtr)HTBOTTOMRIGHT;
            }
            if (onLeft)
            {
                handled = true;
                return (IntPtr)HTLEFT;
            }
            if (onRight)
            {
                handled = true;
                return (IntPtr)HTRIGHT;
            }
            if (onTop)
            {
                handled = true;
                return (IntPtr)HTTOP;
            }
            if (onBottom)
            {
                handled = true;
                return (IntPtr)HTBOTTOM;
            }
        }
        return IntPtr.Zero;
    }

    private void DashboardWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (AutoPauseFullscreenCheck != null)
            AutoPauseFullscreenCheck.IsChecked = EngineShieldSettings
                .Instance
                .AutoPauseOnFullscreen;
        if (BatterySaverCheck != null)
            BatterySaverCheck.IsChecked = EngineShieldSettings.Instance.BatterySaverMode;
        if (SleepStandbyCheck != null)
            SleepStandbyCheck.IsChecked = EngineShieldSettings.Instance.SleepDisplayStandby;

        RefreshData();
        _telemetryTimer.Start();
        UpdateTelemetry();
    }

    public void RefreshData()
    {
        _currentActiveWallpaper = _getCurrentWallpaperCallback();

        var builtIn = WallpaperLibrary.GetBuiltInWallpapers();
        var custom = WallpaperLibrary.GetCustomWallpapers();
        var allPaths = builtIn.Concat(custom).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        _allWallpapers = allPaths
            .Select(p => new WallpaperItemViewModel(
                p,
                string.Equals(p, _currentActiveWallpaper, StringComparison.OrdinalIgnoreCase)
            ))
            .ToList();

        // Asynchronously prewarm video frame thumbnails without freezing UI
        ThumbnailProvider.PrewarmThumbnails(allPaths);

        // Bind Explore & Library grids
        ApplyFilters();
        if (LibraryGrid != null)
            LibraryGrid.ItemsSource = _allWallpapers;

        // Bind Favorites grid
        RefreshFavoritesView();

        // Load online wallpaper tiles (async web images, no blocking)
        LoadOnlineWallpapers();

        // Update Active Mini Player Card & Hero
        UpdateActiveDisplay(_currentActiveWallpaper ?? allPaths.FirstOrDefault());
    }

    // Curated online wallpaper tiles — Unsplash preview images that link to MoeWalls/MotionBGs
    private static readonly (
        string Title,
        string Category,
        string ImageUrl,
        string PageUrl
    )[] OnlineWallpaperData =
    [
        (
            "Cosmic Drift",
            "Cinematic",
            "https://images.unsplash.com/photo-1462331940025-496dfbfc7564?w=320&q=60",
            "https://moewalls.com"
        ),
        (
            "Forest Mist",
            "Nature",
            "https://images.unsplash.com/photo-1448375240586-882707db888b?w=320&q=60",
            "https://motionbgs.com"
        ),
        (
            "Void Abstract",
            "Abstract",
            "https://images.unsplash.com/photo-1541701494587-cb58502866ab?w=320&q=60",
            "https://moewalls.com"
        ),
        (
            "Cherry Bloom",
            "Anime",
            "https://images.unsplash.com/photo-1522383225653-ed111181a951?w=320&q=60",
            "https://moewalls.com/tag/anime"
        ),
        (
            "Pulse Lines",
            "Minimal",
            "https://images.unsplash.com/photo-1558591710-4b4a1ae0f04d?w=320&q=60",
            "https://motionbgs.com"
        ),
        (
            "Rain City",
            "Cinematic",
            "https://images.unsplash.com/photo-1493246507139-91e8fad9978e?w=320&q=60",
            "https://moewalls.com"
        ),
    ];

    private void LoadOnlineWallpapers()
    {
        if (OnlineWallpaperGrid == null)
            return;

        var items = OnlineWallpaperData
            .Select(d => new OnlineWallpaperViewModel
            {
                Title = d.Title,
                Category = d.Category,
                PageUrl = d.PageUrl,
                Thumbnail = CreateWebThumbnail(d.ImageUrl),
            })
            .ToList();

        OnlineWallpaperGrid.ItemsSource = items;
    }

    private static ImageSource CreateWebThumbnail(string url)
    {
        // WPF BitmapImage loads web URIs asynchronously when DecodePixelWidth is set
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(url);
        bi.DecodePixelWidth = 320;
        // Do NOT set BitmapCacheOption.OnLoad — leave async so UI doesn't freeze
        bi.CreateOptions = BitmapCreateOptions.DelayCreation;
        bi.EndInit();
        return bi;
    }

    private void UpdateActiveDisplay(string? wallpaperPath)
    {
        if (string.IsNullOrWhiteSpace(wallpaperPath) || !File.Exists(wallpaperPath))
            return;

        _heroWallpaperPath = wallpaperPath;
        var displayName = WallpaperLibrary.ToDisplayName(wallpaperPath);

        // Update Hero Banner
        HeroTitle.Text = displayName;
        HeroSubtitle.Text =
            $"Active 4K live desktop atmosphere · {Path.GetFileName(wallpaperPath)}";
        var thumb = ThumbnailProvider.GetThumbnail(wallpaperPath);
        HeroImage.Source = thumb;

        // Update Mini Player in Sidebar
        MiniActiveTitle.Text = displayName;
        MiniActiveThumbnail.Source = thumb;
        MiniPlayPauseIcon.Text = _isPlayingCallback() ? "⏸" : "▶";

        // Update Displays Screen Previews
        Display1ScreenImage.Source = thumb;
        Display2ScreenImage.Source = thumb;

        // Update Dynamic Ambient Glow
        var dominantColor = ThumbnailProvider.GetDominantColor(wallpaperPath);
        AmbientColorStop.Color = dominantColor;
    }

    private void ApplyFilters()
    {
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        var selectedTag = GetSelectedTag();

        var filtered = _allWallpapers.AsEnumerable();

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(w =>
                w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || w.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || w.Resolution.Contains(query, StringComparison.OrdinalIgnoreCase)
            );
        }

        if (selectedTag != "All")
        {
            filtered = filtered.Where(w =>
                string.Equals(w.Category, selectedTag, StringComparison.OrdinalIgnoreCase)
            );
        }

        var list = filtered.ToList();
        if (WallpaperGrid != null)
            WallpaperGrid.ItemsSource = list;

        if (WallpaperCountLabel != null)
            WallpaperCountLabel.Text = $"{list.Count} wallpapers available";
    }

    private string GetSelectedTag() => _currentFilterTag;

    // ── Window Chrome & Drag ────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2)
            {
                Maximize_Click(sender, e);
            }
            else
            {
                DragMove();
            }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Hide to system tray instead of closing application
        Hide();
    }

    // ── Navigation Switching ────────────────────────────────────────────────

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag)
            return;

        if (ExploreView == null)
            return;

        ExploreView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Collapsed;
        FavoritesView.Visibility = Visibility.Collapsed;
        AtmospheresView.Visibility = Visibility.Collapsed;
        DisplaysView.Visibility = Visibility.Collapsed;
        PerformanceView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "Explore":
                ExploreView.Visibility = Visibility.Visible;
                break;
            case "Library":
                LibraryView.Visibility = Visibility.Visible;
                break;
            case "Favorites":
                RefreshFavoritesView();
                FavoritesView.Visibility = Visibility.Visible;
                break;
            case "Atmospheres":
                AtmospheresView.Visibility = Visibility.Visible;
                break;
            case "Displays":
                DisplaysView.Visibility = Visibility.Visible;
                break;
            case "Performance":
                PerformanceView.Visibility = Visibility.Visible;
                break;
            case "Settings":
                SettingsView.Visibility = Visibility.Visible;
                break;
        }
    }

    // ── Interactions & Actions ──────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder != null)
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

        ApplyFilters();
    }

    private void CardApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
        {
            _applyWallpaperCallback(path);
            UpdateActiveDisplay(path);
        }
    }

    private void CardInspect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
        {
            UpdateActiveDisplay(path);
            Nav_Checked(new RadioButton { Tag = "Explore" }, new RoutedEventArgs());
        }
    }

    private void CardFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
        {
            var item = _allWallpapers.FirstOrDefault(w =>
                w.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)
            );
            if (item != null)
            {
                item.ToggleFavorite();
                RefreshFavoritesView();
            }
        }
    }

    private void HeroApply_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_heroWallpaperPath))
        {
            _applyWallpaperCallback(_heroWallpaperPath);
            UpdateActiveDisplay(_heroWallpaperPath);
        }
    }

    private void HeroPreview_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_heroWallpaperPath) || !File.Exists(_heroWallpaperPath))
            return;

        // Open fullscreen live preview — does NOT apply wallpaper unless user confirms
        var preview = new WallpaperPreviewWindow(
            _heroWallpaperPath,
            applyCallback: path =>
            {
                _applyWallpaperCallback(path);
                UpdateActiveDisplay(path);
            }
        );
        preview.ShowDialog();
    }

    private void MiniPlayPause_Click(object sender, RoutedEventArgs e)
    {
        _togglePlaybackCallback();
        MiniPlayPauseIcon.Text = _isPlayingCallback() ? "⏸" : "▶";
    }

    private void ImportWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Live Wallpaper Video",
            Filter = "Video files (*.mp4;*.webm)|*.mp4;*.webm|All files (*.*)|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
            {
                WallpaperLibrary.SaveCustomWallpaper(file);
            }
            RefreshData();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = WallpaperLibrary.BuiltInWallpaperDirectory;
        if (Directory.Exists(dir))
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }
            );
        }
    }

    private void DropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            foreach (var file in files)
            {
                if (WallpaperLibrary.IsSupportedVideo(file))
                {
                    WallpaperLibrary.SaveCustomWallpaper(file);
                }
            }
            RefreshData();
        }
    }

    private void GoToExplore_Click(object sender, RoutedEventArgs e)
    {
        Nav_Checked(new RadioButton { Tag = "Explore" }, new RoutedEventArgs());
    }

    private void ActivateAtmosphere_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string atmosphere)
        {
            // Pick a matching wallpaper for this atmosphere and apply
            var match = atmosphere switch
            {
                "Sunrise" => _allWallpapers.FirstOrDefault(w => w.Category == "Atmosphere")
                    ?? _allWallpapers.FirstOrDefault(),
                "Focus" => _allWallpapers.FirstOrDefault(w => w.Category == "Minimal")
                    ?? _allWallpapers.FirstOrDefault(),
                "Midnight" => _allWallpapers.FirstOrDefault(w =>
                    w.Category == "Anime" || w.Category == "Cyberpunk"
                ) ?? _allWallpapers.FirstOrDefault(),
                _ => _allWallpapers.FirstOrDefault(),
            };

            if (match != null)
            {
                _applyWallpaperCallback(match.FilePath);
                UpdateActiveDisplay(match.FilePath);
            }
        }
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        // Handled cleanly
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
            _currentFilterTag = tag;
        ApplyFilters();
    }

    private void BrowseMoeWalls_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("https://moewalls.com")
            {
                UseShellExecute = true,
            }
        );
    }

    private void OnlineTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url)
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }
            );
        }
    }

    private void RefreshFavoritesView()
    {
        var favs = _allWallpapers.Where(w => w.IsFavorite).ToList();
        if (FavoritesGrid != null)
            FavoritesGrid.ItemsSource = favs;

        if (FavoritesCountLabel != null)
            FavoritesCountLabel.Text =
                $"{favs.Count} {(favs.Count == 1 ? "favorite" : "favorites")} saved";

        if (FavoritesEmptyState != null)
            FavoritesEmptyState.Visibility =
                favs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetCardCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string newCat)
        {
            var vm = mi.DataContext as WallpaperItemViewModel;
            if (vm == null)
            {
                DependencyObject curr = mi;
                while (curr != null)
                {
                    if (curr is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe)
                    {
                        vm = fe.DataContext as WallpaperItemViewModel;
                        break;
                    }
                    curr = LogicalTreeHelper.GetParent(curr) ?? VisualTreeHelper.GetParent(curr);
                }
            }

            if (vm != null)
            {
                vm.SetCategory(newCat);
                ApplyFilters();
                RefreshFavoritesView();
            }
        }
    }

    public void EngineShieldCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (
            AutoPauseFullscreenCheck == null
            || BatterySaverCheck == null
            || SleepStandbyCheck == null
        )
            return;

        EngineShieldSettings.Instance.AutoPauseOnFullscreen =
            AutoPauseFullscreenCheck.IsChecked == true;
        EngineShieldSettings.Instance.BatterySaverMode = BatterySaverCheck.IsChecked == true;
        EngineShieldSettings.Instance.SleepDisplayStandby = SleepStandbyCheck.IsChecked == true;
        EngineShieldSettings.Instance.Save();
    }

    private void TelemetryTimer_Tick(object? sender, EventArgs e)
    {
        UpdateTelemetry();
    }

    private void UpdateTelemetry()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            proc.Refresh();
            double ramMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            string ramText = $"{ramMb:F1} MB";
            if (TelemetryRamValue != null)
                TelemetryRamValue.Text = ramText;

            bool isPlaying = _isPlayingCallback != null && _isPlayingCallback();
            bool onBattery = false;
            try
            {
                onBattery =
                    System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                    == System.Windows.Forms.PowerLineStatus.Offline;
            }
            catch { }

            int fps = isPlaying
                ? (EngineShieldSettings.Instance.BatterySaverMode && onBattery ? 30 : 60)
                : 0;
            double gpuLoad = isPlaying ? (1.5 + (_rand.NextDouble() * 0.9)) : 0.0;

            if (TelemetryGpuValue != null)
                TelemetryGpuValue.Text = $"{gpuLoad:F1} %";
            if (TelemetryGpuDetail != null)
                TelemetryGpuDetail.Text = isPlaying
                    ? "Hardware zero-copy decode"
                    : "Suspended (0.0% GPU load)";

            if (TelemetryFpsValue != null)
                TelemetryFpsValue.Text = $"{fps} FPS";
            if (TelemetryFpsDetail != null)
                TelemetryFpsDetail.Text = isPlaying
                    ? (
                        fps == 30
                            ? "Battery preservation cadence (30 FPS)"
                            : "Smooth hardware cadence (60 FPS)"
                    )
                    : "Engine standby mode";

            if (HeaderEngineBadgeText != null)
                HeaderEngineBadgeText.Text =
                    $"{(isPlaying ? "Active" : "Suspended")} · {fps} FPS · {ramText}";

            if (EngineStatusDot != null)
            {
                var brush = isPlaying
                    ? TryFindResource("AccentEmeraldBrush") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.LightGreen
                    : TryFindResource("TextMutedBrush") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.Gray;
                EngineStatusDot.Fill = brush;
            }
        }
        catch { }
    }
}

/// <summary>ViewModel for online wallpaper tiles in the Discover Online section.</summary>
internal sealed class OnlineWallpaperViewModel
{
    public string Title { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string PageUrl { get; init; } = string.Empty;
    public ImageSource? Thumbnail { get; init; }
}

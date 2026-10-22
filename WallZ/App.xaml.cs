using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WallZ.Core;
using WallZ.UI;
using WinForms = System.Windows.Forms;

namespace WallZ;

public partial class App : System.Windows.Application
{
    private readonly Stopwatch _startupWatch = Stopwatch.StartNew();
    private WallpaperHostWindow? _host;
    private DesktopActivityWatcher? _activity;
    private NotifyIconController? _tray;
    private DashboardWindow? _dashboardWindow;
    private IReadOnlyList<string> _builtInWallpapers = Array.Empty<string>();
    private IReadOnlyList<string> _customWallpapers = Array.Empty<string>();
    private string? _pendingWallpaper;
    private bool _isEngineStarting;
    private bool _isShuttingDown;
    private int _wallpaperRequestVersion;
    private Mutex? _singleInstanceMutex;
    private DispatcherTimer? _startupWallpaperTimer;
    private DispatcherTimer? _recoveryTimer;
    private DispatcherTimer? _placementTimer;
    private DispatcherTimer? _surfaceWatchdogTimer;
    private DispatcherTimer? _memoryWatchdogTimer; // trims thumbnail LRU cache under memory pressure

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _singleInstanceMutex = new Mutex(true, "WALL Z", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        AppLog.Write($"Startup begin. {AppLog.GetProcessSnapshot()}");
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        WinForms.Application.SetUnhandledExceptionMode(
            WinForms.UnhandledExceptionMode.CatchException
        );
        WinForms.Application.ThreadException += OnWinFormsThreadException;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        RefreshWallpaperLibrary();
        _pendingWallpaper = WallpaperLibrary.GetStartupWallpaper(
            _builtInWallpapers,
            _customWallpapers
        );

        _tray = new NotifyIconController(
            playPause: TogglePlayback,
            restartWallpaper: RestartWallpaper,
            isPlaying: () => _host?.IsPlaying == true,
            currentWallpaper: () => _host?.CurrentVideo,
            openDashboard: OpenDashboard,
            exit: ExitApp
        );

        AppLog.Write(
            $"Tray ready at {_startupWatch.ElapsedMilliseconds} ms. {AppLog.GetProcessSnapshot()}"
        );

        // Show onboarding on first launch (runs before wallpaper engine so the
        // disk-intensive LibVLC init doesn't compete with the animated window).
        if (!OnboardingWindow.IsCompleted)
        {
            var ob = new OnboardingWindow();
            ob.ShowDialog();
        }

        var dashboard = new DashboardWindow();
        MainWindow = dashboard;
        dashboard.Show();

        QueueWallpaperEngineStartup();
    }

    private void TogglePlayback()
    {
        if (_host is null)
        {
            EnsureWallpaperEngine();
            return;
        }

        if (_host.IsPlaying)
            _host.Pause();
        else
            _host.Resume();
    }

    private void ChangeWallpaper()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose live wallpaper video",
            Filter = "Video files (*.mp4;*.webm)|*.mp4;*.webm|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            RequestWallpaperPlayback(dialog.FileName, saveAsCustomWallpaper: true);
        }
    }

    private void RestartWallpaper()
    {
        if (_host is null)
            EnsureWallpaperEngine();
        else
            _host.Restart();
    }

    private static void OpenWallpaperFolder()
    {
        var dir = WallpaperLibrary.BuiltInWallpaperDirectory;
        if (Directory.Exists(dir))
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }
            );
    }

    private void OpenDashboard()
    {
        if (_dashboardWindow == null)
        {
            _dashboardWindow = new DashboardWindow(
                applyWallpaperCallback: path =>
                    RequestWallpaperPlayback(path, saveAsCustomWallpaper: true),
                togglePlaybackCallback: TogglePlayback,
                isPlayingCallback: () => _host?.IsPlaying == true,
                getCurrentWallpaperCallback: () => _host?.CurrentVideo ?? _pendingWallpaper
            );

            _dashboardWindow.Closed += (_, _) => _dashboardWindow = null;
        }

        _dashboardWindow.RefreshData();
        _dashboardWindow.Show();
        if (_dashboardWindow.WindowState == WindowState.Minimized)
            _dashboardWindow.WindowState = WindowState.Normal;
        _dashboardWindow.Activate();
    }

    private void ManageCustomWallpapers()
    {
        RefreshWallpaperLibrary();

        using var dialog = new WallpaperManagerDialog(_customWallpapers);
        if (dialog.ShowDialog() != WinForms.DialogResult.OK || dialog.SelectedWallpapers.Count == 0)
            return;

        WallpaperLibrary.RemoveCustomWallpapers(dialog.SelectedWallpapers);
        RefreshWallpaperLibrary();
        _tray?.RefreshLibrary(_builtInWallpapers, _customWallpapers);

        if (
            _host?.CurrentVideo is not null
            && dialog.SelectedWallpapers.Contains(
                _host.CurrentVideo,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            _pendingWallpaper = WallpaperLibrary.GetStartupWallpaper(
                _builtInWallpapers,
                _customWallpapers
            );
            if (_pendingWallpaper is not null)
                _host.Play(_pendingWallpaper);
        }
    }

    private void ExitApp()
    {
        _isShuttingDown = true;
        _dashboardWindow?.Close();
        _dashboardWindow = null;
        DesktopIcons.Show();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        WinForms.Application.ThreadException -= OnWinFormsThreadException;
        _recoveryTimer?.Stop();
        _placementTimer?.Stop();
        _surfaceWatchdogTimer?.Stop();
        _startupWallpaperTimer?.Stop();
        _memoryWatchdogTimer?.Stop();
        _activity?.Dispose();
        _tray?.Dispose();
        _host?.Dispose();
        _host = null;
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Shutdown();
    }

    private void StartMemoryWatchdog()
    {
        _memoryWatchdogTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _memoryWatchdogTimer.Stop();
        _memoryWatchdogTimer.Tick -= OnMemoryWatchdogTick;
        _memoryWatchdogTimer.Tick += OnMemoryWatchdogTick;
        _memoryWatchdogTimer.Start();
    }

    private void OnMemoryWatchdogTick(object? sender, EventArgs e)
    {
        if (_isShuttingDown)
            return;
        ThumbnailProvider.TrimCacheIfNeeded(maxMB: 350);
        AppLog.Write($"Memory watchdog. {AppLog.GetProcessSnapshot()}");
    }

    private void PlayWallpaper(string path)
    {
        RequestWallpaperPlayback(path, saveAsCustomWallpaper: false);
    }

    private void RequestWallpaperPlayback(string path, bool saveAsCustomWallpaper)
    {
        if (_isShuttingDown || string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path) || !WallpaperLibrary.IsSupportedVideo(path))
        {
            AppLog.Write($"Cannot apply wallpaper: file missing or unsupported: {path}");
            return;
        }

        var requestVersion = Interlocked.Increment(ref _wallpaperRequestVersion);

        Dispatcher.InvokeAsync(
            () =>
            {
                if (_isShuttingDown || requestVersion != _wallpaperRequestVersion)
                    return;

                if (saveAsCustomWallpaper)
                {
                    var isBuiltIn = _builtInWallpapers.Contains(
                        path,
                        StringComparer.OrdinalIgnoreCase
                    );
                    if (!isBuiltIn)
                        WallpaperLibrary.SaveCustomWallpaper(path);
                }

                _pendingWallpaper = path;
                WallpaperLibrary.SaveLastWallpaper(path);
                RefreshWallpaperLibrary();
                _tray?.RefreshLibrary(_builtInWallpapers, _customWallpapers);

                if (_host is null)
                    EnsureWallpaperEngine();
                else
                    _host.Play(path);

                AppLog.Write($"Successfully applied wallpaper: {path}");
                _dashboardWindow?.RefreshData();
            },
            DispatcherPriority.Normal
        );
    }

    private void QueueWallpaperEngineStartup()
    {
        _startupWallpaperTimer?.Stop();
        _startupWallpaperTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _startupWallpaperTimer.Tick += (_, _) =>
        {
            _startupWallpaperTimer?.Stop();
            EnsureWallpaperEngine();
        };
        _startupWallpaperTimer.Start();
    }

    private void EnsureWallpaperEngine()
    {
        if (_isShuttingDown || _isEngineStarting)
            return;

        _startupWallpaperTimer?.Stop();
        _isEngineStarting = true;
        try
        {
            AppLog.Write("Ensuring wallpaper engine.");
            var workerW = DesktopWorkerW.TryGetExisting();
            if (workerW == IntPtr.Zero)
                workerW = DesktopWorkerW.GetOrCreate();

            _host ??= new WallpaperHostWindow(workerW);
            _host.Show();

            if (_pendingWallpaper is not null)
                _host.Play(_pendingWallpaper);
            StartActivityWatcher();
            StartSurfaceWatchdog();
            StartMemoryWatchdog();
            AppLog.Write(
                $"Wallpaper engine ready at {_startupWatch.ElapsedMilliseconds} ms. {AppLog.GetProcessSnapshot()}"
            );
        }
        catch (Exception ex)
        {
            AppLog.Write("Wallpaper engine failed.", ex);
            _host?.Dispose();
            _host = null;
        }
        finally
        {
            _isEngineStarting = false;
        }
    }

    private void StartActivityWatcher()
    {
        _activity?.Dispose();

        var activityStartupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        EventHandler? startupTick = null;
        startupTick = (_, _) =>
        {
            activityStartupTimer.Stop();
            activityStartupTimer.Tick -= startupTick;
            if (_isShuttingDown)
                return;

            _activity = new DesktopActivityWatcher(
                onDesktopActive: () => _host?.Resume(),
                onDesktopInactive: () => _host?.Pause()
            );
            _activity.Start();
        };
        activityStartupTimer.Tick += startupTick;
        activityStartupTimer.Start();
    }

    private void RefreshWallpaperLibrary()
    {
        _builtInWallpapers = WallpaperLibrary.GetBuiltInWallpapers();
        _customWallpapers = WallpaperLibrary.GetCustomWallpapers();
    }

    private void RecoverWallpaperSurface()
    {
        if (_isShuttingDown)
            return;

        // Debounce rapid recovery requests (e.g. multi-monitor display change
        // fires several events). 1.2 s lets the shell settle before we rebuild.
        _recoveryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _recoveryTimer.Stop();
        _recoveryTimer.Tick -= OnRecoveryTimerTick;
        _recoveryTimer.Tick += OnRecoveryTimerTick;
        _recoveryTimer.Start();
    }

    private void OnRecoveryTimerTick(object? sender, EventArgs e)
    {
        _recoveryTimer?.Stop();
        RecoverWallpaperSurfaceNow();
    }

    private void RecoverWallpaperSurfaceNow()
    {
        if (_isShuttingDown || _isEngineStarting)
            return;

        Dispatcher.BeginInvoke(
            () =>
            {
                try
                {
                    if (_isShuttingDown)
                        return;

                    RefreshWallpaperLibrary();
                    _tray?.RefreshLibrary(_builtInWallpapers, _customWallpapers);

                    var workerW = DesktopWorkerW.TryGetExisting();
                    if (workerW == IntPtr.Zero)
                        workerW = DesktopWorkerW.GetOrCreate();

                    if (_host is null)
                    {
                        EnsureWallpaperEngine();
                        return;
                    }

                    var wallpaperToRestore = _host.CurrentVideo ?? _pendingWallpaper;
                    var oldHost = _host;
                    _host = null;
                    _activity?.Dispose();
                    oldHost?.Dispose();
                    _host = new WallpaperHostWindow(workerW);
                    _host.Show();
                    if (wallpaperToRestore is not null)
                        _host.Play(wallpaperToRestore);

                    StartActivityWatcher();
                    StartSurfaceWatchdog();
                    AppLog.Write("Wallpaper surface recovered.");
                }
                catch (Exception ex)
                {
                    AppLog.Write("Wallpaper recovery failed.", ex);
                    _host?.Dispose();
                    _host = null;
                    EnsureWallpaperEngine();
                }
            },
            DispatcherPriority.Background
        );
    }

    private void StartSurfaceWatchdog()
    {
        // 15-second interval keeps the wallpaper anchored without hammering the
        // shell with SetParent / SetWindowPos calls that can cause brief flicker.
        _surfaceWatchdogTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _surfaceWatchdogTimer.Stop();
        _surfaceWatchdogTimer.Tick -= OnSurfaceWatchdogTick;
        _surfaceWatchdogTimer.Tick += OnSurfaceWatchdogTick;
        _surfaceWatchdogTimer.Start();
    }

    private void OnSurfaceWatchdogTick(object? sender, EventArgs e)
    {
        if (_isShuttingDown || _isEngineStarting || _host is null)
            return;

        try
        {
            var workerW = DesktopWorkerW.TryGetExisting();
            if (workerW != IntPtr.Zero && workerW != _host.DesktopParent)
            {
                AppLog.Write("Desktop WorkerW changed; rebuilding wallpaper host.");
                RecoverWallpaperSurface();
                return;
            }

            _host.RefreshDesktopPlacement();
        }
        catch (Exception ex)
        {
            AppLog.Write("Wallpaper watchdog failed.", ex);
        }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            RecoverWallpaperSurface();
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (
            e.Reason
            is Microsoft.Win32.SessionSwitchReason.SessionUnlock
                or Microsoft.Win32.SessionSwitchReason.RemoteConnect
                or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
        )
        {
            RecoverWallpaperSurface();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_isShuttingDown)
            return;

        _placementTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _placementTimer.Stop();
        _placementTimer.Tick -= OnPlacementTimerTick;
        _placementTimer.Tick += OnPlacementTimerTick;
        _placementTimer.Start();
    }

    private void OnUserPreferenceChanged(
        object sender,
        Microsoft.Win32.UserPreferenceChangedEventArgs e
    )
    {
        if (_isShuttingDown)
            return;

        // Only recover when the desktop icon layout or DWM composition changes.
        // Firing RecoverWallpaperSurface on every "General" preference change
        // (font smoothing, cursor blink, etc.) is too aggressive and causes the
        // wallpaper to visibly flicker on innocuous settings changes.
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.Desktop)
        {
            AppLog.Write($"Desktop preference changed: {e.Category}.");
            RecoverWallpaperSurface();
        }
    }

    private void OnPlacementTimerTick(object? sender, EventArgs e)
    {
        _placementTimer?.Stop();
        try
        {
            _host?.RefreshDesktopPlacement();
            AppLog.Write("Wallpaper placement refreshed.");
        }
        catch (Exception ex)
        {
            AppLog.Write("Wallpaper placement refresh failed.", ex);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("Dispatcher unhandled exception.", e.Exception);
        DesktopIcons.Show();
        e.Handled = true;
    }

    private static void OnWinFormsThreadException(object sender, ThreadExceptionEventArgs e)
    {
        AppLog.Write("WinForms thread exception.", e.Exception);
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            AppLog.Write("AppDomain unhandled exception.", ex);
        else
            AppLog.Write($"AppDomain unhandled exception object: {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e
    )
    {
        AppLog.Write("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}

internal static class AppLog
{
    private static readonly object Gate = new();

    // Cache the log path once — avoids GetFolderPath + string alloc on every write.
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WALL Z",
        "WALL Z.log"
    );

    // Create the directory once at class initialisation time.
    static AppLog()
    {
        try
        {
            var folder = Path.GetDirectoryName(LogFile);
            if (folder is not null)
                Directory.CreateDirectory(folder);
        }
        catch
        { /* If we can't create the folder we'll just swallow errors in Write(). */
        }
    }

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            lock (Gate)
                File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        catch
        {
            // Logging must never be able to take down the tray app.
        }
    }

    public static string GetProcessSnapshot()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var workingSetMb = process.WorkingSet64 / 1024d / 1024d;
            var privateMb = process.PrivateMemorySize64 / 1024d / 1024d;
            var managedMb = GC.GetTotalMemory(false) / 1024d / 1024d;
            return $"ws={workingSetMb:F1}MB private={privateMb:F1}MB managed={managedMb:F1}MB";
        }
        catch
        {
            return "resource snapshot unavailable";
        }
    }
}

// WallpaperLibrary now lives in Core/WallpaperLibrary.cs

internal sealed class NotifyIconController : IDisposable
{
    // Single shared renderer — stateless, safe to share across all menus.
    private static readonly LivePaperMenuRenderer SharedRenderer = new();

    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _playPauseItem;
    private readonly WinForms.ToolStripMenuItem _restartItem;
    private readonly Func<bool> _isPlaying;
    private readonly Func<string?> _currentWallpaper;

    public NotifyIconController(
        Action playPause,
        Action restartWallpaper,
        Func<bool> isPlaying,
        Func<string?> currentWallpaper,
        Action openDashboard,
        Action exit
    )
    {
        _isPlaying = isPlaying;
        _currentWallpaper = currentWallpaper;

        _menu = CreateMenuShell();
        _menu.Opening += (_, _) => RefreshMenuState();
        _menu.Opened += (_, _) => ApplyRoundedMenuShape(_menu);

        _playPauseItem = CreateCommand("Pause wallpaper", "Space", (_, _) => playPause());
        _restartItem = CreateCommand("Restart current", "R", (_, _) => restartWallpaper());
        var dashItem = CreateCommand("Open Dashboard", "D", (_, _) => openDashboard());

        _menu.Items.Add(CreateHeaderItem());
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_playPauseItem);
        _menu.Items.Add(_restartItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(dashItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(CreateCommand("Exit", "Esc", (_, _) => exit()));
        RefreshMenuState();

        _icon = new WinForms.NotifyIcon
        {
            Text = "WALL Z",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => openDashboard();
        _icon.ShowBalloonTip(
            2000,
            "WALL Z",
            "Running in the system tray.",
            WinForms.ToolTipIcon.Info
        );
    }

    // No longer needed — library lists are managed by the dashboard, not the tray.
    // Kept as a no-op so call-sites that haven't been removed yet don't break.
    public void RefreshLibrary(
        IReadOnlyList<string> builtInWallpapers,
        IReadOnlyList<string> customWallpapers
    ) { }

    private static WinForms.ContextMenuStrip CreateMenuShell()
    {
        var menu = new WinForms.ContextMenuStrip
        {
            BackColor = Color.FromArgb(20, 24, 34),
            ForeColor = Color.FromArgb(238, 242, 255),
            Font = new System.Drawing.Font(
                "Segoe UI Variable Text",
                9.25f,
                System.Drawing.FontStyle.Regular
            ),
            Padding = new WinForms.Padding(8, 10, 8, 10),
            ShowImageMargin = true,
            ShowCheckMargin = false,
            Renderer = SharedRenderer,
        };
        return menu;
    }

    private static WinForms.ToolStripMenuItem CreateHeaderItem()
    {
        var item = new WinForms.ToolStripMenuItem("WALL Z")
        {
            Tag = "Header",
            Enabled = false,
            Font = new System.Drawing.Font(
                "Segoe UI Variable Display",
                10.5f,
                System.Drawing.FontStyle.Bold
            ),
            ForeColor = Color.FromArgb(142, 233, 255),
            Padding = new WinForms.Padding(8, 7, 8, 8),
        };
        return item;
    }

    private static WinForms.ToolStripMenuItem CreateCommand(
        string text,
        string shortcut,
        EventHandler? onClick = null
    )
    {
        var item = new WinForms.ToolStripMenuItem(text)
        {
            ShortcutKeyDisplayString = shortcut,
            Padding = new WinForms.Padding(8, 6, 8, 6),
            AutoSize = true,
        };

        if (onClick is not null)
            item.Click += onClick;

        return item;
    }

    private void RefreshMenuState()
    {
        var playing = _isPlaying();
        _playPauseItem.Text = playing ? "Pause wallpaper" : "Resume wallpaper";
        _playPauseItem.ShortcutKeyDisplayString = playing ? "Pause" : "Play";
        _restartItem.Enabled = _currentWallpaper() is not null;
    }

    // Delegate to WallpaperLibrary so we have one canonical display-name formatter.
    private static string ToDisplayName(string path) => WallpaperLibrary.ToDisplayName(path);

    private static void ApplyRoundedMenuShape(WinForms.ToolStrip menu)
    {
        if (menu.Width <= 0 || menu.Height <= 0)
            return;

        var bounds = new Rectangle(0, 0, menu.Width, menu.Height);
        using var path = LivePaperMenuRenderer.CreateRoundedRect(bounds, 12);
        menu.Region?.Dispose();
        menu.Region = new Region(path);
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WALL Z.ico");
        if (!File.Exists(iconPath))
            iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WallZ.ico");
        if (!File.Exists(iconPath))
            iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LivePaperLite.ico");

        try
        {
            if (File.Exists(iconPath))
                return new System.Drawing.Icon(iconPath);
        }
        catch
        {
            // Icon file is missing or corrupted — fall back to the system default.
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void ShowBalloonTip(string message) =>
        _icon.ShowBalloonTip(2500, "WALL Z", message, WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}

internal sealed class WallpaperManagerDialog : WinForms.Form
{
    private readonly WinForms.CheckedListBox _wallpaperList;
    private readonly WinForms.Button _removeButton;
    private readonly IReadOnlyList<string> _wallpapers;

    public IReadOnlyList<string> SelectedWallpapers { get; private set; } = Array.Empty<string>();

    public WallpaperManagerDialog(IReadOnlyList<string> wallpapers)
    {
        _wallpapers = wallpapers;

        Text = "Manage wallpapers";
        StartPosition = WinForms.FormStartPosition.CenterScreen;
        FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new System.Drawing.Size(440, 420);
        BackColor = Color.FromArgb(15, 18, 26);
        ForeColor = Color.FromArgb(238, 242, 255);
        Font = new System.Drawing.Font(
            "Segoe UI Variable Text",
            9.25f,
            System.Drawing.FontStyle.Regular
        );

        var title = new WinForms.Label
        {
            Text = "My wallpapers",
            AutoSize = false,
            Location = new System.Drawing.Point(22, 18),
            Size = new System.Drawing.Size(390, 28),
            Font = new System.Drawing.Font(
                "Segoe UI Variable Display",
                14f,
                System.Drawing.FontStyle.Bold
            ),
            ForeColor = Color.FromArgb(238, 242, 255),
        };

        var subtitle = new WinForms.Label
        {
            Text =
                "Remove saved entries from the tray list. Your original video files stay untouched.",
            AutoSize = false,
            Location = new System.Drawing.Point(24, 48),
            Size = new System.Drawing.Size(390, 38),
            ForeColor = Color.FromArgb(154, 164, 184),
        };

        _wallpaperList = new WinForms.CheckedListBox
        {
            Location = new System.Drawing.Point(24, 96),
            Size = new System.Drawing.Size(392, 228),
            BackColor = Color.FromArgb(22, 27, 38),
            ForeColor = Color.FromArgb(238, 242, 255),
            BorderStyle = WinForms.BorderStyle.FixedSingle,
            CheckOnClick = true,
            IntegralHeight = false,
        };
        _wallpaperList.ItemCheck += (_, _) => BeginInvoke(UpdateRemoveButton);

        foreach (var wallpaper in wallpapers)
            _wallpaperList.Items.Add(ToDisplayLabel(wallpaper));

        if (wallpapers.Count == 0)
        {
            _wallpaperList.Items.Add("No custom wallpapers saved yet.");
            _wallpaperList.Enabled = false;
        }

        var selectAllButton = CreateSecondaryButton(
            "Select all",
            new System.Drawing.Point(24, 340)
        );
        selectAllButton.Click += (_, _) =>
        {
            for (var index = 0; index < _wallpaperList.Items.Count; index++)
                _wallpaperList.SetItemChecked(index, _wallpaperList.Enabled);
            UpdateRemoveButton();
        };

        var clearButton = CreateSecondaryButton("Clear", new System.Drawing.Point(122, 340));
        clearButton.Click += (_, _) =>
        {
            for (var index = 0; index < _wallpaperList.Items.Count; index++)
                _wallpaperList.SetItemChecked(index, false);
            UpdateRemoveButton();
        };

        _removeButton = CreatePrimaryButton("Remove selected", new System.Drawing.Point(232, 340));
        _removeButton.Enabled = false;
        _removeButton.Click += (_, _) => ConfirmSelection();

        var closeButton = CreateSecondaryButton("Close", new System.Drawing.Point(310, 378));
        closeButton.Click += (_, _) =>
        {
            DialogResult = WinForms.DialogResult.Cancel;
            Close();
        };

        Controls.AddRange(
            new WinForms.Control[]
            {
                title,
                subtitle,
                _wallpaperList,
                selectAllButton,
                clearButton,
                _removeButton,
                closeButton,
            }
        );
    }

    private void UpdateRemoveButton()
    {
        _removeButton.Enabled = _wallpaperList.CheckedIndices.Count > 0;
    }

    private void ConfirmSelection()
    {
        SelectedWallpapers = _wallpaperList
            .CheckedIndices.Cast<int>()
            .Where(index => index >= 0 && index < _wallpapers.Count)
            .Select(index => _wallpapers[index])
            .ToArray();

        if (SelectedWallpapers.Count == 0)
            return;

        DialogResult = WinForms.DialogResult.OK;
        Close();
    }

    private static WinForms.Button CreatePrimaryButton(string text, System.Drawing.Point location)
    {
        var button = CreateSecondaryButton(text, location);
        button.Size = new System.Drawing.Size(184, 30);
        button.BackColor = Color.FromArgb(43, 149, 220);
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Color.FromArgb(78, 197, 255);
        return button;
    }

    private static WinForms.Button CreateSecondaryButton(string text, System.Drawing.Point location)
    {
        var button = new WinForms.Button
        {
            Text = text,
            Location = location,
            Size = new System.Drawing.Size(88, 30),
            BackColor = Color.FromArgb(27, 33, 46),
            ForeColor = Color.FromArgb(238, 242, 255),
            FlatStyle = WinForms.FlatStyle.Flat,
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(63, 75, 98);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(36, 47, 66);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(43, 149, 220);
        return button;
    }

    private static string ToDisplayLabel(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path).Replace('-', ' ').Replace('_', ' ');
        var title = string.Join(
            ' ',
            fileName
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..])
        );
        return title.Length == 0 ? path : title;
    }
}

internal sealed class LivePaperMenuRenderer : WinForms.ToolStripProfessionalRenderer
{
    private static readonly Color PanelTop = Color.FromArgb(28, 33, 46);
    private static readonly Color PanelBottom = Color.FromArgb(13, 16, 23);
    private static readonly Color Accent = Color.FromArgb(81, 219, 255);
    private static readonly Color AccentSoft = Color.FromArgb(38, 81, 219, 255);
    private static readonly Color TextMuted = Color.FromArgb(154, 164, 184);

    public LivePaperMenuRenderer()
        : base(new LivePaperColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
    {
        using var brush = new LinearGradientBrush(
            e.AffectedBounds,
            PanelTop,
            PanelBottom,
            LinearGradientMode.Vertical
        );
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
    {
        var rect = new Rectangle(
            System.Drawing.Point.Empty,
            e.ToolStrip.Size - new System.Drawing.Size(1, 1)
        );
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRect(rect, 12);
        using var pen = new Pen(Color.FromArgb(78, 96, 123));
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
            return;

        var rect = new Rectangle(4, 2, e.Item.Width - 8, e.Item.Height - 4);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRect(rect, 7);
        using var brush = new LinearGradientBrush(
            rect,
            Color.FromArgb(55, 106, 221),
            Color.FromArgb(29, 188, 216),
            LinearGradientMode.Horizontal
        );
        e.Graphics.FillPath(brush, path);
        using var glow = new Pen(AccentSoft, 1);
        e.Graphics.DrawPath(glow, path);
    }

    protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
    {
        var rect = new Rectangle(10, e.Item.Height / 2 - 4, 8, 8);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Accent);
        e.Graphics.FillEllipse(brush, rect);
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Color.FromArgb(48, 57, 74));
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item.Tag is "Header")
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var rect = e.TextRectangle;
            using var fontWall = new System.Drawing.Font(
                "Segoe UI Variable Display",
                11f,
                System.Drawing.FontStyle.Bold
            );
            using var fontZ = new System.Drawing.Font(
                "Georgia",
                13.5f,
                System.Drawing.FontStyle.Bold | System.Drawing.FontStyle.Italic
            );

            using var brushWall = new SolidBrush(Color.FromArgb(242, 246, 255));
            using var brushZ = new LinearGradientBrush(
                new Rectangle(rect.X, rect.Y, Math.Max(rect.Width, 100), Math.Max(rect.Height, 20)),
                Color.FromArgb(142, 233, 255),
                Color.FromArgb(255, 215, 115),
                LinearGradientMode.Horizontal
            );

            var wallSize = g.MeasureString(
                "WALL",
                fontWall,
                PointF.Empty,
                StringFormat.GenericTypographic
            );
            float x = rect.Left + 2;
            float y = rect.Top + (rect.Height - wallSize.Height) / 2f;

            g.DrawString("WALL", fontWall, brushWall, x, y, StringFormat.GenericTypographic);
            g.DrawString(
                " Z",
                fontZ,
                brushZ,
                x + wallSize.Width,
                y - 1.5f,
                StringFormat.GenericTypographic
            );
            return;
        }

        e.TextColor = e.Item.Enabled ? e.TextColor : TextMuted;
        base.OnRenderItemText(e);
    }

    public static GraphicsPath CreateRoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class LivePaperColorTable : WinForms.ProfessionalColorTable
{
    public override Color ImageMarginGradientBegin => Color.FromArgb(20, 24, 34);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(20, 24, 34);
    public override Color ImageMarginGradientEnd => Color.FromArgb(20, 24, 34);
    public override Color MenuBorder => Color.FromArgb(78, 96, 123);
    public override Color ToolStripDropDownBackground => Color.FromArgb(20, 24, 34);
    public override Color SeparatorDark => Color.FromArgb(48, 57, 74);
    public override Color SeparatorLight => Color.FromArgb(48, 57, 74);
}

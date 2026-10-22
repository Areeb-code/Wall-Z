using System.Windows.Threading;
using WallZ.Interop;

namespace WallZ.Core;

internal sealed class DesktopActivityWatcher : IDisposable
{
    private readonly Action _onDesktopActive;
    private readonly Action _onDesktopInactive;
    private readonly DispatcherTimer _timer;
    private readonly EventHandler _tickHandler;
    private bool? _lastDesktopVisible;
    private int _coveredTicks;
    private const int CoveredTicksBeforePause = 6;

    // Window class names that belong to the shell / desktop itself — not real apps.
    private static readonly HashSet<string> _shellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "DV2ControlHost", "MsgrIMEWindowClass", "SysShadow", "Button",
        "WALL Z Wallpaper Form", "WallZWallpaperForm", "LivePaperLiteWallpaperForm" // our own window — never counts as "covering"
    };

    public DesktopActivityWatcher(Action onDesktopActive, Action onDesktopInactive)
    {
        _onDesktopActive = onDesktopActive;
        _onDesktopInactive = onDesktopInactive;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tickHandler = (_, _) => CheckDesktopVisibility();
        _timer.Tick += _tickHandler;
    }

    public void Start()
    {
        CheckDesktopVisibility();
        _timer.Start();
    }

    /// <summary>
    /// Returns true when the desktop wallpaper is visible — i.e. no regular
    /// maximised / fullscreen window is fully covering all monitors.
    /// Respects Engine Shield settings (Battery Saver and Auto-Pause on Fullscreen).
    /// </summary>
    private void CheckDesktopVisibility()
    {
        var settings = EngineShieldSettings.Instance;

        // Smart Battery Saver: If enabled and running on laptop battery, pause playback to save power
        if (settings.BatterySaverMode)
        {
            try
            {
                var power = System.Windows.Forms.SystemInformation.PowerStatus;
                if (power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline)
                {
                    if (_lastDesktopVisible != false)
                    {
                        _lastDesktopVisible = false;
                        _onDesktopInactive();
                    }
                    return;
                }
            }
            catch { }
        }

        // Auto-pause during fullscreen games or apps
        bool covered = false;
        if (settings.AutoPauseOnFullscreen)
        {
            var screenBounds = NativeMethods.GetVirtualScreenBounds();
            covered = IsDesktopCovered(screenBounds);
            _coveredTicks = covered ? _coveredTicks + 1 : 0;
        }
        else
        {
            _coveredTicks = 0;
        }

        // Pause only after a short covered period, so normal window transitions
        // and task switching do not cause visible stutters.
        var desktopVisible = !covered || _coveredTicks < CoveredTicksBeforePause;

        if (_lastDesktopVisible == desktopVisible)
            return;

        _lastDesktopVisible = desktopVisible;
        if (desktopVisible)
            _onDesktopActive();
        else
            _onDesktopInactive();
    }

    private static bool IsDesktopCovered(System.Drawing.Rectangle screenBounds)
    {
        // Walk all top-level visible windows. If any non-shell window covers
        // the entire virtual screen, the wallpaper is hidden.
        bool covered = false;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
                return true; // skip invisible windows

            // Skip shell / desktop / taskbar windows.
            var cls = NativeMethods.GetClassName(hwnd);
            if (_shellClasses.Contains(cls))
                return true;

            // Skip minimised windows — they don't cover the desktop.
            if (NativeMethods.IsIconic(hwnd))
                return true;

            // Check if this window's rect covers the full virtual screen.
            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
                return true;

            int winW = rect.Right - rect.Left;
            int winH = rect.Bottom - rect.Top;

            // A window covers the screen when it occupies at least the full
            // width AND height of the primary (or virtual) screen.
            if (winW >= screenBounds.Width && winH >= screenBounds.Height)
            {
                covered = true;
                return false; // stop enumeration early
            }

            return true;
        }, IntPtr.Zero);

        return covered;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= _tickHandler;
    }
}

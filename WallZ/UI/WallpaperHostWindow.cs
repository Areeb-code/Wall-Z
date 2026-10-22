using System.IO;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using WallZ.Core;
using WallZ.Interop;

namespace WallZ.UI;

public sealed class WallpaperHostWindow : Form
{
    // Core.Initialize() is process-wide and must only be called once ever.
    private static int _libVlcCoreInitialized;
    private IntPtr _desktopParent;
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private readonly VideoView _videoView;
    private string? _currentVideo;
    private bool _isDisposed;
    private bool _isRestarting;

    public bool IsPlaying => _player?.IsPlaying == true;
    public string? CurrentVideo => _currentVideo;
    public IntPtr DesktopParent => _desktopParent;

    public WallpaperHostWindow(IntPtr desktopParent)
    {
        _desktopParent = desktopParent;

        _videoView = new VideoView
        {
            Dock = DockStyle.Fill,
        };

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = System.Drawing.Color.Black;
        Text = "WALL Z Wallpaper Form";
        Controls.Add(_videoView);
        ApplyDesktopBounds();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AttachToDesktop();
    }

    public new void Show()
    {
        if (!IsHandleCreated)
            CreateHandle();

        // AttachToDesktop is also called in OnHandleCreated, so one call
        // after base.Show() is enough. Avoid redundant calls that cause flicker.
        base.Show();
        AttachToDesktop();
    }

    private void AttachToDesktop()
    {
        if (!IsHandleCreated)
            return;

        NativeMethods.SetParent(Handle, _desktopParent);

        var style = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_STYLE);
        style &= ~NativeMethods.WS_POPUP;
        style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_STYLE, style);

        ApplyDesktopBounds();
    }

    public void Play(string path)
    {
        if (!File.Exists(path))
            return;

        EnsurePlaybackInfrastructure();

        if (_player is null)
            return;

        // Don't restart an already-playing video unless it's a different file.
        if (string.Equals(_currentVideo, path, StringComparison.OrdinalIgnoreCase) && _player.IsPlaying)
            return;

        // LibVLC 3.x does not reliably replace mid-playing media without an
        // explicit Stop() first. Always stop the current media before starting a
        // new one so the switch is clean and immediate.
        if (_player.IsPlaying || _player.State == VLCState.Paused)
        {
            try { _player.Stop(); } catch { /* safe to ignore */ }
        }

        _player.Scale = 0;
        _player.AspectRatio = null;
        using var media = new Media(_libVlc!, path, FromType.FromPath);
        // input-repeat loops the file natively inside VLC — no gap or black frame
        // on loop boundary. Never use EndReached for looping.
        // NOTE: --no-audio, --avcodec-hw, --avcodec-fast, --file-caching are already
        // set at the LibVLC instance level; do not duplicate them here to avoid
        // redundant per-media string allocations on every Play() / Restart().
        media.AddOption(":input-repeat=65535");
        if (_player.Play(media))
        {
            _currentVideo = path;
            return;
        }

        _currentVideo = null;
        WallZ.AppLog.Write($"Failed to start wallpaper playback: {path}");
    }

    public void Pause()
    {
        if (_player?.IsPlaying == true)
            _player.SetPause(true);
    }

    public void Resume()
    {
        if (_currentVideo is null)
            return;

        if (_player?.IsPlaying == true)
            return;

        if (_player is null)
        {
            Play(_currentVideo);
            return;
        }

        _player.SetPause(false);
        if (!_player.IsPlaying)
        {
            var restartPath = _currentVideo;
            _currentVideo = null;
            Play(restartPath);
        }
    }

    public void Restart()
    {
        if (_currentVideo is null)
            return;

        // Do NOT clear _currentVideo before calling Play — that causes a brief
        // black flash because the guard "already playing same file" is bypassed
        // but the old media is stopped first. Instead, stop the player explicitly
        // and replay; VLC will seamlessly re-open the same file.
        var restartPath = _currentVideo;
        _currentVideo = null; // force Play to reload the media
        if (_player?.IsPlaying == true)
        {
            try { _player.Stop(); } catch { /* safe to ignore */ }
        }
        Play(restartPath);
    }

    public void RecoverAfterPowerResume(IntPtr desktopParent)
    {
        _desktopParent = desktopParent;
        AttachToDesktop();

        if (_currentVideo is null)
            return;

        var restartPath = _currentVideo;
        _currentVideo = null;
        BeginInvoke(() => Play(restartPath));
    }

    public void RefreshDesktopPlacement()
    {
        AttachToDesktop();
    }

    private void ApplyDesktopBounds()
    {
        var bounds = NativeMethods.GetVirtualScreenBounds();
        Bounds = new System.Drawing.Rectangle(0, 0, bounds.Width, bounds.Height);

        if (!IsHandleCreated)
            return;

        NativeMethods.SetWindowPos(
            Handle,
            GetWallpaperZOrderTarget(),
            0,
            0,
            bounds.Width,
            bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOW);
    }

    private IntPtr GetWallpaperZOrderTarget()
    {
        var shellView = DesktopWorkerW.GetShellView();
        if (shellView != IntPtr.Zero && NativeMethods.GetParent(shellView) == _desktopParent)
            return shellView;

        return NativeMethods.HWND_TOP;
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        // EndReached fires from a VLC background thread. With input-repeat=65535
        // this should never fire during normal operation, but guard against it.
        if (_isDisposed || !IsHandleCreated || _isRestarting)
            return;

        try
        {
            BeginInvoke(() =>
            {
                if (_isDisposed || _currentVideo is null || _isRestarting)
                    return;

                _isRestarting = true;
                try
                {
                    Restart();
                }
                finally
                {
                    _isRestarting = false;
                }
            });
        }
        catch (InvalidOperationException)
        {
            // The host can be closing while VLC is raising playback callbacks.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isDisposed = true;
            if (_player is not null)
            {
                _player.EndReached -= OnPlaybackEnded;
                try
                {
                    if (_player.IsPlaying)
                        _player.Stop();
                }
                catch
                {
                }
            }

            _videoView.MediaPlayer = null;
            _videoView.Dispose();
            _player?.Dispose();
            _libVlc?.Dispose();
            _player = null;
            _libVlc = null;
        }

        base.Dispose(disposing);
    }

    private void EnsurePlaybackInfrastructure()
    {
        // Already initialized — nothing to do. This check prevents re-creating
        // a new LibVLC/MediaPlayer pair on every Play() call after recover.
        if (_player is not null && _libVlc is not null)
            return;

        // Core.Initialize() is process-global; only call it once across all
        // WallpaperHostWindow instances (even after dispose/recreate).
        if (Interlocked.CompareExchange(ref _libVlcCoreInitialized, 1, 0) == 0)
            LibVLCSharp.Shared.Core.Initialize();

        _libVlc = new LibVLC(
            "--no-audio",
            "--no-video-title-show",
            "--quiet",
            "--avcodec-hw=any",
            "--avcodec-fast",
            // Higher default caching prevents decode stutter on loop boundary.
            "--file-caching=500",
            // Do NOT add --drop-late-frames or --skip-frames here — those flags
            // cause visible green/black pixel glitches and frame tears. VLC's
            // hardware decoder (dxva2/d3d11va) already handles late frames gracefully.
            "--network-caching=500");
        _player = new MediaPlayer(_libVlc);
        _player.Scale = 0;
        _player.AspectRatio = null;
        _player.EndReached += OnPlaybackEnded;
        _videoView.MediaPlayer = _player;
    }
}

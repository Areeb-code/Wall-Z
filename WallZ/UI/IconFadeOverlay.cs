using System.Drawing;
using WallZ.Core;
using WallZ.Interop;
using WinForms = System.Windows.Forms;

namespace WallZ.UI;

internal sealed class IconFadeOverlay : WinForms.Form
{
    private readonly IconSnapshot _snapshot;
    private readonly WinForms.Timer _timer;
    private readonly DateTime _startedAt;
    private readonly double _durationMs;

    private IconFadeOverlay(IntPtr workerW, IconSnapshot snapshot, TimeSpan duration)
    {
        _snapshot = snapshot;
        _durationMs = Math.Clamp(duration.TotalMilliseconds, 300, 800);
        _startedAt = DateTime.UtcNow;

        FormBorderStyle = WinForms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WinForms.FormStartPosition.Manual;
        Bounds = NativeMethods.GetVirtualScreenBounds();
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Opacity = 0;

        _timer = new WinForms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => Tick();

        Shown += (_, _) =>
        {
            NativeMethods.SetParent(Handle, workerW);
            _timer.Start();
        };
    }

    public static void RunStartupFade(IntPtr workerW, TimeSpan duration)
    {
        var snapshot = DesktopIcons.CaptureIconLayer();
        if (snapshot is null)
            return;

        DesktopIcons.Hide();
        var overlay = new IconFadeOverlay(workerW, snapshot, duration);
        overlay.Show();
    }

    protected override void OnPaint(WinForms.PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Magenta);
        e.Graphics.DrawImageUnscaled(
            _snapshot.Bitmap,
            _snapshot.Bounds.Left - Bounds.Left,
            _snapshot.Bounds.Top - Bounds.Top);
    }

    private void Tick()
    {
        var elapsed = (DateTime.UtcNow - _startedAt).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / _durationMs, 0, 1);
        Opacity = EaseOutCubic(progress);

        if (progress < 1)
            return;

        _timer.Stop();
        DesktopIcons.Show();
        Close();
        Dispose();
    }

    private static double EaseOutCubic(double x) => 1 - Math.Pow(1 - x, 3);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _snapshot.Dispose();
        }

        base.Dispose(disposing);
    }
}

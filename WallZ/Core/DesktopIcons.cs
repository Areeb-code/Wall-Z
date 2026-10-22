using System.Drawing;
using WallZ.Interop;

namespace WallZ.Core;

internal static class DesktopIcons
{
    public static IntPtr GetIconListView()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        var shellView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

        if (shellView == IntPtr.Zero)
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                var view = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (view == IntPtr.Zero)
                    return true;

                shellView = view;
                return false;
            }, IntPtr.Zero);
        }

        return shellView == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.FindWindowEx(shellView, IntPtr.Zero, "SysListView32", "FolderView");
    }

    public static void Hide()
    {
        var icons = GetIconListView();
        if (icons != IntPtr.Zero)
            NativeMethods.ShowWindow(icons, NativeMethods.SW_HIDE);
    }

    public static void Show()
    {
        var icons = GetIconListView();
        if (icons != IntPtr.Zero)
            NativeMethods.ShowWindow(icons, NativeMethods.SW_SHOW);
    }

    public static IconSnapshot? CaptureIconLayer()
    {
        var icons = GetIconListView();
        if (icons == IntPtr.Zero || !NativeMethods.GetWindowRect(icons, out var rect))
            return null;

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            if (!NativeMethods.PrintWindow(icons, hdc, 0))
            {
                bitmap.Dispose();
                return null;
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        return new IconSnapshot(bitmap, new Rectangle(rect.Left, rect.Top, width, height));
    }
}

internal sealed record IconSnapshot(Bitmap Bitmap, Rectangle Bounds) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

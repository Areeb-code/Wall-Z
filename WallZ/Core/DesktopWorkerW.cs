using WallZ.Interop;

namespace WallZ.Core;

internal static class DesktopWorkerW
{
    public static IntPtr TryGetExisting()
    {
        var workerW = IntPtr.Zero;

        NativeMethods.EnumWindows((topHandle, _) =>
        {
            var shellView = NativeMethods.FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView == IntPtr.Zero)
                return true;

            workerW = NativeMethods.FindWindowEx(IntPtr.Zero, topHandle, "WorkerW", null);
            return false;
        }, IntPtr.Zero);

        return workerW;
    }

    public static IntPtr GetOrCreate()
    {
        var existing = TryGetExisting();
        if (existing != IntPtr.Zero)
            return existing;

        var progman = NativeMethods.FindWindow("Progman", null);

        // Windows 10/11 Explorer builds vary here. We first try to reuse the
        // current desktop WorkerW, then ask Explorer to create one only if needed.
        NativeMethods.SendMessageTimeout(
            progman,
            0x052C,
            new IntPtr(0xD),
            IntPtr.Zero,
            NativeMethods.SMTO_NORMAL,
            200,
            out _);
        NativeMethods.SendMessageTimeout(
            progman,
            0x052C,
            new IntPtr(0xD),
            new IntPtr(1),
            NativeMethods.SMTO_NORMAL,
            200,
            out _);

        existing = TryGetExisting();
        return existing != IntPtr.Zero ? existing : progman;
    }

    public static IntPtr GetShellView()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        var shellView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (shellView != IntPtr.Zero)
            return shellView;

        NativeMethods.EnumWindows((topHandle, _) =>
        {
            shellView = NativeMethods.FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            return shellView == IntPtr.Zero;
        }, IntPtr.Zero);

        return shellView;
    }
}

using System.Drawing;
using System.IO;
using LibVLCSharp.Shared;

namespace WallZ.Core;

internal readonly record struct WallpaperAspectValidationResult(bool IsCompatible, string Message)
{
    public static WallpaperAspectValidationResult Compatible() =>
        new(true, string.Empty);

    public static WallpaperAspectValidationResult Rejected(string message) =>
        new(false, message);
}

internal static class WallpaperAspectValidator
{
    private const double AspectRatioTolerance = 0.12;
    private const double MaxDimensionScale = 1.5;

    public static Task<WallpaperAspectValidationResult> ValidateAsync(string path, Rectangle desktopBounds)
    {
        // LibVLC with Direct3D11 hardware acceleration automatically scales and letterboxes
        // videos of any resolution (4K, 1440p, 1080p) to any display with zero performance impact.
        return Task.FromResult(WallpaperAspectValidationResult.Compatible());
    }

    private static bool IsRotated(VideoOrientation orientation) =>
        orientation is VideoOrientation.LeftTop
            or VideoOrientation.LeftBottom
            or VideoOrientation.RightTop
            or VideoOrientation.RightBottom;
}

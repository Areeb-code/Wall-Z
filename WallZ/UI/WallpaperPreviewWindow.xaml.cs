using System.IO;
using System.Windows;
using System.Windows.Input;

namespace WallZ.UI;

/// <summary>
/// Full-screen live preview overlay. Uses WPF MediaElement (WMF) which is
/// entirely separate from the LibVLC engine in WallpaperHostWindow.
/// </summary>
public partial class WallpaperPreviewWindow : Window
{
    private readonly string _videoPath;
    private readonly Action<string>? _applyCallback;

    /// <param name="videoPath">Absolute path to the .mp4 wallpaper.</param>
    /// <param name="applyCallback">Called with the path when user clicks Apply.</param>
    public WallpaperPreviewWindow(string videoPath, Action<string>? applyCallback = null)
    {
        InitializeComponent();

        _videoPath     = videoPath;
        _applyCallback = applyCallback;

        WallpaperTitleLabel.Text = Path.GetFileNameWithoutExtension(videoPath)
            .Replace('-', ' ')
            .Replace('_', ' ')
            .ToTitleCase();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PreviewPlayer.Source = new Uri(_videoPath);
        PreviewPlayer.Play();
    }

    // Loop the video
    private void PreviewPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        PreviewPlayer.Position = TimeSpan.Zero;
        PreviewPlayer.Play();
    }

    // ESC closes without applying
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
            FadeAndClose(applyWallpaper: false);
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        FadeAndClose(applyWallpaper: true);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        FadeAndClose(applyWallpaper: false);
    }

    private void FadeAndClose(bool applyWallpaper)
    {
        PreviewPlayer.Stop();

        if (applyWallpaper)
            _applyCallback?.Invoke(_videoPath);

        DialogResult = applyWallpaper;
        Close();
    }
}

/// <summary>Extension for simple title-casing without a culture dependency.</summary>
internal static class StringPreviewExtensions
{
    public static string ToTitleCase(this string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        return string.Join(' ', input.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }
}

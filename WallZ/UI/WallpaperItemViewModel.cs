using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WallZ.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace WallZ.UI;

public class WallpaperItemViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush FavoriteActiveBrush = new(Color.FromRgb(0xFF, 0x4D, 0x6D)); // Radiant Crimson
    private static readonly SolidColorBrush FavoriteInactiveBrush = new(Color.FromRgb(0x8C, 0x93, 0xA4)); // Subtle slate

    static WallpaperItemViewModel()
    {
        FavoriteActiveBrush.Freeze();
        FavoriteInactiveBrush.Freeze();
    }

    private bool _isFavorite;
    private bool _isActive;
    private ImageSource? _thumbnail;
    private string _category;

    public string FilePath { get; }
    public string Title { get; }
    public string Resolution { get; set; } = "4K";
    public string FrameRate { get; set; } = "60 FPS";
    public string AspectRatio { get; set; } = "16:9";
    public string FileSizeFormatted { get; }

    public string Category
    {
        get => _category;
        set
        {
            if (_category != value)
            {
                _category = value;
                WallpaperLibrary.SaveCategory(FilePath, value);
                OnPropertyChanged();
            }
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite != value)
            {
                _isFavorite = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FavoriteIcon));
                OnPropertyChanged(nameof(FavoriteBrush));
                OnPropertyChanged(nameof(FavoriteToolTip));
                OnPropertyChanged(nameof(FavoriteOpacity));
            }
        }
    }

    public string FavoriteIcon => IsFavorite ? "♥" : "♡";
    public Brush FavoriteBrush => IsFavorite ? FavoriteActiveBrush : FavoriteInactiveBrush;
    public double FavoriteOpacity => IsFavorite ? 1.0 : 0.65;
    public string FavoriteToolTip => IsFavorite ? "Remove from Favorites" : "Add to Favorites";

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
            }
        }
    }

    public ImageSource Thumbnail
    {
        get
        {
            if (_thumbnail == null)
            {
                _thumbnail = ThumbnailProvider.GetThumbnail(FilePath);
            }
            return _thumbnail;
        }
    }

    public WallpaperItemViewModel(string filePath, bool isActive = false)
    {
        FilePath = filePath;
        _isActive = isActive;
        _isFavorite = WallpaperLibrary.IsFavorite(filePath);
        Title = WallpaperLibrary.ToDisplayName(filePath);

        // Dynamic category detection with custom override support
        _category = WallpaperLibrary.DetectCategory(filePath);

        // Derive resolution tag from filename heuristics
        var lower = Path.GetFileName(filePath).ToLowerInvariant();
        if (lower.Contains("3840x2160") || lower.Contains("4k"))
            Resolution = "4K";
        else if (lower.Contains("2560x1440") || lower.Contains("1440p"))
            Resolution = "1440p";
        else
            Resolution = "1080p";

        try
        {
            if (File.Exists(filePath))
            {
                var bytes = new FileInfo(filePath).Length;
                FileSizeFormatted = bytes > 1024 * 1024
                    ? $"{bytes / (1024.0 * 1024.0):F1} MB"
                    : $"{bytes / 1024.0:F0} KB";
            }
            else
            {
                FileSizeFormatted = "Live Video";
            }
        }
        catch
        {
            FileSizeFormatted = "Live Video";
        }

        // Listen for async thumbnail extraction completion
        ThumbnailProvider.ThumbnailUpdated += OnThumbnailUpdated;
    }

    private void OnThumbnailUpdated(string path, ImageSource source)
    {
        if (string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _thumbnail = source;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                OnPropertyChanged(nameof(Thumbnail));
            });
        }
    }

    public void ToggleFavorite()
    {
        WallpaperLibrary.ToggleFavorite(FilePath);
        IsFavorite = WallpaperLibrary.IsFavorite(FilePath);
    }

    public void SetCategory(string newCategory)
    {
        Category = newCategory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

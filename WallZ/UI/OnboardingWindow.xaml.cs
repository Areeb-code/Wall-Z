using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WallZ.UI;

public partial class OnboardingWindow : Window
{
    private static readonly string FlagFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WALL Z", "onboarding-complete.flag");

    private readonly HashSet<string> _selectedStyles = new();

    // Style cards indexed by Tag name for selection toggle
    private readonly List<System.Windows.Controls.Border> _styleCards = new();

    public OnboardingWindow()
    {
        InitializeComponent();

        // Populate detected resolution
        var w = (int)SystemParameters.PrimaryScreenWidth;
        var h = (int)SystemParameters.PrimaryScreenHeight;
        ResolutionLabel.Text = $"{w} × {h}";
        ResolutionTier.Text = GetResolutionTier(w, h);
        DisplaySubtitle.Text = $"Primary · {GetResolutionTier(w, h)} Display";

        // Register all style cards so we can toggle them
        _styleCards.AddRange(new[]
        {
            CinematicCard, MinimalCard, NatureCard,
            AnimeCard, AbstractCard, EverythingCard
        });
    }

    // ── Step navigation ──────────────────────────────────────────────────

    private void Step1Continue_Click(object sender, RoutedEventArgs e)
    {
        TransitionTo(Step1, Step2);
    }

    private void Step2Continue_Click(object sender, RoutedEventArgs e)
    {
        // If user selected nothing, default to Everything
        if (_selectedStyles.Count == 0)
            _selectedStyles.Add("All");

        TransitionTo(Step2, Step3);
    }

    private void Step3Finish_Click(object sender, RoutedEventArgs e)
    {
        WriteFlag();
        DialogResult = true;
        Close();
    }

    // ── Style card selection ──────────────────────────────────────────────

    private void StyleCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border card) return;
        var tag = card.Tag as string ?? string.Empty;

        if (_selectedStyles.Contains(tag))
        {
            // Deselect
            _selectedStyles.Remove(tag);
            card.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            card.BorderThickness = new Thickness(1.5);
        }
        else
        {
            // Select
            _selectedStyles.Add(tag);
            card.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0x99, 0x3A));
            card.BorderThickness = new Thickness(2);

            // Subtle scale-up via RenderTransform
            card.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            card.RenderTransform = new ScaleTransform(1.03, 1.03);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void TransitionTo(UIElement from, UIElement to)
    {
        from.Visibility = Visibility.Collapsed;
        to.Visibility   = Visibility.Visible;
    }

    private static string GetResolutionTier(int w, int h)
    {
        return (w, h) switch
        {
            ( >= 3840, _) => "4K Ultra HD",
            ( >= 2560, _) => "Quad HD (1440p)",
            ( >= 1920, _) => "Full HD (1080p)",
            ( >= 1280, _) => "HD (720p)",
            _             => "Standard"
        };
    }

    private static void WriteFlag()
    {
        try
        {
            var dir = Path.GetDirectoryName(FlagFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FlagFile, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Non-critical — if writing fails, onboarding shows again next launch
        }
    }

    /// <summary>Checks if onboarding has already been completed.</summary>
    public static bool IsCompleted => File.Exists(FlagFile);
}

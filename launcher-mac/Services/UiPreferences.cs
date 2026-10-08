using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace DustoreLauncherV.Mac.Services;

public sealed record ThemeChoice(string Key, string Label, bool Light, IReadOnlyDictionary<string, string> Colors);
public sealed record AccentChoice(string Key, string Label, string Color, string Subtle);

/// <summary>Prime appearance: theme, accent and which launcher sections are shown. Saved per profile.</summary>
public sealed class UiPreferences
{
    public string Theme { get; set; } = "graphite";
    public string Accent { get; set; } = "lavender";
    public bool ShowEx { get; set; } = true;
    public bool ShowStore { get; set; } = true;
    public bool ShowHome { get; set; } = true;
    public bool ShowJams { get; set; } = true;
    public bool ShowAssets { get; set; } = true;
    public bool ShowHero { get; set; } = true;
    public bool CompactShelf { get; set; }
    public bool IntroAnimation { get; set; } = true;
    public bool ReduceMotion { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static UiPreferences Load(string dataDirectory)
    {
        try
        {
            string path = Path.Combine(dataDirectory, "appearance.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    public void Save(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "appearance.json"), JsonSerializer.Serialize(this, Json));
        }
        catch (Exception) { /* Appearance is a convenience; never block the launcher. */ }
    }

    // Graphite is the shipped look; the rest only differ in surfaces and text.
    private static readonly Dictionary<string, string> Graphite = new()
    {
        ["Background"] = "#0B0A10", ["BackgroundSoft"] = "#100F18", ["NavBackground"] = "#100F17", ["NavTranslucent"] = "#E3100F17",
        ["Surface"] = "#17151F", ["SurfaceRaised"] = "#201D2C", ["SurfaceHover"] = "#2A2638", ["SurfacePressed"] = "#13111B",
        ["Fill"] = "#14FFFFFF", ["FillHover"] = "#21FFFFFF", ["Hairline"] = "#13FFFFFF", ["BorderSoft"] = "#1FFFFFFF", ["BorderMid"] = "#33C9BAFF",
        ["BorderStrong"] = "#71677F", ["TextPrimary"] = "#F6F3FF", ["TextSecondary"] = "#CAC4D9", ["TextDim"] = "#9A91AB"
    };

    public static IReadOnlyList<ThemeChoice> Themes { get; } = new[]
    {
        new ThemeChoice("graphite", "Obsidian", false, Graphite),
        new ThemeChoice("midnight", "Полночь", false, With(Graphite, new()
        {
            ["Background"] = "#0A0E17", ["BackgroundSoft"] = "#0F1522", ["NavBackground"] = "#0C111C", ["NavTranslucent"] = "#B80C111C",
            ["Surface"] = "#141B2B", ["SurfaceRaised"] = "#1A2236", ["SurfaceHover"] = "#212B42", ["SurfacePressed"] = "#111827",
            ["BorderStrong"] = "#4A5670", ["TextSecondary"] = "#C2CADB", ["TextDim"] = "#8691A8"
        })),
        new ThemeChoice("plum", "Слива", false, With(Graphite, new()
        {
            ["Background"] = "#120811", ["BackgroundSoft"] = "#1B0C1A", ["NavBackground"] = "#0D050D", ["NavTranslucent"] = "#B80D050D",
            ["Surface"] = "#261122", ["SurfaceRaised"] = "#2E1529", ["SurfaceHover"] = "#371A2E", ["SurfacePressed"] = "#1A0B17",
            ["BorderStrong"] = "#8E4F7C", ["TextPrimary"] = "#FFF4F1", ["TextSecondary"] = "#E6C6D9", ["TextDim"] = "#A7849B"
        })),
        new ThemeChoice("oled", "Чёрная OLED", false, With(Graphite, new()
        {
            ["Background"] = "#000000", ["BackgroundSoft"] = "#08080A", ["NavBackground"] = "#000000", ["NavTranslucent"] = "#E6000000",
            ["Surface"] = "#101012", ["SurfaceRaised"] = "#16161A", ["SurfaceHover"] = "#1E1E23", ["SurfacePressed"] = "#0A0A0C"
        })),
        new ThemeChoice("light", "Светлая", true, new Dictionary<string, string>
        {
            ["Background"] = "#F5F5F7", ["BackgroundSoft"] = "#ECECEF", ["NavBackground"] = "#EAEAED", ["NavTranslucent"] = "#C8F2F2F5",
            ["Surface"] = "#FFFFFF", ["SurfaceRaised"] = "#FFFFFF", ["SurfaceHover"] = "#F0F0F3", ["SurfacePressed"] = "#E4E4E8",
            ["Fill"] = "#0F000000", ["FillHover"] = "#1A000000", ["Hairline"] = "#14000000", ["BorderSoft"] = "#1F000000", ["BorderMid"] = "#2E000000",
            ["BorderStrong"] = "#A1A1A8", ["TextPrimary"] = "#1D1D1F", ["TextSecondary"] = "#3C3C43", ["TextDim"] = "#6A6673"
        })
    };

    public static IReadOnlyList<AccentChoice> Accents { get; } = new[]
    {
        new AccentChoice("lavender", "Лаванда", "#BBA5FF", "#24BBA5FF"),
        new AccentChoice("pink", "Розовый", "#FF62AB", "#33FF62AB"),
        new AccentChoice("violet", "Фиолетовый", "#AF7BFF", "#33AF7BFF"),
        new AccentChoice("blue", "Синий", "#3D9BFF", "#333D9BFF"),
        new AccentChoice("mint", "Мятный", "#3DD9A6", "#333DD9A6"),
        new AccentChoice("orange", "Оранжевый", "#FF8A3D", "#33FF8A3D")
    };

    private static Dictionary<string, string> With(Dictionary<string, string> source, Dictionary<string, string> changes)
    {
        var result = new Dictionary<string, string>(source);
        foreach (var (key, value) in changes) result[key] = value;
        return result;
    }

    /// <summary>Applies theme and accent to the application's dynamic brushes. Free always shows Graphite.</summary>
    public void Apply()
    {
        if (Application.Current is not { } app) return;
        var theme = Edition.IsPrime ? Themes.FirstOrDefault(t => t.Key == Theme) ?? Themes[0] : Themes[0];
        var accent = Edition.IsPrime ? Accents.FirstOrDefault(a => a.Key == Accent) ?? Accents[0] : Accents[0];
        foreach (var (key, color) in theme.Colors) app.Resources[key] = new SolidColorBrush(Color.Parse(color));
        string accentColor = theme.Light ? accent.Key switch
        {
            "pink" => "#B92D70", "violet" => "#7750BC", "blue" => "#2468B7",
            "mint" => "#187859", "orange" => "#AB511F", _ => "#7054BD"
        } : accent.Color;
        app.Resources["Orchid"] = new SolidColorBrush(Color.Parse(accentColor));
        app.Resources["SelectionInk"] = new SolidColorBrush(Color.Parse(theme.Light ? "#FFFFFF" : "#17151F"));
        app.Resources["Success"] = new SolidColorBrush(Color.Parse(theme.Light ? "#18743B" : "#32D74B"));
        app.Resources["Warning"] = new SolidColorBrush(Color.Parse(theme.Light ? "#80530A" : "#FF9F0A"));
        app.Resources["Danger"] = new SolidColorBrush(Color.Parse(theme.Light ? "#B52938" : "#FF453A"));
        app.Resources["AccentSubtle"] = new SolidColorBrush(Color.Parse(theme.Light ? "#24" + accentColor.TrimStart('#') : accent.Subtle));
        app.Resources["SystemAccentColor"] = Color.Parse(accentColor);
        app.Resources["HeroSurface"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new(Color.Parse(theme.Colors["SurfaceRaised"]), 0),
                new(Color.Parse(theme.Colors["Surface"]), 0.75)
            }
        };
        app.RequestedThemeVariant = theme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }
}

using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Rendering;

/// <summary>
/// The shared VS Code look: white icons and text on VS Code blue, so VS Code keys read as one set
/// on the deck. Live keys change background with their state (red for errors, amber for warnings
/// and a paused debugger, green for success, dark grey when there is nothing to show). Commands
/// declare it as ordinary layers the user can restyle.
/// </summary>
internal static class CodeKey
{
    public const string BlueHex = "#007ACC";
    public const string WhiteHex = "#FFFFFF";

    /// <summary>State colours for live keys; white text stays readable on each.</summary>
    public const string ErrorHex = "#C72E2E";
    public const string WarningHex = "#B07800";
    public const string OkHex = "#2E7D32";
    public const string AttentionHex = "#B07800";
    public const string IdleHex = BlueHex;
    public const string DimHex = "#3C3C3C";

    /// <summary>The same colours for folder keys, which take a <see cref="PluginColor"/>.</summary>
    public static readonly PluginColor Blue = PluginColor.FromRgb(0x00, 0x7A, 0xCC);
    public static readonly PluginColor BrightBlue = PluginColor.FromRgb(0x1F, 0x9C, 0xF0);
    public static readonly PluginColor Error = PluginColor.FromRgb(0xC7, 0x2E, 0x2E);
    public static readonly PluginColor Warning = PluginColor.FromRgb(0xB0, 0x78, 0x00);
    public static readonly PluginColor Dim = PluginColor.FromRgb(0x3C, 0x3C, 0x3C);

    /// <summary>Roughly how many characters of 13 px text fit on one line of a 90 px key.</summary>
    public const int LineChars = 11;

    /// <summary>Icon above a caption.</summary>
    public static ButtonLayoutDescriptor IconAndCaption(string glyph, string caption, string background = BlueHex) => new()
    {
        Mode = ButtonLayoutMode.Custom,
        BackgroundColor = background,
        Layers =
        [
            new ButtonLayerDescriptor { Kind = ButtonLayerKind.Symbol, Name = "Icon", Glyph = glyph, IconScale = 0.46, OffsetY = -9, Color = WhiteHex },
            Caption(caption)
        ]
    };

    public static ButtonLayerDescriptor Caption(string text) => new()
    {
        Kind = ButtonLayerKind.Text, Name = "Caption", Text = text, TextSize = 13,
        OffsetY = 26, BoxWidth = 88, BoxHeight = 22, Color = WhiteHex
    };

    /// <summary>A text layer fed by the command's reported value (<see cref="ButtonTextSource.Value"/>) or its detail.</summary>
    public static ButtonLayerDescriptor ValueText(string name, ButtonTextSource source, int size, int offsetY, int lines = 1, string color = WhiteHex) => new()
    {
        Kind = ButtonLayerKind.Text, Name = name, TextSource = source, TextSize = size,
        OffsetY = offsetY, BoxWidth = 88, BoxHeight = lines * (size + 4) + 4, Color = color
    };

    public static ButtonLayerDescriptor Icon(string name, string glyph, double scale, int offsetY, string color = WhiteHex) => new()
    {
        Kind = ButtonLayerKind.Symbol, Name = name, Glyph = glyph, IconScale = scale, OffsetY = offsetY, Color = color
    };

    public static ButtonLayoutDescriptor State(string background, params ButtonLayerDescriptor[] layers) => new()
    {
        Mode = ButtonLayoutMode.Custom,
        BackgroundColor = background,
        Layers = layers
    };

    /// <summary>
    /// The layout every live key state uses: a small icon at the top, the value (one line) in the
    /// middle and the detail (up to two lines) underneath.
    /// </summary>
    public static ButtonLayoutDescriptor Live(string background, string glyph) => State(background,
        Icon("Icon", glyph, 0.3, -26),
        ValueText("Value", ButtonTextSource.Value, 14, 1),
        ValueText("Detail", ButtonTextSource.Detail, 11, 27, lines: 2));

    /// <summary>Shortens text to <paramref name="max"/> characters with an ellipsis, so a long branch or file name keeps its start.</summary>
    public static string Fit(string? text, int max = LineChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var t = text.Trim();
        return t.Length <= max ? t : t[..Math.Max(1, max - 1)].TrimEnd() + "…";
    }
}

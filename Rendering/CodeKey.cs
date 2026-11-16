using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Rendering;

/// <summary>
/// The shared VS Code look: white icons and text on graphite grey, so VS Code keys read as one set
/// on the deck. Live keys change background with their state (red for errors, amber for warnings
/// and a paused debugger, green for success, a darker grey when there is nothing to show). Folder
/// keys for windows and open editors take the window's Peacock colour when it has one. Commands
/// declare it as ordinary layers the user can restyle.
/// </summary>
internal static class CodeKey
{
    /// <summary>The default background: the graphite of VS Code's Dark+ title bar.</summary>
    public const string GraphiteHex = "#3C3C3C";
    public const string WhiteHex = "#FFFFFF";

    /// <summary>State colours for live keys; white text stays readable on each.</summary>
    public const string ErrorHex = "#C72E2E";
    public const string WarningHex = "#B07800";
    public const string OkHex = "#2E7D32";
    public const string AttentionHex = "#B07800";
    public const string IdleHex = GraphiteHex;
    public const string DimHex = "#252526";

    /// <summary>The same colours for folder keys, which take a <see cref="PluginColor"/>.</summary>
    public static readonly PluginColor Graphite = PluginColor.FromRgb(0x3C, 0x3C, 0x3C);

    /// <summary>The active item among graphite keys (the active editor or terminal).</summary>
    public static readonly PluginColor Highlight = PluginColor.FromRgb(0x5E, 0x5E, 0x5E);
    public static readonly PluginColor Error = PluginColor.FromRgb(0xC7, 0x2E, 0x2E);
    public static readonly PluginColor Warning = PluginColor.FromRgb(0xB0, 0x78, 0x00);
    public static readonly PluginColor Dim = PluginColor.FromRgb(0x25, 0x25, 0x26);

    /// <summary>Dark text for light window colours (Peacock's dark foreground).</summary>
    public static readonly PluginColor DarkText = PluginColor.FromRgb(0x15, 0x20, 0x2B);

    /// <summary>The symbol drawn on window keys: a window with brackets, like the plugin's icon.</summary>
    public const string WindowSymbol = "application-brackets-outline";

    /// <summary>A <c>#rgb</c> or <c>#rrggbb</c> colour (any alpha dropped); null for anything else.</summary>
    public static PluginColor? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.Trim();
        if (h.Length < 4 || h[0] != '#') return null;
        h = h[1..];
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        else if (h.Length == 8) h = h[..6];
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return null;
        return PluginColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>Mixes <paramref name="amount"/> (0–1) of white into a colour, for the highlighted key of a coloured set.</summary>
    public static PluginColor Lighten(PluginColor c, double amount = 0.2)
    {
        byte Mix(byte v) => (byte)Math.Round(v + (255 - v) * amount);
        return PluginColor.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    /// <summary>White text, or dark text on a light background (perceived brightness above half).</summary>
    public static PluginColor TextOn(PluginColor background) =>
        (background.R * 299 + background.G * 587 + background.B * 114) / 1000 > 128 ? DarkText : PluginColor.White;

    /// <summary>Roughly how many characters of 13 px text fit on one line of a 90 px key.</summary>
    public const int LineChars = 11;

    /// <summary>Icon above a caption.</summary>
    public static ButtonLayoutDescriptor IconAndCaption(string glyph, string caption, string background = GraphiteHex) => new()
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

    /// <summary>
    /// The layout for live keys whose detail is short or missing: the icon over one text box that
    /// holds the value and, when there is one, the detail on a second line. The box centres what it
    /// holds, so one line or two, the key stays balanced.
    /// </summary>
    public static ButtonLayoutDescriptor Compact(string background, string glyph) => State(background,
        Icon("Icon", glyph, 0.3, -17),
        ValueText("Value", ButtonTextSource.Value, 14, 14, lines: 2));

    /// <summary>Shortens text to <paramref name="max"/> characters with an ellipsis, so a long branch or file name keeps its start.</summary>
    public static string Fit(string? text, int max = LineChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var t = text.Trim();
        return t.Length <= max ? t : t[..Math.Max(1, max - 1)].TrimEnd() + "…";
    }
}

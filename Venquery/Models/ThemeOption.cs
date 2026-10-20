using System.Windows.Media;

namespace Venquery.Models;

/// <summary>One selectable built-in theme. A theme starts from a base palette (dark or light)
/// and applies <see cref="Overrides"/>, a small set of accent-family (and occasionally
/// background) color swaps, on top. <see cref="Swatch"/> feeds the color dot in the picker.</summary>
public sealed class ThemeOption
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>Which base palette this theme builds on: true = dark, false = light.</summary>
    public bool Dark { get; init; }

    /// <summary>Hex color shown as the swatch dot beside the name in the dropdown.</summary>
    public string SwatchHex { get; init; } = "#2F6FED";

    /// <summary>Resource-key → hex color swaps applied over the base palette.</summary>
    public (string Key, string Color)[] Overrides { get; init; } = System.Array.Empty<(string, string)>();

    /// <summary>Brush for the swatch dot in the picker.</summary>
    public Brush Swatch => new SolidColorBrush((Color)ColorConverter.ConvertFromString(SwatchHex)!);

    /// <summary>So a screen reader (and the combo's fallback text) reads the theme name rather
    /// than the type name.</summary>
    public override string ToString() => Name;
}

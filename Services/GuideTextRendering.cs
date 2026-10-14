using System.Globalization;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Grimmcair.Desktop.Services;

public sealed record GuideTextRenderResult(bool UsedRuns, int ColoredRunCount, int AdjustedColorCount,
    int RejectedColorCount, double MinimumContrast, IReadOnlyList<string> RenderedColors);

/// <summary>Renders literal guide text and optional RGB colors; never interprets markup or alpha codes.</summary>
public static class GuideTextRendering
{
    public const double RequiredContrast = 4.5;

    // TextBlock.Text may retain its previous value after Inlines changes. Read the live
    // text container so plain and colored rendering expose the same actual content.
    public static string ReadText(TextBlock target) =>
        new TextRange(target.ContentStart, target.ContentEnd).Text;

    public static GuideTextRenderResult Apply(TextBlock target, string text, IReadOnlyList<GuideTextRun>? runs,
        Color normalColor, IReadOnlyList<Color> paperColors)
    {
        ValidatePaper(paperColors);
        target.Inlines.Clear();
        target.Foreground = Brush(normalColor);
        var normalContrast = MinimumContrast(normalColor, paperColors);
        if (runs is null || runs.Count == 0 || runs.Any(run => run is null || run.Text is null) ||
            !string.Equals(string.Concat(runs.Select(run => run.Text)), text, StringComparison.Ordinal))
        {
            target.Text = text;
            return new(false, 0, 0, 0, normalContrast, []);
        }

        var colored = 0;
        var adjusted = 0;
        var rejected = 0;
        var minimum = double.PositiveInfinity;
        var colors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in runs)
        {
            if (source.Text.Length == 0) continue;
            var run = new Run(source.Text);
            var color = normalColor;
            if (!string.IsNullOrEmpty(source.Color))
            {
                if (TryParseRgb(source.Color, out var original))
                {
                    color = ReadableColor(original, paperColors);
                    run.Foreground = Brush(color);
                    colored++;
                    if (color != original) adjusted++;
                    colors.Add(Hex(color));
                }
                else rejected++;
            }
            minimum = Math.Min(minimum, MinimumContrast(color, paperColors));
            target.Inlines.Add(run);
        }
        return new(true, colored, adjusted, rejected,
            double.IsPositiveInfinity(minimum) ? normalContrast : minimum, colors.Order(StringComparer.Ordinal).ToArray());
    }

    public static bool TryParseRgb(string? value, out Color color)
    {
        color = default;
        if (value is not { Length: 7 } || value[0] != '#' ||
            !byte.TryParse(value.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(value.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(value.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var blue)) return false;
        color = Color.FromRgb(red, green, blue);
        return true;
    }

    /// <summary>Darkens RGB channels proportionally, keeping hue, against every opaque paper color.</summary>
    public static Color ReadableColor(Color original, IReadOnlyList<Color> paperColors)
    {
        ValidatePaper(paperColors);
        var opaque = Color.FromRgb(original.R, original.G, original.B);
        if (MinimumContrast(opaque, paperColors) >= RequiredContrast) return opaque;
        var low = 0.0;
        var high = 1.0;
        for (var i = 0; i < 32; i++)
        {
            var scale = (low + high) / 2;
            if (MinimumContrast(Darken(opaque, scale), paperColors) >= RequiredContrast) low = scale;
            else high = scale;
        }
        // The passing side is retained after quantization, so the final byte RGB also passes.
        return Darken(opaque, low);
    }

    public static double ContrastRatio(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte component)
        {
            var value = component / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static void ValidatePaper(IReadOnlyList<Color> colors)
    {
        if (colors.Count == 0 || colors.Any(color => color.A != 255 || ContrastRatio(Colors.Black, color) < RequiredContrast))
            throw new ArgumentException("Guide text requires opaque, light paper backgrounds.", nameof(colors));
    }

    private static double MinimumContrast(Color color, IReadOnlyList<Color> backgrounds) =>
        backgrounds.Min(background => ContrastRatio(color, background));

    private static Color Darken(Color color, double scale) => Color.FromRgb(
        (byte)Math.Floor(color.R * scale), (byte)Math.Floor(color.G * scale), (byte)Math.Floor(color.B * scale));

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

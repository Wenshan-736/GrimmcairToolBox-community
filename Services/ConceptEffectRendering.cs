using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Grimmcair.Desktop.Services;

/// <summary>Gives confirmed attribute values a distinct, readable presentation on parchment.</summary>
public static class ConceptEffectRendering
{
    public static GuideTextRenderResult Apply(TextBlock target, string text, IReadOnlyList<GuideTextRun>? runs,
        Color normalColor, IReadOnlyList<Color> paperColors)
    {
        // Keep the shared literal-text validation and fallback for unknown RGB values.
        var result = GuideTextRendering.Apply(target, text, runs, normalColor, paperColors);
        if (!result.UsedRuns || runs is null) return result;
        var visibleSources = runs.Where(source => source.Text.Length > 0).ToArray();
        var visibleRuns = target.Inlines.OfType<Run>().ToArray();
        var colors = new HashSet<string>(StringComparer.Ordinal);
        var minimum = double.PositiveInfinity;
        var adjusted = 0;
        for (var i = 0; i < visibleSources.Length; i++)
        {
            var source = visibleSources[i];
            var run = visibleRuns[i];
            var foreground = (run.Foreground as SolidColorBrush)?.Color ?? normalColor;
            if (GuideTextRendering.TryParseRgb(source.Color, out var original))
            {
                if (AttributePalette(source.Color!) is { } palette)
                {
                    foreground = Parse(palette.Foreground);
                    run.Foreground = Brush(foreground);
                    run.Background = Brush(Parse(palette.Background));
                    run.FontWeight = System.Windows.FontWeights.SemiBold;
                }
                if (foreground != original) adjusted++;
                colors.Add($"#{foreground.R:X2}{foreground.G:X2}{foreground.B:X2}");
            }
            minimum = Math.Min(minimum, run.Background is SolidColorBrush background
                ? GuideTextRendering.ContrastRatio(foreground, background.Color)
                : paperColors.Min(paper => GuideTextRendering.ContrastRatio(foreground, paper)));
        }
        return result with { AdjustedColorCount = adjusted, MinimumContrast = double.IsPositiveInfinity(minimum)
            ? result.MinimumContrast : minimum, RenderedColors = colors.Order(StringComparer.Ordinal).ToArray() };
    }

    // Source colors identify the six elements and the mechanic's magic attribute.
    // The UI palette does not create a scaling relationship for an uncolored value.
    private static (string Foreground, string Background)? AttributePalette(string source) => source.ToUpperInvariant() switch
    {
        "#F8AE9C" => ("#FFB29F", "#672719"),
        "#BAE0FC" => ("#A9DFFF", "#173E60"),
        "#B5FBBA" => ("#87F5BB", "#16442E"),
        "#DFBF9F" => ("#F7D4A4", "#533B22"),
        "#FFFFCD" => ("#FFE16B", "#4B3500"),
        "#9669D7" => ("#E0B0FF", "#4F2668"),
        "#75E6E6" => ("#75E6E6", "#17474A"),
        _ => null
    };

    private static Color Parse(string value)
    {
        GuideTextRendering.TryParseRgb(value, out var color);
        return color;
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

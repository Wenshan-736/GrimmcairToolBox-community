using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Media;

namespace Grimmcair.Desktop.Services;

/// <summary>
/// Literal item text with pinned source RGB runs. Optional section colours are
/// a reading aid only and do not imply elemental scaling or Buff grouping.
/// </summary>
public static class ItemDescriptionRendering
{
    public const string PresentationNote = "装备说明优先沿用原文颜色；无原色时按主动、被动和冷却段落区分。配色仅帮助阅读，不表示属性增幅或叠加机制。";
    public const string PassiveSectionColor = "#24636B";
    public const string ActiveSectionColor = "#895219";
    public const string TimingLineColor = "#6D5139";

    private static readonly Regex SectionHeading = new(@"^\s*(主动|被动)\s*(?:技能)?\s*(?:[:：·]|$)", RegexOptions.CultureInvariant);
    private static readonly Regex TimingHeading = new(@"^\s*(?:冷却时间|冷却|施放间隔|持续时间)\s*[:：]", RegexOptions.CultureInvariant);

    public static GuideTextRenderResult Apply(TextBlock target, string text, IReadOnlyList<GuideTextRun>? runs,
        Color normalColor, IReadOnlyList<Color> paperColors, bool distinguishSections = false) =>
        GuideTextRendering.Apply(target, text, distinguishSections ? PresentationRuns(text, runs) : runs,
            normalColor, paperColors);

    /// <summary>Original source colours take precedence over optional structural presentation.</summary>
    public static IReadOnlyList<GuideTextRun> PresentationRuns(string text, IReadOnlyList<GuideTextRun>? sourceRuns)
    {
        List<GuideTextRun> source = sourceRuns is { Count: > 0 } && sourceRuns.All(run => run is not null && run.Text is not null)
            && string.Equals(string.Concat(sourceRuns.Select(run => run.Text)), text, StringComparison.Ordinal)
            ? sourceRuns.Where(run => run.Text.Length > 0).ToList()
            : [new GuideTextRun { Text = text }];
        var result = new List<GuideTextRun>();
        var sourceIndex = 0;
        var sourceOffset = 0;
        var sectionColor = "";
        var lines = text.Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var heading = SectionHeading.Match(line);
            if (heading.Success) sectionColor = heading.Groups[1].Value == "主动" ? ActiveSectionColor : PassiveSectionColor;
            var lineColor = TimingHeading.IsMatch(line) ? TimingLineColor : sectionColor;
            var content = line + (lineIndex < lines.Length - 1 ? "\n" : "");
            var offset = 0;
            while (offset < content.Length)
            {
                var original = source[sourceIndex];
                var length = Math.Min(content.Length - offset, original.Text.Length - sourceOffset);
                var color = string.IsNullOrEmpty(original.Color) ? lineColor : original.Color;
                Append(content.Substring(offset, length), color);
                offset += length;
                sourceOffset += length;
                if (sourceOffset == original.Text.Length) { sourceIndex++; sourceOffset = 0; }
            }
        }
        return result;

        void Append(string content, string color)
        {
            if (content.Length == 0) return;
            if (result.LastOrDefault() is { } last && string.Equals(last.Color, color, StringComparison.Ordinal))
                result[^1] = new GuideTextRun { Text = last.Text + content, Color = color };
            else result.Add(new GuideTextRun { Text = content, Color = color });
        }
    }
}

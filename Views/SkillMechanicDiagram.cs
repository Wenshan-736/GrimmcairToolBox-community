using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

/// <summary>Small vector explanations; coordinates are illustrations, never collision or safe-zone data.</summary>
public sealed class SkillMechanicDiagram : Border
{
    private static readonly Brush Ink = MakeBrush(0x4F, 0x35, 0x23);
    private static readonly Brush Muted = MakeBrush(0x80, 0x62, 0x47);
    private static readonly Brush Line = MakeBrush(0xCB, 0xB3, 0x8C);
    private static readonly Brush Boss = MakeBrush(0x79, 0x4E, 0x2C);
    private static readonly Brush Player = MakeBrush(0x39, 0x6E, 0x91);
    private static readonly Brush Danger = MakeBrush(0xA6, 0x4E, 0x37);
    private static readonly Brush DangerFill = MakeBrush(0xA6, 0x4E, 0x37, 0x45);
    private static readonly Brush TriggerTile = MakeBrush(0xBD, 0x8D, 0x42);
    private static readonly Brush Paper = MakeBrush(0xFA, 0xF1, 0xDB);
    private static readonly Brush PlotPaper = MakeBrush(0xF3, 0xE6, 0xC9);

    public GuideDiagram Diagram { get; }
    public string DiagramKind => Diagram.Kind;
    public int DisplayedCount => ResolveCount(Diagram);
    public double DisplayedAngle => ResolveAngle(Diagram);
    public string DisplayedCenterLabel => string.IsNullOrWhiteSpace(Diagram.CenterLabel) ? "首领" : Diagram.CenterLabel;
    public static bool Supports(string kind) => kind is "sector" or "beam" or "ring" or "orbits" or "cross" or "split" or "share";

    public SkillMechanicDiagram(GuideDiagram diagram)
    {
        Diagram = diagram;
        Background = Paper; BorderBrush = Line; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12);
        AutomationProperties.SetName(this, $"{diagram.Title}，机制示意图。{DisplayedCenterLabel}。{diagram.Caption} {diagram.RangeText}");
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = diagram.Title, Foreground = Ink, FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7)
        });
        content.Children.Add(new DiagramCanvas(diagram) { Height = 146, Margin = new Thickness(0, 0, 0, 8) });
        var legend = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        if (diagram.Kind == "orbits" && !string.IsNullOrWhiteSpace(diagram.CenterLabel) && diagram.CenterLabel != "首领")
        {
            legend.Inlines.Add(new Run("⊕") { Foreground = Ink }); legend.Inlines.Add(new Run($" {diagram.CenterLabel}    "));
        }
        else
        {
            legend.Inlines.Add(new Run("◆") { Foreground = Boss }); legend.Inlines.Add(new Run($" {DisplayedCenterLabel}    "));
        }
        var targetLabel = diagram.CenterLabel == "施法者" ? "目标" : "玩家";
        legend.Inlines.Add(new Run("●") { Foreground = Player }); legend.Inlines.Add(new Run($" {targetLabel}    "));
        legend.Inlines.Add(new Run("▰") { Foreground = Danger }); legend.Inlines.Add(new Run(" 危险区域"));
        if (diagram.Kind == "cross")
        {
            legend.Inlines.Add(new Run("    ■") { Foreground = TriggerTile }); legend.Inlines.Add(new Run(" 触发地砖"));
        }
        content.Children.Add(legend);
        if (!string.IsNullOrWhiteSpace(diagram.Caption)) content.Children.Add(Description(diagram.Caption));
        if (!string.IsNullOrWhiteSpace(diagram.RangeText)) content.Children.Add(Description(diagram.RangeText));
        var note = Description("非实际比例；图中单位位置仅作示例，不代表安全点。");
        note.Margin = new Thickness(0, 6, 0, 0); note.FontSize = 10; content.Children.Add(note);
        Child = content;
    }

    private static TextBlock Description(string text) => new()
    {
        Text = text, FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, LineHeight = 18
    };

    private static Brush MakeBrush(byte red, byte green, byte blue, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue)); brush.Freeze(); return brush;
    }

    private static int ResolveCount(GuideDiagram model) => Math.Clamp(model.Count > 0 ? model.Count : model.Kind == "orbits" ? 2 : 4, 1, 24);
    private static double ResolveAngle(GuideDiagram model) => double.IsFinite(model.AngleDegrees) ? Math.Clamp(model.AngleDegrees, 0, 360) : 90;

    private sealed class DiagramCanvas(GuideDiagram model) : FrameworkElement
    {
        private static readonly Pen HazardPen = new(Danger, 1.5);
        private static readonly Pen GuidePen = new(Line, 1) { DashStyle = DashStyles.Dash };

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            drawing.DrawRoundedRectangle(PlotPaper, null, new Rect(0, 0, ActualWidth, ActualHeight), 6, 6);
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var scale = Math.Min(ActualWidth / 316, ActualHeight / 146);
            drawing.PushTransform(new TranslateTransform((ActualWidth - 316 * scale) / 2, (ActualHeight - 146 * scale) / 2));
            drawing.PushTransform(new ScaleTransform(scale, scale));
            switch (model.Kind)
            {
                case "sector": DrawSector(drawing); break;
                case "beam": DrawBeam(drawing); break;
                case "ring": DrawRing(drawing); break;
                case "orbits": DrawOrbits(drawing); break;
                case "cross": DrawCross(drawing); break;
                case "split": DrawSplit(drawing); break;
                case "share": DrawShare(drawing); break;
            }
            drawing.Pop(); drawing.Pop();
        }

        private void DrawSector(DrawingContext drawing)
        {
            var center = new Point(145, 73); const double radius = 62;
            var angle = ResolveAngle(model);
            if (angle >= 359.9) drawing.DrawEllipse(DangerFill, HazardPen, center, radius, radius);
            else if (angle > 0)
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(center, true, true);
                    context.LineTo(Polar(center, radius, -angle / 2), true, false);
                    context.ArcTo(Polar(center, radius, angle / 2), new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true, false);
                }
                drawing.DrawGeometry(DangerFill, HazardPen, geometry);
            }
            DrawArrow(drawing, center, new Point(225, 73), new Pen(Boss, 1.4));
            DrawBoss(drawing, center); DrawPlayer(drawing, new Point(190, 65)); DrawPlayer(drawing, new Point(117, 121));
        }

        private static void DrawBeam(DrawingContext drawing)
        {
            drawing.DrawRoundedRectangle(DangerFill, HazardPen, new Rect(67, 56, 202, 34), 3, 3);
            DrawArrow(drawing, new Point(90, 73), new Point(267, 73), new Pen(Danger, 1.4));
            DrawBoss(drawing, new Point(60, 73)); DrawPlayer(drawing, new Point(243, 73)); DrawPlayer(drawing, new Point(200, 117));
        }

        private static void DrawRing(DrawingContext drawing)
        {
            var center = new Point(158, 73);
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(center, 61, 61), new EllipseGeometry(center, 37, 37));
            drawing.DrawGeometry(DangerFill, HazardPen, ring);
            DrawBoss(drawing, center); DrawPlayer(drawing, new Point(203, 55)); DrawPlayer(drawing, new Point(248, 95));
        }

        private void DrawOrbits(DrawingContext drawing)
        {
            var center = new Point(158, 73); const double radius = 49;
            drawing.DrawEllipse(null, GuidePen, center, radius, radius);
            var count = ResolveCount(model);
            for (var index = 0; index < count; index++)
            {
                var angle = -90 + 360d * index / count;
                var position = Polar(center, radius, angle);
                var zoneSize = count <= 8 ? 12 : 7;
                drawing.DrawEllipse(DangerFill, HazardPen, position, zoneSize, zoneSize);
                DrawArrow(drawing, Polar(center, radius, angle + 19), Polar(center, radius, angle + 39), new Pen(Danger, 1.4));
            }
            if (string.IsNullOrWhiteSpace(model.CenterLabel) || model.CenterLabel == "首领") DrawBoss(drawing, center);
            else
            {
                var markerPen = new Pen(Ink, 1.2);
                drawing.DrawEllipse(Paper, markerPen, center, 6, 6);
                drawing.DrawLine(markerPen, new Point(center.X - 9, center.Y), new Point(center.X + 9, center.Y));
                drawing.DrawLine(markerPen, new Point(center.X, center.Y - 9), new Point(center.X, center.Y + 9));
                var label = new FormattedText(model.CenterLabel, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 11, Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                drawing.DrawText(label, new Point(center.X - label.Width / 2, center.Y + 11));
            }
            DrawPlayer(drawing, new Point(235, 108));
        }

        private void DrawCross(DrawingContext drawing)
        {
            var horizontal = new RectangleGeometry(new Rect(72, 59, 172, 28));
            var vertical = new RectangleGeometry(new Rect(144, 12, 28, 122));
            drawing.DrawGeometry(DangerFill, HazardPen, new CombinedGeometry(GeometryCombineMode.Union, horizontal, vertical));
            drawing.DrawRectangle(TriggerTile, new Pen(Paper, 1.5), new Rect(151, 66, 14, 14));
            drawing.DrawText(new FormattedText("触发地砖", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 11, Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(180, 91));
            DrawBoss(drawing, new Point(79, 25)); DrawPlayer(drawing, new Point(221, 70)); DrawPlayer(drawing, new Point(217, 124));
        }

        private void DrawSplit(DrawingContext drawing)
        {
            var center = new Point(158, 73); var count = ResolveCount(model);
            for (var index = 0; index < count; index++)
            {
                var position = Polar(center, 51, -90 + 360d * index / count);
                DrawArrow(drawing, center, position, new Pen(Danger, 1.3) { DashStyle = DashStyles.Dash });
                drawing.DrawEllipse(DangerFill, HazardPen, position, 12, 12);
                DrawPlayer(drawing, position);
            }
            DrawBoss(drawing, center);
        }

        private void DrawShare(DrawingContext drawing)
        {
            var impact = new Point(202, 73); var count = ResolveCount(model);
            drawing.DrawEllipse(DangerFill, HazardPen, impact, 54, 54);
            DrawArrow(drawing, new Point(74, 73), new Point(140, 73), new Pen(Danger, 1.4));
            for (var index = 0; index < count; index++)
                DrawPlayer(drawing, count == 1 ? impact : Polar(impact, count <= 8 ? 25 : 37, -90 + 360d * index / count));
            DrawBoss(drawing, new Point(59, 73));
        }

        private static Point Polar(Point center, double radius, double angle)
        {
            var radians = angle * Math.PI / 180; return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }

        private static void DrawBoss(DrawingContext drawing, Point center)
        {
            var diamond = new StreamGeometry();
            using (var context = diamond.Open())
            {
                context.BeginFigure(new Point(center.X, center.Y - 10), true, true);
                context.PolyLineTo([new Point(center.X + 10, center.Y), new Point(center.X, center.Y + 10), new Point(center.X - 10, center.Y)], true, false);
            }
            drawing.DrawGeometry(Boss, new Pen(Paper, 1.5), diamond);
            drawing.DrawEllipse(Paper, null, center, 2.3, 2.3);
        }

        private static void DrawPlayer(DrawingContext drawing, Point center)
        {
            drawing.DrawEllipse(Player, new Pen(Paper, 1.5), center, 5.5, 5.5);
        }

        private static void DrawArrow(DrawingContext drawing, Point from, Point to, Pen pen)
        {
            drawing.DrawLine(pen, from, to);
            var direction = to - from;
            if (direction.Length < 1) return;
            direction.Normalize(); var side = new Vector(-direction.Y, direction.X);
            drawing.DrawLine(new Pen(pen.Brush, 1.4), to, to - direction * 6 + side * 3);
            drawing.DrawLine(new Pen(pen.Brush, 1.4), to, to - direction * 6 - side * 3);
        }
    }
}

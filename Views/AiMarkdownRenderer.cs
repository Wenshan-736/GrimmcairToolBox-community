using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace Grimmcair.Desktop.Views;

/// <summary>Renders untrusted model text without HTML, remote content, or executable Markdown extensions.</summary>
public static class AiMarkdownRenderer
{
    private const int MaxFormattedCharacters = 48_000;
    private const int MaxNodes = 1_200;
    private const int MaxDepth = 16;
    private const int MaxTableCells = 400;
    private static readonly Brush Ink = FrozenBrush(0x4F, 0x35, 0x23);
    private static readonly Brush Muted = FrozenBrush(0x80, 0x62, 0x47);
    private static readonly Brush Accent = FrozenBrush(0x8B, 0x4D, 0x28);
    private static readonly Brush Line = FrozenBrush(0xDD, 0xC9, 0xA8);
    private static readonly Brush CodePaper = FrozenBrush(0xF3, 0xE9, 0xD6);
    private static readonly Brush HeaderPaper = FrozenBrush(0xEB, 0xDB, 0xB9);
    private static readonly FontFamily BodyFont = new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Microsoft YaHei UI");

    // Only explicit, text-only extensions. Never enable generic attributes, media, HTML or scripts.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder
    {
        MaximumNestingDepth = MaxDepth
    }.UsePipeTables().UseAutoLinks().UseCjkFriendlyEmphasis().UsePreciseSourceLocation().DisableHtml().Build();

    /// <summary>Call on the WPF UI thread. The host must enable documents for clickable links.</summary>
    public static FlowDocument Render(string markdown)
    {
        markdown ??= "";
        if (markdown.Length > MaxFormattedCharacters)
            return PlainDocument(markdown, "内容较长，已切换为可滚动的原文显示；可使用“复制 Markdown”复制完整回答。");

        try
        {
            var parsed = Markdown.Parse(markdown, Pipeline);
            var document = NewDocument();
            var renderer = new Renderer(markdown);
            renderer.AddBlocks(parsed, document.Blocks, 0);
            if (document.Blocks.Count == 0) document.Blocks.Add(NewParagraph());
            return document;
        }
        catch (Exception exception) when (exception is RenderLimitException or ArgumentException or InvalidOperationException)
        {
            // Keep every source character, including a partial Markdown fragment arriving in a stream.
            return PlainDocument(markdown, "这段内容暂以原文显示，完整回答仍可选择和复制。");
        }
    }

    private static FlowDocument NewDocument() => new()
    {
        FontFamily = BodyFont,
        FontSize = 14,
        Foreground = Ink,
        Background = Brushes.Transparent,
        PagePadding = new Thickness(0),
        TextAlignment = TextAlignment.Left,
        LineHeight = 24,
        IsHyphenationEnabled = false,
        ColumnGap = 0
    };

    private static Paragraph NewParagraph() => new() { Margin = new Thickness(0, 0, 0, 9) };

    private static FlowDocument PlainDocument(string source, string notice)
    {
        var document = NewDocument();
        document.Blocks.Add(new Paragraph(new Run(notice))
        {
            Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 8)
        });
        document.Blocks.Add(TextScrollBlock(source, wrap: true));
        return document;
    }

    private static TextBox ReadOnlyText(string text, bool wrap) => new()
    {
        Style = null,
        Text = text,
        IsReadOnly = true,
        IsReadOnlyCaretVisible = false,
        IsUndoEnabled = false,
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        // The enclosing ScrollViewer owns scrolling; the editor is measured at its complete content width.
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        MinHeight = 0,
        FontFamily = wrap ? BodyFont : CodeFont,
        FontSize = wrap ? 14 : 13,
        Foreground = Ink,
        Background = CodePaper,
        BorderBrush = Line,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(11, 9, 11, 9),
        VerticalContentAlignment = VerticalAlignment.Top,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };

    private static BlockUIContainer TextScrollBlock(string text, bool wrap)
    {
        var editor = ReadOnlyText(text, wrap);
        if (!wrap)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(CodeFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 13, Ink, 1);
            editor.Width = Math.Max(32, Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 26);
        }
        return ScrollBlock(editor, CodePaper, 360, wrap ? width => editor.Width = Math.Max(1, width - 2) : null);
    }

    private static BlockUIContainer ScrollBlock(UIElement content, Brush background, double maxHeight, Action<double>? resize = null)
        => new(new ViewportContainer
        {
            ResizeContent = resize,
            Child = new ScrollViewer
            {
                Style = null, Content = content, Background = background,
                BorderBrush = Line, BorderThickness = new Thickness(1),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                CanContentScroll = false, MaxHeight = maxHeight,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top
            }
        })
    {
        Margin = new Thickness(0, 2, 0, 11)
    };

    private static SolidColorBrush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static bool TryWebUri(string? target, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(target) || target.Length > 4096 || target.Any(char.IsControl)) return false;
        return Uri.TryCreate(target, UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && uri.Host.Length > 0;
    }

    private static Hyperlink WebLink(Uri uri)
    {
        var link = new Hyperlink { NavigateUri = uri, Foreground = Accent, ToolTip = uri.AbsoluteUri };
        link.RequestNavigate += OpenLink;
        return link;
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs args)
    {
        args.Handled = true;
        if (!TryWebUri(args.Uri?.AbsoluteUri, out var uri)) return;
        try { Process.Start(new ProcessStartInfo(uri!.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {
            if (sender is Hyperlink link) link.ToolTip = "无法打开浏览器，可复制链接：" + uri!.AbsoluteUri;
        }
    }

    private sealed class Renderer(string source)
    {
        private int _nodes;
        private int _tableCells;

        private void Visit(int depth)
        {
            if (++_nodes > MaxNodes || depth > MaxDepth) throw new RenderLimitException();
        }

        public void AddBlocks(ContainerBlock container, BlockCollection destination, int depth)
        {
            foreach (var block in container)
            {
                Visit(depth);
                switch (block)
                {
                    case HeadingBlock heading:
                    {
                        var paragraph = NewParagraph();
                        paragraph.FontSize = heading.Level switch { 1 => 24, 2 => 21, 3 => 18, 4 => 16, _ => 14 };
                        paragraph.FontWeight = FontWeights.SemiBold;
                        paragraph.LineHeight = paragraph.FontSize * 1.5;
                        paragraph.Margin = new Thickness(0, 9, 0, 9);
                        AddInlines(heading.Inline, paragraph.Inlines, depth + 1);
                        destination.Add(paragraph);
                        break;
                    }
                    case ParagraphBlock paragraphBlock:
                    {
                        var paragraph = NewParagraph();
                        AddInlines(paragraphBlock.Inline, paragraph.Inlines, depth + 1);
                        destination.Add(paragraph);
                        break;
                    }
                    case ListBlock list:
                        destination.Add(RenderList(list, depth + 1));
                        break;
                    case QuoteBlock quote:
                    {
                        var section = new Section
                        {
                            BorderBrush = Line, BorderThickness = new Thickness(3, 0, 0, 0),
                            Padding = new Thickness(12, 4, 6, 0), Margin = new Thickness(0, 3, 0, 11),
                            Foreground = Muted
                        };
                        AddBlocks(quote, section.Blocks, depth + 1);
                        destination.Add(section);
                        break;
                    }
                    case CodeBlock code:
                    {
                        if (code is FencedCodeBlock fenced && !string.IsNullOrWhiteSpace(fenced.Info))
                            destination.Add(new Paragraph(new Run(fenced.Info.Length > 80 ? fenced.Info[..80] : fenced.Info))
                            {
                                FontFamily = CodeFont, FontSize = 11, Foreground = Muted,
                                Margin = new Thickness(0, 3, 0, 0)
                            });
                        destination.Add(TextScrollBlock(code.Lines.ToString(), wrap: false));
                        break;
                    }
                    case ThematicBreakBlock:
                        destination.Add(new Paragraph
                        {
                            BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1),
                            Margin = new Thickness(0, 5, 0, 13), FontSize = 1, LineHeight = 1
                        });
                        break;
                    case MdTable table:
                        destination.Add(RenderTable(table, depth + 1));
                        break;
                    case LinkReferenceDefinitionGroup:
                        // Reference definitions are represented by their links, just as in CommonMark.
                        break;
                    case ContainerBlock nested:
                        AddBlocks(nested, destination, depth + 1);
                        break;
                    default:
                        destination.Add(new Paragraph(new Run(SourceOf(block))) { Margin = new Thickness(0, 0, 0, 9) });
                        break;
                }
            }
        }

        private WpfList RenderList(ListBlock markdown, int depth)
        {
            var list = new WpfList
            {
                MarkerStyle = markdown.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Padding = new Thickness(22, 0, 0, 0), Margin = new Thickness(0, 1, 0, 9), MarkerOffset = 5
            };
            if (markdown.IsOrdered && int.TryParse(markdown.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
                list.StartIndex = Math.Clamp(start, 1, 1_000_000);
            foreach (var block in markdown)
            {
                Visit(depth);
                if (block is not ListItemBlock item) continue;
                var target = new System.Windows.Documents.ListItem { Margin = new Thickness(0, 0, 0, markdown.IsLoose ? 5 : 1) };
                AddBlocks(item, target.Blocks, depth + 1);
                if (target.Blocks.Count == 0) target.Blocks.Add(NewParagraph());
                foreach (var paragraph in target.Blocks.OfType<Paragraph>()) paragraph.Margin = new Thickness(0, 0, 0, 3);
                list.ListItems.Add(target);
            }
            return list;
        }

        private WpfBlock RenderTable(MdTable markdown, int depth)
        {
            var columnCount = Math.Max(markdown.ColumnDefinitions.Count, markdown.OfType<MdTableRow>().Select(row => row.Count).DefaultIfEmpty(0).Max());
            if (columnCount == 0 || columnCount > 16) throw new RenderLimitException();
            var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0) };
            // Retain readable minimum column widths, then fill a larger message viewport when one is available.
            var columnWidth = columnCount <= 2 ? 240d : 180d;
            for (var index = 0; index < columnCount; index++)
                table.Columns.Add(new TableColumn { Width = new GridLength(columnWidth) });
            var rows = new TableRowGroup();
            table.RowGroups.Add(rows);
            foreach (var markdownRow in markdown.OfType<MdTableRow>())
            {
                Visit(depth);
                var row = new WpfTableRow { Background = markdownRow.IsHeader ? HeaderPaper : Brushes.Transparent };
                rows.Rows.Add(row);
                var column = 0;
                foreach (var markdownCell in markdownRow.OfType<MdTableCell>())
                {
                    Visit(depth + 1);
                    if (++_tableCells > MaxTableCells) throw new RenderLimitException();
                    var cell = new WpfTableCell
                    {
                        BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 1),
                        Padding = new Thickness(10, 7, 10, 7),
                        FontWeight = markdownRow.IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
                        ColumnSpan = Math.Clamp(markdownCell.ColumnSpan, 1, columnCount - column),
                        TextAlignment = column < markdown.ColumnDefinitions.Count
                            ? markdown.ColumnDefinitions[column].Alignment switch
                            {
                                TableColumnAlign.Center => TextAlignment.Center,
                                TableColumnAlign.Right => TextAlignment.Right,
                                _ => TextAlignment.Left
                            }
                            : TextAlignment.Left
                    };
                    AddBlocks(markdownCell, cell.Blocks, depth + 2);
                    if (cell.Blocks.Count == 0) cell.Blocks.Add(NewParagraph());
                    foreach (var paragraph in cell.Blocks.OfType<Paragraph>()) paragraph.Margin = new Thickness(0);
                    row.Cells.Add(cell);
                    column += cell.ColumnSpan;
                    if (column >= columnCount) break;
                }
            }
            var document = NewDocument();
            document.FontSize = 13;
            document.LineHeight = 22;
            document.PageWidth = columnCount * columnWidth;
            document.Blocks.Add(table);
            var view = new RichTextBox
            {
                Style = null, Document = document, IsReadOnly = true, IsDocumentEnabled = true,
                IsUndoEnabled = false, IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return ScrollBlock(view, Brushes.Transparent, 420, viewportWidth =>
            {
                var width = Math.Max(columnCount * columnWidth, viewportWidth - 2);
                view.Width = width;
                document.PageWidth = width;
                foreach (var column in table.Columns) column.Width = new GridLength(width / columnCount);
            });
        }

        private void AddInlines(ContainerInline? container, InlineCollection destination, int depth)
        {
            if (container is null) return;
            for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
            {
                Visit(depth);
                switch (inline)
                {
                    case LiteralInline literal:
                        destination.Add(new Run(literal.Content.ToString()));
                        break;
                    case HtmlEntityInline entity:
                        destination.Add(new Run(entity.Transcoded.ToString()));
                        break;
                    case CodeInline code:
                        destination.Add(new Run(code.Content) { FontFamily = CodeFont, FontSize = 13, Background = CodePaper });
                        break;
                    case LineBreakInline lineBreak:
                        if (lineBreak.IsHard) destination.Add(new LineBreak());
                        else destination.Add(new Run(" "));
                        break;
                    case EmphasisInline emphasis:
                    {
                        var span = new Span();
                        if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold;
                        if (emphasis.DelimiterCount % 2 == 1) span.FontStyle = FontStyles.Italic;
                        AddInlines(emphasis, span.Inlines, depth + 1);
                        destination.Add(span);
                        break;
                    }
                    case LinkInline link when link.IsImage:
                    {
                        var label = new Span { Foreground = Muted };
                        label.Inlines.Add(new Run("[图片："));
                        AddInlines(link, label.Inlines, depth + 1);
                        label.Inlines.Add(new Run("，未加载]"));
                        destination.Add(label);
                        break;
                    }
                    case LinkInline link:
                    {
                        Span span = TryWebUri(link.Url, out var uri) ? WebLink(uri!) : new Span();
                        AddInlines(link, span.Inlines, depth + 1);
                        if (span.Inlines.Count == 0) span.Inlines.Add(new Run(link.Url ?? ""));
                        destination.Add(span);
                        break;
                    }
                    case AutolinkInline autoLink:
                    {
                        if (TryWebUri(autoLink.Url, out var uri))
                        {
                            var link = WebLink(uri!);
                            link.Inlines.Add(new Run(autoLink.Url));
                            destination.Add(link);
                        }
                        else destination.Add(new Run(autoLink.Url));
                        break;
                    }
                    case ContainerInline nested:
                        AddInlines(nested, destination, depth + 1);
                        break;
                    default:
                        destination.Add(new Run(SourceOf(inline)));
                        break;
                }
            }
        }

        private string SourceOf(MarkdownObject node)
        {
            var start = Math.Clamp(node.Span.Start, 0, source.Length);
            var length = Math.Clamp((long)node.Span.End - start + 1, 0, source.Length - start);
            return source.Substring(start, (int)length);
        }
    }

    private sealed class RenderLimitException : Exception { }


    /// <summary>
    /// Embedded controls otherwise request their unwrapped content width during FlowDocument measurement.
    /// Clamp before measuring their internal ScrollViewer, including the first layout at a narrow window size.
    /// </summary>
    private sealed class ViewportContainer : Decorator
    {
        public Action<double>? ResizeContent { get; init; }

        public ViewportContainer()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            ClipToBounds = true;
            // ActualWidth changes after the parent's first measure. Binding makes the embedded paragraph
            // remeasure on that change and on window resize, rather than retaining its old document width.
            SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RichTextBox), 1),
                Converter = new ViewportWidthConverter()
            });
            Loaded += (_, _) => InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child is null) return new Size();
            var width = double.IsFinite(constraint.Width) ? constraint.Width : HostWidth();
            width = Math.Max(1, Math.Min(width, HostWidth()));
            ResizeContent?.Invoke(width);
            Child.Measure(new Size(width, constraint.Height));
            return new Size(width, Child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Child?.Arrange(new Rect(new Point(), arrangeSize));
            return arrangeSize;
        }

        private double HostWidth()
        {
            DependencyObject? parent = this;
            while ((parent = VisualTreeHelper.GetParent(parent)) is not null)
                if (parent is RichTextBox host && host.ActualWidth > 0)
                    return Math.Max(1, host.ActualWidth - host.Padding.Left - host.Padding.Right
                        - host.BorderThickness.Left - host.BorderThickness.Right
                        - host.Document.PagePadding.Left - host.Document.PagePadding.Right - 4);
            return 320;
        }
    }

    private sealed class ViewportWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double width && width > 0 ? Math.Max(1, width - 4) : double.PositiveInfinity;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

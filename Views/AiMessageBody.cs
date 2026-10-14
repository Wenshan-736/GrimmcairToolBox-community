using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Grimmcair.Desktop.Views;

/// <summary>Selectable Markdown with bounded refresh frequency while SSE fragments arrive.</summary>
public sealed class AiMessageBody : RichTextBox
{
    private readonly bool markdown;
    private readonly DispatcherTimer refresh;
    private string pending = "";
    private string? rendered;
    public event EventHandler? DocumentUpdating;
    public event EventHandler? DocumentUpdated;

    public AiMessageBody(bool markdown)
    {
        this.markdown = markdown;
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsUndoEnabled = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        FontSize = 14;
        MinHeight = 20;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        refresh = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
        refresh.Tick += (_, _) => Flush();
        Unloaded += (_, _) => refresh.Stop();
    }

    public void SetContent(string content, bool immediate = false)
    {
        pending = content;
        if (immediate) Flush();
        else if (!refresh.IsEnabled) refresh.Start();
    }

    public void Flush()
    {
        refresh.Stop();
        if (rendered == pending) return;
        DocumentUpdating?.Invoke(this, EventArgs.Empty);
        try { Document = markdown ? AiMarkdownRenderer.Render(pending) : PlainText(pending); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
        { Document = PlainText(pending); }
        rendered = pending;
        DocumentUpdated?.Invoke(this, EventArgs.Empty);
    }

    private static FlowDocument PlainText(string text) => new(new Paragraph(new Run(text)))
        { PagePadding = new Thickness(0), FontSize = 14, FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(74, 51, 32)) };

    internal string VisibleText => new TextRange(Document.ContentStart, Document.ContentEnd).Text;
}

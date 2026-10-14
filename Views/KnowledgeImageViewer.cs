using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

/// <summary>Package-only imagery. Model Markdown never grants access to this viewer.</summary>
internal static class KnowledgeImageViewer
{
    private static readonly Brush Paper = new SolidColorBrush(Color.FromRgb(255, 250, 239));
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(79, 53, 35));

    internal static void ShowGallery(Window owner)
    {
        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = "当前资料包配图", FontSize = 22, Foreground = Ink, Margin = new Thickness(0, 0, 0, 8) });
        try
        {
            var catalog = KnowledgeImageCatalog.Load();
            stack.Children.Add(new TextBlock { Text = "仅显示已登记且完整收录的本地图片。来源版本与图片完整性分别保留。",
                TextWrapping = TextWrapping.Wrap, Foreground = Ink, Margin = new Thickness(0, 0, 0, 14) });
            var cards = new StackPanel();
            var progress = Notice("");
            var more = new Button { Content = "加载更多配图", HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0) };
            stack.Children.Add(cards);
            stack.Children.Add(progress);
            stack.Children.Add(more);
            var loaded = 0;
            void LoadNextPage()
            {
                var references = catalog.GalleryReferences(loaded);
                foreach (var reference in references) cards.Children.Add(CreateCard(reference, catalog));
                loaded += references.Count;
                progress.Text = catalog.Count == 0 ? "当前资料包没有登记可显示的配图。" : $"已加载 {loaded} / {catalog.Count} 张登记配图。";
                more.Visibility = loaded < catalog.Count ? Visibility.Visible : Visibility.Collapsed;
            }
            more.Click += (_, _) => LoadNextPage();
            LoadNextPage();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { stack.Children.Add(Notice("当前配图登记无法读取，请检查资料包是否完整。")); }
        new Window { Owner = owner, Title = "已收录配图", Width = 860, Height = 760, MinWidth = 480, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Paper,
            Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }.Show();
    }

    internal static FrameworkElement CreateCard(KnowledgeImageReference reference, KnowledgeImageCatalog? catalog = null)
    {
        try
        {
            catalog ??= KnowledgeImageCatalog.Load();
            if (!catalog.TryResolve(reference, out var info)) return Notice("这张配图属于另一版资料包；请重新查询对应资料。");
            var bytes = catalog.Read(reference);
            var bitmap = Decode(bytes, DecodeWidth(info!, 720));
            var stack = new StackPanel();
            var title = new TextBlock { Text = info!.Title, Foreground = Ink, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) };
            stack.Children.Add(title);
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 340,
                MaxWidth = Math.Min(720, info.Width), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "点击放大 · " + info.Title };
            System.Windows.Automation.AutomationProperties.SetName(image, info.Title);
            var enlarge = new Button { Content = image, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand };
            var pinnedCatalog = catalog;
            enlarge.Click += (_, _) => ShowImage(Window.GetWindow(enlarge), reference, pinnedCatalog);
            stack.Children.Add(enlarge);
            if (info.Caption.Length > 0) stack.Children.Add(Notice(info.Caption));
            stack.Children.Add(Notice(SourceDescription(info)));
            stack.Children.Add(new TextBlock { Text = "点击图片放大", Foreground = Ink, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
            return new Border { Child = stack, Padding = new Thickness(12), BorderBrush = new SolidColorBrush(Color.FromRgb(225, 210, 180)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Background = Paper, Margin = new Thickness(0, 8, 0, 8) };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException
            or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
        { return Notice("图片未能通过当前资料包校验，已保留文字说明和对话。请重新查询资料。"); }
    }

    private static void ShowImage(Window? owner, KnowledgeImageReference reference, KnowledgeImageCatalog catalog)
    {
        try
        {
            if (!catalog.TryResolve(reference, out var info)) return;
            var image = new Image { Source = Decode(catalog.Read(reference), DecodeWidth(info!, 2400)), Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var transform = new ScaleTransform(1, 1); image.LayoutTransform = transform;
            var slider = new Slider { Minimum = 0.25, Maximum = 4, Value = 1, Width = 240, Margin = new Thickness(10, 0, 10, 0) };
            slider.ValueChanged += (_, _) => { transform.ScaleX = slider.Value; transform.ScaleY = slider.Value; };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
            controls.Children.Add(new TextBlock { Text = "缩放", VerticalAlignment = VerticalAlignment.Center, Foreground = Ink });
            controls.Children.Add(slider);
            controls.Children.Add(new TextBlock { Text = info!.Width + " × " + info.Height, VerticalAlignment = VerticalAlignment.Center, Foreground = Ink });
            if (info.SourceUrl.Length > 0)
            {
                var source = new Button { Content = "查看原来源", Margin = new Thickness(14, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
                source.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(info.SourceUrl) { UseShellExecute = true }); } catch { source.ToolTip = info.SourceUrl; } };
                controls.Children.Add(source);
            }
            var panel = new DockPanel(); DockPanel.SetDock(controls, Dock.Top); panel.Children.Add(controls);
            var sourceNotice = Notice(SourceDescription(info) + "\n" + info.Caption); sourceNotice.Margin = new Thickness(12);
            DockPanel.SetDock(sourceNotice, Dock.Bottom); panel.Children.Add(sourceNotice);
            panel.Children.Add(new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) });
            new Window { Owner = owner, Title = info.Title, Width = 1040, Height = 740, MinWidth = 480, MinHeight = 320,
                WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                Content = panel, Background = Paper }.Show();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException
            or System.Runtime.InteropServices.COMException) { }
    }

    internal static BitmapImage Decode(byte[] bytes, int maximumSide)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; bitmap.StreamSource = stream;
        // Decode a bounded preview; the zoom window explicitly requests a larger image.
        bitmap.DecodePixelWidth = maximumSide;
        bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }

    private static int DecodeWidth(KnowledgeImageInfo image, int maximumSide) => Math.Max(1,
        (int)Math.Round(image.Width * Math.Min(1d, (double)maximumSide / Math.Max(image.Width, image.Height))));

    private static string SourceDescription(KnowledgeImageInfo info) => string.Join(" · ", new[]
    {
        info.Author.Length > 0 ? "作者：" + info.Author : "作者未注明",
        info.SourceVersion is { Length: > 0 } version ? "图片来源版本：" + version : "图片未单独标注版本",
        info.SourceContextVersion is { Length: > 0 } context ? "来源帖版本：" + context : "",
        "完整收录"
    }.Where(text => text.Length > 0));

    private static TextBlock Notice(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        Foreground = Ink, FontSize = 12, Margin = new Thickness(0, 7, 0, 0), MaxWidth = 900 };
}

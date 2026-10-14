using System.IO;
using System.Windows;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class SaveRenameWindow : Window
{
    private readonly Action<string> renameSave;
    public string? RenamedName { get; private set; }
    internal string VisibleError => ErrorText.Visibility == Visibility.Visible ? ErrorText.Text : "";

    public SaveRenameWindow(string currentName, Action<string> renameSave)
    {
        this.renameSave = renameSave ?? throw new ArgumentNullException(nameof(renameSave));
        InitializeComponent();
        CurrentNameText.Text = "当前：" + currentName;
        NameInput.Text = currentName;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        MaxWidth = Math.Max(320, work.Width - 36); MaxHeight = Math.Max(290, work.Height - 36);
        MinWidth = Math.Min(MinWidth, MaxWidth); MinHeight = Math.Min(MinHeight, MaxHeight);
        Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight);
        NameInput.Focus(); NameInput.SelectAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => TryRename(closeDialog: true);

    internal bool TryRenameForAutomation(string name)
    {
        if (!App.AutomationMode) throw new InvalidOperationException("重命名测试接口仅用于隔离自动验证。");
        NameInput.Text = name;
        return TryRename(closeDialog: false);
    }

    private bool TryRename(bool closeDialog)
    {
        try
        {
            var name = SaveNamePolicy.ValidateManualName(NameInput.Text);
            renameSave(name);
            RenamedName = name; ErrorText.Text = ""; ErrorText.Visibility = Visibility.Collapsed;
            if (closeDialog) DialogResult = true;
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException)
        {
            ErrorText.Text = error.Message; ErrorText.Visibility = Visibility.Visible;
            NameInput.Focus();
            return false;
        }
    }
}

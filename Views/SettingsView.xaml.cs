using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Grimmcair.Desktop.Services;
using Microsoft.Win32;

namespace Grimmcair.Desktop.Views;

public partial class SettingsView : UserControl
{
    private readonly LocalSettingsStore _store;
    public string SettingsFilePath => _store.FilePath;
    public LocalSettings CurrentSettings { get; private set; } = LocalSettings.Suggested();
    public event EventHandler<LocalSettingsSavedEventArgs>? SettingsSaved;
    public bool IsDataImportRunning { get; private set; }

    public SettingsView() : this(App.AiPreviewMode ? Path.Combine(App.AiPreviewDirectory, "settings.json") : null) { }

    public SettingsView(string? settingsFilePath)
    {
        InitializeComponent();
        _store = new LocalSettingsStore(settingsFilePath);
        ReloadSettings();
        RefreshDataLocation();
        if (App.AiPreviewMode)
        {
            LocalSettingsSections.Visibility = Visibility.Collapsed;
            LocalSettingsActions.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshDataLocation()
    {
        var location = DesktopDataPaths.Current;
        DataLocationPanel.Visibility = App.AutomationMode || App.AiPreviewMode || location.IsIsolated ? Visibility.Collapsed : Visibility.Visible;
        DataLocationPath.Text = location.DataDirectory;
        DataLocationDescription.Text = location.IsInstalled
            ? "安装版数据保存在当前 Windows 用户的固定目录，社区版与专用版分开保存。升级或卸载程序时保留这些数据。"
            : "便携版数据保存在程序旁的 UserData 文件夹。升级时请保留整个文件夹。";
        ImportLegacyDataButton.Visibility = location.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        RevertDataButton.Visibility = Visibility.Collapsed;
        if (!location.IsInstalled) return;
        try
        {
            var state = DesktopDataImport.GetSwitchState(location);
            ImportLegacyDataButton.IsEnabled = !state.Pending && !IsDataImportRunning;
            RevertDataButton.Visibility = state.CanRevert ? Visibility.Visible : Visibility.Collapsed;
            RevertDataButton.IsEnabled = !IsDataImportRunning;
            RevertDataButton.Content = state.Pending ? "取消下次切换" : "切回上次数据";
            DataLocationStatus.Text = state.Pending
                ? "已安排下次启动使用：" + state.NextDirectory + "。当前窗口仍使用原数据，重启前新增记录也保留在原目录。"
                : "旧数据需主动选择导入。整套复制并校验后，下次启动使用；当前数据完整保留，不合并或覆盖。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { ImportLegacyDataButton.IsEnabled = false; DataLocationStatus.Text = "数据目录选择记录无法读取，原文件已保留。"; }
    }

    private async void ImportLegacyData_Click(object sender, RoutedEventArgs e)
    {
        if (IsDataImportRunning || App.AutomationMode || !DesktopDataPaths.Current.IsInstalled) return;
        var picker = new OpenFolderDialog { Title = "选择旧程序旁的 UserData 文件夹", Multiselect = false };
        var owner = Window.GetWindow(this);
        if (picker.ShowDialog(owner) != true) return;
        if (MessageBox.Show(owner, "请先正常关闭旧版工具箱。\n\n将完整复制所选 UserData 并校验，下次启动使用。"
            + "原文件和当前安装版数据都会保留，不合并、不覆盖，可在设置中切回。"
            + "\n\n仅自动调整副本中指向旧 UserData 的本地归档路径；外部游戏或存档路径保持不变。"
            + "加密密钥和聊天上下文需要原 Windows 用户才能读取。\n\n所选文件夹：\n" + picker.FolderName,
            "导入旧版数据", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        IsDataImportRunning = true;
        ImportLegacyDataButton.IsEnabled = false;
        RevertDataButton.IsEnabled = false;
        DataLocationStatus.Text = "正在复制和校验旧数据，请等待完成后再关闭窗口……";
        try
        {
            var result = await Task.Run(() => DesktopDataImport.ImportForNextStart(picker.FolderName, DesktopDataPaths.Current));
            RefreshDataLocation();
            DataLocationStatus.Text = $"已校验 {result.FileCount:N0} 个文件，调整 {result.UpdatedPaths} 处副本内路径。"
                + "重启后使用导入数据；原文件与当前数据均已保留。\n" + result.DataDirectory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { DataLocationStatus.Text = "未切换数据：" + error.Message; }
        finally
        {
            IsDataImportRunning = false;
            var status = DataLocationStatus.Text;
            RefreshDataLocation();
            DataLocationStatus.Text = status;
        }
    }

    private void RevertData_Click(object sender, RoutedEventArgs e)
    {
        if (IsDataImportRunning || App.AutomationMode) return;
        try { DesktopDataImport.RevertForNextStart(DesktopDataPaths.Current); RefreshDataLocation(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { DataLocationStatus.Text = "未切换数据：" + error.Message; }
    }

    public LocalSettings ReadEditorValues() => new()
    {
        GameDirectory = GameDirectoryBox.Text,
        SaveSourceDirectory = SaveSourceDirectoryBox.Text,
        ReplaySourceDirectory = ReplaySourceDirectoryBox.Text,
        ScreenshotDirectory = ScreenshotDirectoryBox.Text,
        SaveArchiveDirectory = SaveArchiveDirectoryBox.Text,
        ReplayArchiveDirectory = ReplayArchiveDirectoryBox.Text,
        ScreenshotHotkey = ScreenshotHotkeyBox.Text,
        LoadHotkey = LoadHotkeyBox.Text,
        SaveHotkey = SaveHotkeyBox.Text
    };

    public void SetEditorValues(LocalSettings settings)
    {
        GameDirectoryBox.Text = settings.GameDirectory;
        _lastAppliedGameDirectory = settings.GameDirectory;
        SaveSourceDirectoryBox.Text = settings.SaveSourceDirectory;
        ReplaySourceDirectoryBox.Text = settings.ReplaySourceDirectory;
        ScreenshotDirectoryBox.Text = settings.ScreenshotDirectory;
        SaveArchiveDirectoryBox.Text = settings.SaveArchiveDirectory;
        ReplayArchiveDirectoryBox.Text = settings.ReplayArchiveDirectory;
        ScreenshotHotkeyBox.Text = settings.ScreenshotHotkey;
        LoadHotkeyBox.Text = settings.LoadHotkey;
        SaveHotkeyBox.Text = settings.SaveHotkey;
        SaveSourceHint.Text = "请选择游戏写入 .txt 存档的文件夹；保存设置后检查并归档新内容，再持续监听。";
    }

    public bool TrySaveSettings(out string message)
    {
        try
        {
            var settings = ReadEditorValues().Normalize();
            if (!string.Equals(settings.SaveSourceDirectory, CurrentSettings.SaveSourceDirectory, StringComparison.OrdinalIgnoreCase)
                && settings.ValidateSaveSourceDirectory() is { } sourceError)
                throw new ArgumentException(sourceError);
            var previous = CurrentSettings;
            _store.Save(settings);
            CurrentSettings = settings;
            SetEditorValues(settings);
            message = "设置已保存到本机，正在应用存档来源与归档位置。";
            SettingsStatus.Text = message;
            SettingsSaved?.Invoke(this, new LocalSettingsSavedEventArgs(previous, settings));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            message = error.Message;
            SettingsStatus.Text = $"未保存：{message}";
            return false;
        }
    }

    public void ReloadSettings()
    {
        try
        {
            CurrentSettings = _store.Load();
            SetEditorValues(CurrentSettings);
            SettingsStatus.Text = File.Exists(_store.FilePath) ? "已读取本机设置。修改后请点击保存设置。" : "当前为建议值，点击保存设置后才会记录到本机。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            SetEditorValues(LocalSettings.Suggested());
            SettingsStatus.Text = $"无法读取本机设置，现显示建议值；原设置未修改。{error.Message}";
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string fieldName } || FindName(fieldName) is not TextBox field) return;
        var dialog = new OpenFolderDialog { Title = "选择文件夹", Multiselect = false };
        if (Directory.Exists(field.Text)) dialog.InitialDirectory = field.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            field.Text = dialog.FolderName;
            SettingsStatus.Text = "路径已修改，点击保存设置后记录到本机。";
            if (field == GameDirectoryBox) ApplyGameDirectory(dialog.FolderName);
            if (field == SaveSourceDirectoryBox)
            {
                var error = ReadEditorValues().ValidateSaveSourceDirectory();
                SaveSourceHint.Text = error ?? "这个文件夹中有 .txt 文件；保存设置后扫描并监听，只有成功解析的存档才归档。";
            }
        }
    }

    public void ApplyGameDirectory(string directory)
    {
        SetEditorValues(ReadEditorValues().WithGameDirectory(directory));
        SaveSourceHint.Text = "已按游戏目录填写 TWRPG 存档来源和 Replay 录像来源，可手动调整。";
        SettingsStatus.Text = "已补充两个来源路径，点击保存设置后应用。";
    }

    private void GameDirectory_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var path = GameDirectoryBox.Text.Trim();
        if (Directory.Exists(path) && path != _lastAppliedGameDirectory)
        { ApplyGameDirectory(path); _lastAppliedGameDirectory = path; }
    }
    private string _lastAppliedGameDirectory = "";

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox field) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift) return;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        if (modifiers == ModifierKeys.None && key is Key.Delete or Key.Back)
        {
            field.Clear();
            SettingsStatus.Text = "快捷键已清除，点击保存设置后记录为不绑定。";
            return;
        }
        if ((modifiers & ModifierKeys.Windows) != 0 || key is Key.LWin or Key.RWin)
        {
            SettingsStatus.Text = "请使用 Ctrl、Alt 或 Shift，不使用 Windows 系统组合键。";
            return;
        }
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)key - (int)Key.D0).ToString() : key.ToString());
        if (!LocalSettings.TryNormalizeHotkey(string.Join('+', parts), out var normalized, out var error))
        {
            SettingsStatus.Text = error;
            return;
        }
        field.Text = normalized;
        var errors = ReadEditorValues().Validate();
        SettingsStatus.Text = errors.Count > 0 ? string.Join(" ", errors) : "快捷键已修改，点击保存设置后记录到本机。";
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string fieldName } && FindName(fieldName) is TextBox field) field.Clear();
        SettingsStatus.Text = "快捷键已清除，点击保存设置后记录为不绑定。";
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        SetEditorValues(LocalSettings.Suggested());
        SettingsStatus.Text = "已在页面填入建议值，尚未覆盖本机设置。点击保存设置后才会记录。";
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadSettings();
    private void Save_Click(object sender, RoutedEventArgs e) => TrySaveSettings(out _);
}

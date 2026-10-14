using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private DesktopConnectionSettings _desktopConnection = new();
    private DesktopApiClient? _desktopClient;
    private DesktopApiSession? _desktopSession;
    private DesktopCloudStateStore? _desktopStateStore;
    private DesktopCloudSync? _desktopSync;
    private JsonObject? _desktopReference;
    private List<JsonObject> _remoteSaves = [], _remoteCharacters = [];
    private JsonObject? _remoteQuota;
    private readonly SemaphoreSlim _desktopWorkGate = new(1, 1);
    private readonly CancellationTokenSource _desktopLifetime = new();
    private Task _desktopVerifyLoop = Task.CompletedTask;
    private DesktopApiVerificationLoop? _desktopVerificationLoop;
    private readonly List<Task> _desktopRetiredVerifications = [];
    private CancellationTokenSource? _desktopVerificationCancellation;
    private DispatcherTimer? _desktopSyncTimer;
    private DispatcherTimer? _desktopCloudPollTimer;
    private bool _desktopConnecting, _desktopBusy, _desktopConsentConfirmed;
    private string DesktopSettingsPath => Path.Combine(Path.GetDirectoryName(_simulationPath)!, "desktop-connection.json");
    private DesktopCredentialStore DesktopCredentials => new(Path.Combine(Path.GetDirectoryName(_simulationPath)!, "desktop-login"));
    private bool DesktopOnlineMode => DesktopEdition.SupportsCloud && !App.AutomationMode && (_desktopConnection.BaseUrl.Length > 0 || !DesktopPreviewBuild);
    private bool DesktopFeaturesAllowed => !DesktopOnlineMode || _desktopSession?.IsAllowed == true;
    private const bool DesktopPreviewBuild = false;

    private void LoadDesktopConnectionSettings()
    {
        if (DesktopEdition.IsCommunity) { RefreshDesktopAuthorization(); return; }
        if (App.AutomationMode) return;
        try
        {
            _desktopConnection = DesktopConnectionSettings.Load(DesktopSettingsPath);
            if (!File.Exists(DesktopSettingsPath)) _desktopConnection.BaseUrl = "https://www.grimmcair.com.cn";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            // A broken configured connection must not silently fall back to offline authorization.
            _desktopConnection.BaseUrl = "配置读取失败";
            DesktopAuthText.Text = "云服务配置无法读取，请联系维护者。";
        }
        RefreshDesktopAuthorization();
    }

    private async Task InitializeDesktopApiAsync()
    {
        if (!DesktopEdition.SupportsCloud) { RefreshDesktopAuthorization(); return; }
        _desktopSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _desktopSyncTimer.Tick += async (_, _) =>
        {
            _desktopSyncTimer.Stop();
            await RunDesktopActionAsync(() => SyncDesktopSavesAsync(false), false);
        };
        // Cloud updates use their own quiet polling schedule, independent of hourly authorization.
        _desktopCloudPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _desktopCloudPollTimer.Tick += (_, _) => QueueDesktopSync();
        _desktopCloudPollTimer.Start();
        RefreshDesktopAuthorization();
        if (DesktopOnlineMode && _desktopSession?.IsAllowed == true)
            await RunDesktopActionAsync(ReadDesktopCloudAsync, false);
        QueueDesktopSync();
    }

    public bool RequiresDesktopStartupLogin => DesktopOnlineMode;

    public async Task<bool> AuthenticateDesktopStartupAsync()
    {
        await ConnectDesktopAsync(startup: true);
        return _desktopSession?.IsAllowed == true;
    }

    private async void DesktopLogin_Click(object sender, RoutedEventArgs e)
    {
        if ((_desktopSession?.State is DesktopSessionState.PendingVerification or DesktopSessionState.Verifying)
            && _desktopVerificationLoop is { } verification)
        {
            if (_desktopConnecting || _desktopBusy || _closing || _closed) return;
            _desktopConnecting = true;
            RefreshDesktopAuthorization();
            try { await verification.RetryNowAsync(_desktopVerificationCancellation?.Token ?? _desktopLifetime.Token); }
            catch (Exception error) when (IsDesktopError(error))
            { StatusText.Text = error is OperationCanceledException ? "连接重试已停止。" : error.Message; }
            finally { _desktopConnecting = false; RefreshDesktopAuthorization(); }
            return;
        }
        await ConnectDesktopAsync();
    }

    private async Task ConnectDesktopAsync(bool startup = false)
    {
        if (!DesktopEdition.SupportsCloud) return;
        if (_desktopConnecting || _desktopBusy || _closing || _closed) return;
        _desktopConnecting = true;
        DesktopApiClient? candidateClient = null;
        DesktopApiSession? candidate = null;
        try
        {
            Views.DesktopLoginWindow? login = null;
            var credentialWarning = "";
            login = new Views.DesktopLoginWindow(_desktopConnection.BaseUrl,
                async (string url, string username, string key, CancellationToken token) =>
                {
                    candidateClient?.Dispose();
                    candidateClient = new DesktopApiClient(url);
                    candidate = new DesktopApiSession(candidateClient);
                    await candidate.LoginAsync(username, key, token);
                    try
                    {
                        if (login!.RememberLogin) DesktopCredentials.Save(url, username, key);
                        else DesktopCredentials.Clear(url);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    { credentialWarning = "已登录，但记住登录信息未能保存，下次可能需要重新输入。"; }
                });
            try
            {
                if (_desktopConnection.BaseUrl.Length > 0 && DesktopCredentials.Load(_desktopConnection.BaseUrl) is { } remembered)
                    login.PrefillCredentials(remembered, autoSubmit: startup);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            { login.ShowRememberedLoginUnavailable(); }
            ConfigureDesktopLoginPresentation(login, startup);
            if (login.ShowDialog() != true || candidate?.IsAllowed != true || candidateClient is null) return;
            await _desktopWorkGate.WaitAsync();
            try
            {
                // Do not let a new account inherit another session's consent, queue or cached list.
                _desktopSyncTimer?.Stop();
                _desktopSync = null; _desktopStateStore = null; _desktopReference = null;
                _remoteSaves = []; _remoteCharacters = []; _remoteQuota = null; _desktopConsentConfirmed = false;
                await RetireDesktopVerification();
                _desktopClient?.Dispose();
                _desktopClient = candidateClient; candidateClient = null;
                _desktopSession = candidate;
                _desktopConnection.BaseUrl = _desktopClient.BaseUrl;
                var active = _desktopSession;
                active.Changed += () => Dispatcher.BeginInvoke(() =>
                {
                    if (_desktopSession != active) return;
                    RefreshDesktopAuthorization();
                    RefreshSimulationContext(false);
                });
                _desktopVerificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_desktopLifetime.Token);
                var verificationToken = _desktopVerificationCancellation.Token;
                _desktopVerificationLoop = new DesktopApiVerificationLoop(active, () => DesktopVerifiedAsync(active, verificationToken));
                _desktopVerifyLoop = _desktopVerificationLoop.RunAsync(verificationToken);
                if (!startup) await ReadDesktopCloudAsync();
            }
            finally { _desktopWorkGate.Release(); }
            RefreshDesktopAuthorization();
            RefreshSimulationContext(false);
            QueueDesktopSync();
            if (credentialWarning.Length > 0) StatusText.Text = credentialWarning;
        }
        catch (Exception error) when (IsDesktopError(error))
        {
            if (startup) MessageBox.Show(error.Message, "登录格林姆迦尔的妙妙工具箱·专用版", MessageBoxButton.OK, MessageBoxImage.Information);
            else ShowDesktopError(error);
        }
        finally { candidateClient?.Dispose(); _desktopConnecting = false; RefreshDesktopAuthorization(); }
    }

    private void ConfigureDesktopLoginPresentation(Views.DesktopLoginWindow login, bool startup)
    {
        // Startup has no previously shown owner. The login is its sole visible, taskbar-accessible surface.
        login.Owner = startup ? null : this;
        login.ShowInTaskbar = startup;
        login.WindowStartupLocation = startup ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
    }

    private Task DesktopVerifiedAsync(DesktopApiSession session, CancellationToken token) =>
        Dispatcher.InvokeAsync(async () =>
        {
            if (token.IsCancellationRequested || _desktopSession != session || _closing || _closed) return;
            RefreshDesktopAuthorization();
            RefreshSimulationContext(false);
            if (_desktopReference is null) await RunDesktopActionAsync(ReadDesktopCloudAsync, false);
            if (!token.IsCancellationRequested && _desktopSession == session) QueueDesktopSync();
        }, DispatcherPriority.Normal, token).Task.Unwrap();

    private void RefreshDesktopAuthorization()
    {
        if (DesktopLoginButton is null) return;
        if (DesktopEdition.IsCommunity)
        {
            MainPages.IsEnabled = true;
            DesktopAuthorizationGate.Visibility = Visibility.Collapsed;
            DesktopAccountPanel.Visibility = Visibility.Collapsed;
            DesktopAuthText.Text = "";
            DesktopLoginButton.Visibility = Visibility.Collapsed;
            DesktopLogoutButton.Visibility = Visibility.Collapsed;
            RefreshCloudButton.Visibility = Visibility.Collapsed;
            SaveManagement.Visibility = Visibility.Collapsed;
            SaveSyncBox.Visibility = Visibility.Collapsed;
            SaveSyncStatus.Visibility = Visibility.Collapsed;
            RunningModeText.Text = "本地运行 · 社区版";
            return;
        }
        MainPages.IsEnabled = DesktopFeaturesAllowed;
        DesktopAuthorizationGate.Visibility = DesktopFeaturesAllowed ? Visibility.Collapsed : Visibility.Visible;
        var sessionState = _desktopSession?.State;
        var retrying = (sessionState is DesktopSessionState.PendingVerification or DesktopSessionState.Verifying)
            && _desktopVerificationLoop is not null;
        DesktopAuthorizationMessage.Text = (_desktopSession?.Message ?? "请登录网站账户并验证专用密钥后继续使用。")
            + (sessionState == DesktopSessionState.PendingVerification && retrying ? "\n会自动重试连接，也可点击下方按钮立即重试。" : "");
        var connectionButton = retrying ? "立即重试" : sessionState is DesktopSessionState.RequiresLogin or DesktopSessionState.Denied
            ? "重新登录" : "登录 / 重新验证";
        DesktopLoginButton.Content = DesktopOnlineMode ? (_desktopSession?.IsAllowed == true ? "切换账户 / 连接" : connectionButton) : "连接云服务";
        System.Windows.Automation.AutomationProperties.SetName(DesktopLoginButton, "账户与云服务：" + DesktopLoginButton.Content);
        DesktopAuthorizationRetryButton.Content = connectionButton;
        DesktopAuthorizationRetryButton.IsEnabled = DesktopLoginButton.IsEnabled = !_desktopConnecting && !_desktopBusy
            && sessionState != DesktopSessionState.Verifying;
        DesktopAuthText.Text = !DesktopOnlineMode ? "" : _desktopSession?.IsAllowed == true
            ? $"{_desktopSession.LastResponse?.User.Nickname} · 已在线验证" : _desktopSession?.Message ?? "请登录并验证专用版密钥";
        DesktopLogoutButton.Visibility = _desktopSession is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshCloudButton.Visibility = DesktopOnlineMode ? Visibility.Visible : Visibility.Collapsed;
        SaveManagement.Header = DesktopOnlineMode ? "管理存档与云同步" : "管理存档与云同步 · 本地模拟";
        RunningModeText.Text = DesktopOnlineMode ? (_desktopSession?.IsAllowed == true ? "云服务已连接" : "待在线验证") : "本地运行 · 预览版";
        if (!DesktopFeaturesAllowed) _chatInputCancellation?.Cancel();
    }

    private async void DesktopLogout_Click(object sender, RoutedEventArgs e)
    {
        if (_desktopBusy || _desktopConnecting) return;
        await RunDesktopActionAsync(async () =>
        {
            var session = _desktopSession;
            try { if (session is not null) await session.LogoutAsync(_desktopLifetime.Token); }
            finally
            {
                try { DesktopCredentials.Clear(_desktopConnection.BaseUrl); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                { StatusText.Text = "已退出账户，但本机记住登录信息未能移除，请检查文件访问权限。"; }
                await RetireDesktopVerification();
                _desktopSession = null; _desktopClient?.Dispose(); _desktopClient = null;
                _desktopSync = null; _desktopReference = null; _remoteSaves = []; _remoteCharacters = []; _remoteQuota = null;
                _desktopConsentConfirmed = false;
            }
        });
    }

    private async Task StopDesktopApiAsync()
    {
        _desktopSyncTimer?.Stop(); _desktopCloudPollTimer?.Stop(); _desktopLifetime.Cancel();
        _ = RetireDesktopVerification();
        try { await Task.WhenAll(_desktopRetiredVerifications); }
        catch (Exception error) when (IsDesktopError(error))
        { StatusText.Text = "后台连接已停止，本地内容仍会保留。"; }
        await _desktopWorkGate.WaitAsync();
        try { _desktopClient?.Dispose(); }
        finally { _desktopWorkGate.Release(); }
    }

    private Task RetireDesktopVerification()
    {
        var cancellation = _desktopVerificationCancellation;
        var loop = _desktopVerificationLoop;
        var running = _desktopVerifyLoop;
        _desktopVerificationCancellation = null; _desktopVerificationLoop = null;
        _desktopVerifyLoop = Task.CompletedTask;
        cancellation?.Cancel();
        loop?.Dispose();
        _desktopRetiredVerifications.RemoveAll(task => task.IsCompletedSuccessfully);
        var retired = DrainDesktopVerificationAsync(running, loop, cancellation);
        _desktopRetiredVerifications.Add(retired);
        return retired;
    }

    private static async Task DrainDesktopVerificationAsync(Task running, DesktopApiVerificationLoop? loop,
        CancellationTokenSource? cancellation)
    {
        try { await running; }
        finally
        {
            try { if (loop is not null) await loop.StopAsync(); }
            finally { cancellation?.Dispose(); }
        }
    }

    private async Task<JsonObject> SendDesktopAsync(string method, string path, string? body, CancellationToken token)
    {
        if (!DesktopEdition.SupportsCloud) throw new InvalidOperationException("社区版不提供云服务。");
        var session = _desktopSession ?? throw new InvalidOperationException("请先登录云服务。");
        if (_desktopSync is not null && (_desktopSync.Account.UserId != session.UserId || _desktopSync.Account.BaseUrl != session.BaseUrl))
            throw new InvalidOperationException("云同步账户已变化，请重新登录。");
        return await session.SendAsync(method, path, body, token);
    }

    private async Task ReadDesktopCloudAsync()
    {
        if (_desktopSession?.IsAllowed != true) throw new InvalidOperationException("请先完成在线验证。");
        _desktopStateStore ??= new DesktopCloudStateStore(Path.Combine(Path.GetDirectoryName(_simulationPath)!, "desktop-cloud-state.json"));
        var token = _desktopLifetime.Token;
        if (_desktopReference is null)
        {
            var reference = await SendDesktopAsync("GET", "/api/desktop/material-needs/reference", null, token);
            _desktopSync = new DesktopCloudSync(MaterialPlanner.Load(), ResourcePack.ReadText("guide.json"), reference.ToJsonString(),
                _desktopStateStore!, _desktopClient!.BaseUrl, _desktopSession.UserId!.Value);
            _desktopReference = reference;
        }
        var saves = await SendDesktopAsync("GET", "/api/desktop/cloud-saves", null, token);
        var characters = await SendDesktopAsync("GET", "/api/desktop/characters", null, token);
        _remoteSaves = ReadObjectList(saves, "items");
        _remoteCharacters = ReadObjectList(characters, "items");
        _remoteQuota = saves["quota"]?.DeepClone().AsObject();
        RefreshRemoteCloudList();
    }

    private static List<JsonObject> ReadObjectList(JsonObject doc, string key) => doc[key] is JsonArray array
        ? array.Select(n => n?.DeepClone().AsObject() ?? throw new InvalidDataException("云服务列表格式不正确。")).ToList()
        : throw new InvalidDataException("云服务列表格式不正确。");

    private async void RefreshCloud_Click(object sender, RoutedEventArgs e) => await RunDesktopActionAsync(ReadDesktopCloudAsync);
    private void QueueDesktopSync()
    {
        if (!DesktopOnlineMode || _desktopSession?.IsAllowed != true || _desktopSync is null || _closing || _closed) return;
        _desktopSyncTimer?.Stop(); _desktopSyncTimer?.Start();
    }

    private bool RemoteSyncEnabled(string localId) => _desktopSync?.GetBinding(localId).Enabled == true;
    private JsonObject? RemoteSave(string localId)
    {
        var id = _desktopSync?.GetBinding(localId).SaveId;
        return _remoteSaves.FirstOrDefault(s => s["saveId"]?.ToString() == id);
    }
    private JsonObject? RemoteCharacter(CloudSave save) => _remoteCharacters.FirstOrDefault(c =>
        c["gameAccount"]?.ToString() == save.GameAccount && c["professionId"]?.ToString() == _desktopSync?.ProfessionId(save));

    private void RefreshRemoteCloudList()
    {
        if (!DesktopOnlineMode) return;
        var previous = (CloudCopiesBox.SelectedItem as CloudCopyChoice)?.Id;
        var sorted = _remoteSaves.OrderBy(s => s["gameAccount"]?.ToString(), StringComparer.Ordinal)
            .ThenBy(s => s["professionId"]?.ToString(), StringComparer.Ordinal)
            .ThenByDescending(s => s["isMain"]?.GetValue<bool>() == true)
            .ThenBy(s => s["saveId"]?.ToString(), StringComparer.Ordinal).ToList();
        var copies = sorted.Select(s => {
            var same = sorted.Where(other => other["gameAccount"]?.ToString() == s["gameAccount"]?.ToString()
                && other["professionId"]?.ToString() == s["professionId"]?.ToString()
                && string.Equals(other["saveName"]?.ToString(), s["saveName"]?.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
            return new CloudCopyChoice(s["saveId"]!.ToString(), RemoteChoiceLabel(s, same.Count > 1 ? same.IndexOf(s) + 1 : 0));
        }).ToList();
        CloudCopiesBox.ItemsSource = copies;
        CloudCopiesBox.SelectedItem = copies.FirstOrDefault(c => c.Id == previous) ?? copies.FirstOrDefault();
        CloudCapacityText.Text = _remoteQuota is null ? "" : $"云槽位 {_remoteQuota["used"]} / {_remoteQuota["limit"]}";
    }

    private string RemoteProfessionName(string? id) => (_desktopReference?["professions"] as JsonArray)?
        .FirstOrDefault(p => p?["id"]?.ToString() == id)?["name"]?.ToString() ?? "职业待识别";

    private void RefreshRemoteSaveControls(CloudSave? save)
    {
        var binding = save is null ? null : _desktopSync?.GetBinding(save.Id);
        SaveSyncBox.IsChecked = binding?.Enabled == true;
        SaveSyncBox.IsEnabled = save is { IsDemo: false } && _desktopSync is not null && !_desktopBusy && DesktopFeaturesAllowed;
        var cloudMain = save is null ? null : RemoteSave(save.Id);
        var roleHasAnotherMain = save is not null && cloudMain is null && RemoteCharacter(save)?["mainSaveId"] is not null;
        MainSaveBox.IsChecked = cloudMain is null ? !roleHasAnotherMain && save?.IsMain == true : cloudMain["isMain"]?.GetValue<bool>() == true;
        MainSaveBox.IsEnabled = save is { IsDemo: false } && binding?.Enabled == true && !_desktopBusy && DesktopFeaturesAllowed;
        MainSaveBox.ToolTip = cloudMain is not null && save?.IsMain == true && cloudMain["isMain"]?.GetValue<bool>() != true
            ? "当前为本地主档、云端副档。打开开关会显式转为云主档，并上报目标和爵及以上材料需求。"
            : "设为主档会上报目标装备和爵及以上材料需求，供队长查看分配。关闭则撤回公开需求，继续保存云档；目标保留在本机。第一份存档默认打开，同角色只能有一个云主档。";
        LinkCloudButton.Visibility = Visibility.Visible;
        UseRemoteSnapshotButton.Visibility = Visibility.Visible;
        LinkCloudButton.IsEnabled = save is { IsDemo: false } && _desktopSync is not null && !_desktopBusy && DesktopFeaturesAllowed;
        UseRemoteSnapshotButton.IsEnabled = save is { IsDemo: false } && RemoteSave(save.Id) is not null && !_desktopBusy && DesktopFeaturesAllowed;
        SaveSyncStatus.Visibility = binding is not null && (binding.Enabled || binding.Deleted || binding.LastError.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        var failed = binding?.LastError.Length > 0;
        var current = save is not null && binding?.AcceptedSyncEnabled == true && binding.AcceptedLocalFingerprint == _desktopSync!.GetFingerprint(save);
        SaveSyncStatus.Text = binding?.Deleted == true ? "云档已删除 · 本地保留"
            : failed ? binding?.Enabled == false ? "本机已暂停 · 云端待处理" : "更新失败"
            : !DesktopFeaturesAllowed ? "待在线验证" : current ? "已更新" : "等待更新";
        SaveSyncStatus.Foreground = (Brush)new BrushConverter().ConvertFromString(failed ? "#B2372D" : current ? "#347344" : "#806247")!;
        SaveSyncStatus.ToolTip = binding?.Deleted == true ? "旧云档已删除，本地存档与历史保留。重新开启云同步会建立新的云端关联。"
            : failed ? binding!.LastError : binding?.LastSyncAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        SaveSyncBox.ToolTip = "开启后，本地变化自动上传；云端更新会在后台取回到这份存档。历史版本只保留在本机，云图标只表示已开启。";
        var cloud = save is null ? null : RemoteSave(save.Id);
        SaveNote.Text = cloud is null ? "属性显示存档中的基础分配。多台电脑请关联同一个云档。"
            : $"当前关联：{cloud["saveName"]}（{(cloud["isMain"]?.GetValue<bool>() == true ? "云主档" : "云副档")}）。更新只检查当前所选存档。";
    }

    private static bool IsDesktopError(Exception e) => e is DesktopApiException or InvalidOperationException or InvalidDataException
        or IOException or UnauthorizedAccessException or JsonException or ArgumentException or OperationCanceledException;
    private void ShowDesktopError(Exception error)
    {
        var message = error is OperationCanceledException ? "操作已停止，本地存档仍然保留。" : error.Message;
        StatusText.Text = message;
        if (!App.AutomationMode && !_closing && !_closed) MessageBox.Show(this, message, "云存档", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async Task RunDesktopActionAsync(Func<Task> action, bool showError = true)
    {
        if (!DesktopEdition.SupportsCloud) return;
        if (_closed || _closing) return;
        if (!await _desktopWorkGate.WaitAsync(0)) { if (!showError) QueueDesktopSync(); return; }
        _desktopBusy = true;
        try { await action(); }
        catch (Exception error) when (IsDesktopError(error))
        {
            if (showError) ShowDesktopError(error);
            else StatusText.Text = error is OperationCanceledException ? "云更新已暂停。" : error.Message;
        }
        finally
        {
            _desktopBusy = false; _desktopWorkGate.Release();
            RefreshDesktopAuthorization(); RefreshSimulationContext(false);
        }
    }

    private async Task<bool> ConfirmRemoteSharingAsync()
    {
        var notice = await SendDesktopAsync("GET", "/api/desktop/cloud-consent", null, _desktopLifetime.Token);
        _desktopConsentConfirmed = notice["confirmed"]?.GetValue<bool>() == true;
        if (_desktopConsentConfirmed) return true;
        var message = notice["message"]?.GetValue<string>();
        var version = notice["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("同步告知读取不完整，请稍后重试。");
        if (new Views.DesktopConsentWindow(message) { Owner = this }.ShowDialog() != true) return false;
        var result = await SendDesktopAsync("POST", "/api/desktop/cloud-consent",
            new JsonObject { ["version"] = version, ["confirmed"] = true }.ToJsonString(), _desktopLifetime.Token);
        _desktopConsentConfirmed = result["confirmed"]?.GetValue<bool>() == true && result["version"]?.ToString() == version;
        return _desktopConsentConfirmed;
    }
}

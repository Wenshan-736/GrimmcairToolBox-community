using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop.Views;

public partial class DesktopLoginWindow : Window
{
    private readonly Func<string, string, string, CancellationToken, Task> loginAsync;
    private readonly string configuredBaseUrl;
    private readonly bool cloudConfigurationReady;
    private const string ConfigurationUnavailableMessage = "云服务配置尚未就绪，请联系维护者。";
    private CancellationTokenSource? attemptCancellation;
    private bool busy;
    private bool closing;
    private bool autoSubmit;
    private bool contentRendered;

    /// <summary>
    /// The callback must keep a newly created session provisional until ShowDialog returns true.
    /// This overload supports existing callers; the token overload also cancels an in-flight request on close.
    /// </summary>
    public DesktopLoginWindow(string initialBaseUrl, Func<string, string, string, Task> loginAsync)
        : this(initialBaseUrl, WithoutCancellation(loginAsync)) { }

    public DesktopLoginWindow(string initialBaseUrl,
        Func<string, string, string, CancellationToken, Task> loginAsync)
    {
        ArgumentNullException.ThrowIfNull(loginAsync);
        this.loginAsync = loginAsync;
        configuredBaseUrl = initialBaseUrl?.Trim() ?? "";
        try
        {
            DesktopApiClient.NormalizeBaseUrl(configuredBaseUrl);
            cloudConfigurationReady = true;
        }
        catch (DesktopApiException) { cloudConfigurationReady = false; }
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Loaded += (_, _) =>
        {
            if (!cloudConfigurationReady) ShowError(ConfigurationUnavailableMessage);
            UsernameInput.Focus();
        };
        ContentRendered += async (_, _) =>
        {
            if (contentRendered) return;
            contentRendered = true;
            if (autoSubmit) await SubmitAsync();
        };
    }

    public string BaseUrl => configuredBaseUrl;
    public string Username => UsernameInput.Text.Trim();
    public string Key => DesktopKeyInput.Password;
    public bool RememberLogin => RememberLoginInput.IsChecked == true;
    internal bool IsBusy => busy;
    internal string ErrorMessage => ErrorText.Text;

    public void PrefillCredentials(DesktopRememberedCredentials credentials, bool autoSubmit = false)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (busy || closing) return;
        UsernameInput.Text = credentials.Username;
        DesktopKeyInput.Password = credentials.Key;
        RememberLoginInput.IsChecked = true;
        this.autoSubmit = autoSubmit;
    }

    public void ShowRememberedLoginUnavailable() => ShowError("记住的登录资料暂时无法读取，请重新填写用户名和专用密钥。");

    private static Func<string, string, string, CancellationToken, Task> WithoutCancellation(
        Func<string, string, string, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return (baseUrl, username, key, _) => callback(baseUrl, username, key);
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await SubmitAsync();

    internal async Task SubmitAsync()
    {
        if (busy || closing) return;
        ErrorText.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
        if (!cloudConfigurationReady)
        {
            ShowError(ConfigurationUnavailableMessage); return;
        }
        if (string.IsNullOrWhiteSpace(UsernameInput.Text))
        {
            ShowError("请填写网站账户。"); UsernameInput.Focus(); return;
        }
        if (!HasSecret(DesktopKeyInput))
        {
            ShowError("请填写专用密钥。"); DesktopKeyInput.Focus(); return;
        }
        using var cancellation = new CancellationTokenSource();
        attemptCancellation = cancellation;
        SetBusy(true);
        try
        {
            // The callback verifies online first, then may persist the entered pair with DPAPI if RememberLogin is checked.
            await loginAsync(BaseUrl, UsernameInput.Text.Trim(), DesktopKeyInput.Password, cancellation.Token);
            if (closing || cancellation.IsCancellationRequested) return;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (closing || cancellation.IsCancellationRequested) { }
        catch (DesktopApiException error)
        {
            if (closing) return;
            ShowError(error.Code switch
            {
                "invalid_base_url" or "invalid_path" => ConfigurationUnavailableMessage,
                "otp_required" or "otp_invalid" => "云服务版本需要更新，请联系维护者。",
                _ => error.Message
            });
        }
        catch (Exception)
        {
            if (!closing) ShowError("登录未完成，请检查网络后重试。");
        }
        finally
        {
            attemptCancellation = null;
            if (!closing)
            {
                SetBusy(false);
            }
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        LoginFields.IsEnabled = !value;
        LoginButton.IsEnabled = !value;
        LoginButton.Content = value ? "正在登录…" : "登录";
        BusyText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool HasSecret(PasswordBox input)
    {
        using var secret = input.SecurePassword;
        return secret.Length > 0;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        ErrorText.BringIntoView();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        closing = true;
        try { attemptCancellation?.Cancel(); }
        catch (AggregateException) { /* A cancellation callback must not prevent closing or expose request details. */ }
        DesktopKeyInput.Clear();
    }
}

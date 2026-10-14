using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class App : Application
{
    private static int _fatalNoticeShown;
    private static readonly bool _suppressDiagnosticUi;
    private static readonly Exception? _dataPathFailure;

    static App()
    {
        // Runs before generated InitializeComponent and before ordinary window creation.
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _suppressDiagnosticUi = arguments.Any(IsDiagnosticAutomationArgument);
        try { DesktopDataPaths.Initialize(arguments); DesktopInstallationSession.HoldForCurrentProcess(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { _dataPathFailure = error; }
        var directory = IsolatedDiagnosticDirectory(arguments);
        if (_dataPathFailure is not null) directory ??= Path.Combine(Path.GetTempPath(), "Grimmcair", "StartupLogs");
        DesktopDiagnostics.Initialize(directory);
        DesktopDiagnostics.Mark(DesktopDiagnosticStage.AppConstructing);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error)
                DesktopDiagnostics.ReportException(DesktopDiagnosticStage.AppDomainUnhandledException, error);
            if (args.IsTerminating) ShowFatalDiagnosticNotice();
        };
    }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        DesktopDiagnostics.Mark(DesktopDiagnosticStage.AppConstructed);
    }

    public static bool AutomationMode { get; private set; }
    public static bool AiPreviewMode { get; private set; }
    private static string? _aiPreviewDirectory = null;
    public static string AiPreviewDirectory => _aiPreviewDirectory ?? Path.Combine(AppContext.BaseDirectory, "AiPreviewData");
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    protected override async void OnStartup(StartupEventArgs e)
    {
        DesktopDiagnostics.Mark(DesktopDiagnosticStage.StartupEntered);
        base.OnStartup(e);
        var arguments = e.Args;
        try
        {
            if (_dataPathFailure is not null) throw new InvalidDataException("用户数据位置无法确认，原数据保持不变。", _dataPathFailure);
            arguments = KnowledgePackSelection.Initialize(arguments);
            if (arguments is ["--ai-knowledge-mcp"])
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                await AiKnowledgeMcp.RunServerAsync();
                Shutdown(0);
                return;
            }
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.MainWindowConstructing);
            var window = new MainWindow();
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.MainWindowConstructed);
            MainWindow = window;
            if (window.RequiresDesktopStartupLogin)
            {
                // Cancelling the only visible login window must not accidentally show the main interface.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DesktopDiagnostics.Mark(DesktopDiagnosticStage.AuthenticationStarted);
                if (!await window.AuthenticateDesktopStartupAsync())
                {
                    DesktopDiagnostics.Mark(DesktopDiagnosticStage.ShutdownRequested);
                    window.Close();
                    Shutdown(0);
                    return;
                }
                DesktopDiagnostics.Mark(DesktopDiagnosticStage.AuthenticationCompleted);
                ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.MainWindowShowing);
            window.Show();
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.MainWindowShown);
            if (DesktopEdition.SupportsCloud && arguments.Contains("--cloud-demo"))
            {
                await window.InitializeAsync();
                window.OpenLocalSimulationDemo();
            }
            else if (arguments.Length > 0)
            {
                await window.InitializeAsync();
                await window.ImportFilesAsync(arguments.Where(File.Exists));
            }
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.StartupCompleted);
        }
        catch (Exception error)
        {
            DesktopDiagnostics.ReportException(DesktopDiagnosticStage.StartupFailed, error);
            if (e.Args.Contains("--ai-knowledge-mcp"))
            {
                Shutdown(1);
                return;
            }
            ShowFatalDiagnosticNotice();
            DesktopDiagnostics.Mark(DesktopDiagnosticStage.ShutdownRequested);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DesktopDiagnostics.Mark(DesktopDiagnosticStage.Exiting);
        DesktopDiagnostics.ReportExit(e.ApplicationExitCode);
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        DesktopDiagnostics.ReportException(DesktopDiagnosticStage.DispatcherUnhandledException, args.Exception);
        ShowFatalDiagnosticNotice();
        // Do not set Handled: an unexpected fatal UI fault must not continue in an unknown state.
    }

    private static void ShowFatalDiagnosticNotice()
    {
        if (_suppressDiagnosticUi || Interlocked.Exchange(ref _fatalNoticeShown, 1) != 0) return;
        try
        {
            var location = DesktopDiagnostics.LogDirectory;
            MessageBox.Show("程序遇到错误，无法继续运行。请重新启动，并将诊断日志提供给维护者。\n\n"
                + (location is null ? "本次未能写入日志。可尝试使用诊断启动脚本检查启动环境。" : "诊断日志位置：\n" + location),
                "格林姆迦尔的妙妙工具箱", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* Showing a diagnostic must not itself become another unhandled exception. */ }
    }

    private static bool IsDiagnosticAutomationArgument(string value) => value == "--ai-knowledge-mcp";

    private static string? IsolatedDiagnosticDirectory(string[] arguments)
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable(DesktopDiagnostics.DirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)) return Path.GetFullPath(configured);
        }
        catch { }
        return null;
    }
}

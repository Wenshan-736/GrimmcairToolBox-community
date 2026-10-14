using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Grimmcair.Desktop.Services;

/// <summary>Fixed event names prevent callers from accidentally logging user input or credentials.</summary>
public enum DesktopDiagnosticStage
{
    AppConstructing, AppConstructed, StartupEntered, MainWindowConstructing, MainWindowConstructed,
    AuthenticationStarted, AuthenticationCompleted, MainWindowShowing, MainWindowShown,
    InitializationStarted, ResourcesLoaded, SettingsLoaded, LocalStateLoading, LocalStateLoaded, ArchivesLoaded, CloudInitializationStarted,
    InitializationCompleted, StartupCompleted, ShutdownRequested, Exiting,
    StartupFailed, DispatcherUnhandledException, AppDomainUnhandledException
}

/// <summary>Best-effort local diagnostics. No message, URL, argument, file content or account data is accepted.</summary>
public static class DesktopDiagnostics
{
    public const string DirectoryEnvironmentVariable = "GRIMMCAIR_DIAGNOSTICS_DIRECTORY";
    private static readonly object Gate = new();
    private static DesktopDiagnosticLog? _log;
    private static bool _initialized;

    public static string? LogDirectory
    {
        get { try { return _log?.DirectoryPath; } catch { return null; } }
    }

    /// <summary>An explicit directory isolates automated checks; it never falls back to real user storage.</summary>
    public static void Initialize(string? isolatedDirectory = null)
    {
        try
        {
            lock (Gate)
            {
                if (_initialized) return;
                _initialized = true;
                var primary = isolatedDirectory ?? Path.Combine(DesktopDataPaths.Current.DataDirectory, "logs");
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var fallback = isolatedDirectory is null && !DesktopDataPaths.Current.IsIsolated && Path.IsPathFullyQualified(localAppData)
                    ? Path.Combine(localAppData, "Grimmcair", DesktopDataPaths.Current.Edition, "logs") : null;
                _log = new DesktopDiagnosticLog(primary, fallback);
                _log.WriteEnvironment();
            }
        }
        catch { /* Diagnostics must never prevent application startup. */ }
    }

    public static void Mark(DesktopDiagnosticStage stage)
    {
        try { _log?.WriteStage(stage); } catch { }
    }

    public static void ReportException(DesktopDiagnosticStage stage, Exception error)
    {
        try { _log?.WriteException(stage, error); } catch { }
    }

    public static void ReportExit(int code)
    {
        try { _log?.WriteExit(code); } catch { }
    }

    /// <summary>Also used by isolated automation failure artifacts; never reads Message, Data, or ToString().</summary>
    internal static string SafeExceptionSummary(Exception error) => DesktopDiagnosticLog.DescribeException(error);
}

/// <summary>Injectable directories keep checks independent of App, WPF and the user's actual UserData.</summary>
internal sealed class DesktopDiagnosticLog
{
    internal const int DefaultMaximumBytes = 256 * 1024;
    internal const int RetainedFileCount = 3;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string?[] _directories;
    private readonly int _maximumBytes;
    private readonly string _session = Guid.NewGuid().ToString("N")[..12];
    private int _nextDirectory;
    private DesktopDiagnosticStage _lastStage = DesktopDiagnosticStage.AppConstructing;
    internal string? DirectoryPath { get; private set; }

    internal DesktopDiagnosticLog(string primaryDirectory, string? fallbackDirectory, int maximumBytes = DefaultMaximumBytes)
    {
        _directories = [primaryDirectory, fallbackDirectory];
        _maximumBytes = Math.Clamp(maximumBytes, 4096, 16 * 1024 * 1024);
    }

    internal bool WriteEnvironment()
    {
        try
        {
            var version = (Assembly.GetEntryAssembly() ?? typeof(DesktopDiagnostics).Assembly).GetName().Version?.ToString() ?? "unknown";
            return Write("environment", "version=" + version + " os=" + Environment.OSVersion.Platform
                + " osVersion=" + Environment.OSVersion.Version + " osArchitecture=" + RuntimeInformation.OSArchitecture
                + " processArchitecture=" + RuntimeInformation.ProcessArchitecture + " runtime=" + Environment.Version);
        }
        catch { return false; }
    }

    internal bool WriteStage(DesktopDiagnosticStage stage)
    {
        try
        {
            lock (_gate)
            {
                _lastStage = Enum.IsDefined(stage) ? stage : DesktopDiagnosticStage.StartupFailed;
                return Write("stage", "name=" + _lastStage);
            }
        }
        catch { return false; }
    }

    internal bool WriteException(DesktopDiagnosticStage stage, Exception error)
    {
        try
        {
            var eventStage = Enum.IsDefined(stage) ? stage : DesktopDiagnosticStage.StartupFailed;
            return Write("exception", "at=" + eventStage + " lastStage=" + _lastStage + " " + DescribeException(error));
        }
        catch { return false; }
    }

    internal bool WriteExit(int code) => Write("exit", "code=" + code.ToString(CultureInfo.InvariantCulture));

    private bool Write(string kind, string safeDetails)
    {
        try
        {
            lock (_gate)
            {
                var line = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    + " session=" + _session + " elapsedMs=" + _clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                    + " event=" + kind + " " + safeDetails;
                // Every emitted field is ASCII metadata. Bound individual records as well as whole files.
                var limit = Math.Min(8192, _maximumBytes - 128);
                if (line.Length > limit) line = line[..(limit - 15)] + " [truncated]";
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                for (; _nextDirectory < _directories.Length; _nextDirectory++)
                {
                    if (_directories[_nextDirectory] is not { Length: > 0 } directory) continue;
                    try
                    {
                        var full = Path.GetFullPath(directory);
                        if (!Path.IsPathFullyQualified(directory)) continue;
                        RejectReparseAncestors(full);
                        Directory.CreateDirectory(full);
                        RejectReparseAncestors(full);
                        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())));
                        using var mutex = new Mutex(false, "Local\\Grimmcair.DesktopDiagnostics." + digest);
                        var acquired = false;
                        try
                        {
                            try { acquired = mutex.WaitOne(80); }
                            catch (AbandonedMutexException) { acquired = true; }
                            if (!acquired) return false; // Never block the UI behind another application's log write.
                            var current = Path.Combine(full, "desktop.log");
                            RejectReparseFile(current);
                            if (File.Exists(current) && new FileInfo(current).Length + bytes.Length > _maximumBytes)
                                Rotate(full);
                            using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
                            stream.Write(bytes);
                            stream.Flush();
                            DirectoryPath = full;
                            return true;
                        }
                        finally { if (acquired) mutex.ReleaseMutex(); }
                    }
                    catch { DirectoryPath = null; }
                }
                return false;
            }
        }
        catch { return false; }
    }

    private static void Rotate(string directory)
    {
        var paths = new[] { "desktop.log", "desktop.1.log", "desktop.2.log" }.Select(name => Path.Combine(directory, name)).ToArray();
        foreach (var path in paths) RejectReparseFile(path);
        if (File.Exists(paths[2])) File.Delete(paths[2]);
        if (File.Exists(paths[1])) File.Move(paths[1], paths[2]);
        if (File.Exists(paths[0])) File.Move(paths[0], paths[1]);
    }

    private static void RejectReparseFile(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unsafe diagnostics target.");
    }

    private static void RejectReparseAncestors(string directory)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unsafe diagnostics directory.");
    }

    internal static string DescribeException(Exception error)
    {
        try
        {
            var text = new StringBuilder();
            Exception? current = error;
            for (var inner = 0; current is not null && inner < 4; inner++, current = current.InnerException)
            {
                if (inner > 0) text.Append(" inner=");
                text.Append("type=").Append(SafeType(current.GetType()))
                    .Append(" hresult=0x").Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture));
                var frames = new StackTrace(current, fNeedFileInfo: false).GetFrames();
                if (frames is null) continue;
                text.Append(" stack=");
                foreach (var frame in frames.Take(24))
                {
                    var method = frame.GetMethod();
                    var declaringType = method?.DeclaringType;
                    if (method is null || declaringType is null || !TrustedAssembly(declaringType.Assembly))
                    { text.Append("external;"); continue; }
                    text.Append(SafeType(declaringType)).Append("::").Append(Identifier(method.Name)).Append(';');
                }
            }
            return text.Length <= 7000 ? text.ToString() : text.ToString(0, 6980) + " [truncated]";
        }
        catch { return "type=UnavailableExceptionDetails"; }
    }

    private static string SafeType(Type type)
    {
        if (!TrustedAssembly(type.Assembly)) return "ExternalExceptionOrFrame";
        if (type.IsGenericType) type = type.GetGenericTypeDefinition();
        return Identifier(type.FullName ?? type.Name);
    }

    private static bool TrustedAssembly(Assembly assembly)
    {
        if (assembly == typeof(DesktopDiagnostics).Assembly || assembly == typeof(Exception).Assembly) return true;
        // Other trusted framework frames must also carry Microsoft's framework public-key token.
        var name = assembly.GetName();
        var token = Convert.ToHexString(name.GetPublicKeyToken() ?? []);
        return (name.Name?.StartsWith("System.", StringComparison.Ordinal) == true
                || name.Name?.StartsWith("Microsoft.", StringComparison.Ordinal) == true
                || name.Name is "WindowsBase" or "PresentationCore" or "PresentationFramework")
            && token is "B03F5F7F11D50A3A" or "B77A5C561934E089" or "31BF3856AD364E35" or "CC7B13FFCD2DDD51";
    }

    private static string Identifier(string value) => new(value.Take(200)
        .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '`' ? character : '_').ToArray());
}

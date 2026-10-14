using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Grimmcair.Desktop.Views;

namespace Grimmcair.Desktop.Services;

/// <summary>One short-lived UI-thread notice; repeated saves refresh it instead of stacking windows.</summary>
public sealed class ArchiveNotificationService(Dispatcher dispatcher, TimeSpan? duration = null) : IDisposable
{
    private ArchiveToastWindow? _current;
    private bool _disposed;
    internal ArchiveToastWindow? Current => _current;
    public bool ShowScreenshot(string saveName, string archivePath) => Show(ArchiveNotificationText.Screenshot(saveName, archivePath));
    public bool ShowReplays(IReadOnlyList<ReplayArchiveResult> archives) => ArchiveNotificationText.Replays(archives) is { } notice && Show(notice);
    public bool Show(string title, string message) => Show(new ArchiveNotice(title, message));

    private bool Show(ArchiveNotice notice)
    {
        dispatcher.VerifyAccess();
        if (_disposed || dispatcher.HasShutdownStarted) return false;
        try
        {
            if (_current is null)
            {
                var toast = new ArchiveToastWindow(duration ?? TimeSpan.FromSeconds(4));
                toast.Closed += (_, _) => { if (ReferenceEquals(_current, toast)) _current = null; };
                _current = toast;
            }
            _current.Present(notice.Title, notice.Message);
            return true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // Saving already succeeded. A cosmetic notification failure must not
            // misreport the file as unsaved or alter replay persistence/retry state.
            _current?.Close(); _current = null;
            return false;
        }
    }

    public void PrepareForScreenshot()
    {
        dispatcher.VerifyAccess();
        if (_current is null) return;
        _current.Close(); _current = null;
        // Foreground capture uses the composed desktop. Wait for removal of our
        // previous notice so repeated screenshots do not photograph that notice.
        DwmFlush();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _current?.Close(); _current = null;
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Grimmcair.Desktop.Views;

/// <summary>A transient, non-activating notice on the foreground monitor's work area.</summary>
public partial class ArchiveToastWindow : Window
{
    private const long NoActivate = 0x08000000, ToolWindow = 0x00000080, AppWindow = 0x00040000;
    private readonly DispatcherTimer _dismiss;
    private IntPtr _monitor;
    private HwndSource? _source;
    private bool _positioning;
    internal string MessageText => NoticeMessage.Text;
    internal string HeadingText => NoticeTitle.Text;
    internal bool HasNoActivateStyle => (GetWindowLongPtr(new WindowInteropHelper(this).Handle, -20).ToInt64() & NoActivate) != 0;
    internal bool HasToolWindowStyle => (GetWindowLongPtr(new WindowInteropHelper(this).Handle, -20).ToInt64() & ToolWindow) != 0;
    internal bool IsInsideWorkArea
    {
        get
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            return GetMonitorInfo(_monitor, ref info) && GetWindowRect(new WindowInteropHelper(this).Handle, out var rect)
                && rect.Right <= info.Work.Right && rect.Bottom <= info.Work.Bottom && rect.Left >= info.Work.Left && rect.Top >= info.Work.Top;
        }
    }

    public ArchiveToastWindow(TimeSpan duration)
    {
        InitializeComponent();
        _dismiss = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = duration };
        _dismiss.Tick += (_, _) => Close();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var styles = GetWindowLongPtr(handle, -20).ToInt64();
            SetWindowLongPtr(handle, -20, new IntPtr((styles | NoActivate | ToolWindow) & ~AppWindow));
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowMessage);
            PositionAtCorner();
        };
        SizeChanged += (_, _) => PositionAtCorner();
        ContentRendered += (_, _) => PositionAtCorner();
        Closed += (_, _) => { _dismiss.Stop(); _source?.RemoveHook(WindowMessage); _source = null; };
    }

    public void Present(string title, string message)
    {
        _dismiss.Stop();
        _monitor = MonitorFromWindow(GetForegroundWindow(), 2); // nearest monitor, even with a minimized app
        NoticeTitle.Text = title; NoticeMessage.Text = message;
        if (!IsVisible) Show();
        PositionAtCorner();
        _dismiss.Start();
    }

    private void PositionAtCorner()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_positioning || handle == IntPtr.Zero || _monitor == IntPtr.Zero) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(_monitor, ref info) || !GetWindowRect(handle, out var rect)) return;
        _positioning = true;
        try
        {
            var gap = (int)Math.Round(16 * Math.Max(96u, GetDpiForWindow(handle)) / 96d);
            var x = Math.Max(info.Work.Left, info.Work.Right - (rect.Right - rect.Left) - gap);
            var y = Math.Max(info.Work.Top, info.Work.Bottom - (rect.Bottom - rect.Top) - gap);
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // no size/z-order/activation change
        }
        finally { _positioning = false; }
    }

    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
        if (message == 0x0084) { handled = true; return new IntPtr(-1); } // HTTRANSPARENT: no click target
        if (message == 0x02E0) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionAtCorner)); // DPI changed
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

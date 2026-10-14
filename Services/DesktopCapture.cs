using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Grimmcair.Desktop.Services;

/// <summary>Native foreground-window PNG capture, invoked only by the user's screenshot action.</summary>
public static class DesktopCapture
{
    public static byte[] CaptureForegroundPngBytes()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || IsIconic(window)) throw new InvalidOperationException("当前没有可截图的前台窗口。");
        var oldDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr screenDc = IntPtr.Zero, memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, previousObject = IntPtr.Zero;
        try
        {
            if (!GetWindowRect(window, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取窗口范围。");
            // DWM bounds exclude the invisible resize margins. If unavailable, the normal rect works.
            if (DwmGetWindowAttribute(window, 9, out var frameBounds, Marshal.SizeOf<NativeRect>()) == 0)
                bounds = frameBounds;
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("当前窗口没有可截图的可见区域。");
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取屏幕画面。");
            memoryDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建截图画面。");
            previousObject = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, bounds.Left, bounds.Top, 0x00CC0020 | 0x40000000))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法复制窗口画面。");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previousObject);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            if (oldDpiContext != IntPtr.Zero) SetThreadDpiAwarenessContext(oldDpiContext);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
}

/// <summary>A single screenshot hotkey belonging to the existing WPF window.</summary>
public sealed class DesktopScreenshotHotkey : IDisposable
{
    private const int HotkeyId = 0x47C1;
    private readonly Action _onScreenshot;
    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    /// <summary>Construct after the window has received SourceInitialized.</summary>
    public DesktopScreenshotHotkey(Window window, Action onScreenshot)
    {
        _onScreenshot = onScreenshot;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) throw new InvalidOperationException("截图快捷键需要在窗口创建后注册。");
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("无法访问窗口快捷键消息。");
        _source.AddHook(WindowMessage);
    }

    /// <returns>Null on success; a visible explanation if the configured hotkey is unavailable.</returns>
    public string? Configure(string? hotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!LocalSettings.TryNormalizeHotkey(hotkey, out var normalized, out var error)) return error;
        if (_registered) { UnregisterHotKey(_source.Handle, HotkeyId); _registered = false; }
        if (normalized.Length == 0) return null;
        uint modifiers = 0x4000; // MOD_NOREPEAT: holding a key creates only one image.
        var parts = normalized.Split('+');
        foreach (var modifier in parts[..^1]) modifiers |= modifier switch { "Alt" => 1u, "Ctrl" => 2u, "Shift" => 4u, _ => 0u };
        var key = parts[^1];
        var virtualKey = LocalSettings.HotkeyVirtualKey(key);
        if (!RegisterHotKey(_source.Handle, HotkeyId, modifiers, virtualKey))
            return $"截图快捷键 {normalized} 无法注册，可能已被其他程序占用。请在设置中更换组合。";
        _registered = true;
        return null;
    }

    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _onScreenshot();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered) UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(WindowMessage);
        _registered = false;
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr window, int id);
}

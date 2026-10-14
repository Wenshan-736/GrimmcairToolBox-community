using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Grimmcair.Desktop.Services;

public sealed class DesktopChatHotkeys : IDisposable
{
    private readonly HwndSource _source;
    private readonly Action<bool> _pressed;
    private readonly HashSet<int> _registered = [];
    private bool _disposed;
    public DesktopChatHotkeys(Window window, Action<bool> pressed)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("窗口尚未就绪。");
        _pressed = pressed;
        _source.AddHook(WindowMessage);
    }
    public string? Configure(string? load, string? save)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Suspend();
        var errors = new List<string>();
        foreach (var (id, name, value) in new[] { (0x47C2, "下档", load), (0x47C3, "存档", save) })
        {
            if (!LocalSettings.TryNormalizeHotkey(value, out var hotkey, out var error)) { errors.Add(error); continue; }
            if (hotkey.Length == 0) continue;
            var parts = hotkey.Split('+');
            uint modifiers = 0x4000;
            foreach (var part in parts[..^1]) modifiers |= part switch { "Alt" => 1u, "Ctrl" => 2u, "Shift" => 4u, _ => 0u };
            if (RegisterHotKey(_source.Handle, id, modifiers, LocalSettings.HotkeyVirtualKey(parts[^1]))) _registered.Add(id);
            else errors.Add($"{name}快捷键 {hotkey} 被占用，请在设置中更换。");
        }
        return errors.Count == 0 ? null : string.Join("\n", errors);
    }
    public void Suspend()
    {
        foreach (var id in _registered) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }
    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && _registered.Contains(wParam.ToInt32()))
        { handled = true; _pressed(wParam.ToInt32() == 0x47C2); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (_disposed) return;
        Suspend(); _source.RemoveHook(WindowMessage); _disposed = true;
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr window, int id);
}

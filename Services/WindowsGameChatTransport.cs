using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace Grimmcair.Desktop.Services;

public sealed class WindowsGameChatTransport : IGameChatTransport, IDisposable
{
    private readonly IntPtr _target;
    private readonly IDataObject? _previousClipboard;
    private readonly uint _originalClipboardSequence;
    private uint? _ourClipboardSequence;
    private bool _disposed;

    public WindowsGameChatTransport(IntPtr assistantWindow)
    {
        _target = GetForegroundWindow();
        GetWindowThreadProcessId(_target, out var processId);
        if (_target == IntPtr.Zero || _target == assistantWindow || processId == Environment.ProcessId)
            throw new InvalidOperationException("请先切到游戏窗口，再按下档或存档快捷键。");
        _originalClipboardSequence = GetClipboardSequenceNumber();
        _previousClipboard = Clipboard.GetDataObject();
        if (GetClipboardSequenceNumber() != _originalClipboardSequence)
            throw new InvalidOperationException("剪贴板正在更新，本次输入已取消。");
    }

    public void EnsureTarget()
    {
        if (_disposed || GetForegroundWindow() != _target)
            throw new InvalidOperationException("窗口已切换，已停止剩余输入。请关闭游戏聊天输入框后再试。");
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
            throw new InvalidOperationException("检测到新的修饰键输入，本次游戏输入已停止。请松开按键并关闭聊天输入框后再试。");
    }
    public Task WaitAsync(int milliseconds, CancellationToken cancellation) => Task.Delay(milliseconds, cancellation);
    public void SetClipboardText(string text)
    {
        if (GetClipboardSequenceNumber() != (_ourClipboardSequence ?? _originalClipboardSequence))
            throw new InvalidOperationException("剪贴板已被更改，已停止剩余输入。");
        Clipboard.SetText(text);
        _ourClipboardSequence = GetClipboardSequenceNumber();
    }
    public void Enter()
    {
        EnsureTarget();
        Send(Key(0x0D), Key(0x0D, true));
    }
    public void Paste()
    {
        EnsureTarget();
        if (_ourClipboardSequence != GetClipboardSequenceNumber())
            throw new InvalidOperationException("剪贴板已被更改，已停止剩余输入。");
        Send(Key(0x11), Key(0x56), Key(0x56, true), Key(0x11, true));
    }
    public bool TryCancelLine()
    {
        if (_disposed || GetForegroundWindow() != _target ||
            new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)) return false;
        try { Send(Key(0x1B), Key(0x1B, true)); return true; }
        catch (Win32Exception) { return false; }
    }
    private static Input Key(ushort key, bool release = false) => new()
    { Type = 1, Value = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = release ? 2u : 0u } } };
    private static void Send(params Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            // A partial Ctrl+V must not leave Ctrl logically held.
            var release = new[] { Key(0x11, true), Key(0x56, true), Key(0x0D, true) };
            SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
            throw new Win32Exception("输入未能发送，请检查游戏与助手的权限是否一致。");
        }
    }
    public static async Task WaitForHotkeyReleaseAsync(string hotkey, CancellationToken cancellation)
    {
        var key = (int)LocalSettings.HotkeyVirtualKey(hotkey.Split('+')[^1]);
        for (var attempts = 0; attempts < 100; attempts++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (new[] { key, 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(k => (GetAsyncKeyState(k) & 0x8000) == 0)) return;
            await Task.Delay(20, cancellation);
        }
        throw new InvalidOperationException("按键尚未松开，本次输入已取消。");
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ourClipboardSequence is not uint sequence || GetClipboardSequenceNumber() != sequence) return;
        try
        {
            if (_previousClipboard is null) Clipboard.Clear();
            else Clipboard.SetDataObject(_previousClipboard, true);
        }
        catch (ExternalException) { /* Do not overwrite another application's clipboard or retry the send. */ }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Value; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}

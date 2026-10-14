using System.IO;

namespace Grimmcair.Desktop.Services;

/// <summary>Only the map's load/save chat protocol; never a general macro or script runner.</summary>
public static class GameChatCommands
{
    // The map's Preload writer splits at JASS 's' (115), with '-' as continuation marker.
    public const int PayloadPerLine = 115;
    private const string Alphabet = "abcdefghijlkponmqrstuvwxyzABCDEFGHKLMNOPQRSTUVWXYZ0123456789@#$%&?";

    public static IReadOnlyList<string> Load(IEnumerable<string> completeCodes)
    {
        var groups = completeCodes.ToArray();
        if (groups.Length is < 1 or > 16) throw new InvalidDataException("所选存档没有完整的载入代码。");
        var lines = new List<string>();
        string? command = null;
        foreach (var group in groups)
        {
            var separator = group.IndexOf(' ');
            var prefix = separator > 0 ? group[..separator] : "";
            if (prefix is not ("-load" or "-aload") || (command is not null && command != prefix))
                throw new InvalidDataException("存档代码类型不一致，无法下档。");
            command = prefix;
            var payload = group[(separator + 1)..];
            if (payload.Length is < 1 or > 8192 || payload.Any(c => !Alphabet.Contains(c)))
                throw new InvalidDataException("存档代码不完整，无法下档。");
            for (var index = 0; index < payload.Length; index += PayloadPerLine)
            {
                var count = Math.Min(PayloadPerLine, payload.Length - index);
                lines.Add(prefix + " " + payload.Substring(index, count) + (index + count < payload.Length ? "-" : ""));
            }
        }
        return lines;
    }

    public static IReadOnlyList<string> Save(string? saveName, bool account = false)
    {
        var command = account ? "-asave" : "-save";
        if (string.IsNullOrEmpty(saveName)) return [command];
        if (saveName.Contains(' ') || saveName.Contains('-'))
            throw new InvalidDataException("游戏会删除保存名称中的空格和横线。为避免覆盖其他档案，请选择不含这些字符的存档后再保存。");
        if (saveName.Length > 100 || saveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || saveName.Any(char.IsControl)
            || saveName is "." or ".." || saveName.EndsWith('.') || saveName.EndsWith(' '))
            throw new InvalidDataException("所选存档名称不能作为游戏保存名称。");
        return [command + " " + saveName];
    }
}

public interface IGameChatTransport
{
    void EnsureTarget();
    Task WaitAsync(int milliseconds, CancellationToken cancellation);
    void SetClipboardText(string text);
    void Enter();
    void Paste();
    bool TryCancelLine();
}

public static class GameChatSequence
{
    public static async Task<int> SendAsync(IReadOnlyList<string> lines, IGameChatTransport input, CancellationToken cancellation)
    {
        var sent = 0;
        var lineOpen = false;
        try
        {
            foreach (var line in lines)
            {
                cancellation.ThrowIfCancellationRequested();
                input.EnsureTarget();
                input.SetClipboardText(line);
                input.EnsureTarget();
                cancellation.ThrowIfCancellationRequested();
                input.Enter(); lineOpen = true;
                await input.WaitAsync(180, cancellation);
                input.EnsureTarget();
                cancellation.ThrowIfCancellationRequested();
                input.Paste();
                await input.WaitAsync(180, cancellation);
                input.EnsureTarget();
                cancellation.ThrowIfCancellationRequested();
                input.Enter(); lineOpen = false;
                sent++;
                await input.WaitAsync(450, cancellation);
                cancellation.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            // Never submit a partial line while recovering. Escape is allowed only in the original window.
            if (lineOpen) input.TryCancelLine();
            throw;
        }
        return sent;
    }
}

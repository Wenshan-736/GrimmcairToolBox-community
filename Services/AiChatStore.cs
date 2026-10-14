using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grimmcair.Desktop.Services;

public sealed class AiStoredMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Status { get; set; } = "complete";
    public List<string> Sources { get; set; } = [];
    public List<KnowledgeImageReference> Images { get; set; } = [];
    [JsonIgnore]
    public string ContextOwner { get; set; } = "";
    [JsonIgnore]
    public List<AiChatMessage> ContextMessages { get; set; } = [];
}

public sealed class AiConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新对话";
    public bool ThinkingEnabled { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    [System.Text.Json.Serialization.JsonIgnore]
    public string UpdatedDisplay => UpdatedUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public long Revision { get; set; }
    public List<AiStoredMessage> Messages { get; set; } = [];
}

/// <summary>Stores user/assistant display messages, with completed protocol context encrypted separately inside the same atomic document.</summary>
public sealed class AiChatStore
{
    private const int MaximumProtectedBytes = 65536;
    private const int MaximumConversationBytes = 64 * 1024 * 1024;
    private const int MaximumContextBytes = 16 * 1024 * 1024;
    private const string ConnectionReadError = "AI 连接配置无法解密或已损坏，原文件已保留。";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string rootDirectory;
    private readonly string conversationsDirectory;
    private readonly string connectionPath;
    private readonly string mutexName;
    private IReadOnlyList<string> lastReadErrors = Array.Empty<string>();
    public IReadOnlyList<string> LastReadErrors => lastReadErrors;

    private sealed class ConnectionDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public string Kind { get; set; } = "grimmcair.ai-connection";
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ThinkingVerified { get; set; }
    }

    private sealed class ConversationDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public AiConversation Conversation { get; set; } = new();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProtectedContext { get; set; }
    }

    private sealed class ContextDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public string ConversationId { get; set; } = "";
        public long Revision { get; set; }
        public List<TurnContextDocument> Turns { get; set; } = [];
    }

    private sealed class TurnContextDocument
    {
        public int MessageIndex { get; set; }
        public string Owner { get; set; } = "";
        public List<ProtocolMessageDocument> Messages { get; set; } = [];
    }

    // AiChatMessage intentionally ignores reasoning in general JSON serialization. Only this private,
    // explicitly encrypted DTO carries it to disk; it never appears in display JSON or diagnostics.
    private sealed class ProtocolMessageDocument
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
        public string? ReasoningContent { get; set; }
        public string? ToolCallId { get; set; }
        public List<AiToolCall> ToolCalls { get; set; } = [];
    }

    public AiChatStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        conversationsDirectory = Path.Combine(this.rootDirectory, "conversations");
        connectionPath = Path.Combine(this.rootDirectory, "connection.dpapi");
        mutexName = "Local\\Grimmcair.AiChat." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(this.rootDirectory.ToUpperInvariant())));
    }

    public AiConnection? LoadConnection() => WithLock(ReadConnection);

    /// <summary>The UI must enable this action only after the current URL/key/model passed its connection test.</summary>
    public void SaveConnection(AiConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateConnection(connection.BaseUrl, connection.ApiKey, connection.Model);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new ConnectionDocument
        { BaseUrl = connection.BaseUrl.Trim(), ApiKey = connection.ApiKey, Model = connection.Model.Trim(), ThinkingVerified = connection.ThinkingVerified }, Json);
        byte[] protectedBytes;
        try { protectedBytes = Transform(plaintext, protect: true); }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException)
        { throw new InvalidDataException("AI 连接配置未能使用当前 Windows 用户加密，未保存。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        WithLock(() =>
        {
            EnsureSafeDirectories();
            if (File.Exists(connectionPath))
            {
                RejectLink(connectionPath);
                try { _ = ReadConnection(); }
                catch (InvalidDataException)
                {
                    // An explicit save follows a successful UI test. Retain the old ciphertext for recovery.
                    var retainedPath = Path.Combine(rootDirectory, "connection.corrupt." + Guid.NewGuid().ToString("N") + ".dpapi");
                    try { File.Move(connectionPath, retainedPath); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { throw new InvalidDataException("损坏的 AI 连接配置未能保留，未保存新配置。"); }
                }
            }
            WriteAtomic(connectionPath, protectedBytes);
            return true;
        });
    }

    public IReadOnlyList<AiConversation> ListConversations() => WithLock<IReadOnlyList<AiConversation>>(() =>
    {
        var errors = new List<string>();
        var conversations = new List<AiConversation>();
        EnsureSafeDirectories();
        if (Directory.Exists(conversationsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(conversationsDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!IsValidId(id)) { errors.Add("对话目录中有无法识别的文件，已保留。"); continue; }
                try { conversations.Add(ReadConversation(path, id)); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                { errors.Add($"对话 {id} 暂时无法读取，原文件已保留。"); }
            }
        }
        lastReadErrors = errors.AsReadOnly();
        return conversations.OrderByDescending(item => item.UpdatedUtc).ToArray();
    });

    /// <summary>Persists the complete display history without truncation; rejects a stale window's revision.</summary>
    public void SaveConversation(AiConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ValidateConversation(conversation);
        WithLock(() =>
        {
            EnsureSafeDirectories();
            var path = ConversationPath(conversation.Id);
            if (File.Exists(path))
            {
                AiConversation existing;
                try { existing = ReadConversation(path, conversation.Id); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                { throw new InvalidDataException("这份对话无法读取，原文件已保留，未覆盖。"); }
                if (existing.Revision != conversation.Revision)
                    throw new InvalidDataException("这份对话已在其他窗口更新，请重新打开后再继续。");
            }
            else if (conversation.Revision != 0)
                throw new InvalidDataException("这份对话已被删除或移动，未重新创建。请新建对话。");

            // Only the configured secret is known here; do not permit it to be echoed into visible history.
            var apiKey = ReadConnection()?.ApiKey;
            var escapedKey = string.IsNullOrEmpty(apiKey) ? null : JsonSerializer.Serialize(apiKey)[1..^1];
            string SafeText(string value)
            {
                if (string.IsNullOrEmpty(apiKey)) return value;
                var safe = value.Replace(apiKey, "[密钥已隐藏]", StringComparison.Ordinal);
                return escapedKey == apiKey ? safe : safe.Replace(escapedKey!, "[密钥已隐藏]", StringComparison.Ordinal);
            }
            var saved = new AiConversation
            {
                Id = conversation.Id, Title = SafeText(conversation.Title), CreatedUtc = conversation.CreatedUtc,
                ThinkingEnabled = conversation.ThinkingEnabled,
                UpdatedUtc = DateTimeOffset.UtcNow < conversation.CreatedUtc ? conversation.CreatedUtc : DateTimeOffset.UtcNow,
                Revision = checked(conversation.Revision + 1),
                Messages = conversation.Messages.Select(message => new AiStoredMessage
                {
                    Role = message.Role, Content = SafeText(message.Content), Status = SafeText(message.Status),
                    Sources = message.Sources.Select(SafeText).ToList(),
                    Images = message.Images.Select(reference => new KnowledgeImageReference(
                        SafeText(reference.ImageId), SafeText(reference.EntryId), SafeText(reference.KnowledgeRevision), SafeText(reference.Sha256))).ToList()
                }).ToList()
            };
            var protectedContext = ProtectContext(conversation, saved.Revision, SafeText);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new ConversationDocument { SchemaVersion = 2, Conversation = saved, ProtectedContext = protectedContext }, Json);
            if (bytes.Length > MaximumConversationBytes)
                throw new InvalidDataException("这份对话过大，原记录已保留。请新建对话继续。");
            WriteAtomic(path, bytes);
            conversation.Revision = saved.Revision;
            conversation.UpdatedUtc = saved.UpdatedUtc;
        });
    }

    public void DeleteConversation(string id) => WithLock(() =>
    {
        var path = ConversationPath(id);
        EnsureSafeDirectories();
        RejectLink(path);
        if (File.Exists(path)) File.Delete(path);
    });

    private AiConnection? ReadConnection()
    {
        EnsureSafeDirectories();
        if (!File.Exists(connectionPath)) return null;
        byte[]? plaintext = null;
        try
        {
            RejectLink(connectionPath);
            var size = new FileInfo(connectionPath).Length;
            if (size is < 1 or > MaximumProtectedBytes) throw new InvalidDataException();
            plaintext = Transform(File.ReadAllBytes(connectionPath), protect: false);
            var document = JsonSerializer.Deserialize<ConnectionDocument>(plaintext, Json) ?? throw new InvalidDataException();
            if (document.SchemaVersion != 1 || document.Kind != "grimmcair.ai-connection") throw new InvalidDataException();
            ValidateConnection(document.BaseUrl, document.ApiKey, document.Model);
            // Legacy global thinkingEnabled is deliberately ignored. Each conversation owns its mode.
            return new(document.BaseUrl, document.ApiKey, document.Model, ThinkingEnabled: false, ThinkingVerified: document.ThinkingVerified);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException
            or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException(ConnectionReadError); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void ValidateConnection(string baseUrl, string apiKey, string model)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Length > 4096 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 16384 || apiKey.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(model) || model.Length > 512 || model.Any(char.IsControl))
            throw new InvalidDataException("AI 连接配置格式无效，未保存。");
    }

    private static AiConversation ReadConversation(string path, string expectedId)
    {
        RejectLink(path);
        var length = new FileInfo(path).Length;
        if (length is < 1 or > MaximumConversationBytes) throw new InvalidDataException();
        var document = JsonSerializer.Deserialize<ConversationDocument>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException();
        if (document.SchemaVersion is not (1 or 2) || document.Conversation is null
            || (document.SchemaVersion == 2 && document.ProtectedContext is null)) throw new InvalidDataException();
        var value = document.Conversation;
        ValidateConversation(value);
        if (value.Id != expectedId || value.Revision < 1) throw new InvalidDataException();
        if (document.ProtectedContext is not null) RestoreContext(value, document.ProtectedContext);
        return value;
    }

    private static void ValidateConversation(AiConversation value)
    {
        if (!IsValidId(value.Id) || value.Title is null || value.Messages is null || value.Revision < 0
            || value.CreatedUtc == default || value.UpdatedUtc == default || value.Revision == long.MaxValue)
            throw new InvalidDataException("对话格式无效，未保存。");
        foreach (var message in value.Messages)
        {
            if (message is null || message.Role is not ("user" or "assistant") || message.Content is null
                || message.Status is null || message.Status.Length > 64 || message.Status.Any(char.IsControl)
                || message.Sources is null || message.Sources.Any(source => source is null))
                throw new InvalidDataException("对话仅保存用户与助手的显示内容，格式无效，未保存。");
            if (message.Images is null || message.Images.Count > KnowledgeImageCatalog.MaximumMessageImages
                || message.Images.Any(reference => !KnowledgeImageCatalog.IsValidReference(reference))
                || message.Role != "assistant" && message.Images.Count > 0)
                throw new InvalidDataException("对话图片仅保存已登记资料的引用，格式无效，未保存。");
            ValidateContext(message);
        }
    }

    private static void ValidateContext(AiStoredMessage message)
    {
        const string invalid = "对话的加密上下文格式无效，原文件已保留。";
        if (message.ContextOwner is null || message.ContextMessages is null) throw new InvalidDataException(invalid);
        if (message.ContextMessages.Count == 0)
        {
            if (message.ContextOwner.Length != 0) throw new InvalidDataException(invalid);
            return;
        }
        if (message.Role != "assistant" || message.Status != "complete" || message.ContextMessages.Count > AiChatWorkflow.MaximumProtocolMessages)
            throw new InvalidDataException(invalid);
        var owner = message.ContextOwner.Split('\n');
        // Legacy two-part owners remain readable; BuildContext will use their visible text only.
        if (owner.Length is not (2 or 3) || owner[0].Length > 4096 || !IsContextIdentifier(owner[1], 512)
            || owner.Length == 3 && (owner[2].Length != 64 || !owner[2].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException(invalid);
        try { if (AiChatClient.NormalizeBaseUrl(owner[0]) != owner[0]) throw new InvalidDataException(invalid); }
        catch (AiChatException) { throw new InvalidDataException(invalid); }
        var pending = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < message.ContextMessages.Count; index++)
        {
            var item = message.ContextMessages[index];
            if (item is null || item.Role is not ("assistant" or "tool") || item.Content is null || item.Content.Length > 1024 * 1024
                || item.ReasoningContent?.Length > 256 * 1024 || item.ToolCalls is null || item.ToolCalls.Count > 16)
                throw new InvalidDataException(invalid);
            if (item.Role == "assistant")
            {
                if (pending.Count != 0 || !string.IsNullOrEmpty(item.ToolCallId)) throw new InvalidDataException(invalid);
                if (item.ToolCalls.Count == 0 && index != message.ContextMessages.Count - 1) throw new InvalidDataException(invalid);
                foreach (var call in item.ToolCalls)
                {
                    if (call is null || !IsContextIdentifier(call.Id, 256) || !IsContextIdentifier(call.Name, 64)
                        || !call.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') || call.Arguments is null
                        || call.Arguments.Length > 65536 || !pending.Add(call.Id)) throw new InvalidDataException(invalid);
                    try { using var args = JsonDocument.Parse(call.Arguments); if (args.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException(invalid); }
                    catch (JsonException) { throw new InvalidDataException(invalid); }
                }
            }
            else if (!IsContextIdentifier(item.ToolCallId, 256) || !pending.Remove(item.ToolCallId!) || item.ToolCalls.Count != 0
                || !string.IsNullOrEmpty(item.ReasoningContent)) throw new InvalidDataException(invalid);
        }
        if (pending.Count != 0 || message.ContextMessages[^1].Role != "assistant" || message.ContextMessages[^1].ToolCalls.Count != 0)
            throw new InvalidDataException(invalid);
    }

    private static bool IsContextIdentifier(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);

    private static string ContextPurpose(string id, long revision) => $"grimmcair.ai-context:v1:{id}:{revision}";

    private static string ProtectContext(AiConversation conversation, long revision, Func<string, string> safeText)
    {
        var context = new ContextDocument { ConversationId = conversation.Id, Revision = revision };
        for (var index = 0; index < conversation.Messages.Count; index++)
        {
            var message = conversation.Messages[index];
            if (message.ContextMessages.Count == 0) continue;
            var turn = new TurnContextDocument
            {
                MessageIndex = index, Owner = safeText(message.ContextOwner),
                Messages = message.ContextMessages.Select(item => new ProtocolMessageDocument
                {
                    Role = item.Role, Content = safeText(item.Content), ReasoningContent = item.ReasoningContent is null ? null : safeText(item.ReasoningContent),
                    ToolCallId = item.ToolCallId is null ? null : safeText(item.ToolCallId),
                    ToolCalls = item.ToolCalls.Select(call => new AiToolCall(safeText(call.Id), safeText(call.Name), safeText(call.Arguments))).ToList()
                }).ToList()
            };
            var sanitized = new AiStoredMessage { Role = "assistant", Status = "complete", ContextOwner = turn.Owner, ContextMessages = RestoreMessages(turn.Messages) };
            ValidateContext(sanitized);
            context.Turns.Add(turn);
        }
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(context, Json);
        try
        {
            if (plaintext.Length > MaximumContextBytes) throw new InvalidDataException("对话的加密上下文过大，原文件已保留，请新建对话。");
            return Convert.ToBase64String(Transform(plaintext, true, ContextPurpose(conversation.Id, revision), MaximumContextBytes + MaximumProtectedBytes));
        }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException)
        { throw new InvalidDataException("对话上下文未能使用当前 Windows 用户加密，原文件已保留。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static List<AiChatMessage> RestoreMessages(List<ProtocolMessageDocument> messages) => messages.Select(item =>
        item is null ? throw new InvalidDataException() : new AiChatMessage
        { Role = item.Role, Content = item.Content, ReasoningContent = item.ReasoningContent, ToolCallId = item.ToolCallId, ToolCalls = item.ToolCalls }).ToList();

    private static void RestoreContext(AiConversation conversation, string protectedContext)
    {
        byte[]? plaintext = null;
        try
        {
            if (protectedContext.Length is < 1 or > (MaximumContextBytes + MaximumProtectedBytes) * 2) throw new InvalidDataException();
            var bytes = Convert.FromBase64String(protectedContext);
            if (bytes.Length > MaximumContextBytes + MaximumProtectedBytes) throw new InvalidDataException();
            plaintext = Transform(bytes, false, ContextPurpose(conversation.Id, conversation.Revision), MaximumContextBytes + MaximumProtectedBytes);
            if (plaintext.Length > MaximumContextBytes) throw new InvalidDataException();
            var context = JsonSerializer.Deserialize<ContextDocument>(plaintext, Json) ?? throw new InvalidDataException();
            if (context.SchemaVersion != 1 || context.ConversationId != conversation.Id || context.Revision != conversation.Revision
                || context.Turns is null || context.Turns.Count > conversation.Messages.Count) throw new InvalidDataException();
            var indices = new HashSet<int>();
            foreach (var turn in context.Turns)
            {
                if (turn is null || turn.MessageIndex < 0 || turn.MessageIndex >= conversation.Messages.Count || !indices.Add(turn.MessageIndex)
                    || turn.Messages is null || turn.Messages.Count == 0) throw new InvalidDataException();
                var message = conversation.Messages[turn.MessageIndex];
                message.ContextOwner = turn.Owner;
                message.ContextMessages = RestoreMessages(turn.Messages);
                ValidateContext(message);
            }
        }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException or FormatException or JsonException or InvalidDataException or ArgumentException)
        { throw new InvalidDataException("对话的加密上下文无法读取，原文件已保留。请使用原 Windows 用户打开。"); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static bool IsValidId(string? id) => id is not null && id.Length == 32
        && Guid.TryParseExact(id, "N", out var parsed) && id == parsed.ToString("N");

    private string ConversationPath(string id)
    {
        if (!IsValidId(id)) throw new InvalidDataException("对话编号无效。");
        return Path.Combine(conversationsDirectory, id + ".json");
    }

    private void EnsureSafeDirectories()
    {
        RejectLink(rootDirectory);
        RejectLink(conversationsDirectory);
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("AI 数据路径是文件系统链接，未读取或写入。");
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        RejectLink(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidDataException("AI 数据未能写入本机，已有文件已保留，请检查文件权限或磁盘空间。"); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void WithLock(Action action) => WithLock(() => { action(); return true; });

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("AI 数据正在由另一个窗口保存，请稍后重试。");
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Transform(byte[] value, bool protect, string purpose = "grimmcair.ai-connection:v1", int maximumBytes = MaximumProtectedBytes)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("AI 密钥保存需要 Windows 用户保护。");
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes(purpose));
        var input = new DataBlob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var optionalEntropy = new DataBlob { Size = entropy.Length, Data = Marshal.AllocHGlobal(entropy.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            Marshal.Copy(entropy, 0, optionalEntropy.Data, entropy.Length);
            var success = protect
                ? CryptProtectData(ref input, null, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success || output.Size < 1 || output.Size > maximumBytes || output.Data == IntPtr.Zero)
                throw new CryptographicException("Windows 用户保护未完成。");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Wipe(input.Data, input.Size); Marshal.FreeHGlobal(input.Data);
            Wipe(optionalEntropy.Data, optionalEntropy.Size); Marshal.FreeHGlobal(optionalEntropy.Data);
            if (output.Data != IntPtr.Zero) { Wipe(output.Data, output.Size); LocalFree(output.Data); }
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private static void Wipe(IntPtr pointer, int size)
    {
        if (pointer == IntPtr.Zero || size <= 0) return;
        var zero = new byte[Math.Min(size, 4096)];
        for (var offset = 0; offset < size; offset += zero.Length)
            Marshal.Copy(zero, 0, IntPtr.Add(pointer, offset), Math.Min(zero.Length, size - offset));
    }
}

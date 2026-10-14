using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

public sealed class DesktopCloudBinding
{
    public string LocalSaveId { get; set; } = "";
    public string SaveId { get; set; } = "";
    // A recreated cloud slot has a new lifecycle; its deleted predecessor is private recovery evidence.
    public string PredecessorSaveId { get; set; } = "";
    public bool Enabled { get; set; }
    public string Status { get; set; } = "disabled";
    public string LastError { get; set; } = "";
    public DateTimeOffset? LastSyncAt { get; set; }
    public string AcceptedLocalFingerprint { get; set; } = "";
    public string AcceptedContentFingerprint { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public long CloudRevision { get; set; }
    public long AcceptedClientRevision { get; set; }
    public string AcceptedGoalsRevision { get; set; } = "";
    public string AcceptedGoalFingerprint { get; set; } = "";
    public string AcceptedSaveName { get; set; } = "";
    public bool AcceptedIsMain { get; set; }
    public bool AcceptedSyncEnabled { get; set; }
    // User explicitly linked a cloud secondary to the sole local main; its goals stay local.
    public bool LocalMainAsCloudSecondary { get; set; }
    public bool Deleted { get; set; }
}

/// <summary>Private retry journal. BodyJson contains save codes; never log or display it.</summary>
public sealed class DesktopCloudOperation
{
    public string RequestId { get; init; } = "";
    public string LocalSaveId { get; init; } = "";
    public string SaveId { get; init; } = "";
    public string CharacterId { get; init; } = "";
    public string GameAccount { get; init; } = "";
    public string ProfessionId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public string BodyJson { get; init; } = "";
    public string BodySha256 { get; init; } = "";
    public string LocalFingerprint { get; init; } = "";
    public string ContentFingerprint { get; init; } = "";
    public string GoalFingerprint { get; init; } = "";
    // A metadata-only main withdrawal validates these versions without accepting unobserved code or goals.
    public long ExpectedClientRevision { get; init; }
    public string ExpectedGoalsRevision { get; init; } = "";
    public bool ExpectedSyncEnabled { get; init; }
    public bool LocalWasMain { get; init; }
    public string Status { get; set; } = "prepared";
    // Persisted before attempting a write, including the very first attempt.
    public bool MustQueryReceipt { get; set; }
    public override string ToString() => $"{Kind} ({Status})";
}

public sealed class DesktopCloudAccountState
{
    public string BaseUrl { get; set; } = "";
    public long UserId { get; set; }
    public List<DesktopCloudBinding> Bindings { get; set; } = [];
    // Replaced local associations are private evidence only. The old server slots are never deleted or stopped by replacement.
    public List<DesktopCloudBinding> RetiredBindings { get; set; } = [];
    public List<DesktopCloudOperation> PendingOperations { get; set; } = [];
    // Explicitly retired terminal operations remain private local recovery evidence, never transportable.
    public List<DesktopCloudOperation> RetiredFailedOperations { get; set; } = [];
    // Server committed, but the local library has not yet applied the main/goal transfer.
    public List<DesktopCloudOperation> CommittedMainTransfers { get; set; } = [];
    public Dictionary<string, long> CharacterRevisions { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DesktopCloudStateDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Kind { get; set; } = "grimmcair.desktop-cloud-state";
    public List<DesktopCloudAccountState> Accounts { get; set; } = [];
}

public sealed class DesktopCloudStateStore
{
    private readonly string path;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public DesktopCloudStateDocument State { get; }

    public DesktopCloudStateStore(string path)
    {
        this.path = System.IO.Path.GetFullPath(path);
        State = File.Exists(this.path)
            ? JsonSerializer.Deserialize<DesktopCloudStateDocument>(File.ReadAllText(this.path), Json)
                ?? throw new InvalidDataException("云同步状态文件为空，未自动重建。")
            : new();
        if (State.SchemaVersion != 1 || State.Kind != "grimmcair.desktop-cloud-state")
            throw new InvalidDataException("云同步状态版本不兼容，未自动重建。");
        foreach (var account in State.Accounts)
        {
            if (account.UserId <= 0 || NormalizeBaseUrl(account.BaseUrl) != account.BaseUrl)
                throw new InvalidDataException("云同步账户分区无效。");
            if (account.Bindings.Select(x => x.LocalSaveId).Distinct(StringComparer.Ordinal).Count() != account.Bindings.Count
                || account.Bindings.Select(x => x.SaveId).Distinct(StringComparer.Ordinal).Count() != account.Bindings.Count)
                throw new InvalidDataException("云同步存档绑定重复。");
            foreach (var binding in account.Bindings.Concat(account.RetiredBindings))
            {
                RequireUuid(binding.SaveId);
                if (binding.PredecessorSaveId.Length > 0)
                {
                    RequireUuid(binding.PredecessorSaveId);
                    if (binding.PredecessorSaveId == binding.SaveId)
                        throw new InvalidDataException("云同步槽位不能继承自身的旧标识。");
                }
            }
            if (account.RetiredBindings.Any(binding => binding.Enabled))
                throw new InvalidDataException("旧云绑定归档不能继续参与本机自动同步。");
            foreach (var operation in account.PendingOperations.Concat(account.CommittedMainTransfers).Concat(account.RetiredFailedOperations))
            {
                RequireUuid(operation.RequestId); RequireUuid(operation.SaveId);
                if (Hash(operation.BodyJson) != operation.BodySha256)
                    throw new InvalidDataException("待确认操作内容校验失败，禁止重新提交。");
            }
        }
    }

    public DesktopCloudAccountState ForAccount(string baseUrl, long userId)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        baseUrl = NormalizeBaseUrl(baseUrl);
        lock (gate)
        {
            var result = State.Accounts.SingleOrDefault(x => x.UserId == userId && x.BaseUrl == baseUrl);
            if (result is not null) return result;
            result = new() { BaseUrl = baseUrl, UserId = userId };
            State.Accounts.Add(result); Save();
            return result;
        }
    }

    public void Save()
    {
        lock (gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(State, Json));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(true); }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("服务地址必须是完整 HTTP(S) 地址，且不能包含账户、查询参数或片段。", nameof(value));
        return uri.AbsoluteUri.TrimEnd('/');
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string RequireUuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty)
            throw new InvalidDataException("云端标识不是有效的标准 UUID。");
        return id.ToString("D", CultureInfo.InvariantCulture);
    }
}

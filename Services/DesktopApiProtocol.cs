using System.Net;

namespace Grimmcair.Desktop.Services;

public sealed class DesktopApiUser
{
    public long Id { get; init; }
    public string Nickname { get; init; } = "";
    public string Role { get; init; } = "";
}

public sealed class DesktopApiQuota
{
    public int Limit { get; init; }
    public int Used { get; init; }
    public int Level { get; init; }
}

// Contains only display/authorization facts; credentials never enter UI snapshots.
public sealed class DesktopAuthResponse
{
    public string Decision { get; init; } = "allow";
    public string ReasonCode { get; init; } = "ok";
    public DateTimeOffset ServerTime { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset SessionExpiresAt { get; init; }
    public int VerificationIntervalSeconds { get; init; }
    public DesktopApiQuota Quota { get; init; } = new();
    public DesktopApiUser User { get; init; } = new();
}

public enum DesktopSessionState
{
    LoggedOut,
    Verifying,
    Allowed,
    PendingVerification,
    Denied,
    RequiresLogin
}

// Never attach the raw HTTP body, request, credentials or transport exception.
public sealed class DesktopApiException : Exception
{
    public int? StatusCode { get; }
    public string Code { get; }
    public string? Decision { get; }
    public TimeSpan? RetryAfter { get; }
    public bool OutcomeUnknown { get; }

    public DesktopApiException(string code, int? statusCode = null, string? decision = null,
        TimeSpan? retryAfter = null, bool outcomeUnknown = false)
        : base(MessageFor(SafeCode(code)))
    {
        Code = SafeCode(code);
        StatusCode = statusCode;
        Decision = decision is "deny" or "require_login" ? decision : null;
        RetryAfter = retryAfter;
        OutcomeUnknown = outcomeUnknown;
    }

    internal static bool IsBusinessCode(string code) => code is
        "consent_required" or "consent_version_mismatch" or "consent_not_confirmed" or
        "unsupported_data_version" or "unknown_profession" or "unknown_item" or
        "invalid_request" or "invalid_goals" or "invalid_needs" or "invalid_needs_status" or
        "snapshot_mismatch" or "goals_mismatch" or "unknown_goal" or "duplicate_goal" or "duplicate_need" or
        "secondary_needs" or "needs_required" or "idempotency_conflict" or
        "cloud_revision_conflict" or "character_revision_conflict" or "client_revision_conflict" or
        "character_identity_conflict" or "main_switch_required" or "main_save_required" or
        "main_goals_transfer_required" or "already_main_save" or "quota_exceeded" or
        "main_successor_required" or "save_not_found" or "character_not_found" or "save_deleted" or
        "operation_not_found";

    internal static string SafeCode(string? code) => code switch
    {
        "login_required" or "session_expired" or "credentials_changed" or "role_changed" or
        "invalid_credentials" or "otp_required" or "otp_invalid" or "key_required" or "key_invalid" or
        "key_expired" or "key_revoked" or "account_inactive" or "account_binding_required" or
        "password_change_required" or "test_identity_disabled" or "invalid_base_url" or "invalid_path" or
        "invalid_response" or "redirect_blocked" or "response_too_large" or "request_too_large" or
        "network_error" or "timeout" or "pending_verification" or "identity_changed" or
        "rate_limited" or "length_required" or "service_unavailable" or "server_error" => code,
        not null when IsBusinessCode(code) => code,
        _ => "server_error"
    };

    private static string MessageFor(string code) => code switch
    {
        "invalid_base_url" => "云服务连接配置无效，请联系维护者。",
        "invalid_path" => "请求地址不属于当前桌面服务接口。",
        "invalid_credentials" => "用户名或专用密钥不正确。",
        "otp_required" => "请私聊机器人发送“网站登录”，再填写取得的一次性验证码。",
        "otp_invalid" => "一次性验证码无效或已过期，请重新获取。",
        "key_required" => "请填写专用版密钥。",
        "key_invalid" => "专用版密钥无效或与当前账户不匹配。",
        "key_expired" => "专用版密钥已过期，请在网站续期或更换。",
        "key_revoked" => "专用版密钥已撤销或更换，请在网站查看。",
        "account_inactive" or "test_identity_disabled" => "当前账户不可用，请在网站检查账户状态。",
        "account_binding_required" => "请先在网站绑定有效的个人积分账户。",
        "password_change_required" => "请先登录网站修改初始密码。",
        "login_required" or "session_expired" or "credentials_changed" or "role_changed" or "identity_changed" => "桌面登录需要重新验证，请重新登录。",
        "consent_required" or "consent_version_mismatch" or "consent_not_confirmed" => "请阅读并确认当前云存档及材料共享告知。",
        "unsupported_data_version" or "unknown_profession" or "unknown_item" => "云端与本地资料版本不一致，请核对当前资料。",
        "idempotency_conflict" => "该操作编号已有不同内容，请先核对原操作结果。",
        "cloud_revision_conflict" or "character_revision_conflict" or "client_revision_conflict" => "云端版本已经变化，请刷新后处理冲突。",
        "character_identity_conflict" => "该云档所属游戏账号或职业不一致，请核对存档。",
        "main_switch_required" or "main_save_required" or "main_goals_transfer_required" or "already_main_save" => "请先完成主档及目标的正确转交。",
        "quota_exceeded" => "云存档额度已满，本地存档仍会保留。",
        "main_successor_required" => "请先指定新的主档，再删除当前云端主档。",
        "save_deleted" => "旧云档已删除，本地存档已保留。请重新开启云同步，建立新的云端关联。",
        "save_not_found" or "character_not_found" => "云档状态已变化，请刷新云端列表。",
        "operation_not_found" => "云端尚未找到该操作回执。",
        "request_too_large" => "上传内容超过服务限制，请保留本地存档并检查内容。",
        "length_required" => "请求长度不符合服务要求。",
        "rate_limited" => "请求较频繁，请稍后重试。",
        "network_error" or "timeout" or "service_unavailable" => "暂时无法验证服务连接，专用功能已暂停，未提交内容仍会保留。",
        "pending_verification" => "正在等待本次在线验证，专用功能暂未启用。",
        "redirect_blocked" => "云服务连接配置需要更新，请联系维护者。",
        "invalid_response" or "response_too_large" => "服务响应无法验证，专用功能暂未启用。",
        not null when IsBusinessCode(code) => "本次云操作的数据或状态不符合要求，请刷新并核对存档与目标。",
        _ => "服务暂时无法完成请求，本地数据仍会保留。"
    };
}

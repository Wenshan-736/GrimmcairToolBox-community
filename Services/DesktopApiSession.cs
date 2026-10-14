using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

// A fresh instance always starts without permission. No calendar comparison or cached allow.
public sealed class DesktopApiSession
{
    // Cover the hourly timer's dispatch gap and one bounded HTTP request, never an indefinite in-flight lease.
    private const int VerificationSchedulingToleranceSeconds = 20;
    private readonly DesktopApiClient _client;
    private readonly TimeProvider _clock;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _authentication = new(1, 1);
    private long _generation;
    private long _verifiedAt;
    private long _failedAt;
    private TimeSpan? _retryAfter;
    private DesktopSessionState _state = DesktopSessionState.LoggedOut;
    private DesktopAuthResponse? _lastResponse;
    private string _message = "请登录专用版。";

    public DesktopApiSession(DesktopApiClient client) : this(client, TimeProvider.System) { }
    internal DesktopApiSession(DesktopApiClient client, TimeProvider clock) { _client = client; _clock = clock; }

    // Events may originate on a worker thread. UI subscribers marshal to their dispatcher.
    public event Action? Changed;
    public string BaseUrl => _client.BaseUrl;
    public DesktopAuthResponse? LastResponse { get { lock (_stateLock) return _lastResponse; } }
    public long? UserId { get { lock (_stateLock) return _lastResponse?.User.Id; } }
    public string Message { get { ExpireVerification(); lock (_stateLock) return _message; } }
    public int VerificationIntervalSeconds { get { lock (_stateLock) return _lastResponse?.VerificationIntervalSeconds ?? 3600; } }
    public DesktopSessionState State { get { ExpireVerification(); lock (_stateLock) return _state; } }
    public bool IsAllowed { get { ExpireVerification(); lock (_stateLock) return _state == DesktopSessionState.Allowed; } }
    internal TimeProvider Clock => _clock;
    public TimeSpan TimeUntilVerification
    {
        get
        {
            ExpireVerification();
            lock (_stateLock)
            {
                if (_state != DesktopSessionState.Allowed || _lastResponse is null) return TimeSpan.Zero;
                return Positive(TimeSpan.FromSeconds(_lastResponse.VerificationIntervalSeconds) - _clock.GetElapsedTime(_verifiedAt));
            }
        }
    }
    internal TimeSpan TimeUntilRetryAllowed
    {
        get { lock (_stateLock) return _retryAfter is { } delay ? Positive(delay - _clock.GetElapsedTime(_failedAt)) : TimeSpan.Zero; }
    }

    public async Task<DesktopAuthResponse> LoginAsync(string username, string key,
        CancellationToken cancellationToken = default)
    {
        long generation;
        lock (_stateLock)
        {
            generation = ++_generation;
            _lastResponse = null;
            _state = DesktopSessionState.Verifying;
            _message = "正在登录并验证专用版。";
            _client.ClearCredentials();
        }
        Changed?.Invoke();
        bool entered = false;
        try
        {
            await _authentication.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            EnsureCurrent(generation);
            var login = await _client.LoginAsync(username, key, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(generation);
            // Login alone never enables features: require this session's startup verification.
            var verified = await _client.VerifyAsync(cancellationToken).ConfigureAwait(false);
            if (verified.User.Id != login.User.Id) throw new DesktopApiException("identity_changed", decision: "require_login");
            cancellationToken.ThrowIfCancellationRequested();
            AcceptVerification(verified, generation);
            return verified;
        }
        catch (DesktopApiException error) { ApplyFailure(error, generation, verification: true); throw; }
        catch (OperationCanceledException)
        {
            lock (_stateLock) { if (entered || _generation == generation) _client.ClearCredentials(); }
            MarkPending(generation);
            throw;
        }
        finally
        {
            if (entered)
            {
                lock (_stateLock) { if (_generation != generation) _client.ClearCredentials(); }
                _authentication.Release();
            }
        }
    }

    public async Task<DesktopAuthResponse> VerifyAsync(CancellationToken cancellationToken = default)
        => (await VerifyCoreAsync(waitForTurn: true, cancellationToken).ConfigureAwait(false))!;

    // Timer and manual recovery share this non-queuing path; an in-flight verification is sufficient.
    public Task<DesktopAuthResponse?> TryVerifyAsync(CancellationToken cancellationToken = default)
        => VerifyCoreAsync(waitForTurn: false, cancellationToken);

    private async Task<DesktopAuthResponse?> VerifyCoreAsync(bool waitForTurn, CancellationToken cancellationToken)
    {
        long generation;
        long? previousUser;
        lock (_stateLock)
        {
            generation = _generation;
            previousUser = _lastResponse?.User.Id;
        }
        bool entered = false;
        try
        {
            if (waitForTurn) { await _authentication.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true; }
            else entered = await _authentication.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!entered) return null;
            bool changed;
            lock (_stateLock)
            {
                EnsureCurrent(generation);
                if (!waitForTurn && (_state is not (DesktopSessionState.Allowed or DesktopSessionState.PendingVerification)
                    || TimeUntilRetryAllowed > TimeSpan.Zero)) return null;
                // A bounded background request does not flash the authorization gate for a still allowed session.
                // Any failed request suspends permission; a pending session stays gated until the new allow.
                changed = _state != DesktopSessionState.Allowed;
                if (changed)
                {
                    _state = DesktopSessionState.Verifying;
                    _message = "正在在线验证专用版。";
                }
            }
            if (changed) Changed?.Invoke();
            var response = await _client.VerifyAsync(cancellationToken).ConfigureAwait(false);
            if (previousUser is not null && previousUser != response.User.Id)
                throw new DesktopApiException("identity_changed", decision: "require_login");
            cancellationToken.ThrowIfCancellationRequested();
            AcceptVerification(response, generation);
            return response;
        }
        catch (DesktopApiException error) { ApplyFailure(error, generation, verification: true); throw; }
        catch (OperationCanceledException) { MarkPending(generation); throw; }
        finally
        {
            if (entered) _authentication.Release();
        }
    }

    public async Task<JsonObject> SendAsync(string method, string relativePath, string? bodyJson,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowed) throw new DesktopApiException("pending_verification");
        long generation;
        lock (_stateLock)
        {
            if (_state != DesktopSessionState.Allowed) throw new DesktopApiException("pending_verification");
            generation = _generation;
        }
        try
        {
            Task<JsonObject> sending;
            lock (_stateLock)
            {
                EnsureCurrent(generation);
                if (_state != DesktopSessionState.Allowed) throw new DesktopApiException("pending_verification");
                // Capture HTTP credentials before a competing login may change the account.
                sending = _client.SendAsync(method, relativePath, bodyJson, cancellationToken);
            }
            var response = await sending.ConfigureAwait(false);
            EnsureCurrent(generation, outcomeUnknown: method != "GET");
            return response;
        }
        catch (DesktopApiException error) { ApplyFailure(error, generation, verification: false); throw; }
        catch (OperationCanceledException) { MarkPending(generation); throw; }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        lock (_stateLock)
        {
            generation = ++_generation;
            _state = DesktopSessionState.LoggedOut;
            _lastResponse = null;
            _message = "已退出专用版。";
        }
        Changed?.Invoke();
        bool entered = false;
        try
        {
            await _authentication.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            EnsureCurrent(generation);
            await _client.LogoutAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Do not clear a newer login if an old cancelled logout is finishing.
            lock (_stateLock) { if (_generation == generation) _client.ClearCredentials(); }
            if (entered) _authentication.Release();
        }
    }

    private void EnsureCurrent(long generation, bool outcomeUnknown = false)
    {
        lock (_stateLock)
            if (_generation != generation) throw new DesktopApiException("identity_changed", decision: "require_login", outcomeUnknown: outcomeUnknown);
    }

    private void AcceptVerification(DesktopAuthResponse response, long generation)
    {
        lock (_stateLock)
        {
            EnsureCurrent(generation);
            _lastResponse = response;
            _verifiedAt = _clock.GetTimestamp();
            _retryAfter = null;
            _state = DesktopSessionState.Allowed;
            _message = "专用版已在线验证。";
        }
        Changed?.Invoke();
    }

    private void ApplyFailure(DesktopApiException error, long generation, bool verification)
    {
        bool changed = false;
        lock (_stateLock)
        {
            if (_generation != generation) return;
            // Consent helpers also return decision=deny; they deny that action, not authentication.
            if (!verification && DesktopApiException.IsBusinessCode(error.Code)) return;
            if (error.Decision == "require_login" || error.Code is "login_required" or "session_expired" or
                "credentials_changed" or "role_changed" or "invalid_credentials" or "otp_required" or "otp_invalid" or "identity_changed")
            {
                _state = DesktopSessionState.RequiresLogin;
                _client.ClearCredentials();
            }
            else if (error.Decision == "deny" || error.Code is "key_required" or "key_invalid" or "key_expired" or
                "key_revoked" or "account_inactive" or "account_binding_required" or "password_change_required" or "test_identity_disabled")
                _state = DesktopSessionState.Denied;
            else if (verification || error.StatusCode is 401 or 403 or >= 500 || error.Code is "network_error" or "timeout" or
                "invalid_response" or "response_too_large" or "redirect_blocked")
                _state = DesktopSessionState.PendingVerification;
            else return;
            _message = error.Message;
            _failedAt = _clock.GetTimestamp();
            _retryAfter = error.RetryAfter;
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    private void MarkPending(long generation)
    {
        lock (_stateLock)
        {
            if (_generation != generation) return;
            _state = DesktopSessionState.PendingVerification;
            _message = "验证尚未完成，专用功能已暂停。";
            _retryAfter = null;
        }
        Changed?.Invoke();
    }

    private void ExpireVerification()
    {
        bool changed = false;
        lock (_stateLock)
        {
            if (_state == DesktopSessionState.Allowed && _lastResponse is not null
                && _clock.GetElapsedTime(_verifiedAt) >= TimeSpan.FromSeconds(
                    (long)_lastResponse.VerificationIntervalSeconds + VerificationSchedulingToleranceSeconds))
            {
                _state = DesktopSessionState.PendingVerification;
                _message = "需要再次在线验证，专用功能暂时暂停。";
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private static TimeSpan Positive(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;
}

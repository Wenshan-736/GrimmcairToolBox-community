using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Grimmcair.Desktop.Services;

public sealed class DesktopApiClient : IDisposable
{
    private const int MaximumRequestBytes = 2 * 1024 * 1024;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly object _credentialLock = new();
    private string? _token;
    private string? _key;
    public string BaseUrl => _baseUri.AbsoluteUri.TrimEnd('/');

    public DesktopApiClient(string baseUrl) : this(baseUrl, CreateHandler()) { }

    internal static HttpClientHandler CreateHandler() => new()
    { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };

    // Injected handlers are used by the in-memory protocol checks only, never by UI settings.
    internal DesktopApiClient(string baseUrl, HttpMessageHandler handler)
    {
        _baseUri = ValidateBaseUri(baseUrl);
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public static string NormalizeBaseUrl(string baseUrl) => ValidateBaseUri(baseUrl).AbsoluteUri.TrimEnd('/');

    public void SetCredentials(string token, string key)
    {
        ValidateHeaderSecret(token, "login_required");
        ValidateHeaderSecret(key, "key_invalid");
        lock (_credentialLock) { _token = token; _key = key; }
    }

    public void ClearCredentials() { lock (_credentialLock) { _token = null; _key = null; } }

    public async Task<DesktopAuthResponse> LoginAsync(string username, string key,
        CancellationToken cancellationToken = default)
    {
        ClearCredentials();
        if (string.IsNullOrWhiteSpace(username) || username.Length > 32) throw new DesktopApiException("invalid_request");
        ValidateHeaderSecret(key, string.IsNullOrEmpty(key) ? "key_required" : "key_invalid");
        var body = new JsonObject { ["username"] = username }.ToJsonString();
        var json = await SendCoreAsync("POST", "/api/desktop/auth/login", body, null, key, cancellationToken).ConfigureAwait(false);
        var result = ParseAuth(json);
        var token = ReadString(json, "accessToken");
        if (ReadString(json, "tokenType") != "Bearer") throw new DesktopApiException("invalid_response");
        ValidateHeaderSecret(token, "invalid_response");
        SetCredentials(token, key);
        return result;
    }

    public async Task<DesktopAuthResponse> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var (token, key) = Credentials(requireKey: true);
        return ParseAuth(await SendCoreAsync("POST", "/api/desktop/auth/verify", "{}", token, key, cancellationToken).ConfigureAwait(false));
    }

    public async Task<JsonObject> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var (token, _) = Credentials(requireKey: false);
        try
        {
            var result = await SendCoreAsync("POST", "/api/desktop/auth/logout", "{}", token, null, cancellationToken).ConfigureAwait(false);
            if (result["ok"] is not JsonValue value || !value.TryGetValue<bool>(out var ok) || !ok)
                throw new DesktopApiException("invalid_response", outcomeUnknown: true);
            return result;
        }
        finally { ClearCredentials(); }
    }

    public Task<JsonObject> SendAsync(string method, string relativePath, string? bodyJson,
        CancellationToken cancellationToken = default)
    {
        ValidateBusinessRoute(method, relativePath);
        var (token, key) = Credentials(requireKey: true);
        return SendCoreAsync(method, relativePath, bodyJson, token, key, cancellationToken);
    }

    private (string Token, string? Key) Credentials(bool requireKey)
    {
        lock (_credentialLock)
        {
            if (_token is null) throw new DesktopApiException("login_required", decision: "require_login");
            if (requireKey && _key is null) throw new DesktopApiException("key_required", decision: "deny");
            return (_token, requireKey ? _key : null);
        }
    }

    private async Task<JsonObject> SendCoreAsync(string method, string relativePath, string? bodyJson,
        string? token, string? key, CancellationToken cancellationToken)
    {
        bool mutation = method != "GET";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(_baseUri, relativePath.TrimStart('/')));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("X-Desktop-Key", key);
        if (mutation)
        {
            byte[] bytes;
            try
            {
                bodyJson ??= "{}";
                using var body = JsonDocument.Parse(bodyJson);
                if (body.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                bytes = Utf8.GetBytes(bodyJson);
            }
            catch (Exception ex) when (ex is JsonException or EncoderFallbackException)
            { throw new DesktopApiException("invalid_request"); }
            if (bytes.Length > MaximumRequestBytes) throw new DesktopApiException("request_too_large");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Content.Headers.ContentLength = bytes.Length;
        }
        else if (bodyJson is not null) throw new DesktopApiException("invalid_request");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400) throw new DesktopApiException("redirect_blocked", status, outcomeUnknown: mutation);
            JsonObject? payload = null;
            try
            {
                var bytes = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
                payload = JsonNode.Parse(Utf8.GetString(bytes)) as JsonObject;
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException) { }
            catch (DesktopApiException ex) when (ex.Code == "response_too_large")
            { throw new DesktopApiException("response_too_large", status, outcomeUnknown: mutation); }
            if (status != (int)HttpStatusCode.OK)
            {
                var detail = payload?["detail"] as JsonObject;
                var code = TryString(detail?["code"]);
                var decision = TryString(detail?["decision"]);
                if (DesktopApiException.SafeCode(code) == "server_error") code = status switch
                { 411 => "length_required", 413 => "request_too_large", 422 => "invalid_request", 429 => "rate_limited", >= 500 => "service_unavailable", _ => "server_error" };
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryDate && response.Headers.Date is { } serverDate)
                    retryAfter = retryDate - serverDate;
                if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
                throw new DesktopApiException(code!, status, decision, retryAfter, mutation && status >= 500);
            }
            if (payload is null) throw new DesktopApiException("invalid_response", status, outcomeUnknown: mutation);
            return payload;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DesktopApiException("timeout", outcomeUnknown: mutation); }
        catch (OperationCanceledException) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { throw new DesktopApiException("network_error", outcomeUnknown: mutation); }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new DesktopApiException("response_too_large");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > MaximumResponseBytes) throw new DesktopApiException("response_too_large");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static Uri ValidateBaseUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\') || value.Contains('%')
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri)))
            || uri.AbsolutePath.Contains('%')) throw new DesktopApiException("invalid_base_url");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static bool IsLoopback(Uri uri) => uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static void ValidateHeaderSecret(string value, string code)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256 || value.Any(c => c <= ' ' || c >= 127))
            throw new DesktopApiException(code);
    }

    private static void ValidateBusinessRoute(string method, string path)
    {
        if (string.IsNullOrEmpty(path) || path.Any(char.IsControl) || path.Contains('\\') || path.Contains('#') || path.Contains('%'))
            throw new DesktopApiException("invalid_path");
        const string root = "/api/desktop/";
        if (!path.StartsWith(root, StringComparison.Ordinal)) throw new DesktopApiException("invalid_path");
        var local = path[root.Length..];
        bool valid = (method, local) switch
        {
            ("GET", "cloud-consent" or "cloud-saves" or "characters" or "material-needs" or "material-needs/reference") => true,
            ("POST", "cloud-consent") => true,
            _ => false
        };
        if (!valid && method == "GET" && local.StartsWith("material-needs?saveId=", StringComparison.Ordinal))
            valid = IsId(local["material-needs?saveId=".Length..]);
        if (!valid)
        {
            var parts = local.Split('/');
            valid = parts.Length switch
            {
                2 when IsId(parts[1]) => (method, parts[0]) is ("PUT" or "DELETE", "cloud-saves") or ("GET", "operations"),
                3 when IsId(parts[1]) => (method, parts[0], parts[2]) is ("GET", "cloud-saves", "content")
                    or ("PATCH", "cloud-saves", "sync-state" or "main-state") or ("PUT", "characters", "main-save"),
                _ => false
            };
        }
        if (!valid) throw new DesktopApiException("invalid_path");
    }

    private static bool IsId(string value) => value.Length == 36 && Guid.TryParseExact(value, "D", out _);
    private static string? TryString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string ReadString(JsonObject json, string name) => TryString(json[name]) ?? throw new DesktopApiException("invalid_response");
    private static long ReadInteger(JsonObject json, string name) => json[name] is JsonValue value && value.TryGetValue<long>(out var number)
        ? number : throw new DesktopApiException("invalid_response");
    private static DateTimeOffset ReadTimestamp(JsonObject json, string name)
    {
        var text = ReadString(json, name);
        if (!Regex.IsMatch(text, @"T.*(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new DesktopApiException("invalid_response");
        return date;
    }

    private static DesktopAuthResponse ParseAuth(JsonObject json)
    {
        var decision = ReadString(json, "decision");
        if (decision != "allow") throw new DesktopApiException("server_error", decision: decision);
        if (ReadString(json, "reasonCode") != "ok" || json["quota"] is not JsonObject quota || json["user"] is not JsonObject user)
            throw new DesktopApiException("invalid_response");
        long interval = ReadInteger(json, "verificationIntervalSeconds"), id = ReadInteger(user, "id");
        long limit = ReadInteger(quota, "limit"), used = ReadInteger(quota, "used"), level = ReadInteger(quota, "level");
        if (interval is <= 0 or > int.MaxValue || id <= 0 || limit is < 0 or > int.MaxValue
            || used is < 0 or > int.MaxValue || level is < 0 or > int.MaxValue) throw new DesktopApiException("invalid_response");
        return new DesktopAuthResponse
        {
            ServerTime = ReadTimestamp(json, "serverTime"), ExpiresAt = ReadTimestamp(json, "expiresAt"),
            SessionExpiresAt = ReadTimestamp(json, "sessionExpiresAt"), VerificationIntervalSeconds = (int)interval,
            Quota = new DesktopApiQuota { Limit = (int)limit, Used = (int)used, Level = (int)level },
            User = new DesktopApiUser { Id = id, Nickname = ReadString(user, "nickname"), Role = ReadString(user, "role") }
        };
    }

    public void Dispose() { ClearCredentials(); _http.Dispose(); }
}

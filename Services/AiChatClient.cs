using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Grimmcair.Desktop.Services;

public sealed record AiConnection(string BaseUrl, string ApiKey, string Model, bool ThinkingEnabled = false, bool ThinkingVerified = false)
{
    public override string ToString() => "AI connection (credentials hidden)";
}

public sealed record AiToolCall(string Id, string Name, string Arguments);
public sealed record AiCompletion(string Content, IReadOnlyList<AiToolCall> ToolCalls, string FinishReason,
    [property: JsonIgnore] string? ReasoningContent = null)
{
    public override string ToString() => $"AI completion ({FinishReason}, {ToolCalls.Count} tool calls)";
}

public sealed class AiChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    // Private protocol state: only the explicit encrypted context store may persist this field.
    // Default serialization must not put it in visible history or diagnostic snapshots.
    [JsonIgnore]
    public string? ReasoningContent { get; set; }
    public string? ToolCallId { get; set; }
    public List<AiToolCall> ToolCalls { get; set; } = [];
}

// Deliberately excludes provider response bodies, URLs, credentials and inner exceptions.
public sealed class AiChatException(string code, int? statusCode = null) : Exception(SafeMessage(code, statusCode))
{
    public string Code { get; } = code;
    public int? StatusCode { get; } = statusCode;

    private static string SafeMessage(string code, int? status) => code switch
    {
        "invalid_base_url" => "请填写 HTTPS API 地址；本机服务也可以使用 localhost 的 HTTP 地址。地址不能包含账号、查询参数或片段。",
        "invalid_key" => "请填写有效的 API 密钥，密钥不能包含空白或换行。",
        "invalid_model" => "请先选择模型。",
        "invalid_request" => "对话或工具参数无效，请新建对话后重试。",
        "request_too_large" => "对话内容过长，请新建对话后重试。",
        "response_too_large" => "服务返回的内容过长，已停止接收。",
        "invalid_response" => "服务返回的格式不符合 OpenAI 兼容接口，请检查地址和模型。",
        "incomplete_stream" => "回答传输提前结束，当前内容可能不完整。请检查连接后重新提问。",
        "invalid_tool_call" => "模型返回的知识库调用不完整或不受支持，本次未执行调用。",
        "truncated_tool_call" => "服务在查询参数传输完成前截断或拦截了回答，本次未执行该批调用。已收到的文字已保留，请缩小问题范围后重试。",
        "stream_required" => "服务没有返回流式回答，请选择支持流式输出的聊天模型。",
        "redirect_blocked" => "服务要求跳转到其他地址，已停止请求。请直接填写最终 API 地址。",
        "timeout" => "等待 AI 服务超时，已停止本次请求。",
        "network_error" => "无法连接 AI 服务或连接中断，请检查地址和网络。",
        "provider_error" => "AI 服务返回错误，请检查模型、额度及服务状态。",
        "http_error" when status is 401 or 403 => "AI 服务拒绝认证，请检查密钥和模型访问权限。",
        "http_error" when status == 429 => "AI 服务暂时限流或额度不足，请稍后检查服务状态。",
        "http_error" => $"AI 服务请求失败（HTTP {status}），请检查地址、模型及服务状态。",
        _ => "AI 请求未完成，请检查连接设置。"
    };
}

/// <summary>Small OpenAI-compatible Chat Completions client. It never executes tools or retries requests.</summary>
public sealed class AiChatClient : IDisposable
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumStreamCharacters = 1024 * 1024;
    private const int MaximumEventCharacters = 128 * 1024;
    private const int MaximumContentCharacters = 256 * 1024;
    private const int MaximumReasoningCharacters = 256 * 1024;
    private const int MaximumToolArguments = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public AiChatClient()
    {
        _http = new HttpClient(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = true;
    }

    // Injection is intended for synthetic protocol checks; callers own their client/handler.
    public AiChatClient(HttpClient httpClient) => _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    internal static HttpClientHandler CreateHandler() => new()
    { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };

    public static bool CanControlThinking(AiConnection connection) => CanControlThinking(connection.BaseUrl);

    public static bool CanControlThinking(string? baseUrl)
    {
        try
        {
            return string.Equals(new Uri(NormalizeBaseUrl(baseUrl!)).Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase);
        }
        catch (AiChatException) { return false; }
    }

    public static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Length > 2048 ||
            baseUrl.Any(char.IsControl) || baseUrl.Contains('\\') ||
            !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new AiChatException("invalid_base_url");
        // Dot segments must be checked before Uri canonicalization removes them.
        string decoded;
        try { decoded = Uri.UnescapeDataString(baseUrl.Trim()); }
        catch (UriFormatException) { throw new AiChatException("invalid_base_url"); }
        if (decoded.Any(char.IsControl) || decoded.Contains('\\') || decoded.Split('/').Any(x => x is "." or ".."))
            throw new AiChatException("invalid_base_url");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) path = path[..^17];
        else if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) path = path[..^7];
        if (path.Length == 0) path = "/v1";
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(AiConnection connection, CancellationToken ct = default)
    {
        using var timeout = CreateTimeout(ct, TimeSpan.FromSeconds(45));
        try
        {
            using var request = CreateRequest(connection, HttpMethod.Get, "models");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var root = JsonNode.Parse(bytes) as JsonObject;
            if (root?["data"] is not JsonArray data || data.Count > 10000) throw new AiChatException("invalid_response");
            var models = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in data)
            {
                if (item is not JsonObject model || !TryString(model["id"], out var id) || !IsIdentifier(id, 256))
                    throw new AiChatException("invalid_response");
                models.Add(id);
            }
            return models.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        { throw new AiChatException("timeout"); }
        catch (HttpRequestException) { throw new AiChatException("network_error"); }
        catch (IOException) { throw new AiChatException("network_error"); }
        catch (JsonException) { throw new AiChatException("invalid_response"); }
        catch (DecoderFallbackException) { throw new AiChatException("invalid_response"); }
    }

    public async Task<AiCompletion> CompleteStreamingAsync(AiConnection connection,
        IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools,
        Action<string> onDelta, CancellationToken ct = default, string? forcedTool = null,
        Action<string>? onReasoningDelta = null)
    {
        ArgumentNullException.ThrowIfNull(onDelta);
        using var timeout = CreateTimeout(ct, TimeSpan.FromMinutes(3));
        try
        {
            using var request = CreateRequest(connection, HttpMethod.Post, "chat/completions");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            // Match only the official host; arbitrary compatible providers receive no extra parameter.
            var isDeepSeek = CanControlThinking(connection);
            request.Content = new StringContent(BuildRequest(connection.Model, messages, tools, forcedTool,
                isDeepSeek ? connection.ThinkingEnabled : null), Utf8, "application/json");
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
            headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            EnsureSuccess(response);
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new AiChatException("stream_required");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new AiChatException("response_too_large");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var output = new StringBuilder();
            StringBuilder? reasoning = null;
            var eventData = new StringBuilder();
            var calls = new SortedDictionary<int, ToolBuilder>();
            string? finishReason = null;
            await foreach (var line in ReadLinesAsync(stream, timeout.Token).ConfigureAwait(false))
            {
                if (line.Length == 0)
                {
                    if (eventData.Length == 0) continue;
                    var data = eventData.ToString();
                    eventData.Clear();
                    if (data.Trim() == "[DONE]") break;
                    ReadChunk(data, output, calls, ref finishReason, ref reasoning, onDelta, onReasoningDelta);
                    continue;
                }
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                if (eventData.Length != 0) eventData.Append('\n');
                eventData.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
                if (eventData.Length > MaximumEventCharacters) throw new AiChatException("response_too_large");
            }
            // A final EOF without an SSE delimiter must not turn a partial event into a tool call.
            if (eventData.Length != 0 || finishReason is null) throw new AiChatException("incomplete_stream");
            // A finish_reason already closes the selected choice; some compatible providers omit [DONE].
            if (calls.Count > 0 && finishReason is "length" or "content_filter") throw new AiChatException("truncated_tool_call");
            if (calls.Count > 0 && finishReason != "tool_calls") throw new AiChatException("invalid_tool_call");
            if (finishReason == "tool_calls" && calls.Count == 0) throw new AiChatException("invalid_tool_call");
            var allowedNames = tools.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            var finalCalls = calls.Values.Select(x => x.Build(allowedNames)).ToArray();
            if (finalCalls.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != finalCalls.Length)
                throw new AiChatException("invalid_tool_call");
            if (forcedTool is not null && (finalCalls.Length != 1 || finalCalls[0].Name != forcedTool))
                throw new AiChatException("invalid_tool_call");
            return new AiCompletion(output.ToString(), finalCalls, finishReason, reasoning?.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        { throw new AiChatException("timeout"); }
        catch (HttpRequestException) { throw new AiChatException("network_error"); }
        catch (IOException) { throw new AiChatException("network_error"); }
        catch (JsonException) { throw new AiChatException("invalid_response"); }
        catch (DecoderFallbackException) { throw new AiChatException("invalid_response"); }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken ct, TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        linked.CancelAfter(duration);
        return linked;
    }

    private static HttpRequestMessage CreateRequest(AiConnection connection, HttpMethod method, string route)
    {
        var baseUrl = NormalizeBaseUrl(connection.BaseUrl);
        if (string.IsNullOrWhiteSpace(connection.ApiKey) || connection.ApiKey.Length > 4096 ||
            connection.ApiKey.Any(c => c <= 32 || c >= 127)) throw new AiChatException("invalid_key");
        var request = new HttpRequestMessage(method, baseUrl + "/" + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        return request;
    }

    private static string BuildRequest(string model, IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools, string? forcedTool, bool? deepSeekThinking)
    {
        if (!IsIdentifier(model, 256)) throw new AiChatException("invalid_model");
        if (messages.Count is < 1 or > 256 || tools.Count > 32) throw new AiChatException("invalid_request");
        var messageArray = new JsonArray();
        foreach (var message in messages)
            messageArray.Add(SerializeMessage(message, deepSeekThinking == true));
        var body = new JsonObject { ["model"] = model, ["messages"] = messageArray, ["stream"] = true };
        if (deepSeekThinking.HasValue) body["thinking"] = new JsonObject { ["type"] = deepSeekThinking.Value ? "enabled" : "disabled" };
        if (tools.Count > 0)
        {
            if (tools.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count) throw new AiChatException("invalid_request");
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                if (!IsToolName(tool.Name) || tool.Description.Length > 4096) throw new AiChatException("invalid_request");
                toolArray.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
                    { ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = tool.Parameters.DeepClone() } });
            }
            body["tools"] = toolArray;
            if (forcedTool is not null)
            {
                if (!tools.Any(x => x.Name == forcedTool)) throw new AiChatException("invalid_request");
                body["tool_choice"] = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = forcedTool } };
            }
        }
        else if (forcedTool is not null) throw new AiChatException("invalid_request");
        var serialized = body.ToJsonString();
        if (Utf8.GetByteCount(serialized) > MaximumResponseBytes) throw new AiChatException("request_too_large");
        return serialized;
    }

    // Also used by context selection to count the exact JSON bytes sent on the wire.
    internal static JsonObject SerializeMessage(AiChatMessage message, bool fillMissingReasoning = false)
    {
        if (message.Role is not ("system" or "user" or "assistant" or "tool") ||
            message.Content is null || message.Content.Length > MaximumContentCharacters || message.ToolCalls is null || message.ToolCalls.Count > 16)
            throw new AiChatException("invalid_request");
        var json = new JsonObject { ["role"] = message.Role, ["content"] = message.Content };
        if (message.ReasoningContent is not null)
        {
            if (message.Role != "assistant") throw new AiChatException("invalid_request");
            if (message.ReasoningContent.Length > MaximumReasoningCharacters) throw new AiChatException("request_too_large");
            json["reasoning_content"] = message.ReasoningContent;
        }
        else if (fillMissingReasoning && message.Role == "assistant") json["reasoning_content"] = "";
        if (message.Role == "tool")
        {
            if (!IsIdentifier(message.ToolCallId, 256)) throw new AiChatException("invalid_request");
            json["tool_call_id"] = message.ToolCallId;
        }
        else if (message.ToolCallId is not null) throw new AiChatException("invalid_request");
        if (message.ToolCalls.Count > 0)
        {
            if (message.Role != "assistant") throw new AiChatException("invalid_request");
            var toolCalls = new JsonArray();
            foreach (var call in message.ToolCalls)
            {
                if (!IsIdentifier(call.Id, 256) || !IsToolName(call.Name) || call.Arguments is null || call.Arguments.Length > MaximumToolArguments)
                    throw new AiChatException("invalid_request");
                toolCalls.Add(new JsonObject { ["id"] = call.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments } });
            }
            json["tool_calls"] = toolCalls;
        }
        return json;
    }

    private static void ReadChunk(string data, StringBuilder output, SortedDictionary<int, ToolBuilder> calls,
        ref string? finishReason, ref StringBuilder? reasoning, Action<string> onDelta, Action<string>? onReasoningDelta)
    {
        if (JsonNode.Parse(data) is not JsonObject root) throw new AiChatException("invalid_response");
        if (root["error"] is not null) throw new AiChatException("provider_error");
        if (root["choices"] is not JsonArray choices) throw new AiChatException("invalid_response");
        foreach (var node in choices)
        {
            if (node is not JsonObject choice || !TryInteger(choice["index"], out var index)) throw new AiChatException("invalid_response");
            if (index != 0) continue;
            if (finishReason is not null) throw new AiChatException("invalid_response");
            if (choice["delta"] is not JsonObject delta) throw new AiChatException("invalid_response");
            if (delta["reasoning_content"] is not null)
            {
                if (!TryString(delta["reasoning_content"], out var reasoningDelta)) throw new AiChatException("invalid_response");
                reasoning ??= new StringBuilder();
                if (reasoning.Length + reasoningDelta.Length > MaximumReasoningCharacters)
                    throw new AiChatException("response_too_large");
                reasoning.Append(reasoningDelta);
                if (reasoningDelta.Length > 0) onReasoningDelta?.Invoke(reasoningDelta);
            }
            if (delta["content"] is not null)
            {
                if (!TryString(delta["content"], out var text)) throw new AiChatException("invalid_response");
                AppendContent(text, output, onDelta);
            }
            if (delta["refusal"] is not null)
            {
                if (!TryString(delta["refusal"], out var refusal)) throw new AiChatException("invalid_response");
                AppendContent(refusal, output, onDelta);
            }
            if (delta["function_call"] is not null) throw new AiChatException("invalid_tool_call");
            if (delta["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var item in toolCalls)
                {
                    if (item is not JsonObject call || !TryInteger(call["index"], out var callIndex) || callIndex is < 0 or > 15)
                        throw new AiChatException("invalid_tool_call");
                    if (!calls.TryGetValue(callIndex, out var builder)) calls[callIndex] = builder = new ToolBuilder();
                    builder.Append(call);
                }
            }
            else if (delta["tool_calls"] is not null) throw new AiChatException("invalid_tool_call");
            if (choice["finish_reason"] is not null)
            {
                if (!TryString(choice["finish_reason"], out var reason) || reason is not ("stop" or "tool_calls" or "length" or "content_filter"))
                    throw new AiChatException("invalid_response");
                finishReason = reason;
            }
        }
    }

    private static void AppendContent(string text, StringBuilder output, Action<string> onDelta)
    {
        if (text.Length == 0) return;
        if (output.Length + text.Length > MaximumContentCharacters) throw new AiChatException("response_too_large");
        output.Append(text);
        onDelta(text);
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var buffer = new char[1024];
        var line = new StringBuilder();
        int total = 0;
        bool afterCr = false;
        while (true)
        {
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            int count = await reader.ReadAsync(buffer.AsMemory(), idleTimeout.Token).ConfigureAwait(false);
            if (count == 0)
            {
                if (line.Length > 0) yield return line.ToString();
                yield break;
            }
            total += count;
            if (total > MaximumStreamCharacters) throw new AiChatException("response_too_large");
            for (int i = 0; i < count; i++)
            {
                char c = buffer[i];
                if (afterCr && c == '\n') { afterCr = false; continue; }
                afterCr = c == '\r';
                if (c is '\r' or '\n')
                {
                    yield return line.ToString();
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                    if (line.Length > MaximumEventCharacters) throw new AiChatException("response_too_large");
                }
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) throw new AiChatException("response_too_large");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaximumResponseBytes) throw new AiChatException("response_too_large");
            output.Write(buffer, 0, count);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400) throw new AiChatException("redirect_blocked");
        if (!response.IsSuccessStatusCode) throw new AiChatException("http_error", status);
    }

    private static bool TryString(JsonNode? node, out string text)
    {
        text = "";
        return node is JsonValue value && value.TryGetValue(out text!) && text is not null;
    }
    private static bool TryInteger(JsonNode? node, out int value)
    { value = 0; return node is JsonValue json && json.TryGetValue(out value); }
    private static bool IsIdentifier(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool IsToolName(string? value) => IsIdentifier(value, 64) && value!.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private sealed class ToolBuilder
    {
        private readonly StringBuilder _id = new();
        private readonly StringBuilder _name = new();
        private readonly StringBuilder _arguments = new();

        public void Append(JsonObject call)
        {
            if (call["type"] is not null && (!TryString(call["type"], out var type) || type != "function")) throw new AiChatException("invalid_tool_call");
            AppendString(call["id"], _id, 256);
            if (call["function"] is JsonObject function)
            {
                AppendString(function["name"], _name, 64);
                AppendString(function["arguments"], _arguments, MaximumToolArguments);
            }
            else if (call["function"] is not null) throw new AiChatException("invalid_tool_call");
        }

        public AiToolCall Build(HashSet<string> allowedNames)
        {
            var id = _id.ToString();
            var name = _name.ToString();
            var arguments = _arguments.ToString();
            if (!IsIdentifier(id, 256) || !allowedNames.Contains(name)) throw new AiChatException("invalid_tool_call");
            try
            {
                if (JsonNode.Parse(arguments) is not JsonObject) throw new AiChatException("invalid_tool_call");
            }
            catch (JsonException) { throw new AiChatException("invalid_tool_call"); }
            return new AiToolCall(id, name, arguments);
        }

        private static void AppendString(JsonNode? node, StringBuilder builder, int limit)
        {
            if (node is null) return;
            if (!TryString(node, out var text) || builder.Length + text.Length > limit) throw new AiChatException("invalid_tool_call");
            builder.Append(text);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        if (_ownsClient) _http.Dispose();
        // Keep the cancellation source alive until active operations finish; it owns no wait handle.
    }
}

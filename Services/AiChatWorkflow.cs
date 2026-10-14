using System.Text;
using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

/// <summary>Only the bundled, read-only knowledge MCP is exposed to the model.</summary>
public sealed class AiChatWorkflow
{
    private const int MaximumContextMessages = 192;
    private const int MaximumContextBytes = 768 * 1024;
    private const int MaximumToolCalls = AiKnowledgeSkill.MaximumToolCalls;
    private const int MaximumToolRounds = MaximumToolCalls; // One-call rounds can use the full budget before one final request.
    private const int MaximumCallsPerBatch = AiKnowledgeSkill.MaximumCallsPerBatch;
    // Twenty-four completed single-call rounds, a final accepted batch of sixteen, and the final answer.
    internal const int MaximumProtocolMessages = MaximumToolCalls * 2 + 16;
    private const int SummarizeAfterResultCharacters = 120_000;
    private const int MaximumResultCharacters = 150_000;
    private const int SummarizeAfterContextBytes = 1536 * 1024;
    private const int MaximumFinalContextBytes = 2 * 1024 * 1024 - 4096;
    private const string OmittedToolResult = "{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"本次资料查询已达到次数、批次或资料容量限制，该调用未执行或结果未纳入。请仅依据此前成功取得的资料作答，并明确尚未确认的部分。\"}]}";
    private const string FinalInstructions = """
        本轮资料查询已经结束，不再提供工具。请现在直接回答用户原来的问题，综合此前已经成功取得的资料，比较、归纳并给出结论，不能只说准备继续查询。
        明确区分已确认的内容和仍缺资料的部分；未执行的查询、报错或未纳入的结果不能当作证据。资料不足时说明具体哪些问题还不能回答，并建议怎样缩小下一次问题范围。
        不要编造尚未取得的资料，不要声称执行过被省略的查询，不要输出工具调用或继续等待工具结果。
        """;

    internal delegate Task<AiCompletion> CompletionOperation(IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools, Action<string> onText, CancellationToken cancellationToken, Action<string>? onReasoning);

    public async Task<IReadOnlyList<AiChatMessage>> RunAsync(AiConnection connection, IReadOnlyList<AiChatMessage> history,
        Action<string> onText, Action<string> onStatus, Action<string> onSource,
        CancellationToken cancellationToken, bool test = false, Action<string>? onReasoning = null,
        Action<string, string>? onKnowledgeResult = null)
    {
        using var client = new AiChatClient();
        await using var knowledge = new AiKnowledgeMcpClient();
        onStatus("正在连接本地知识库……");
        var tools = await knowledge.ListToolsAsync(cancellationToken);
        return await RunWithServicesAsync(history, tools,
            (messages, offeredTools, text, token, reasoning) => client.CompleteStreamingAsync(connection, messages, offeredTools, text, token, onReasoningDelta: reasoning),
            knowledge.CallToolAsync, onText, onStatus, onSource, cancellationToken, test, onReasoning, onKnowledgeResult,
            knowledgeSkill: AiKnowledgeSkill.Load());
    }

    // The same bounded orchestration is exercised with in-memory services by the regression checks.
    internal static async Task<IReadOnlyList<AiChatMessage>> RunWithServicesAsync(IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools, CompletionOperation complete,
        Func<string, JsonObject, CancellationToken, Task<string>> callTool,
        Action<string> onText, Action<string> onStatus, Action<string> onSource,
        CancellationToken cancellationToken, bool test = false, Action<string>? onReasoning = null,
        Action<string, string>? onKnowledgeResult = null, string? knowledgeSkill = null)
    {
        // Let the model select the diagnostic tool itself. Named/required tool_choice
        // is rejected by some otherwise compatible providers in thinking mode.
        if (test) tools = tools.Where(tool => tool.Name == "knowledge_status").ToArray();
        var allowed = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (!allowed.Contains("knowledge_status")) throw new InvalidOperationException("本地知识库尚未准备好。");
        // The request context is bounded; stored conversation history is never truncated.
        var instructions = knowledgeSkill ?? AiKnowledgeSkill.BehaviorRules;
        var context = new List<AiChatMessage> { new() { Role = "system", Content = instructions } };
        context.AddRange(history);
        var currentQuestionIndex = context.FindLastIndex(message => message.Role == "user");
        if (currentQuestionIndex < 1) throw new AiChatException("invalid_request");
        var currentTurn = new List<AiChatMessage>();
        var toolCount = 0;
        var statusVerified = false;
        var resultCharacters = 0;
        var summaryRequired = false;
        for (var round = 0; round <= MaximumToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var finalOnly = summaryRequired || round == MaximumToolRounds || toolCount >= MaximumToolCalls
                || resultCharacters >= SummarizeAfterResultCharacters || ContextBytes(context) >= SummarizeAfterContextBytes;
            var requestContext = context;
            if (finalOnly)
            {
                onStatus(test ? "正在用已取得资料整理测试回答……" : "查询已结束，正在用已取得资料整理最终回答……");
                // Alter only this request's system instruction. Stored assistant/tool history remains unchanged.
                requestContext = [new() { Role = "system", Content = instructions + "\n\n" + FinalInstructions }];
                requestContext.AddRange(context.Skip(1));
                var trimmedHistory = false;
                while (ContextBytes(requestContext) > MaximumFinalContextBytes || requestContext.Count > 256)
                {
                    // Keep the latest question and every message of this turn, including private reasoning.
                    // Only complete older user turns may be removed to make room for the final request.
                    var nextUser = context.FindIndex(2, message => message.Role == "user");
                    if (currentQuestionIndex <= 1 || nextUser < 2 || nextUser > currentQuestionIndex)
                        throw new InvalidOperationException("本轮问题与资料过长，无法在接口限制内完整整理。已收到的文字已保留，请缩小问题范围或新建对话。");
                    context.RemoveRange(1, nextUser - 1);
                    currentQuestionIndex -= nextUser - 1;
                    requestContext.RemoveRange(1, nextUser - 1);
                    trimmedHistory = true;
                }
                if (trimmedHistory) onStatus("本次省略了较早的完整对话，正在用本轮已取得资料整理最终回答……");
            }
            else onStatus(test ? "正在等待模型测试响应……" : "正在等待模型响应……");
            bool visibleStarted = false, reasoningStarted = false;
            AiCompletion answer;
            try
            {
                answer = await complete(requestContext, finalOnly ? Array.Empty<AiToolDefinition>() : tools, text =>
                {
                    if (!visibleStarted)
                    {
                        visibleStarted = true;
                        onStatus(test ? "正在核对测试回答……" : finalOnly ? "正在整理最终回答……" : "正在整理回答……");
                    }
                    onText(text);
                }, cancellationToken, delta =>
                {
                    if (!reasoningStarted)
                    {
                        reasoningStarted = true;
                        onStatus(test ? "模型正在思考测试问题……" : "模型正在思考……");
                    }
                    onReasoning?.Invoke(delta);
                });
            }
            catch (AiChatException error) when (finalOnly)
            { throw new InvalidOperationException("资料查询已结束，但最终整理未完成。" + error.Message + " 已收到的文字已保留。"); }
            var assistant = new AiChatMessage { Role = "assistant", Content = answer.Content,
                ReasoningContent = answer.ReasoningContent, ToolCalls = answer.ToolCalls.ToList() };
            if (answer.FinishReason is "length" or "content_filter")
                throw new InvalidOperationException(finalOnly
                    ? "最终回答被服务截断或拦截，已收到的文字已保留。请缩小问题范围后继续。"
                    : "回答被服务截断或拦截，已收到的文字已保留。可以缩短问题后继续。");
            if (answer.ToolCalls.Count == 0)
            {
                if (test && !statusVerified) throw new InvalidOperationException("模型没有成功查询本地知识库，测试未通过。请重试或选择支持工具调用的模型。");
                if (string.IsNullOrWhiteSpace(answer.Content)) throw new InvalidOperationException("模型没有返回可显示的回答，请检查所选模型。");
                currentTurn.Add(assistant);
                onStatus(test ? "测试通过：流式回答与本地知识库调用均正常。" : "回答已完成，正在保存……");
                return currentTurn;
            }
            if (finalOnly)
                throw new InvalidOperationException("资料查询已结束，但模型仍请求工具，未完成最终回答。已收到的文字已保留，请缩小问题范围后重试。");
            if (answer.Content.Length > 0) onText("\n\n");
            context.Add(assistant);
            currentTurn.Add(assistant);
            for (var callIndex = 0; callIndex < answer.ToolCalls.Count; callIndex++)
            {
                var call = answer.ToolCalls[callIndex];
                cancellationToken.ThrowIfCancellationRequested();
                if (!allowed.Contains(call.Name)) throw new InvalidOperationException("模型请求了未开放的工具，本次查询已停止。");
                JsonObject args;
                try { args = JsonNode.Parse(call.Arguments) as JsonObject ?? throw new FormatException(); }
                catch { throw new InvalidOperationException("模型返回的知识库查询参数无效，请重新提问。"); }
                if (toolCount >= MaximumToolCalls || callIndex >= MaximumCallsPerBatch) summaryRequired = true;
                var result = OmittedToolResult;
                if (!summaryRequired)
                {
                    onStatus(call.Name == "get_knowledge_entry" ? "正在阅读资料……" : "正在查询本地知识库……");
                    var queried = await callTool(call.Name, args, cancellationToken);
                    toolCount++;
                    // Leave room to close every already-accepted call with a bounded error result if necessary.
                    var reserved = (answer.ToolCalls.Count - callIndex - 1) * OmittedToolResult.Length;
                    if (queried.Length <= MaximumResultCharacters - resultCharacters - reserved) result = queried;
                    else summaryRequired = true;
                }
                resultCharacters += result.Length;
                if (resultCharacters >= SummarizeAfterResultCharacters) summaryRequired = true;
                var toolMessage = new AiChatMessage { Role = "tool", ToolCallId = call.Id, Content = result };
                context.Add(toolMessage);
                currentTurn.Add(toolMessage);
                var toolFailed = JsonNode.Parse(result)?["isError"]?.GetValue<bool>() == true;
                if (!toolFailed) onKnowledgeResult?.Invoke(call.Name, result);
                if (!toolFailed && call.Name == "knowledge_status") statusVerified = true;
                if (!toolFailed && call.Name == "get_knowledge_entry" && args["id"] is { } id) onSource(id.ToString());
                else if (!toolFailed && call.Name == "search_knowledge" && args["query"] is { } query) onSource("搜索：" + query.ToString());
            }
        }
        throw new InvalidOperationException("最终回答未完成，已收到的文字已保留。请缩小问题范围后继续。");
    }

    public static string GetContextOwner(AiConnection connection, string? knowledgePackageSha256 = null)
    {
        var packageSha256 = knowledgePackageSha256 ?? ResourcePack.Current.PackageSha256;
        if (packageSha256.Length != 64 || !packageSha256.All(char.IsAsciiHexDigit))
            throw new ArgumentException("资料包身份必须是完整 SHA-256。", nameof(knowledgePackageSha256));
        return AiChatClient.NormalizeBaseUrl(connection.BaseUrl) + "\n" + connection.Model + "\n" + packageSha256.ToLowerInvariant();
    }

    private static long ContextBytes(IReadOnlyList<AiChatMessage> messages) => 2 + messages.Sum(message =>
        (long)Encoding.UTF8.GetByteCount(AiChatClient.SerializeMessage(message, fillMissingReasoning: true).ToJsonString()) + 1);

    public static IReadOnlyList<AiChatMessage> BuildContext(IReadOnlyList<AiStoredMessage> stored, out bool limited,
        AiConnection? connection = null, string? knowledgePackageSha256 = null)
    {
        var result = new List<AiChatMessage>();
        var owner = connection is null ? null : GetContextOwner(connection, knowledgePackageSha256);
        var bytes = 2; // JSON array brackets; leave additional space for the system prompt and this turn's tools.
        limited = false;
        for (var index = stored.Count - 1; index >= 0;)
        {
            AiStoredMessage? assistant = null;
            if (stored[index].Role == "assistant" && index > 0 && stored[index - 1].Role == "user")
                assistant = stored[index--];
            var user = stored[index--];
            if (user.Role != "user" || string.IsNullOrEmpty(user.Content)) continue;
            var turn = new List<AiChatMessage> { new() { Role = "user", Content = user.Content } };
            if (assistant is not null)
            {
                // Older two-part owners cannot establish which public data produced their tool evidence.
                // Keep visible conversation text, but only replay private protocol from this exact package.
                if (assistant.Status == "complete" && owner is not null && assistant.ContextOwner == owner &&
                    TryCloneProtocol(assistant.ContextMessages, out var protocol))
                    turn.AddRange(protocol);
                else if (!string.IsNullOrEmpty(assistant.Content))
                    turn.Add(new AiChatMessage { Role = "assistant", Content = assistant.Content });
            }
            long turnBytes;
            try
            {
                // Count explicit reasoning/tool payloads, not default serialization (which omits private reasoning).
                // Including empty assistant reasoning is conservative for providers that do not require it.
                turnBytes = turn.Sum(message => (long)Encoding.UTF8.GetByteCount(
                    AiChatClient.SerializeMessage(message, fillMissingReasoning: true).ToJsonString()) + 1);
            }
            catch (AiChatException) { turnBytes = long.MaxValue; }
            if (result.Count + turn.Count > MaximumContextMessages || turnBytes > MaximumContextBytes - bytes)
            {
                if (result.Count == 0) throw new AiChatException("request_too_large");
                limited = true;
                break; // Never cut through a user/assistant/tool turn, or discard the newest question silently.
            }
            result.InsertRange(0, turn);
            bytes += (int)turnBytes;
        }
        return result;
    }

    private static bool TryCloneProtocol(IReadOnlyList<AiChatMessage>? messages, out List<AiChatMessage> copy)
    {
        copy = [];
        if (messages is null || messages.Count is < 1 or > MaximumProtocolMessages) return false;
        int index = 0;
        try
        {
            while (index < messages.Count)
            {
                var assistant = messages[index++];
                if (assistant is null || assistant.Role != "assistant" || assistant.ToolCallId is not null) return false;
                _ = AiChatClient.SerializeMessage(assistant);
                copy.Add(CloneMessage(assistant));
                if (assistant.ToolCalls.Count == 0)
                    return index == messages.Count && !string.IsNullOrWhiteSpace(assistant.Content);
                var pending = assistant.ToolCalls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
                if (pending.Count != assistant.ToolCalls.Count) return false;
                foreach (var call in assistant.ToolCalls)
                    if (JsonNode.Parse(call.Arguments) is not JsonObject) return false;
                while (pending.Count > 0)
                {
                    if (index >= messages.Count) return false;
                    var tool = messages[index++];
                    if (tool is null || tool.Role != "tool" || tool.ToolCalls.Count != 0 || tool.ReasoningContent is not null ||
                        tool.ToolCallId is null || !pending.Remove(tool.ToolCallId)) return false;
                    _ = AiChatClient.SerializeMessage(tool);
                    copy.Add(CloneMessage(tool));
                }
            }
        }
        catch (Exception error) when (error is AiChatException or System.Text.Json.JsonException or NullReferenceException)
        { return false; }
        return false; // A complete turn ends with an assistant answer, never an outstanding tool result.
    }

    private static AiChatMessage CloneMessage(AiChatMessage value) => new()
    {
        Role = value.Role, Content = value.Content, ReasoningContent = value.ReasoningContent,
        ToolCallId = value.ToolCallId, ToolCalls = value.ToolCalls.ToList()
    };
}

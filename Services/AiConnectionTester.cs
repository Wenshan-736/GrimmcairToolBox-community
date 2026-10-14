namespace Grimmcair.Desktop.Services;

internal sealed record AiConnectionTestResult(AiConnection Connection, string Status);

/// <summary>Checks normal access first, then independently verifies the optional thinking capability.</summary>
internal sealed class AiConnectionTester
{
    private readonly Func<AiConnection, Action<string>, Action<string>, CancellationToken, Task> runTest;

    // The injected operation has the same success contract as Workflow(test: true): a successful
    // knowledge_status tool result and a final visible answer, with no reasoning retained here.
    internal AiConnectionTester(Func<AiConnection, Action<string>, Action<string>, CancellationToken, Task>? runTest = null) =>
        this.runTest = runTest ?? RunWorkflowAsync;

    internal async Task<AiConnectionTestResult> TestAsync(AiConnection connection, Action<string> onStatus, CancellationToken cancellationToken)
    {
        var basic = connection with { ThinkingEnabled = false, ThinkingVerified = false };
        cancellationToken.ThrowIfCancellationRequested();
        onStatus("正在测试普通回答与本地知识库调用……");
        await runTest(basic, status => onStatus("普通回答测试 · " + status), _ => { }, cancellationToken);
        if (!AiChatClient.CanControlThinking(basic))
            return new(basic, "普通回答测试通过：流式响应和本地 MCP 均正常，可以保存。此接口暂未提供可切换的深度思考能力检测。");

        var receivedReasoning = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            onStatus("普通回答已通过，正在检测深度思考能力……");
            await runTest(basic with { ThinkingEnabled = true }, status => onStatus("深度思考测试 · " + status), delta =>
            {
                if (!string.IsNullOrWhiteSpace(delta)) receivedReasoning = true;
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return new(basic, "普通回答测试已通过，可以保存。深度思考检测已停止或超时，本次未验证通过；可以稍后重新测试。");
        }
        catch (Exception)
        {
            // A service/network/tool failure is not evidence that the selected model lacks thinking.
            // Preserve the independently verified normal mode, without exposing provider diagnostics.
            return new(basic, "普通回答测试已通过，可以保存。深度思考检测未完成，本次未验证通过；请检查连接与服务状态后重新测试。");
        }
        return receivedReasoning
            ? new(basic with { ThinkingVerified = true }, "普通回答与深度思考测试均通过：流式响应和本地 MCP 均正常。保存后可在每个对话中选择是否开启深度思考。")
            : new(basic, "普通回答测试已通过，可以保存。深度思考检测未收到思考响应，本次未验证通过；可以稍后重新测试。");
    }

    private static async Task RunWorkflowAsync(AiConnection connection, Action<string> onStatus, Action<string> onReasoning, CancellationToken cancellationToken)
    {
        await new AiChatWorkflow().RunAsync(connection,
            [new AiChatMessage { Role = "user", Content = "请调用 knowledge_status 查看本地知识库状态，再用一句简短中文确认连接正常。" }],
            _ => { }, onStatus, _ => { }, cancellationToken, test: true, onReasoning: onReasoning);
    }
}

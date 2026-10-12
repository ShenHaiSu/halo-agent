# Stage 03 - 04：跨线程流式节流推送、取消控制与 Stage 03 验收标准

> **所属阶段**：Stage 03
> **核心目标**：确保双协议流式输出在 Win7 低配 CPU 与核显关闭加速的环境下依然保持 60fps 丝滑渲染，支持毫秒级随时中断，并完成 Stage 03 验收。

---

## 一、 高性能 SSE 行读取器（`SseStreamReader.cs`）

为降低 GC 压力并兼容不同网关的换行符（`\n` 或 `\r\n`），实现基于 `StreamReader` 的标准 SSE 帧状态机：

```csharp
namespace Host.Desktop.LlmGateway.Sse;

public readonly record struct SseFrame(string? EventType, string Data);

public static class SseStreamReader
{
    public static async IAsyncEnumerable<SseFrame> ReadFramesAsync(
        Stream responseStream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(responseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        string? currentEvent = null;
        var dataBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync().WaitAsync(ct).ConfigureAwait(false);
            if (line is null) // EOF
            {
                if (dataBuilder.Length > 0)
                {
                    yield return new SseFrame(currentEvent, dataBuilder.ToString());
                }
                yield break;
            }

            if (line.Length == 0) // 空行标志一个 SSE Event Frame 结束
            {
                if (dataBuilder.Length > 0)
                {
                    yield return new SseFrame(currentEvent, dataBuilder.ToString());
                    dataBuilder.Clear();
                }
                currentEvent = null;
                continue;
            }

            if (line.StartsWith(':')) // SSE 注释心跳行（如 ": keep-alive"），直接忽略
                continue;

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string payload = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                dataBuilder.Append(payload);
            }
        }
    }
}
```

---

## 二、 16ms 帧合并推送与毫秒级取消（Cancellation）

### 1. 增量文本合并优化（Delta Coalescing）
在 `JsEventDispatcher` 队列消费周期（`16ms`）内，如果同一个 `SessionId` 连续产生了 15 个 `TextDelta`（如 `"你"`, `"好"`, `"，"`, `"我"`...），推送器在出队时**自动将连续的同类型 `TextDelta` / `ReasoningDelta` 字符串拼接为 1 个合并事件**再发往前端 Vue，使前端 DOM 重绘与 Markdown 解析次数降低 85% 以上，极大缓解 Win7 纯 CPU 软件渲染压力。

### 2. 隨時中止生成（`CancelGeneration`）
* 每个正在运行的会话请求关联一个独立的 `CancellationTokenSource`（存储在 `ConcurrentDictionary<string, CancellationTokenSource> _activeSessionTokens`）。
* 用户在前端点击 `[停止生成]` 按钮 -> 调用 `window.hostBridge.cancelSessionRun(sessionId)` -> C# 立即触发 `cts.Cancel()` -> `HttpClient` 底层 Socket 流立即断开，保留已生成的半截文本与思考内容落盘，并向前端推送 `StreamCancelled` 状态。

---

## 三、 LLM 链路追踪审计日志（`llm-trace-YYYYMMDD.log`）

每次调用 `LlmGatewayClient.StreamAsync` 时，自动在 `<DataRootPath>/logs/llm-trace-YYYYMMDD.log` 中写入结构化审计记录：
* 请求开始：`[REQ] reqId=4f82... | session=sess_01 (hash=a94f8c2d1e07b35a) | provider=OpenAI | model=gpt-4o | protocol=CHAT | url=https://.../v1/chat/completions | proxy=SYSTEM(http://127.0.0.1:7890) | customHeaders=[X-Session-Hash=a94f8c2d1e07b35a]`
* 响应完成：`[RES] reqId=4f82... | status=200 | ttftMs=412 | totalMs=3280 | inTokens=2048 (cached=1536, hitRate=75.0%) | outTokens=310 | toolCalls=1(exec_command)`

---

## 四、 Stage 03 验收标准（Acceptance Criteria）

| 验收编号 | 验收维度 | 具体测试步骤 | 预期通过标准 |
| :--- | :--- | :--- | :--- |
| **S03-AC01** | **`chat` 协议流式文本与 ToolCall 验证** | 选择配置为 `chat` 协议的模型，发送流式文本对话及触发工具调用的指令 | 1. 正常请求 `/v1/chat/completions`<br>2. 流式文本与思考过程 (`reasoning_content`) 实时逐字平滑显示<br>3. 分片 `delta.tool_calls` 完整拼合出 `CallId`、`FunctionName` 与合法的 JSON `Arguments` |
| **S03-AC02** | **`resp` 协议流式文本与 ToolCall 验证** | 选择配置为 `resp` 协议的模型，发送流式文本对话及包含多轮 `function_call_output` 的请求 | 1. 正常请求 `/v1/responses`，请求体包含 `instructions`、`input` 与扁平化 `tools`<br>2. 正确解析 `response.output_text.delta` 与 `response.function_call_arguments.done`<br>3. 多轮工具结果回传后模型能继续正常回答 |
| **S03-AC03** | **自定义 Header 与 Cache 命中统计** | 检查 `logs/llm-trace-YYYYMMDD.log` 与前端消息底部的 Token 统计栏 | 日志与 UI 均正确展示 `Input Tokens`、`Output Tokens` 以及 `Cached Tokens`（缓存命中数），且请求头中携带了已解析的 `${session-hash}` |
| **S03-AC04** | **流式生成中途随时取消** | 在模型流式长文输出过程中点击“停止”按钮 | 100ms 内流式输出立即停止，底层 HTTP 连接关闭，已输出内容不丢失，界面恢复可输入状态 |

# Stage 03 - 01：OpenAI 双协议（Chat Completions & Responses）流式网关（前置检查与当期目标）

> **所属阶段**：Stage 03（双协议 LLM 网关与流式归一化引擎）
> **依赖前置**：Stage 02 已通过全部验收项（`HaloHttpClientFactory`、`ProviderConfig`、`HeaderVariableResolver` 已就绪）
> **直接覆盖核心需求**：需求 #2（支持 OpenAI 的 `chat` 协议以及 `response` 协议）、需求 #7（按 Model 配置协议与 `maxOutputTokens`）

---

## 一、 前置检查（Preflight Checklist）

1. **Stage 02 网络与配置基线确认**：
   * [x] `HaloHttpClientFactory.CreateClient(provider)` 已能根据 Provider 代理策略返回配置好 `SocketsHttpHandler` 的 `HttpClient`。
   * [x] `HeaderVariableResolver.ApplyCustomHeaders` 已能将 `${session-hash}` 等预设变量注入 `HttpRequestMessage`。
   * [x] `ModelConfig.Protocol` 已区分 `LlmProtocolType.Chat` (`chat`) 与 `LlmProtocolType.Resp` (`resp`)。
2. **CEF 线程与流式传输规范确认**：
   * [x] 严格遵循 `CEFSHARP_ARCHITECTURE_STANDARD.md` 第四章第 2 条：LLM 网络流式读取必须在后台异步线程（`HttpCompletionOption.ResponseHeadersRead`）执行，通过 `JsEventDispatcher` 批量推送至前端，严禁阻塞主线程或 IPC 方法调用。

---

## 二、 为什么需要“双协议统一中间层（Unified IR）”？

OpenAI 生态目前并存两套主流 API 协议，二者在**请求结构、工具定义格式、多轮 Tool Result 回传方式、以及 SSE 流式事件格式**上存在根本性差异：

| 对比维度 | OpenAI `Chat Completions` (`chat` 协议) | OpenAI `Responses API` (`resp` 协议) |
| :--- | :--- | :--- |
| **HTTP 端点路径** | `POST /v1/chat/completions` | `POST /v1/responses` |
| **系统提示词字段** | `messages` 数组中的 `{ "role": "system" \| "developer", "content": "..." }` | 顶层字段 `"instructions": "..."` 或 `input` 中的 developer message |
| **对话历史结构** | `messages: [...]`（Assistant 含 `tool_calls` 数组，Tool 结果为 `role: "tool", tool_call_id: "..."`） | `input: [...]`（扁平化 Item 列表：`type: "message"`、`type: "function_call"`、`type: "function_call_output"`） |
| **Tools 定义结构** | 嵌套式：`{ "type": "function", "function": { "name", "description", "parameters" } }` | 扁平式：`{ "type": "function", "name", "description", "parameters" }` |
| **最大输出 Token 字段** | `max_tokens`（或 `max_completion_tokens`） | `max_output_tokens` |
| **SSE 流式帧结构** | 仅 `data: {...}`（解析 `choices[0].delta.content` 与 `delta.tool_calls[index]`） | 标准 `event: <type>\ndata: {...}`（如 `response.output_text.delta`、`response.function_call_arguments.delta`） |

**架构决策**：
在上层 Agent 编排器（Stage 06）与底层双协议之间建立 **`ILlmProtocolAdapter` 统一中间表示层（Unified IR）**。无论是会话持久化（Stage 04）、工具调用分发（Stage 05）还是前端界面渲染，全部只与 **Unified IR** 打交道，彻底屏蔽 `chat` 与 `resp` 的底层报文差异，甚至支持**在同一个会话中途无缝切换 `chat` 模型与 `resp` 模型**！

---

## 三、 统一中间表示模型（`UnifiedLlmModels.cs`）

```csharp
namespace Host.Desktop.LlmGateway.Models;

public sealed class UnifiedLlmRequest
{
    public required string RequestId { get; init; }
    public required ProviderConfig Provider { get; init; }
    public required ModelConfig Model { get; init; }
    public required HeaderResolutionContext HeaderContext { get; init; }
    public required string SystemInstructions { get; init; }
    public required IReadOnlyList<UnifiedChatMessage> Messages { get; init; }
    public required IReadOnlyList<UnifiedToolDefinition> Tools { get; init; }
}

public sealed class UnifiedToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>
    /// 标准 JSON Schema 对象（type: "object", properties: {...}, required: [...]）
    /// </summary>
    public required JsonElement ParametersSchema { get; init; }
}

public enum UnifiedStreamEventKind
{
    StreamStarted,
    ReasoningDelta,      // 思考过程增量（兼容 DeepSeek reasoning_content / OpenAI reasoning summary）
    TextDelta,           // 正文文本增量
    ToolCallStarted,     // 模型决定发起某个工具调用（已拿到 callId 与 functionName）
    ToolCallArgsDelta,   // 工具参数 JSON 增量片段
    ToolCallCompleted,   // 单个工具调用参数接收完毕
    UsageUpdated,        // Token 消耗统计（含 Prompt Cache 命中统计）
    StreamCompleted,     // 整轮响应完成
    StreamError          // 协议层或网络层错误
}

public sealed record UnifiedStreamEvent(
    UnifiedStreamEventKind Kind,
    string? TextDelta = null,
    UnifiedToolCall? ToolCall = null,
    UnifiedTokenUsage? Usage = null,
    string? ErrorCode = null,
    string? ErrorMessage = null
);

public sealed record UnifiedToolCall(
    string CallId,          // 对应 Chat 的 tool_call.id 或 Responses 的 call_id
    string? ItemId,         // 对应 Responses API 的 fc_xxx item id（可选）
    string FunctionName,
    string ArgumentsJson
);

public sealed record UnifiedTokenUsage(
    int InputTokens,
    int OutputTokens,
    int CachedInputTokens,  // 重点：记录 Prompt Cache 命中的 Token 数，验证 ${session-hash} 收益
    int TotalTokens
);
```

---

## 四、 Stage 03 交付产物清单

| 模块分类 | 文件路径 | 核心职责说明 |
| :--- | :--- | :--- |
| **统一模型** | `src/Host.Desktop/LlmGateway/Models/UnifiedLlmModels.cs` | 双协议无关的请求、工具定义、流式事件、Token 统计模型 |
| **SSE 解析器** | `src/Host.Desktop/LlmGateway/Sse/SseStreamReader.cs` | 零拷贝高性能 UTF-8 `event:` / `data:` 行流式解析器 |
| **Chat 适配器** | `src/Host.Desktop/LlmGateway/Adapters/ChatCompletionsAdapter.cs` | OpenAI `/v1/chat/completions` 请求序列化与 `delta` 流式解析 |
| **Resp 适配器** | `src/Host.Desktop/LlmGateway/Adapters/ResponsesApiAdapter.cs` | OpenAI `/v1/responses` 请求序列化与 `response.*` 事件流解析 |
| **网关门面** | `src/Host.Desktop/LlmGateway/LlmGatewayClient.cs` | 根据 `ModelConfig.Protocol` 自动路由适配器、注入代理与自定义 Header |
| **本阶段文档** | `02-chat-completions-adapter.md` / `03-responses-api-adapter.md` / `04-stream-pipeline-and-acceptance.md` | 详细实现方案与验收标准 |

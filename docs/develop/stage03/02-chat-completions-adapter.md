# Stage 03 - 02：OpenAI Chat Completions (`chat`) 协议适配器实现方案

> **所属阶段**：Stage 03
> **对应协议枚举**：`LlmProtocolType.Chat` (`/v1/chat/completions`)

---

## 一、 请求报文构建规范（Request Serialization）

当 `ModelConfig.Protocol == LlmProtocolType.Chat` 时，`ChatCompletionsAdapter` 将 `UnifiedLlmRequest` 转换为标准 `/v1/chat/completions` JSON 载荷：

### 1. 请求 JSON 结构映射
```json
{
  "model": "gpt-4o",
  "stream": true,
  "stream_options": {
    "include_usage": true
  },
  "max_tokens": 4096,
  "temperature": 0.2,
  "messages": [
    {
      "role": "system",
      "content": "<SystemInstructions>"
    },
    {
      "role": "user",
      "content": "帮我查看当前目录下的文件"
    },
    {
      "role": "assistant",
      "content": null,
      "tool_calls": [
        {
          "id": "call_abc123",
          "type": "function",
          "function": {
            "name": "list_dir",
            "arguments": "{\"path\":\".\"}"
          }
        }
      ]
    },
    {
      "role": "tool",
      "tool_call_id": "call_abc123",
      "content": "docs/\nsrc/\nHaloAgent.sln"
    }
  ],
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "list_dir",
        "description": "List directory contents",
        "parameters": {
          "type": "object",
          "properties": {
            "path": { "type": "string" }
          },
          "required": ["path"]
        }
      }
    }
  ]
}
```

### 2. 兼容性细节处理
* **`max_tokens` 控制**：严格读取当前 `ModelConfig.MaxOutputTokens` 填入请求体。
* **`stream_options.include_usage`**：默认开启以获取流式末尾的 `usage`（含 `prompt_tokens_details.cached_tokens`）。若部分老旧第三方兼容接口因不识别 `stream_options` 返回 `400 Bad Request`，网关内置自动降级重试（移除 `stream_options` 重发一次并记住该 Provider 特征）。
* **空 `content` 兼容**：当 `role == "assistant"` 且包含 `tool_calls` 时，部分严格兼容网关要求 `"content": ""` 而非 `null`，适配器统一输出空字符串 `""` 或有效文本以保证最大兼容性。

---

## 二、 SSE 流式响应与分片 ToolCall 聚合状态机

### 1. 分片 ToolCall 累加器（`ToolCallAccumulator`）
在 `/v1/chat/completions` 流式返回中，`choices[0].delta.tool_calls` 是一个带 `"index"` 的数组：
* **首帧（First Chunk）**：携带 `index: 0`、`id: "call_abc123"`、`function.name: "exec_command"`、`function.arguments: ""`。
* **后续帧（Subsequent Chunks）**：仅携带 `index: 0` 与 `function.arguments: "{\"command\":"`（此时 `id` 和 `name` 为 `null`！）。
* **并发多工具调用**：模型可能在单轮同时返回 `index: 0` 和 `index: 1` 两个工具调用。

因此适配器内部维护 `SortedDictionary<int, PartialToolCallState>`：
```csharp
private sealed class PartialToolCallState
{
    public int Index { get; init; }
    public string CallId { get; set; } = string.Empty;
    public string FunctionName { get; set; } = string.Empty;
    public StringBuilder ArgumentsBuilder { get; } = new();
    public bool StartedEventEmitted { get; set; }
}
```

### 2. 流式帧解析核心流程
1. 读取 SSE `data:` 行：
   * 若为 `data: [DONE]`：遍历 `_toolCallStates`，对尚未触发完成事件的每个工具发出 `UnifiedStreamEventKind.ToolCallCompleted`，随后发出 `StreamCompleted` 并结束循环。
2. 解析 JSON 根节点：
   * **思考链提取（兼容 DeepSeek R1 / V3 / 硅基流动 / OpenRouter）**：
     检查 `delta` 下是否存在 `reasoning_content` 或 `reasoning` 字段；若非空，立即 `yield return new UnifiedStreamEvent(UnifiedStreamEventKind.ReasoningDelta, TextDelta: reasoning)`。
   * **正文增量提取**：
     检查 `delta.content`；若非空，立即 `yield return new UnifiedStreamEvent(UnifiedStreamEventKind.TextDelta, TextDelta: content)`。
   * **工具调用分片提取**：
     遍历 `delta.tool_calls`：
     * 按 `index` 获取或创建 `PartialToolCallState`。
     * 若 `id` 非空，更新 `state.CallId = id`。
     * 若 `function.name` 非空，更新 `state.FunctionName += name`。
     * 当 `CallId` 与 `FunctionName` 均就绪且 `!state.StartedEventEmitted` 时，发出 `ToolCallStarted` 事件（前端立即渲染出“正在准备调用工具 `exec_command`...”加载卡片）。
     * 若 `function.arguments` 非空，追加至 `state.ArgumentsBuilder`，并发出 `ToolCallArgsDelta`。
   * **Token 用量提取**：
     检查顶层 `usage` 节点：
     * `InputTokens` = `prompt_tokens`
     * `OutputTokens` = `completion_tokens`
     * `CachedInputTokens` = `usage.prompt_tokens_details?.cached_tokens ?? usage.prompt_cache_hit_tokens ?? 0`（同时兼容 OpenAI 与 DeepSeek 的缓存命中字段名！）
     * 发出 `UsageUpdated` 事件。

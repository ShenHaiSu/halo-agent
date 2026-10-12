# Stage 03 - 03：OpenAI Responses (`resp`) 协议适配器实现方案

> **所属阶段**：Stage 03
> **对应协议枚举**：`LlmProtocolType.Resp` (`/v1/responses`)
> **核心对应需求**：需求 #2（支持 OpenAI 的 `response` 协议）与需求 #7（每个 model 可配置 `chat`/`resp` 协议）

---

## 一、 OpenAI Responses API (`/v1/responses`) 协议特性与映射规则

OpenAI 新一代 `Responses API` 将传统的消息树重构为扁平化的多态 `Item` 列表（`input` 数组），并将工具 Schema 从嵌套式调整为顶层扁平式。
为了支持用户在任意会话中使用 `resp` 协议（甚至在同一会话中途从 `chat` 切换到 `resp`），`ResponsesApiAdapter` 在客户端无状态维护完整的 `input` 历史转换（不强依赖服务端的 `previous_response_id`，从而完美兼容官方 OpenAI 以及各类无状态第三方 Responses 代理网关）。

### 1. 请求报文构建规范（Request Serialization）
当 `ModelConfig.Protocol == LlmProtocolType.Resp` 时，生成的 `/v1/responses` 请求体结构如下：

```json
{
  "model": "gpt-4.1",
  "stream": true,
  "instructions": "<SystemInstructions>",
  "max_output_tokens": 8192,
  "temperature": 0.2,
  "input": [
    {
      "type": "message",
      "role": "user",
      "content": [
        { "type": "input_text", "text": "帮我查看当前目录下的文件" }
      ]
    },
    {
      "type": "message",
      "role": "assistant",
      "content": [
        { "type": "output_text", "text": "我先列出当前目录内容。" }
      ]
    },
    {
      "type": "function_call",
      "call_id": "call_abc123",
      "name": "list_dir",
      "arguments": "{\"path\":\".\"}"
    },
    {
      "type": "function_call_output",
      "call_id": "call_abc123",
      "output": "docs/\nsrc/\nHaloAgent.sln"
    }
  ],
  "tools": [
    {
      "type": "function",
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
  ]
}
```

### 2. Unified IR 到 Responses `input` 数组的转换细则
* **System Prompt**：填入顶层 `"instructions"` 字段。
* **User 消息**：映射为 `{ "type": "message", "role": "user", "content": [{ "type": "input_text", "text": msg.Content }] }`。
* **Assistant 消息**：
  * 若包含正文文本 `msg.Content`：映射一条 `{ "type": "message", "role": "assistant", "content": [{ "type": "output_text", "text": msg.Content }] }`。
  * 若包含 `msg.ToolCalls`：依次追加 `{ "type": "function_call", "call_id": tc.CallId, "name": tc.FunctionName, "arguments": tc.ArgumentsJson }`。
* **Tool 执行结果消息**：映射为 `{ "type": "function_call_output", "call_id": msg.ToolCallId, "output": msg.Content }`。

---

## 二、 Responses API 的 SSE 事件流状态机解析

与 `Chat Completions` 仅靠 `data:` 行不同，`Responses API` 采用语义化 `event: <event_name>` 驱动。`ResponsesApiAdapter` 核心状态机处理以下标准事件：

| SSE `event:` 名称 | 载荷特征 (`data:` JSON) | 转换为 `UnifiedStreamEvent` 行为 |
| :--- | :--- | :--- |
| `response.created` | `{ "type": "response.created", "response": { "id": "resp_..." } }` | 发出 `UnifiedStreamEventKind.StreamStarted` |
| `response.reasoning_summary_text.delta`<br>或 `response.reasoning_text.delta` | `{ "delta": "..." }` | 发出 `UnifiedStreamEventKind.ReasoningDelta(delta)` |
| `response.output_text.delta` | `{ "item_id": "msg_...", "delta": "..." }` | 发出 `UnifiedStreamEventKind.TextDelta(delta)` |
| `response.output_item.added` | 当 `item.type == "function_call"` 时：<br>`{ "item": { "id": "fc_1", "type": "function_call", "call_id": "call_xyz", "name": "exec_command" } }` | 记录映射 `item_id ("fc_1") -> (call_id, name)`，并立即发出 `ToolCallStarted` 事件 |
| `response.function_call_arguments.delta` | `{ "item_id": "fc_1", "delta": "{\"cmd\":" }` | 按 `item_id` 追加参数缓冲，发出 `ToolCallArgsDelta` 事件 |
| `response.function_call_arguments.done` | `{ "item_id": "fc_1", "arguments": "{\"cmd\":\"dir\"}" }` | 锁定最终 `arguments`，发出 `ToolCallCompleted(callId, itemId, name, arguments)` |
| `response.output_item.done` | 当 `item.type == "function_call"` 且此前未触发 `.done` 时兜底 | 兜底发出 `ToolCallCompleted`（防止部分精简网关省略 `arguments.done` 事件） |
| `response.completed` | `{ "response": { "usage": { "input_tokens": 1200, "output_tokens": 150, "input_tokens_details": { "cached_tokens": 1024 } } } }` | 提取 `input_tokens`、`output_tokens`、`cached_tokens`，依次发出 `UsageUpdated` 与 `StreamCompleted` |
| `response.failed` / `error` | `{ "error": { "code": "...", "message": "..." } }` | 发出 `UnifiedStreamEventKind.StreamError` |

---

## 三、 异常与非 200 响应统一诊断

无论 `chat` 还是 `resp` 协议，当 HTTP 状态码非 `200 OK`（如 `401 Unauthorized`、`404 Not Found`、`429 Too Many Requests`、`502 Bad Gateway`）时：
1. 读取响应 Body 前 `4096` 字符。
2. 尝试解析标准 OpenAI 错误对象 `{ "error": { "message": "...", "type": "...", "code": "..." } }`。
3. 结合当前请求协议给出人性化诊断提示：
   * 例如：当用户对仅支持 `chat` 的旧网关配置了 `resp` 协议收到 `404 Not Found (/v1/responses)` 时，网关自动附加提示：`"上游接口返回 404 (/v1/responses)，该 Provider 可能尚未支持 OpenAI Responses 协议，建议在模型设置中切换为 Chat 协议重试。"`

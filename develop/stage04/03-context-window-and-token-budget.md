# Stage 04 - 03：基于模型配额的上下文窗口预算与历史裁剪引擎

> **所属阶段**：Stage 04
> **核心对应需求**：需求 #7（每个 model 可以配置最大上下文 `maxContextTokens`、最大输出 token `maxOutputTokens`）

---

## 一、 上下文预算公式（Context Budget Formula）

每个 Model 配置了两个关键数值：
* `MaxContextTokens`（例如 `64000` 或 `128000`）
* `MaxOutputTokens`（例如 `4096` 或 `8192`）

如果在构造请求时把 127,000 Tokens 的历史塞进 `128000` 上下文窗口，再声明 `max_tokens: 4096`，上游 OpenAI / DeepSeek 接口会直接报 `400 context_length_exceeded` 错误。

因此，`TokenBudgetService` 在组装每次 LLM 请求前执行严格配额计算：

$$\text{MaxInputBudget} = \text{MaxContextTokens} - \text{MaxOutputTokens} - \text{SafetyBuffer}$$

其中：
* $\text{ReservedFixedTokens} = \text{Tokens}(\text{SystemInstructions}) + \text{Tokens}(\text{ToolsSchema}) + \text{SafetyBuffer}(1024)$
* $\text{HistoryWindowBudget} = \text{MaxInputBudget} - \text{ReservedFixedTokens}$

---

## 二、 零外部非托管依赖的离线高速 Token 估算器

在 Windows 7 上，部分依赖 Rust/C++ 原生绑定库的 Tokenizer 在 32 位或无 AVX 指令集的老 CPU 上可能加载失败。因此采用 **纯 C# 启发式精准估算 + 服务端 Usage 校准双机制**：

1. **离线估算算法（`EstimateTokens(string text)`）**：
   * ASCII 英文字符/代码符号：约 `3.6 chars / token`
   * CJK 中日韩字符：约 `0.65 chars / token`（每个汉字在 `cl100k_base` / `o200k_base` 中占 `1 ~ 2` 个 token，按 `1.55 tokens / 汉字` 保守上浮计算）
   * 每条消息结构开销：固定附加 `+6 tokens`，每个 `ToolCall` 结构附加 `+12 tokens`。
2. **服务端真实 Token 校准**：
   * 每当收到 Stage 03 返回的 `UnifiedTokenUsage.InputTokens` 时，将该值记录在最新一条 Assistant 消息的元数据中，作为下一轮计算历史基准的精确锚点。

---

## 三、 保持工具调用链完整性的滑动窗口裁剪（Pair-Preserving Truncation）

### 1. 核心痛点：为什么不能简单按条数丢弃最老的消息？
在 `Chat Completions` 与 `Responses API` 中：
* 一条带有 `tool_calls` 的 `assistant` 消息，**后面必须紧跟**对应 `tool_call_id` 的 `tool` 结果消息。
* 一条 `role: "tool"`（或 `function_call_output`）消息，**前面必须存在**发起该 `call_id` 的 `assistant`（或 `function_call`）消息。
如果滑动窗口恰好从中间切断（例如丢掉了 `assistant(tool_calls)` 却保留了 `tool(result)`），LLM 网关将 100% 拒绝请求并返回 `400 Invalid parameter: messages with role 'tool' must be a response to a preceding message with 'tool_calls'`。

### 2. 原子消息组（`AtomicMessageTurnGroup`）分组裁剪算法
`TokenBudgetService.BuildWithinBudget(messages, historyBudget)` 将扁平消息列表聚合为不可分割的 **原子事务组（Turn Group）**：
* **普通对话组**：单条 `User` 消息，或单条不含工具调用的 `Assistant` 消息。
* **工具调用闭环组（Tool Call Group）**：`[Assistant (with tool_calls: call_1, call_2), ToolResult(call_1), ToolResult(call_2)]` 被捆绑为一个整体原子单元。

**裁剪流程**：
1. 始终无条件保留：
   * 第一条原始 `User` 任务目标消息（Pinned Anchor，防止长程 ReAct 循环后 Agent 忘记最初的用户需求）；
   * 最新一轮正在进行的 `User` 输入与当前未闭环的 `Tool Call Group`。
2. 从最新向最旧倒序累加 `AtomicMessageTurnGroup` 的 Token 数：
   * 若加入某组后未超过 `HistoryWindowBudget`，则完整纳入该组；
   * 一旦超出预算，将该较早的 `Turn Group` **整组跳过**，并在首条保留消息与后续历史之间插入一条简短系统过渡提示：`"[注：为适配当前模型最大上下文限制，较早的 N 轮历史对话已自动折叠省略]"`。
3. **兼容 Prompt Cache 优化**：
   * 为避免每轮都微调裁剪边界导致上游 `${session-hash}` 对应的 KV Cache 前缀全部失效，裁剪采用 **按 20% 阶梯水位线（High-Water Mark = 92% -> 一次性裁剪至 70% 低水位线）** 的块状淘汰策略，使接下来的十余轮对话前缀保持 100% 字节级稳定！

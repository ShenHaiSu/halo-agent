# 02 流式写入字节展示设计（需求 1）

## 2.1 设计一句话

字节数不在工具执行侧产生，而在 **LLM 参数流到达侧**产生：`AgentOrchestrator` 在 `ToolCallArgsDelta` 处对每个 `callId` 做 **UTF-8 增量累加**并以 **150ms 节流**发布 `ToolCallUpdated{receivedBytes}`；前端 `ToolCallCard` 对 `write_file/edit_file` 渲染一行字节计数，数字经 `formatSmartBytes()` 格式化为需求指定的递进式文案。总量未知 → **不定态（字节跳动 + 微光 shimmer），不画假进度条**。

## 2.2 为什么选这个切面（三种方案对比）

| 方案 | 切面 | 否决/采纳理由 |
|---|---|---|
| A. 改 `WriteFileTool.ExecuteAsync` 分块写 + 进度回调 | 工具执行侧 | ❌ 否决。执行是本地瞬间写（毫秒级），用户等的根本不是落盘，而是 **LLM 生成参数的漫长流式过程**；且工具侧无 JS 通道推中途事件，改造成本高、收益零。 |
| B. 前端按 `argsDelta.length` 估算 | 前端 `agentStore` | ⚠️ 备选。`String.length` 是 UTF-16 码元数，中文差 1.5 倍，与终态 `FileInfo.Length`（UTF-8 字节）对不上，会被用户抓包打脸。 |
| C. **Orchestrator 增量 UTF-8 累加 + 节流发布（采纳）** | `AgentOrchestrator:406-435` | ✅ 与终态字节同一单位（UTF-8）；增量 `GetByteCount(delta)` O(片段) 无 O(n²)；只改发布载荷，向后兼容；天然覆盖 `write_file/edit_file` 所有大参数工具。 |

字节口径：`receivedBytes` = 该 call 累计收到的 **args JSON 线字节数**（`Encoding.UTF8.GetByteCount(argsDelta)` 逐片累加）。与终态文件字节不完全相等是**预期内**（含 JSON 转义与 `file_path` 开销），文案必须写“参数已接收”而非“文件已写入”，完成态再替换为工具返回的精确 `N 行 / M 字节`。另附可选 `contentBytes`（尽力从 `ArgsBuilder` 正则抠 `content` 字段，失败则回退 `receivedBytes`），前端优先显示 `contentBytes`（更贴近用户直觉），悬停 title 显示两者。

## 2.3 后端改动（精确到位置）

**文件：`src/Host.Desktop/Services/AgentOrchestrator.cs`**

1. `PendingToolCall` 新增字段（约 343 行附近）：
   ```csharp
   private sealed class PendingToolCall {
     public string CallId; public string? ItemId; public string FunctionName = "";
     public StringBuilder ArgsBuilder = new();
     public long ReceivedBytes = 0;          // 新增：UTF-8 累加
     public long LastPublishedBytes = -1;   // 新增：节流用
     public long LastPublishTick = 0;       // 新增：Environment.TickCount64
   }
   ```
2. `ToolCallArgsDelta` 分支（约 406-414 行）改为：
   ```csharp
   case UnifiedStreamEventKind.ToolCallArgsDelta:
     if (pendingTools.TryGetValue(cid, out var p) && evt.TextDelta != null) {
       p.ArgsBuilder.Append(evt.TextDelta);
       try { p.ReceivedBytes += Encoding.UTF8.GetByteCount(evt.TextDelta); }
       catch { p.ReceivedBytes = p.ArgsBuilder.Length; } // 永不抛
       bool isFileWrite = p.FunctionName is "write_file" or "edit_file";
       long now = Environment.TickCount64;
       bool throttleHit = (now - p.LastPublishTick) >= 150 || evt.TextDelta.Length > 4096;
       if (isFileWrite ? throttleHit : true) {  // 仅文件写入工具节流，其余保持原频率
         p.LastPublishTick = now; p.LastPublishedBytes = p.ReceivedBytes;
         PublishTurnEvent(sessionId, "ToolCallUpdated", new {
           callId = cid, functionName = p.FunctionName,
           argsDelta = evt.TextDelta,               // 保持原字段（兼容旧前端）
           receivedBytes = p.ReceivedBytes,         // 新增
           argsLength = p.ArgsBuilder.Length        // 新增（调试用）
         });
       }
       // 节流丢弃的 delta 仍在 ArgsBuilder 中，最终 ToolCallCompleted 会带全量，不丢数据
     }
   ```
   注意：节流丢弃的 `argsDelta` 前端拼不出全量 args——因此 `ToolCallCompleted`（约 415-435 行）必须带 `receivedBytes` 终值 + 全量 `argumentsJson`，前端以 Completed 为准对账（见 2.4）。
3. `ToolCallStarted/Completed` 同步带 `receivedBytes: 0 / 终值`，`ToolExecutionCompleted` 带 `fileBytes`（从 `ToolResult` 消息里已有的 `M 字节` 解析或由 `WriteFileTool` 显式返回结构化字段——推荐后者：`ToolResult` 加 `FileBytes` 可选字段，零风险）。
4. 首包即时性：`ToolCallStarted` 到达即发布一次 `receivedBytes:0`，前端立刻挂出计数行（避免“首 150ms 黑屏”被误判为假死）。

性能：单次 `GetByteCount` 只扫当前片段（通常几十–几百字符），万级 chunk 总成本 O(总字节)，无二次方；`PublishTurnEvent → JsEventDispatcher` 本就批量 16ms 合并下行，150ms 节流后 CefSharp Bridge 压力下降一个数量级。

## 2.4 前端改动（精确到文件）

**新增 `src/Web.Client/src/utils/formatSmartBytes.ts`**（唯一口径，全端复用）：
```ts
export function formatSmartBytes(bytes: number): string {
  bytes = Math.max(0, Math.floor(bytes ?? 0));
  const exact = `${bytes.toLocaleString('en-US')} Bytes`; // 精确值千分位，爽感来源
  if (bytes < 1024) return `${exact}`;                    // e.g. "912 Bytes"
  const kb = bytes / 1024;
  if (bytes < 1024 * 1024)
    return `${kb.toFixed(kb < 10 ? 2 : 1)} KB = ${exact}`; // e.g. "48.83 KB = 50,000 Bytes"
  const mb = bytes / (1024 * 1024);
  return `${mb.toFixed(2)} MB = ${(kb).toLocaleString('en-US',{maximumFractionDigits:1})} KB = ${exact}`;
}
export function formatSmartBytesShort(bytes:number){ /* 瓦片/表头用：12.4 KB，不带精确值 */ }
```
边界：`0 → "0 Bytes"`；负数/NaN → 钳制 0；`1023 → Bytes`，`1024 → "1.00 KB = 1,024 Bytes"`（需求“平滑过渡”即在此阈值切换格式，不断跳单位）。

**`stores/agentStore.ts`（约 320-360 行 `ToolCallStarted/Updated/Completed`）**：
- `ToolCallCard` 模型新增 `receivedBytes: number; totalHint?: number`（total 恒未知，保持 `undefined`）。
- `ToolCallUpdated`：`card.receivedBytes = evt.receivedBytes ?? card.receivedBytes + estimate(evt.argsDelta)`（兼容旧后端：无字节字段时前端用 `new TextEncoder().encode(delta).length` 估算，保证新旧混跑不崩）。
- `ToolCallCompleted`：以终值对账并冻结数字（避免节流欠量导致终值偏小）。
- 渲染节流：store 照单收（便宜），**组件侧 `requestAnimationFrame` 或 150ms `computed+watch` 节流渲染**即可；数字变化 <1KB 时可跳帧（防中文逐字抖动掉帧）。

**`components/chat/ToolCallCard.vue`**（`write_file/edit_file` 专属行，`data-testid="tool-bytes-counter"`）：
```
[icon] 写入 src/xxx.ts · 参数已接收 48.83 KB = 50,000 Bytes ✨（流式接收中…）
```
- 样式：`font-variant-numeric: tabular-nums`（等宽数字防跳动）+ `n-text depth-3` + 呼吸点动画（CSS keyframes，chrome109 安全）；**禁用 `n-progress`**（总量未知，画百分比是欺诈）。
- 状态机：`receiving（蓝色脉冲）→ executing（工具实际落盘，"正在写入磁盘…"）→ done（绿色对勾 + 终态 N 行/M 字节）→ cancelled/error（灰/红冻结最后值）`。
- 非文件工具（`exec_command/grep` 等）**不显示**此行（防噪音）；`read_file` 结果大时走既有截断提示，不复用本行。

## 2.5 爽感细节（需求明示“减少枯燥等待”）

1. 数字用 `toLocaleString` 千分位 + 等宽字体，跳动时有“码表感”。
2. KB 小数位数动态：`<10KB → 2 位`（早期变化可见），`≥10KB → 1 位`（后期不抖）。
3. 超过 50KB 显示火苗/火箭 emoji 一次（纯 CSS/字符，无图资源，Win7 安全）。
4. hover 显示 tooltip：`参数线字节（含 JSON 转义）≈ xx；文件实际字节以完成态为准`——提前堵住“数字对不上”的客诉。

## 2.6 异常与兼容

| 场景 | 处理 |
|---|---|
| 旧后端无 `receivedBytes` | 前端 `TextEncoder` 回退估算，不白屏 |
| 旧前端无字节行 | 后端多发两个字段被忽略，零破坏 |
| 流式中取消 | 冻结最后值 + “已取消”态，不清零 |
| 超大 content（>5MB） | 仍全量进 `ArgsBuilder`（现状如此，本需求不改截断策略）；计数器照常跳，MB 格式分支此时被演练到 |
| 高频 chunk（<1ms 连发） | 150ms 节流 + `>4KB 大片直通`，Bridge 不风暴 |

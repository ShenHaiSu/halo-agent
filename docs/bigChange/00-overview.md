# bigChange 总览：彻底重构路线图（第一参考）

> 本目录是重构实现的**第一参考**。`docs/contextAndRender/` 是第二参考（优秀方案的思想来源，栈不同不可直搬）。
> 允许破坏性改动、不兼容旧版本。所有阶段以“删除旧语义”为前提，不做双轨兼容。

## 0. 现状锚点（重构前必须确认）

- 后端：`src/Host.Desktop/LlmGateway/LlmGatewayClient.cs`（双协议门面）+ `LlmGatewayClient.Resilience.cs`（重试/降级）+ `Adapters/ChatCompletionsAdapter*.cs + ResponsesApiAdapter*.cs` + `Infrastructure/Network/HaloHttpClientFactory.cs + HeaderVariableResolver.cs`。
- 上下文：`Services/SystemPromptBuilder*.cs` + `PrefixCachePredictor.cs` + `PromptLastSentStore.cs` + `TokenBudgetService*.cs` + `UsageRecorder.cs` + `CompactionService.cs`。
- 轮次：`Services/Agent/TurnPlanner.cs + TurnSetup.cs + LlmCaller.cs + HistoryAppender.cs` + `WorkspaceSessionService*.cs`。
- 前端：`src/Web.Client/src/features/agent/stores/useAgentStream.ts + model.ts + turnEvents.ts` + `composables/useTimeline*.ts` + `components/TimelineView.vue + MessageTimeline.vue` + `features/session/utils/markdownRender.ts + turnGroup.ts`。

## 1. 设计总纲（从第二参考学什么）

1. 网络：单装配线、顺序不可换、观测先于重试（`capture → classify → retry`）。
2. 上下文：缓存命中 = 字节级稳定（固定排序 + 增量补丁 + 易变后置 + usage-anchor）。
3. 渲染：历史冻结、尾部独活（history/tail 分离 + 引用保稳 + rAF 合批 + 块级 memo）。

 ## 2. 分阶段地图（严格按序）
 
 | 阶段 | 文档 | 主题 | 前置 |
 |---|---|---|---|
 | P0-1 | `01-network-pipeline.md` | Header 管道 + 绑定 + 捕获层 | 无 |
 | P0-2 | `02-network-retry-transport.md` | 重试分池 + 空闲看门狗 + 传输重建 | 01 完成 |
 | P1-1 | `03-context-stable-prefix.md` | System 固定排序 + 增量补丁 + journal | 01 完成 |
 | P1-2 | `04-context-budget-estimate.md` | 预算公式 + usage-anchor + 校准 | 03 完成 |
 | P1-3 | `05-context-compaction.md` | 切点 + 摘要输入 + 原子重写 | 03+04 完成 |
 | P2-1 | `06-render-transcript.md` | history/tail 分离 + Pinia 合批 | 05 完成 |
 | P2-2 | `07-render-markdown.md` | 块级 memo + tail-only + 高亮懒加载 | 06 完成 |
 | P2-3 | `09-history-scroll.md` | follow/自由 + ShowJump + 两段分页 | 06 完成 |
 | P2-4 | `10-minimap-smooth-settle.md` | minimap + 平滑文本 + settle | 06+09 完成 |
 | P2-5 | `11-search-disclosure.md` | 搜索 + 历史窗降级 + Disclosure | 06 完成 |
 | P2-6 | `12-turn-tool-cards.md` | Turn 四态 + 工具卡 + 动作卡 | 11 完成 |
 | P2-7 | `13-markdown-gates.md` | Markdown 门控 + 表图 + 溢出降级 | 07 完成 |
 | P2-8 | `14-composer.md` | Composer + Slash + chip/附件 | 12 完成 |
 | P3 | `08-cutover-testing.md` | 破坏性切换 + 迁移 + 全量验收 | 全部完成 |

P0 未全绿禁止开 P1；P1 未全绿禁止开 P2。

## 3. 破坏性改动总清单（允许不兼容）

- 删除：`TokenBudgetService` 固定 1024 buffer 语义、`CompactionService` 按 group 数切分语义、`markdownRender` 整源渲染语义、`SendRequestAsync` 固定 1s/2s/4s 重试语义。
- 删除：旧 `TruncationNotice` 插入位置约定、旧 `PrefixCachePredictor` 纯日志口径（转正为预算输入）。
- 不保留：旧 session 压缩中间格式、旧前端 `timelineItems` 全量重算行为、旧 `blockHash` 整源缓存 key。
- 迁移只做一次性：见 `08`。不做版本分支，不做特性开关。

## 4. 全局验收（P3 出口）

- 长会话（200+ 轮）流式不掉帧，尾部帧解析 ≤1ms/帧 median（见 07 标准）。
- 相同会话连续两轮 system 前缀 hash 不变（易变块只出现在尾部），见 03 标准。
- 429/5xx/传输中断可恢复且不双计费，`Retry-After` 被尊重，见 02 标准。
- 预算抖动收敛：有 usage 锚点时连续 5 轮估算误差带内，见 04 标准。
- 压缩后可继续对话且工具链不断，见 05 标准。
 ## 5. 文档使用规则
 
 - 本目录第一参考；`docs/contextAndRender/` 第二参考；`docs/uiuxAndStyle/` 第三参考（行为/阈值/文案，不抄代码）。
 - 对应关系：01→context01；02→context02；03→context03；04/05→context04；06→context05；07/13→context06；09→uiux01；10→uiux02+03；11→uiux04；12→uiux05；13→uiux06；14→uiux07。

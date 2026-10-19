# 00 导航与结论：要不要全量转 zcode？

> 落盘位置：`docs/learnDocs/zcode-migration/` ｜ 对比基线：`develop/src/{Host.Desktop,Web.Client}` vs `docs/learnDocs/zcodeDocs/00~07`
> 一句话结论：**值得转，但不是小改。这是约 40~55% 链路代码的重写，集中在网关适配、流式归一、增量通道、前端存储四处。**

## 1. 本系列怎么读

| 篇 | 文件 | 回答的问题 |
|---|---|---|
| 00 | `00-README-导航与结论.md`（本篇） | 转不转？改多大？先看这篇 |
| 01 | `01-总体对比-全链路差异总表.md` | 六层一览：哲学、模型、事件、通道差异 |
| 02 | `02-协议分发与网关层对比.md` | C# 双适配器 vs TS 三工厂 + fetch 洋葱 |
| 03 | `03-请求投影层对比.md` | system/reasoning/媒体/工具声明谁做得对 |
| 04 | `04-流式归一与编排层对比.md` | 统一事件、组装器、重试、Agent 循环 |
| 05 | `05-业务投影与增量通道对比.md` | 行 + 封闭增量 vs 双事件 + 全量回拉 |
| 06 | `06-前端存储轮次与渲染对比.md` | 快照存储/轮次组装/卡片桥接差异 |
| 07 | `07-全量迁移工作量定量评估.md` | 文件级改动量、风险、阶段划分 |
| 08 | `08-后端施工手册.md` | C# 端一步步改（含坑位） |
| 09 | `09-前端施工手册.md` | Vue/TS 端一步步改（含坑位） |
| 10 | `10-风险清单与验收标准.md` | 联调排错表 + DoD + 回滚线 |

阅读顺序：00 → 01 → 07（决策）→ 02~06（细节）→ 08~10（动手）。

## 2. 核心差异速览（六层）

```text
zcode： 对外统一 Model 接口 → 分发层选 SDK 工厂 → 请求投影 → SDK 流 → 流式归一(ModelStreamEvent)
        → 业务投影(行+增量) → coalesce → 快照/增量帧 → 前端投影存储 → 轮次组装 → 行渲染

halo：  UnifiedLlmRequest → LlmGatewayClient 按 Protocol 路由 → ILlmProtocolAdapter.Serialize
        → SSE(SseStreamReader) → ParseStreamAsync → UnifiedStreamEvent(10种)
        → Channel → AgentOrchestrator 累积/落盘/工具串行执行
        → 双通道推送(llm:stream-event + agent:turn-event，经 JsEventDispatcher 16ms 批)
        → 前端双轨(agentStore 瞬态 + workspaceStore 全量 messages.jsonl 回拉) → turnGroup 分组渲染
```

最大分水岭只有一条：**zcode 在后端做了“行 + 封闭增量 + 快照”的业务投影，前端只消费投影；halo 把“原始流事件 + 全量消息回拉”同时推给前端，前端自己拼状态。**

## 3. 改动量 verdict

| 维度 | 结论 |
|---|---|
| 后端网关 | 中改：保留双适配器骨架，补三处 zcode 规则（Responses JSON 修补、业务错识别、endpoint 后缀纪律），约 3 文件 |
| 后端编排 | 大改：引入工具组装器三集合 + 首有效事件后禁重试 + 废弃 attempt 收口，`AgentOrchestrator` 约 30% 逻辑动 |
| 增量通道 | 重写：新增行模型 + `apply/coalesce` + 快照帧 + 双档位投递，`JsEventDispatcher` 从字符串合并升级为操作合并 |
| 前端存储 | 重写：新增投影存储（snapshot 整体替换 + fromSeq 衔接 + 重订阅），`agentStore` 瘦身为视图缓存 |
| 前端渲染 | 中改：保留 `MessageTimeline/ToolCallCard` 外壳，内部改为行分发 + 桥接 + renderer 选择 + 兜底卡 |
| 总量 | **约 12~15 个文件新建/重写，8~10 个文件改造；测试需新增 coalesce 等价单测 + 移植清单 E2E** |

详细量化见 `07`。建议分三期：P0 网关补齐（低风险）→ P1 增量通道 + 前端存储（高收益）→ P2 渲染规范化（体验）。

## 4. 什么不要动

- `SystemPromptBuilder` 稳定区/非稳定区分层、`TokenBudgetService` 三阶裁剪、`PrefixCachePredictor` 五块快照、`CompactionService` 独立压缩请求：halo 比 zcode 文档更细，**保留并作为行投影的上游输入**，不要用 zcode 的简化版覆盖。
- `ToolRegistry/ToolSchemaBuilder` 的 15 工具 + strict 门控 + 400 strict 降级：已对齐 zcode 硬规则，**只补缺口**（空工具名占位、media 占位文案统一）。
- CefSharp `window.__haloReceiveHostEvents` 批量通道机制本身：保留，只升级载荷语义。

## 5. 名词对照表（防止鸡同鸭讲）

| zcode 词 | halo 现实对应 | 备注 |
|---|---|---|
| `apiType` | `LlmProtocolType{Chat,Resp}` + `ModelConfig.Protocol` | halo 缺 `anthropic-messages`，先加枚举再谈分发 |
| `toSdkKind/createLanguageModel` | `ILlmProtocolAdapter + LlmGatewayClient 路由` | C# 无 AI-SDK，用适配器等价实现即可 |
| `ModelStreamEvent` | `UnifiedStreamEventKind(10种)` | 字段大体重合，缺 `tool_input_end` 语义与组装器 |
| `ConversationRow/Delta` | `SessionStreamEventDto + agent:turn-event payload` | halo 无行、无封闭操作集，这是最大缺口 |
| `snapshot/delta帧` | `messages.jsonl 全量 + 双事件增量` | halo 无 seq/revision/subscriptionId |
| `TurnUnit` | `turnGroup.buildTurnGroups` | 思想一致，实现细节差 4 条过滤规则 |

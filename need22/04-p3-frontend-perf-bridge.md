# need22-04 P3 前端性能与桥接健壮性

> 等级：P2。前置：P2 单真相源（虚拟列表依赖稳定行表）。

## 1. 前置检查

- [ ] 录基线：200+ 消息会话流式一轮，记录长任务（>200ms）数量与 `renderMarkdown+hljs.highlightAuto` 耗时（`MessageTimeline.vue:188,240,287`）。
- [ ] 录基线：多图轮 `thumbnailCache` 内存增长（`utils/composerModel.ts:164,182`）。
- [ ] 录基线：`BindObjectAsync` 失败路径是否静默进 Mock（`bridge/hostBridge.ts:1789,1976,1992,2003,2282,2300`）。

## 2. 既定目标

1. 长会话流式零卡顿：单帧 <5ms 增量，3k 字回答无 >200ms 长任务。
2. 图片内存有界：缩略图 LRU + `objectURL`，不长期持有 `dataURL`。
3. 桥接可观测：Mock 显式横幅，失败 toast + 重试，不再静默分叉。
4. 断线有感知：`StreamError/TurnError` 落盘可查 + 可重试。

## 3. 改动清单

| # | 文件 | 现状 | 改为 |
|---|---|---|---|
| G1 | `components/chat/MessageTimeline.vue:188,240,287`、`composables/useTimelineGroups.ts` | `sessionMessages` 全量驻留，每次 `computed` 重跑 `buildTurnGroups O(n)`；每 `TextDelta` 全量 `md.render+highlightAuto` | 时间轴虚拟列表（只挂载视口±buffer）；`markdown` 按块缓存（终态一次渲染，流式纯文本）；`buildTurnGroups` 增量（仅末 turn 重算） |
| G2 | `components/chat/ToolCallCard.vue:244,278` | 每次 `JSON.parse(argsJson)` | `argsJson` 解析结果 `memoize`，按 `callId+hash` 缓存 |
| G3 | `utils/composerModel.ts:164,182`、`PromptComposer` 缩略图 | `thumbnailCache:Record<dataURL>` 只增不淘汰；`FileReader` 转 `base64` 常驻 | LRU（如 20 张）+ `URL.createObjectURL` 预览，发送时才转码，revoke 释放；大图（>5MB）压缩后再预览 |
| G4 | `stores/subSessionStore.ts:66` | 子会话 `setInterval 3s` 全量拉消息 | 改为事件驱动（后端推 `sub-event`），轮询仅作降级（15s 一次 + 失活暂停） |
| G5 | `bridge/hostBridge.ts` 全篇（2200+ 行） | `BindObjectAsync` 一次失败即降级 Mock 无提示；大量 `typeof fn!=='function'` 吞错返回空态；`Date→{}` 靠补丁；6 参失败回退 5 参靠 `try/catch` | 拆 `types/client/mock/proxy` 三模块 + 头部 `BRIDGE_VERSION + negotiate()`；Mock 必须显式横幅（红色条“桥接未连接”）；失败统一 `{code,message}` + toast + 重试按钮 |
| G6 | `stores/agentStore.ts:535,619`、`stores/workspaceStore.ts:549` | `catch` 多为空或 `return false`；`StreamError` 仅写字符串；`budget/refreshAll` 失败静默 | 错误统一进 `lastError{code,message,retry}`；`StreamError/TurnError` 落 `turnNotice` + 可重试按钮；`budget` 失败显式角标 |

## 4. 验收标准与验收方法

| 验收项 | 标准 | 方法 |
|---|---|---|
| D1 长会话 | 200 消息 + 3k 字流式，无 >200ms 长任务 | Chrome DevTools Performance 录制 + 断言 |
| D2 图片 | 10 张 5MB 图轮内存增量 <200MB 且可回收 | 任务管理器快照 + `URL.revoke` 计数 |
| D3 桥接 | 断桥接有红色横幅 + 重试，不再静默 Mock | 杀 `BindObjectAsync` fixture + 截图 |
| D4 错误 | 断网/429 有 toast + 重试按钮，`turnNotice` 可查 | 拔网 + mock 429 两组用例 |

## 5. 回滚

* G1/G5 独立分支；G1 回滚即恢复全量渲染；G5 回滚不影响 G1–G4。

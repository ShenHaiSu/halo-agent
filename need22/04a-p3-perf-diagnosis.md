# need22-04a P3 性能与桥接诊断与代码证据

> 只摆证据。路径前缀 `develop/src/Web.Client/src/`。

## 1. 长会话卡顿：全量 + 全量渲染

| 位置 | 现状 | 后果 |
|---|---|---|
| `MessageTimeline.vue:188,240,287` | `sessionMessages` 全量驻留；每次 `computed` 重跑 `buildTurnGroups/buildTimelineEntries O(n)` | 200 消息每次 keystroke 级重算 |
| 同上 | 每 `TextDelta` 触发全量 `md.render + hljs.highlightAuto` | 流式期每 token 一次全量排版，长任务频发 |
| `ToolCallCard.vue:244,278` | 每次 `JSON.parse(argsJson)`，无 memo | 工具卡多时重复解析 |
| `composables/useTimelineGroups.ts` | 无增量、无虚拟列表，全部挂载 | 首屏 + 滚动双卡 |

基线待录：200 消息 + 3k 字流式，Performance 面板长任务（>200ms）计数。

## 2. 图片内存无界

| 位置 | 现状 |
|---|---|
| `utils/composerModel.ts:164,182` | `thumbnailCache:Record<dataURL>` 只增不淘汰；`FileReader` 转 `base64` 常驻内存 |
| `PromptComposer` | 大图（>5MB）直接预览，无压缩 |
| 后端 `MultimodalPlanner` | 同步读盘转 `dataURL` 全量推前端（P1-T6 联动改 `imageRef`） |

## 3. 子会话轮询浪费

| 位置 | 现状 |
|---|---|
| `stores/subSessionStore.ts:66` | `setInterval 3s` 全量拉消息，无事件驱动，失活页签也在拉 |

## 4. 桥接脆弱：静默分叉

| 位置 | 现状 | 后果 |
|---|---|---|
| `bridge/hostBridge.ts:1789` | `BindObjectAsync` 一次失败即降级 Mock，无提示 | 联调/生产行为分叉，问题难复现 |
| `:1976,1992,2003,2282,2300` | 大量 `typeof fn!=='function'` 吞错返回空态（`ASK/SUB/USAGE_UNSUPPORTED`） | 调用方只 `console.error`，用户无感知 |
| `:2111,2117` | 大量 `catch{}` 空吞 | 同上 |
| 全篇 2200+ 行 | 无 `BRIDGE_VERSION` 协商；`Date→{}` 靠 `normalizeBridgePayload/toIso` 补丁；6 参失败回退 5 参靠 `try/catch` | 版本兼容靠运气 |

## 5. 错误静默

| 位置 | 现状 |
|---|---|
| `stores/agentStore.ts:535,619`、`workspaceStore.ts:549` | `catch` 多为空或 `return false`；`StreamError/TurnError` 仅写 `lastError` 字符串；`budget/refreshAll` 失败静默，`finishTurn` 仍清空流式卡 |

## 6. 定级

| 等级 | 项 |
|---|---|
| P2 阻塞 | 长会话卡顿、桥接静默 Mock |
| P2 严重 | 图片内存、错误静默 |
| P3 一般 | 子会话轮询、JSON 重复解析 |

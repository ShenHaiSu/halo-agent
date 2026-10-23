# need22-04b P3 性能与桥接施工方案

> 对应诊断：[04a](./04a-p3-perf-diagnosis.md)。前置：P2 单真相源。

## 1. 前置检查

- [ ] 三基线已录：长任务计数、图片内存增长、断桥 Mock 复现。
- [ ] 确认 P1-T6 的 `imageRef/fileRef` 字段名（前后端对齐）。

## 2. G1 时间轴虚拟列表 + markdown 缓存（独立分支）

* **改**：`MessageTimeline.vue` + `useTimelineGroups.ts`
  1. 虚拟列表：只挂载视口±buffer（如上下各 5 个 turn），其余占位（`min-height` 估计 + `IntersectionObserver` 校准）。
  2. `markdown` 两档：流式期纯文本（`whitespace:pre-wrap`），终态一次 `md.render` 并按 `blockHash` 缓存；`hljs` 只对终态代码块跑，且 `highlightAuto` 改 `highlight(lang)`（语言已知时）。
  3. `buildTurnGroups` 增量：仅末 turn 重算，其余复用（依赖 P2 行表 `rowId` 稳定）。
* **约束**：`data-testid/row-id/kind` 选择器不变；折叠默认态不变。

## 3. G2/G3/G4 小项

* **G2**：`ToolCallCard.vue:244,278`：`argsJson` 按 `callId+hash` memo 缓存解析结果。
* **G3**：`composerModel.ts:164,182`：缩略图 LRU 20 张 + `URL.createObjectURL` 预览，发送时才转码，`revoke` 释放；>5MB 压缩后再预览（`canvas.toBlob`）。
* **G4**：`subSessionStore.ts:66`：改为事件驱动（后端推 `sub-event`，P1 事件通道复用），轮询降级 15s 一次 + `document.hidden` 时暂停。

## 4. G5 拆桥接 + 版本协商（独立分支）

* **改**：`bridge/hostBridge.ts` 拆四模块：
  ```text
  bridge/types.ts   ← 类型 + BRIDGE_VERSION + 错误码
  bridge/client.ts  ← 真实 CefSharp 调用（proxy）
  bridge/mock.ts    ← Mock（仅开发用，显式标记）
  bridge/proxy.ts   ← 入口：negotiate() 选 client/mock
  ```
* **改**：头部 `BRIDGE_VERSION=2 + negotiate()`（握手失败直接抛，不静默）；Mock 必须显式红色横幅“桥接未连接（Mock 数据）”；所有失败统一 `{code,message}` + toast + 重试按钮；删除空 `catch{}`（至少 `console.warn+code`）。
* **改**：`Date` 序列化、`startAgentTurn` 6→5 参回退收敛到 `proxy` 一处，业务代码不再 `try/catch` 兼容。

## 5. G6 错误可观测

* **改**：统一 `lastError{code,message,retry:()=>void}`；`StreamError/TurnError` 落 `turnNotice` + 重试按钮；`budget/refreshAll` 失败显式角标（顶栏 ⚠）。

## 6. 提交切分

```text
g1-vlist → g2-memo → g3-lru → g4-sub-events → g5-bridge-split → g6-errors
```

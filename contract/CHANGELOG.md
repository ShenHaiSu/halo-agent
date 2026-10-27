# 契约变更记录

> **规则**：已冻结的字段**不允许**改语义或类型。任何破坏性变更需升 `contractVersion` 并写迁移说明。
> 只允许新增**可选**字段（不写 = 使用默认值）。

## [1] - 2026-10-27 · P3 增补（非破坏性）
## [1] — 2026-10-27 · P07 联调修复增补（非破坏性）

**非破坏性增补**（`contractVersion` 保持 `1`）：

- 新增 `asset.themes.list`（params: null）→ `{ items: ThemeSummary[] }`（只列元信息，不含 tokens）
- 新增 `asset.themes.get`（params: `{ id }`）→ `ThemeFile`（完整 token 集）
- `settings.get` 结果新增可选 `network` 子对象 `{ mode, url, retry, headers, headersEnabled, providerHeaders }`（顶层 `proxy/headers/extraHeadersEnabled` 保留兼容）
- `settings.saveNetwork` 参数 `providerHeaders` 修正为对象映射 `Record<provider, HeaderPair[]>`（旧 schema 误写为数组，前端实际发送对象；无存量数组客户端）
- `settings.saveExtraHeaders` 结果新增可选 `notes: string[]`
- 新增类型 `ThemeSummary` / `ThemeFile`

**背景**：P3 文档 §11.2 曾规划 `asset.listThemes` / `asset.readTheme`，但从未进入契约；P4 前端已按此调用导致 `method.notFound`。本次按既有 `asset.<资源>.<动作>` 命名规范正式纳入契约，旧名不再保留（代码未发布）。

**影响**：Engine.Server 注册表 +2 方法，`KnownMethods` 58 → 60，`agent.d.ts` 重新生成。

P3 按阶段文档要求补充分工元数据与宿主对话框方法。`contractVersion` 保持 1：
- 全部 58 个方法新增可选字段 `implementedIn`（`engine` | `host`，缺省按 `engine` 理解）
- 新增 4 个宿主实现方法：`fs.browse` / `fs.pickFiles` / `fs.pickSavePath` / `fs.readImageAsBase64`
  （`implementedIn: "host"`，由宿主 `AgentBridge` 就地处理，不经过引擎进程）
- 既有 54 个方法的 `params` / `result` 形状零改动；帧信封、事件、错误码零改动

## [1] - 2025-01-01 · 冻结

首次冻结。包含 54 个方法、24 个事件、15 个错误码。

**冻结内容**：
- 帧信封：`req` / `res` / `evt`，字段 `t/id/m/p/r/e/ok/seq/k`
- `state.snapshot` 为全量快照，前端据此完整重建界面
- 询问机制：单待答 + `askId` + 引擎侧政策兜底

**后续约束**：
- P2–P5 只允许新增可选字段
- 破坏性变更必须升版本并在本文档记录迁移方式

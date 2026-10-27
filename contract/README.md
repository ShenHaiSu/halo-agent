# HaloAgent 通信契约

唯一真源：`agent.schema.json`（`contractVersion: 1`，已冻结 2025-01-01）。

## 文件

- `agent.schema.json` — 帧信封 + 60 方法（56 engine + 4 host）+ 24 事件 + 15 错误码
- `CHANGELOG.md` — 版本历史（破坏性变更必须记）
- `README.md` — 本文件

## 使用

- C#：`Engine.Abstractions` 的 DTO 与 `HaloJson.Options` 必须与 schema 字段名一致；
  `tests/Contract.Tests` 会校验三方一致。
- TS：`node scripts/gen-contract-types.mjs` 生成
  `src/Web.Client/src/contract/agent.d.ts`，`pnpm typecheck` 校验使用点。

## 扩展规则（冻结后）

- 只允许新增**可选**字段；
- 禁止修改已有字段语义/类型；破坏性变更需升 `contractVersion` 并写迁移说明。

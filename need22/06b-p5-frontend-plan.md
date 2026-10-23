# need22-06b P5 前端工程化施工方案

> 对应诊断：[06a](./06a-p5-frontend-diagnosis.md)。只改构建与规范，不改视觉。

## 1. 前置检查

- [ ] P2/P3 已合入；`npx vue-tsc -b` 绿。
- [ ] 确认 Node 版本（锁 22）与 pnpm 版本。

## 2. J1–J3 脚本 + 质量门禁 + CI

* **J1** `package.json` 补脚本：
  ```json
  "typecheck": "vue-tsc --noEmit -p tsconfig.json",
  "selfcheck": "node --experimental-strip-types scripts/toc.selfcheck.mjs",
  "lint": "eslint .",
  "format": "prettier --write .",
  "build": "npm run typecheck && npm run selfcheck && vite build"
  ```
* **J2** 加 `eslint + prettier + lint-staged + husky(pre-commit)`；存量报错先 `warn`，一周后收紧 `error`；规则与 `.editorconfig` 对齐。
* **J3** 新增 `.github/workflows/web.yml` + `.nvmrc(22)` + `engines`：
  ```yaml
  steps: pnpm i --frozen-lockfile → typecheck → selfcheck → build
  ```
  `verifyDepsBeforeRun` 改回默认 `true`。

## 3. J4 拆 hostBridge（与 P3-G5 同一人，独立分支）

```text
bridge/types.ts   ← 类型 + BRIDGE_VERSION + 错误码
bridge/client.ts  ← 真实 CefSharp 调用
bridge/mock.ts    ← Mock（显式标记）
bridge/proxy.ts   ← negotiate() 入口
```

单文件目标 <800 行；`catch{}` 全改为 `{code,message}` + 日志。

## 4. J5–J7 瘦身与规范

* **J5**：`naive` 按需引入或 `manualChunks(naive-ui/echarts/vue-vendor)`；`emptyOutDir` 改仅清 `assets/`；`chunkSizeWarning` 恢复 500。
* **J6**：`vue/naive-ui/vite/pinia/vue-router` 锁精确版去 `^`；`noUnusedLocals/Parameters` 改 `true`（分批修存量，先 `warn`）。
* **J7**：`stores Persisted*` 类型导出补齐；`cards/*Card.vue` 嵌套拍平一层；行内时间/字节格式化收敛到 `utils/`。

## 5. 提交切分

```text
j1-scripts → j2-lint → j3-ci → j4-bridge-split → j5-slim → j6-lock-j7-types
```

# need22-06 P5 前端工程化

> 等级：P2。前置：P2+P3。只改构建与规范，不改视觉与交互。

## 1. 前置检查

- [ ] 确认 `package.json:6-9` 仅 `dev/build/preview`；`scripts/toc.selfcheck.mjs` 悬空未接入。
- [ ] 确认 `vite.config.ts:22 emptyOutDir:true`、`main.ts:3` 全量 `import naive`、`chunkSizeWarning:2500`。
- [ ] 确认无 `.nvmrc`、无 eslint/prettier/husky、无 CI。

## 2. 既定目标

1. 脚本补齐：`typecheck/selfcheck/lint/format` 独立可跑，`build` 前串行。
2. CI 门禁：`frozen-lockfile → typecheck → selfcheck → build`。
3. `hostBridge` 可维护：拆模块 + 版本协商 + 错误码。
4. 构建瘦身：首屏 <1MB（gzip 前 <2.9M 现状减半起步）。

## 3. 改动清单

| # | 动作 | 说明 |
|---|---|---|
| J1 | `package.json` 补脚本 | `typecheck: vue-tsc --noEmit -p tsconfig.json`、`selfcheck: node --experimental-strip-types scripts/toc.selfcheck.mjs`、`lint: eslint .`、`format: prettier --write .`；`build: npm run typecheck && npm run selfcheck && vite build` |
| J2 | 加 `eslint + prettier + lint-staged + husky(pre-commit)` | 规则与 `.editorconfig` 对齐；存量报错先 `warn` 后收紧 |
| J3 | 新增 `.github/workflows/web.yml` + `.nvmrc(Node 22)` + `engines` | `pnpm i --frozen-lockfile → typecheck → selfcheck → build`；`verifyDepsBeforeRun` 改回默认 `true` |
| J4 | 拆 `src/bridge/hostBridge.ts` | `bridge/types.ts` + `bridge/client.ts` + `bridge/mock.ts` + `bridge/proxy.ts`；头部 `BRIDGE_VERSION + negotiate()`；`catch{}` 改为 `{code,message}` + 日志 |
| J5 | 瘦身构建 | `naive` 按需引入或 `manualChunks`（`naive-ui/echarts/vue-vendor`三分）；`emptyOutDir` 改为仅清 `assets/`（`vite-plugin` 或构建前 `rimraf assets`）；`chunkSizeWarning` 恢复 500 |
| J6 | 锁定关键依赖去 `^` | `vue/naive-ui/vite/pinia/vue-router` 锁精确版；其余保持 `^`；`tsconfig noUnusedLocals/Parameters` 改 `true`（分批修存量） |
| J7 | `stores/*` 类型导出补齐 | `Persisted*` 等统一导出；`components/settings/agent/cards` 嵌套拍平一层 |

## 4. 验收标准与验收方法

| 验收项 | 标准 | 方法 |
|---|---|---|
| F1 脚本 | 四脚本独立绿 | `pnpm typecheck/selfcheck/lint --no-fix` |
| F2 CI | PR 必跑 web 门禁 | 推空 PR 看 Actions |
| F3 拆分 | `hostBridge.ts` 单文件 <800 行 | `wc -l src/bridge/*` |
| F4 瘦身 | 主包 <1.5M（先减半，后续再压） | `ls -lh ../Host.Desktop/wwwroot/assets` |
| F5 无视觉回归 | 像素级不变 | P3 的回归截图复用 |

## 5. 回滚

* J1–J3 纯新增，回滚即删；J4 独立分支；J6 锁版回滚即恢复 `^`。

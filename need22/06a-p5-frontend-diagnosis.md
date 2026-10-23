# need22-06a P5 前端工程化诊断

> 只摆证据。根 `develop/src/Web.Client/`。

## 1. 构建：单薄但可用

| 位置 | 现状 |
|---|---|
| `package.json:6-9` | 仅 `dev/build/preview`；`build=vue-tsc -b && vite build` 类型检查耦合构建，无独立 `typecheck` |
| `scripts/toc.selfcheck.mjs:2` | `node --experimental-strip-types` 直接 import 纯函数断言，思路好但未接入 scripts，悬空 |
| `vite.config.ts:13-26` | `@` 别名、`target/chrome109`、`outDir=../Host.Desktop/wwwroot`、`sourcemap:false`，桌面端取向正确 |
| `vite.config.ts:22` | `emptyOutDir:true` 会清空整个 `wwwroot`，宿主另放文件会被误删 |
| `src/main.ts:3` | 全量 `import naive`，单包 `index-*.js 2.9M`，`chunkSizeWarning:2500` 掩盖 |
| 环境 | 无 `.nvmrc/.node-version`，无 `import.meta.env` 管理 |

## 2. 依赖：锁定有，浮动大

| 位置 | 现状 |
|---|---|
| `pnpm-lock.yaml(61KB)` | 已提交；`tsconfig strict:true` 且别名与 vite 对齐，健康 |
| `package.json` | 除 `echarts/typescript` 外全 `^` 浮动，大版本漂移风险 |
| `pnpm-workspace.yaml:5` | `verifyDepsBeforeRun:false` 弱校验；单包项目用 workspace+`allowBuilds` 过度配置 |

## 3. 质量：零门禁

| 缺失 | 现状 |
|---|---|
| 无 eslint/prettier/husky/CI | `tsconfig:15-16 noUnusedLocals/Parameters:false` 放水；`sourcemap:false` 可接受但无错误上报兜底 |
| `wwwroot/assets` 2.9M、`node_modules 303M`、`tsbuildinfo` | 均被 `.gitignore` 忽略未污染 git，健康（本地臃肿不算债） |

## 4. 规范：巨石 + 嵌套

| 位置 | 现状 |
|---|---|
| `src/bridge/hostBridge.ts` 2200+ 行 | 大量 `catch{}` 空吞（`:2111,2117`），无版本协商 |
| `stores/*` | 部分 `type` 未导出（`Persisted*` 缺失） |
| `components/settings/agent/cards/*Card.vue` | 嵌套过深；`utils/*(9个)` 复用尚可，时间/字节格式化有行内重复 |

## 5. 定级

| 等级 | 项 |
|---|---|
| P2 阻塞 | 无 CI、无独立 typecheck/selfcheck、hostBridge 巨石 |
| P3 一般 | naive 全量、emptyOutDir、依赖浮动、noUnused 放水 |

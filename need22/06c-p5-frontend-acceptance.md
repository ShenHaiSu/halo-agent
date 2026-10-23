# need22-06c P5 前端工程化验收与回滚

> 对应方案：[06b](./06b-p5-frontend-plan.md)。

## 1. 前置检查

- [ ] P2/P3 已合入；`npx vue-tsc -b` 绿。

## 2. 验收矩阵

| # | 验收项 | 标准 | 方法 |
|---|---|---|---|
| F1 脚本 | `typecheck/selfcheck/lint` 独立绿 | `pnpm typecheck && pnpm selfcheck && pnpm lint --no-fix` | 命令 |
| F2 CI | PR 必跑 web 门禁；`--frozen-lockfile` 生效 | 推空 PR 看 Actions | CI |
| F3 拆分 | `bridge/` 单文件 <800 行；无空 `catch{}` | `wc -l src/bridge/* && grep -rn "catch{}" src/bridge` 为空 | 命令 |
| F4 瘦身 | 主包 <1.5M（现状 2.9M 先减半） | `ls -lh ../Host.Desktop/wwwroot/assets` | 命令 |
| F5 无视觉回归 | P3 回归截图复用，像素级不变 | 截图 diff | 对比 |

## 3. 回滚

| 场景 | 动作 |
|---|---|
| 锁版导致装包失败 | 恢复 `^`，逐个钉版本 |
| `noUnused` 改 true 爆红 | 先回 `false`，分批修（按文件白名单渐进） |
| J4 拆桥接翻车 | 回滚 `j4` 分支（与 P3-G5 同分支策略） |

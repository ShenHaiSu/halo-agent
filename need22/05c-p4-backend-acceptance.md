# need22-05c P4 后端工程化验收与回滚

> 对应方案：[05b](./05b-p4-backend-plan.md)。

## 1. 前置检查

- [ ] P0/P1 已合入（CI 门禁依赖测试绿）。

## 2. 验收矩阵

| # | 验收项 | 标准 | 方法 |
|---|---|---|---|
| E1 集中版本 | 改 `Directory.Packages.props` 一处，全解构建绿 | 改版后 `dotnet build` | 本地 |
| E2 格式化 | `dotnet format --verify-no-changes` 绿 | 本地 + CI 双跑 | 命令 |
| E3 CI | PR 必跑 build/test/publish；`SingleFile=true` 被拦截 | 推空 PR 看 Actions | CI |
| E4 清理 | `clean.cmd` 后 `bin/obj/artifacts` 为空且可重编 | 运行 + 重编 | 脚本 |
| E5 签名 | 发布物签名状态可查（自签占位亦可） | `signtool verify` | 命令 |
| E6 版本 | iss 包版本 == Assembly == tag | 打包核对 | 手工 |

## 3. 回滚

* 纯新增文件，回滚即删；H1/H2 回滚后恢复各 csproj 原版本号；CI 红灯不阻塞 P0–P3 热修（`workflow_dispatch` 跳过门禁通道保留）。

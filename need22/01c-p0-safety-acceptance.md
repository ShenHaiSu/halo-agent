# need22-01c P0 安全验收与回滚

> 对应方案：[01b](./01b-p0-safety-plan.md)。

## 1. 前置检查（验收前必须满足）

- [ ] 施工分支 `dotnet test -c Release` 全绿。
- [ ] 攻击样本库 `Stage15P0AttackSamples.cs` 已合入并绿。
- [ ] 无前端协议变更（只增错误码，不改字段）。

## 2. 验收矩阵

| # | 验收项 | 标准 | 方法 | 命令/位置 |
|---|---|---|---|---|
| A1 | 路径逃逸封堵 | `../` 写、`C:\Windows` 读、symlink 穿越全部 `PATH_OUTSIDE_WORKSPACE`，无文件写出 | 攻击样本单测 + 手工探针 | `dotnet test --filter Stage15P0` |
| A2 | 命令约束 | 越界 `workdir`/超长/`timeout<=0`/白名单外全部拒绝且有码 | 四组单测 + `exec_command` 探针 | 同上 |
| A3 | 未知工具拒绝 | 未知工具 `UNKNOWN_TOOL`，不再透传 | `ToolCallValidator` 单测 | `dotnet test --filter Stage17Validation` |
| A4 | Schema 对齐 | `skill_tool create`、`goal_tool progress` 合法走通 | 回归 + 手工调用 | 同上 |
| A5 | 备份可靠 | 并发 20 写一致；配额超限拒绝；`Undo` 100% 找回 | 并发脚本 + 配额压测 + 回放 | `tests/.../Stage15*` |
| A6 | 敏感封堵 | `callId=../../x` 拒绝；`sk-` 落盘前脱敏；`grep sk- artifacts` 为空 | 样本 + 全盘 grep | `grep -r "sk-" artifacts` |
| A7 | 无回归 | 全量测试绿 | 全量 | `dotnet test -c Release` |

## 3. 验收方法（照抄执行）

```bash
# 1. 安全单测
dotnet test tests/HaloAgent.Tests -c Release --filter "Stage15P0|Stage17Validation"
# 2. 全量回归
dotnet test tests/HaloAgent.Tests -c Release
# 3. 敏感落地抽查（发布目录）
grep -r "sk-" artifacts/publish  || echo OK
```

## 4. 回滚表

| 场景 | 动作 | 复验 |
|---|---|---|
| S1 误伤正常读写 | 回滚 `s1-*` 两提交 | A1 样本复验（预期部分放行）+ 记录豁免根 |
| S2 误杀合法命令 | 白名单加命令（配置项），不回滚代码 | A2 复验 |
| S3 误杀合法工具 | 回滚 `s3-*`，补 Schema 后重上 | A3/A4 复验 |
| S4 备份失败率升 | 回滚 `s4-backup`，旧备份保留只读 | A5 复验 |
| S5 脱敏误伤 | 放宽正则（白名单），不回滚 | A6 复验 |

## 5. 准出 gate

* A1–A7 全部通过才能合入主干；任一失败停在施工分支，不进 P1。

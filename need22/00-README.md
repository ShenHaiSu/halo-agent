# need22 全链路优化一站式方案：总览与分期地图

> 状态：方案（待排期）。作用域：`develop/` 全栈。
> 来源：对 `Host.Desktop`（编排/网关/工具/安全）与 `Web.Client`（状态/渲染/桥接）+ 前后端工程化的三路诊断收敛。
> 一句话目标：先堵安全洞，再治稳定性与性能，最后还工程债，全程可回滚、每阶段独立验收。

## 1. 现状一句话

* 后端：`AgentOrchestrator` 上帝类 + 轮首冻结不一致；`ToolRegistry→Dispatch` 有校验但路径越界只警告、`exec_command` 直透 `cmd.exe`、备份非原子；`Channel` 无界、`Barrier` 无超时、`HttpClient` 无超时、压缩持锁阻塞。
* 前端：`agent.turns/workspace.sessionMessages/askUser/subSession` 多真相源；`select/load/heal` 无取消令牌；时间线全量驻留 + 每 `TextDelta` 全量 `markdown+hljs`；`hostBridge.ts` 2200+ 行巨石 + 静默降级 Mock。
* 工程：前后端构建闭环都有但无 CI/格式化/签名；`wwwroot` 构建产物与 `bin/obj/artifacts` 本地堆积；依赖浮动（前端全 `^`、后端无中央包管理）。

## 2. 分期地图（每阶段 = 诊断 a + 方案 b + 验收 c）

| 阶段 | 主题 | 等级 | 三篇文档 | 前置依赖 |
|---|---|---|---|---|
| P0 | 后端安全加固 | P0 最高 | [a诊断](./01a-p0-safety-diagnosis.md) · [b方案](./01b-p0-safety-plan.md) · [c验收](./01c-p0-safety-acceptance.md) | 无，可立即开工 |
| P1 | 后端稳定性与性能 | P1 | [a诊断](./02a-p1-stability-diagnosis.md) · [b方案](./02b-p1-stability-plan.md) · [c验收](./02c-p1-stability-acceptance.md) | P0 回归基线 |
| P2 | 前端状态与交互收敛 | P1 | [a诊断](./03a-p2-state-diagnosis.md) · [b方案](./03b-p2-state-plan.md) · [c验收](./03c-p2-state-acceptance.md) | P1 错误码到位 |
| P3 | 前端性能与桥接健壮性 | P2 | [a诊断](./04a-p3-perf-diagnosis.md) · [b方案](./04b-p3-perf-plan.md) · [c验收](./04c-p3-perf-acceptance.md) | P2 单真相源 |
| P4 | 后端工程化 | P2 | [a诊断](./05a-p4-backend-diagnosis.md) · [b方案](./05b-p4-backend-plan.md) · [c验收](./05c-p4-backend-acceptance.md) | P0+P1 |
| P5 | 前端工程化 | P2 | [a诊断](./06a-p5-frontend-diagnosis.md) · [b方案](./06b-p5-frontend-plan.md) · [c验收](./06c-p5-frontend-acceptance.md) | P2+P3 |
| — | 总验收与路线图 | — | [总验收](./07-acceptance-roadmap.md) | 全部 |

> 原则：P0/P1 不动前端协议（只增字段不改字段）；P2/P3 不动后端落盘格式（`messages.jsonl`）；P4/P5 不改运行时行为，只改构建与规范。

## 3. 统一施工纪律

1. 每阶段独立分支、独立提交、独立回滚；合入前 `dotnet test` + `vue-tsc -b` 全绿。
2. 安全类改动默认拒绝优先于警告；行为变更写进 `docs/pitfalls` 或对应 `README`。
3. 性能类改动必须先有基线数据（见各 `a诊断` 篇“基线待录”），无基线不准优化。
4. 桥接/协议改动只增不改，旧字段保留至少一个版本。
5. 每阶段内按 `a → b → c` 阅读：先看诊断证据，再照方案施工，最后按验收矩阵准出。

## 4. 完整文档索引

```text
00-README（本文）
├─ P0 安全
│  ├─ 01a-p0-safety-diagnosis.md    证据：越界放行/命令直透/备份明文（风险定级）
│  ├─ 01b-p0-safety-plan.md         方案：S1–S5 改动 + 8 提交切分
│  └─ 01c-p0-safety-acceptance.md   验收：A1–A7 矩阵 + 验证命令 + 回滚表
├─ P1 稳定
│  ├─ 02a-p1-stability-diagnosis.md
│  ├─ 02b-p1-stability-plan.md      （T1–T7 + 提交切分）
│  └─ 02c-p1-stability-acceptance.md（B1–B7 + mock 网关）
├─ P2 状态
│  ├─ 03a-p2-state-diagnosis.md
│  ├─ 03b-p2-state-plan.md          （F1–F6 + 提交切分）
│  └─ 03c-p2-state-acceptance.md    （C1–C6 + 录屏存档）
├─ P3 性能桥接
│  ├─ 04a-p3-perf-diagnosis.md
│  ├─ 04b-p3-perf-plan.md           （G1–G6 + 提交切分）
│  └─ 04c-p3-perf-acceptance.md     （D1–D6 + Performance 录制）
├─ P4 后端工程
│  ├─ 05a-p4-backend-diagnosis.md
│  ├─ 05b-p4-backend-plan.md        （H1–H9 + 提交切分）
│  └─ 05c-p4-backend-acceptance.md   （E1–E6）
├─ P5 前端工程
│  ├─ 06a-p5-frontend-diagnosis.md
│  ├─ 06b-p5-frontend-plan.md       （J1–J7 + 提交切分）
│  └─ 06c-p5-frontend-acceptance.md （F1–F5）
└─ 07-acceptance-roadmap.md         总验收矩阵 + 验证命令 + 回滚 + 工作量
```

> 旧单篇（`01-p0-backend-safety.md` 等 6 篇）已被上面 18 篇细化版替代，可删除；保留亦不影响阅读，准出以 `a/b/c` 三篇为准。

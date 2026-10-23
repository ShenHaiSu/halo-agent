# need22-07 总验收矩阵与路线图

## 1. 总验收矩阵（准出以各 `c验收` 篇为准）

| 阶段 | 三篇 | 一句话准出 |
|---|---|---|
| P0 安全 | [a](./01a-p0-safety-diagnosis.md) [b](./01b-p0-safety-plan.md) [c](./01c-p0-safety-acceptance.md) | 攻击样本全拒（A1–A7）+ `dotnet test` 绿 |
| P1 稳定 | [a](./02a-p1-stability-diagnosis.md) [b](./02b-p1-stability-plan.md) [c](./02c-p1-stability-acceptance.md) | 超时/重试/压缩/用量 B1–B7 全过 |
| P2 状态 | [a](./03a-p2-state-diagnosis.md) [b](./03b-p2-state-plan.md) [c](./03c-p2-state-acceptance.md) | 无双影/无竞态/问询不错票 C1–C6 |
| P3 性能桥接 | [a](./04a-p3-perf-diagnosis.md) [b](./04b-p3-perf-plan.md) [c](./04c-p3-perf-acceptance.md) | 无 >200ms 长任务 D1–D6 + 断桥有横幅 |
| P4 后端工程 | [a](./05a-p4-backend-diagnosis.md) [b](./05b-p4-backend-plan.md) [c](./05c-p4-backend-acceptance.md) | CI + 格式化 + 签名 E1–E6 |
| P5 前端工程 | [a](./06a-p5-frontend-diagnosis.md) [b](./06b-p5-frontend-plan.md) [c](./06c-p5-frontend-acceptance.md) | 四脚本绿 + 主包减半 F1–F5 |

## 2. 统一验证命令

```bash
# 后端（develop/ 下）
dotnet test tests/HaloAgent.Tests -c Release
dotnet publish src/Host.Desktop -r win-x64 --self-contained true -c Release
build/clean.cmd   # P4 落地后

# 前端（develop/src/Web.Client/ 下）
pnpm i --frozen-lockfile
pnpm typecheck && pnpm selfcheck   # P5 落地后
pnpm build
```

## 3. 建议施工顺序（最小阻塞）

```text
P0（安全，可立即）→ P1（稳定，共用基线）
  → P2（状态，需 P1 错误码）→ P3（性能，需 P2 行表）
    → P4/P5（工程，可并行）→ 本篇总验收
```

* 人力充足时 P4 可与 P2 并行（零运行时交集）；P5 可与 P3 并行（J4 拆桥接需与 G5 同一人）。
* 每个阶段合入前必须跑 §2 对应命令，全绿才能进下一阶段。
* 阅读顺序：每阶段 `a诊断 → b方案 → c验收`。

## 4. 分阶段回滚表

| 回滚场景 | 动作 | 详见 |
|---|---|---|
| P0 单条误伤正常工具 | 回滚对应 S 提交，攻击样本库复验 | [01c](./01c-p0-safety-acceptance.md) §4 |
| P1 超时过严 | 只调配置值，无需回滚代码 | [02c](./02c-p1-stability-acceptance.md) §4 |
| P2 F1 单轨化翻车 | 回滚 `p2-f1` 分支 | [03c](./03c-p2-state-acceptance.md) §4 |
| P3 G1 虚拟列表翻车 | 回滚 `g1-vlist` | [04c](./04c-p3-perf-acceptance.md) §4 |
| P4/P5 工程翻车 | 删新增文件/恢复 `^` | [05c](./05c-p4-backend-acceptance.md) §3 / [06c](./06c-p5-frontend-acceptance.md) §3 |

## 5. 工作量粗估（单人）

| 阶段 | 规模 | 备注 |
|---|---|---|
| P0 | 3–5 天 | 攻击样本 + 单测为主 |
| P1 | 4–6 天 | 压测 + mock 网关 |
| P2 | 3–4 天 | F1 大分支占一半 |
| P3 | 3–5 天 | 虚拟列表 + 拆桥 |
| P4 | 1–2 天 | 纯新增文件 |
| P5 | 2–3 天 | 拆桥接 + 瘦身 |

> 先 P0+P1 止血，再谈体验；工程债（P4/P5）放到最后但 CI 越早越好——若排期紧，允许 P4-H4 与 P5-J3（两条 CI）提前到 P0 之后插队落地。

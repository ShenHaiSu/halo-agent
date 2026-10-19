# need10-04 联调与测试计划（合闸门）

> 前置：`02+03` 完成。本篇全绿方可合闸。

## 1. 合闸命令（ frozen ）

```bat
dotnet build HaloAgent.sln -c Release
dotnet test tests/HaloAgent.Tests -c Release
cd src/Web.Client && npx vue-tsc -b && npx vite build
```

外加评审 grep 卡点（零增改证明）：

```bash
grep -rn "n-data-table" src/Web.Client/src/components/settings/SkillMemoryView.vue && exit 1 || echo "no-ui-table OK"
grep -rn "listSkillMemory\|deleteSkillMemory" src/Web.Client/src/bridge/hostBridge.ts
```

## 2. 后端单测（`tests/HaloAgent.Tests/Stage10SkillMemoryUiTests.cs` 新建）

- T1 `List_all_无工作区只返Global`：project 空、不抛。
- T2 `Scope_all_写拒绝`：`SetEnabled/Delete(scope=all)` → `SKILL_VALIDATION`。
- T3 `非法名拒绝`：`../con/超长` → `SKILL_VALIDATION/PATH_TRAVERSAL`。
- T4 `删除留bak`：删后同级存在 1 个 `.deleted-*.bak`，`List` 不再含该名。
- T5 `开关保留正文`：`SetEnabled` 前后 `BodyMarkdown/Hash` 不变，仅 `enabled` 翻转。
- T6 `Project覆盖Global读合并`：同名时 `all` 只返 project 一条。

## 3. 前端 `data-testid`（联调硬依赖）

`skill-memory-tab / sm-kind-seg / sm-scope-select / sm-workspace-select / sm-search / sm-refresh / sm-native-table / sm-switch-{scope}-{name} / sm-preview-{scope}-{name} / sm-folder-{scope}-{name} / sm-delete-{scope}-{name} / sm-preview-modal / sm-preview-folder / sm-empty`

## 4. 手工矩阵（真后端 × Mock 一致）

| # | 步骤 | 预期 |
|---|---|---|
| M1 | 开抽屉→第 8 Tab | 标题「技能与记忆」，无横溢；`defaultTab="skill-memory"` 直达 |
| M2 | 切 技能/记忆+域+关键字 | 内存过滤即时，无请求抖动；无工作区黄字正确 |
| M3 | 点名称/预览 | 弹窗只读 800 字+截断注脚，无编辑框 |
| M4 | Switch 关→开 | 成功文案带下一轮生效；下一轮 prompt 快照对齐 |
| M5 | 删除→强确认→确认 | 行消失，提示含 `.bak` 名；资源管理器同级可见 bak |
| M6 | 打开文件夹 | explorer 定位到 `<...>/<kind>/<name>/`，可见 `readme.md` |
| M7 | 存储迁移后 | Global 列表跟新盘走，Project 随工作区不变 |
| M8 | Mock 模式 | 5 方法占位数据可用，明确 `mock 仅示意` |

## 5. 验收勾选

- [ ] 无 `n-data-table`；原生 table 在 1020px/94vw 无横向滚动。
- [ ] 无新建/编辑入口；grep 零命中。
- [ ] 开关/删除/预览/文件夹四动作真后端可用；文案与 `01 §4` 逐字一致。
- [ ] `dotnet+vue-tsc+vite` 全绿；`04` T1-T6+M1-M8 全过。

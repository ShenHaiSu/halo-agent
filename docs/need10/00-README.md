# need10 施工方案总览（设置中心 Skill / Memory 只读管理 Tab）

> 输出路径：`docs/need10/`；方案先行，只写文档不改代码。
> 前置冻结：`need09-01` 存储（双域路径/命名/`readme.md`+`skill.json`/`memory.json`+`assets/`+`_index.json`）、`need09-07` 工具语义、`need06-04`/`need08-03` Tab 施工范式。
> 基线：`SettingsCenterDrawer` 7 Tab（`appearance/providers/network/storage/runtime/prompt/agent`）、`hostBridge.ts` 无 skill/memory 方法、`SkillMemoryService`（`List/Get/Delete/SetEnabled/ResolveAssetPath` 唯一出入口）、`SkillToolShared`（模型侧 CRUD）。

## 1. 一句话目标

在「设置与诊断中心」新增第 8 Tab「技能与记忆」，给用户**查（搜索/列表/预览）+ 删 + 开关**三个动作，**不给增/改**（无新建按钮、无正文编辑框）。点击条目卡片/行内预览按钮看简短预览（标题+摘要+正文前 N 字符），另有「打开文件夹」直达落盘目录，`Switch` 即时启停。表格一律原生 `<table>`，禁用 `n-data-table`。

## 2. 需求与文档映射（按顺序施工）

| 需求 | 文档 | 回答的问题 | 主要触点 |
|---|---|---|---|
| R1 权限与信息架构 | `01-requirements-analysis.md` | 谁能点什么、Tab 放哪、文案冻结 | Drawer、权限矩阵 |
| R2 后端+Bridge | `02-backend-bridge.md` | 5 个只读/受限方法签名、DTO、校验、事件 | `HostBridgeFacade.SkillMemory.cs`、`SkillMemoryService` 复用 |
| R3 前端 Tab UI | `03-frontend-ui.md` | 单 Tab 双类切换、原生 table、卡片预览、Switch、删除、打开文件夹 | `SkillMemoryView.vue`、`skillMemoryStore.ts`、`hostBridge.ts` |
| R4 联调验收 | `04-integration-test-plan.md` | 怎么证明做对了、合闸命令、手工矩阵 | `dotnet test`、`vue-tsc`、`data-testid` |

依赖：`01 → 02 → 03 → 04`。`02` Bridge 签名先合入，`03` Mock 可并行。

## 3. 范围一句话

- **做**：第 8 Tab + 种类切换（skill/memory）+ 域筛选（global/project/all）+ 关键字搜索 + 原生表格列表 + 预览弹窗（只读）+ Switch 开关 + 删除（强确认+.bak 提示）+ 打开文件夹 + Mock。
- **不做**：新建/编辑正文/`assets` 上传/重命名/拖拽排序/导入导出/向量搜索/按会话覆盖/云同步。模型侧 `skill_tool`/`memory_tool` 语义不动；`SkillMemoryService` 存储格式不动；不新增 `_index.json` 字段。

## 4. 通用施工原则（四篇通用，冲突以各篇“精确到文件/行”为准）

1. **只减不加**：前端不拼写盘路径（防 `PATH_TRAVERSAL` 蒙版），一切路径由后端 `DirPath` 回传+`OpenPathInExplorer` 复用打开。
2. **下一轮生效**：开关/删除成功文案必带「下一轮对话起生效」，流式中本轮不受影响（沿用 need08/need09 话术）。
3. **失败不断会话**：单条损坏跳过+Warn+横幅，会话可正常发起；删除失败回滚行状态。
4. **Win7/CefSharp109 红线**：不升级框架；不引 `n-data-table`（本篇明确禁用）；预览正文做长度截断（防 8000 字符卡顿）。
5. **可测性**：`data-testid` 见 `04`；后端 `dotnet build+test`，前端 `vue-tsc -b && vite build`；`04` 全表绿方可合闸。

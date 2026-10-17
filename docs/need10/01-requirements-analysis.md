# need10-01 需求分析（权限/信息架构/文案冻结）

> 前置：`00-README`。本篇冻结后才允许开工 `02/03/04`。

## 1. 用户故事（3 条，评审逐字对）

1. 作为用户，我要在设置中心找到 Skill/Memory，按关键字搜到条目，点开看它是什么（标题+摘要+正文节选），确认无用后删除或关掉开关，且**全程碰不到编辑框**。
2. 作为用户，我要区分 Global（跟数据根走）与 Project（跟当前工作区 `.haloagent/` 走），切工作区后 Project 列表跟着变。
3. 作为用户，我要点「打开文件夹」直接看到 `readme.md` 落盘位置，用外部编辑器自行处理（产品不接管手改）。

## 2. 权限矩阵（冻结，后端硬拦+前端隐藏）

| 动作 | 用户 UI | 模型 tool | 后端 Bridge |
|---|---|---|---|
| 查：列表/搜索/详情预览 | ✅ 允许 | ✅（`list/get`） | ✅ 新增 2 只读方法 |
| 开关：`enabled on/off` | ✅ 允许（行内 Switch） | ✅（`enable/disable`） | ✅ 新增 1 受限方法 |
| 删：整目录删除（留 `.bak`） | ✅ 允许（强确认） | ✅（`delete`） | ✅ 新增 1 受限方法 |
| 增：新建条目 | ❌ 无按钮无入口 | ✅（模型专属） | ❌ 不新增 `Create` |
| 改：改正文/pin/priority/资产 | ❌ 无编辑框（预览只读） | ✅（模型专属） | ❌ 不新增 `Update/WriteAsset` |

越权一律后端拒：`SKILL_VALIDATION / SKILL_NOT_FOUND / PATH_TRAVERSAL`（沿用 `SkillMemoryService` Code 前缀）。

## 3. 信息架构（Drawer 内位置）

`components/settings/SettingsCenterDrawer.vue`（现 7 Tab）：

1. 新增第 8 `n-tab-pane name="skill-memory" tab="技能与记忆"`，**顺序固定最后（agent 之后）**，不打乱老肌肉记忆（沿用 need06/need08 规则）。
2. `SettingsCenterTab` 联合类型追加 `'skill-memory'`；`defaultTab` 透传逻辑不动，支持 `defaultTab="skill-memory"` 直达。
3. Tab 内容根组件：新建 `components/settings/SkillMemoryView.vue`（600 行内），自上而下三区（不可调序）：
   - **A. 说明与筛选区**：只读说明文案 + `kind` 分段（技能/记忆）+ `scope` 下拉（全部/全局/项目）+ `工作区` 下拉（复用 `workspaceStore`，仅 Project/All 有效）+ 关键字搜索框 + 刷新按钮。
   - **B. 列表区**：原生 `<table>`（见 `03`），行=条目；空态/加载/失败三态齐备。
   - **C. 预览弹窗**：`n-modal` 内 `n-card`（只读），标题+元信息+正文节选+资产提示+「打开文件夹」按钮。

## 4. 固定文案（不可自由发挥）

- Tab 标题：`技能与记忆`。
- A 区说明：`仅查看、开关与删除，不支持新建与编辑。正文由模型经 skill_tool/memory_tool 维护；开关与删除下一轮对话起生效。Project 域随当前工作区 .haloagent/ 走。`
- 空态：`暂无条目（scope/关键字过滤后为空）。用模型对话让它先创建，或换个筛选试试。`
- 删除强确认（`dialog.error`）：标题 `确认删除？`，正文 `将删除 {scope} {kind} '{name}'，同级保留一份 .deleted-时间.bak 后不可撤销。下一轮对话起生效。`
- 开关成功：`已{启用|停用} {kind} '{name}'（{scope}）。下一轮对话起生效。`
- 删除成功：`已删除{scope} {kind} '{name}'（备份 {bakFileName}）。下一轮对话起生效。`
- 预览截断注脚：`正文过长，仅显示前 800 字，完整内容点「打开文件夹」查看 readme.md。`

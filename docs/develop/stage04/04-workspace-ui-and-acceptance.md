# Stage 04 - 04：多项目/多会话前端导航与上下文水位仪交互设计（Naive UI + Tailwind）及 Stage 04 验收标准

> **所属阶段**：Stage 04
> **核心目标**：基于 **Naive UI (`NSplit` / `NPopover` / `NDropdown` / `NProgress` / `NScrollbar`) + Tailwind CSS v3.4** 实现清晰高效的“多工作路径（项目文件夹） -> 多会话”二级导航工作台，并完成 Stage 04 验收。

---

## 一、 左侧边栏二级导航布局与交互设计（`WorkspaceSessionSidebar.vue`）

左侧边栏位于主界面 `<n-split>` 的左窗格（默认宽度 `260px`，支持在 `200px ~ 420px` 之间丝滑拖拽调节，也支持一键折叠为 `0px`），自上而下分为 **三大功能区**：

```text
+--------------------------------------------------+
| 1. 顶部：当前工作区切换舱 (Workspace Switcher)   |
| +----------------------------------------------+ |
| | [SVG: FolderGit2] halo-agent             [v] | |
| | F:\MyProgram\CSharp\halo-agent               | |
| +----------------------------------------------+ |
|                                                  |
| 2. 中部：会话操作与搜索栏                        |
| [ + 新建会话 (NButton Primary Block)          ]  |
| [ [SVG: Search] 搜索当前项目下的会话...       ]  |
|                                                  |
| 3. 下部：分组会话列表 (NScrollbar 独立滚动)      |
|   PINNED (置顶)                                  |
|   * [●] 架构设计与阶段文档拆解          [...]    |
|         14.2K Tokens · Hash: a94f8c              |
|   TODAY (今天)                                   |
|   * [ ] 调试 CMD 与 Python 执行器       [...]    |
|   * [ ] 接入 Responses 协议流式测试     [...]    |
|                                                  |
| ------------------------------------------------ |
| 4. 侧栏底部快捷工具条                            |
| [打开项目目录] [清空空会话]        [共 3 个会话] |
+--------------------------------------------------+
```

### 1. 工作路径（Workspace）弹出式切换器（`<n-popover trigger="click" placement="bottom-start" style="width: 340px">`）
* **触发器卡片**：采用 Tailwind `bg-[var(--bg-elevated)] hover:border-blue-500/50 transition-colors cursor-pointer rounded-md p-2.5 border border-[var(--border-subtle)]`，清晰展示当前项目名称与单行省略的完整物理路径（悬停 `<n-tooltip>` 显示全路径）。
* **弹出面板内容**：
  * **顶部搜索与统计**：`已绑定工作路径 (共 N 个项目)`。
  * **项目列表项**：
    * 左侧：`FolderOpen`（当前激活，蓝色高亮）或 `Folder` 图标。
    * 中部：项目名称 + 完整路径小字 + `<n-badge :value="ws.sessionCount" type="info" />` 会话数徽标。
    * **目录失效预警**：若后端 `Directory.Exists(ws.rootPath)` 返回 `false`，显示琥珀色 `AlertTriangle` SVG 图标 + `<n-tag type="warning" size="tiny">目录不存在</n-tag>`，点击可触发 `[重新定位文件夹]`。
    * 右侧快捷图标按钮（`<n-button quaternary circle size="tiny">`）：`在资源管理器中打开` (`ExternalLink`)、`从列表移除（不删物理文件）` (`Trash2`)。
  * **底部固定操作栏**：
    * `<n-button dashed block type="primary" @click="handleAddWorkspace">`（图标：`FolderPlus`）：`+ 添加新的工作路径 (绑定本地项目文件夹)`。

---

### 2. 当前项目下的多会话列表交互（Session List）
* **`[+ 新建会话]` 与快捷键支持**：
  * 点击按钮或按下快捷键 `Ctrl + N`，立即在当前激活的 Workspace 下创建新会话，自动生成固定 16 位 `${session-hash}`，并将输入焦点自动移入底部聊天输入框。
* **会话条目微交互与上下文菜单（`<n-dropdown>`）**：
  * **运行态呼吸灯**：若某后台会话正在执行 ReAct 循环或 `exec_command`，条目左侧显示蓝色脉冲动画圆点（Tailwind `animate-ping`），支持多会话并行切换查看而不会中断后台任务。
  * **行内重命名**：双击会话标题或点击右键菜单的“重命名”，标题原地切换为 `<n-input size="tiny" autofocus>`，回车或失焦即保存。
  * **右键/更多菜单（`<n-dropdown :options="sessionMenuOptions">`）**：
    1. `重命名会话`（图标：`Edit3`）
    2. `置顶 / 取消置顶会话`（图标：`Pin`）
    3. `复制会话哈希 (${session-hash})`（图标：`Hash`，点击后通过 `useMessage().success('已复制 Session Hash: a94f8c2d1e07b35a')` 提示）
    4. `打开会话 JSONL 存储位置`（图标：`FileText`）
    5. `删除会话`（图标：`Trash2`，红色警示项，带 `<n-popconfirm>` 二次确认）

---

## 二、 顶部工作台导航栏与上下文水位仪设计（`WorkbenchTopBar.vue`）

位于右窗格顶部的 `40px` 固定导航栏，集中承载**当前会话的模型切换、协议标识、`${session-hash}` 审计与上下文窗口水位监控**：

### 1. 会话级模型与协议快速切换器
* 使用 `<n-select filterable size="small" style="width: 260px">` 渲染按 Provider 分组（`NSelectGroupOption`）的模型列表。
* **自定义 Option 渲染函数（`renderOption` / `renderLabel`）**：
  * 每个模型选项右侧直观附带协议胶囊与窗口规格：
    * 若 `model.protocol === 'chat'`：显示蓝色微标 `CHAT`
    * 若 `model.protocol === 'resp'`：显示紫罗兰色微标 `RESP`
    * 旁注：`128K / Out 8K`

### 2. 上下文预算水位仪（Context Quota Meter）
* 在顶部栏右侧展示一个带环形进度条 `<n-progress type="circle" :percentage="contextUsagePercent" :stroke-width="10" style="width: 20px; height: 20px" />` 的胶囊按钮：`14.2K / 128K (11%)`。
* 颜色随水位自动预警：`< 70%` 科技蓝、`70% ~ 90%` 琥珀黄、`> 90%` 珊瑚红（提示即将触发滑动窗口自动裁剪）。
* **悬停详情弹出卡（`<n-popover trigger="hover" placement="bottom-end" style="width: 320px">`）**：
  * **线性进度条**：清晰展示 `已用上下文 (14,210)`、`最大输出预留 (8,192)`、`剩余安全可用空间 (105,598)` 的比例。
  * **会话路由与缓存指标列表**：
    * `当前会话 Hash (${session-hash})`：`a94f8c2d1e07b35a` `[一键复制]`
    * `当前通信协议`：`OpenAI Responses API (/v1/responses)`
    * `累计 Prompt Cache 命中率`：`81.4% (已节省 42,800 Tokens)`

---

## 三、 Stage 04 验收标准（Acceptance Criteria）

| 验收编号 | 验收维度 | 具体测试步骤 | 预期通过标准 |
| :--- | :--- | :--- | :--- |
| **S04-AC01** | **多工作路径添加与隔离** | 通过左上角工作区切换器添加 2 个不同的本地文件夹（如 `Project_A` 与 `Project_B`），在 `Project_A` 下创建 3 个会话，在 `Project_B` 下创建 2 个会话 | 1. 切换工作路径时，左侧会话列表严格按所属项目隔离展示<br>2. `<DataRootPath>/sessions/` 下按各自的 `workspaceId` 分目录存储 |
| **S04-AC02** | **多会话 JSONL 持久化与恢复** | 在多个会话中发送消息后完全关闭程序，重新启动 | 1. 上次激活的工作区与会话自动恢复<br>2. 所有会话的历史消息、Token 统计、以及固定 `SessionHash` 与关闭前 100% 一致 |
| **S04-AC03** | **上下文预算与工具链成对裁剪** | 将某模型的 `MaxContextTokens` 临时调小为 `4000`、`MaxOutputTokens` 设为 `1000`，连续进行多轮包含 Tool Call 的对话使其超过 `3000` Tokens | 1. 顶部水位仪准确反映占用比例；`TokenBudgetService` 自动按原子组裁剪早期历史，不触发上游 `400 context_length_exceeded`<br>2. 裁剪后的消息序列中绝不出现孤立的 `tool` 消息或缺失结果的 `tool_calls` |
| **S04-AC04** | **存储路径动态迁移联动验证** | 在存在多个工作区与多条会话历史后，通过设置中心将 C 盘存储路径修改并迁移至新目录 | 所有工作区索引、所有会话的 `meta.json` 与 `messages.jsonl` 在新目录下完整可读，历史记录零丢失 |

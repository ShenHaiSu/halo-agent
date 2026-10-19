# Stage 04 - 01：多工作路径（Workspace）、多会话（Session）与上下文预算管理（前置检查与当期目标）

> **所属阶段**：Stage 04（多工作区、多会话与上下文工程）
> **依赖前置**：Stage 01 ~ Stage 03 已全部完成并通过验收
> **直接覆盖核心需求**：需求 #4（支持多个工作路径，每个项目下多个会话）、需求 #5（历史写入 C 盘可配置存储路径）、需求 #7（按模型最大上下文与最大输出 Token 控制上下文）、需求 #8（会话固定 `${session-hash}` 生成与持久化）

---

## 一、 前置检查（Preflight Checklist）

1. **前置模块就绪确认**：
   * [x] `StoragePathManager` 已提供 `<DataRootPath>/workspaces/` 与 `<DataRootPath>/sessions/` 标准目录访问属性。
   * [x] `HostBridgeFacade` 已支持在 WinForms 主线程弹出原生 `FolderBrowserDialog`（用于用户添加新的项目工作路径）。
   * [x] `HeaderVariableResolver.ComputeDeterministicHash16` 已就绪，可用于生成会话创建时即锁定的 `SessionHash`。

---

## 二、 当期阶段目标（Stage 04 Objectives）

1. **多工作路径（Workspace）全生命周期管理**：
   * 用户可添加任意多个本地文件夹作为工作区（例如 `D:\ProjectA`、`E:\LegacyWin7App`、`F:\PythonScripts`）。
   * 自动检测工作路径在磁盘上是否依然存在（若文件夹被外部移动或删除，在 UI 标记“路径失效”并支持重新定位绑定）。
   * 记录每个工作区的最近访问时间、默认关联的 Provider/Model 偏好、以及项目级自定义指令（可选 `.halo/AGENTS.md` 或项目备注）。
2. **单项目下多会话（Multi-Session per Workspace）管理**：
   * 每个 `Workspace` 下支持创建、切换、重命名、置顶、归档、删除、导出多个独立 `Session`。
   * 每个 `Session` 在创建瞬间生成**全生命周期恒定不变的 `SessionHash`**（16 位小写 SHA-256 Hex），持久化在会话 `meta.json` 中，确保即使跨天重启软件继续同一会话，发给 Provider 的 `${session-hash}` 依然 100% 一致。
   * 采用 **`sessions-index.json`（轻量索引） + `messages.jsonl`（Append-Only 消息流）** 双层存储结构，切换工作区时无需全量解析所有会话的庞大聊天记录，毫秒级渲染列表。
3. **基于模型 `maxContextTokens` 与 `maxOutputTokens` 的上下文窗口预算引擎（`TokenBudgetService`）**：
   * 发送请求前，严格根据当前选中 Model 的 `MaxContextTokens` 和 `MaxOutputTokens` 计算可用输入预算：`AvailableInputTokens = MaxContextTokens - MaxOutputTokens - SafetyMargin`。
   * 当历史消息超出预算时，自动执行**保持 Tool Call 成对完整性的滑动窗口裁剪（Pair-Preserving Truncation）**，绝不因截断半截 `tool_calls` 导致 OpenAI `400 Bad Request` 报错。

---

## 三、 Stage 04 交付产物清单

| 模块分类 | 文件路径 | 核心职责说明 |
| :--- | :--- | :--- |
| **领域模型** | `src/Host.Desktop/Core/Models/WorkspaceModels.cs` | `WorkspaceInfo`、`SessionMetadata`、`UnifiedChatMessage` 持久化模型 |
| **仓储服务** | `src/Host.Desktop/Infrastructure/Storage/SessionRepository.cs` | `messages.jsonl` 追加写、流式按行读取、会话目录清理 |
| **业务服务** | `src/Host.Desktop/Services/WorkspaceSessionService.cs` | 多工作区注册、多会话切换、标题自动生成与 `SessionHash` 管理 |
| **上下文引擎** | `src/Host.Desktop/Services/TokenBudgetService.cs` | 离线高速 Token 估算器与成对工具调用保护裁剪算法 |
| **前端状态** | `src/Web.Client/src/stores/workspaceStore.ts` | 当前激活工作区、会话列表、激活会话消息流状态管理 |
| **前端组件** | `src/Web.Client/src/components/sidebar/WorkspaceSessionSidebar.vue` | 左栏工作路径切换器 + 会话分组列表 + 搜索过滤 |

---

## 四、 本阶段文档导航

* 下一篇：[`02-workspace-and-session-manager.md`](./02-workspace-and-session-manager.md) —— 详细设计多工作区与多会话领域模型、`SessionHash` 锁定与 JSONL 存储方案。
* 第三篇：[`03-context-window-and-token-budget.md`](./03-context-window-and-token-budget.md) —— 详细设计基于模型上下文配额的 Token 预算与无损工具链裁剪方案。
* 第四篇：[`04-workspace-ui-and-acceptance.md`](./04-workspace-ui-and-acceptance.md) —— 详细设计多工作区/多会话前端交互与 Stage 04 验收标准。

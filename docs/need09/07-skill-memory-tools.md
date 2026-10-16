# need09-07 Skill / Memory 管理工具（模型自主 CRUD）

> 前置：`01/02/05/06`。本篇覆盖需求 R9。
> 现状证据：`ToolRegistry(registerBuiltIns=true 注册 6 工具 + Register 同名覆盖 + DispatchAsync 未知工具/异常转自愈 + ToolOutputGuard + GetDefinitions 导出双协议 Schema)`；`IToolHandler{Name/Description/GetDefinition/ExecuteAsync}`；`AgentOrchestrator` 每次调度前填 `ToolContext{WorkspaceRoot/WorkspaceId/SessionId/DataRoot}`；`SystemPromptBuilder` 的 skill/memory 块下一轮生效。

## 1. 工具数量决策（冻结：2 个，不做 6 个）

| 方案 | 结论 | 理由 |
|---|---|---|
| 6 个（skill_list/get/create/update/delete + memory_*） | ❌ 否决 | tools 数组每 +1 即 +~300 tokens 常驻 + 前缀后段变长；6 个新增 ≈ +1800 tokens/轮，违背 02 低 token 目标 |
| **2 个（`skill_tool` + `memory_tool`，内含 action 枚举）** | ✅ 冻结 | 常驻 +~600 tokens；`action` 枚举值稳定排序（见 §3），改功能不改工具数，前缀极稳定 |
| 1 个（`knowledge_tool` 合并 skill+memory） | ❌ 否决 | skill（能力）与 memory（事实） prompt 指南不同，合并导致 Schema 分支复杂、误调率高 |

## 2. Schema（冻结，双工具对称）

### 2.1 `skill_tool`

```json
{"type":"object","properties":{
  "action":{"type":"string","description":"list|get|create|update|delete|enable|disable. Default list."},
  "scope":{"type":"string","description":"global|project. Default project. list 时可填 all."},
  "name":{"type":"string","description":"Skill 名（get/create/update/delete/enable/disable 必填，list 可空）。"},
  "content":{"type":"string","description":"create/update 时的 readme 正文（含可选 frontmatter）。"},
  "pin":{"type":"string","description":"auto|full（仅 create/update 可选，默认 auto）。"},
  "priority":{"type":"integer","description":"0..1000，默认 100。"},
  "enabled":{"type":"boolean","description":"create 时初始开关，默认 true。"}
},"required":["action"]}
```

### 2.2 `memory_tool`（同构，`pin` 枚举不同）

- `pin: pinned|archived|auto（默认 auto→pinned？冻结：默认 pinned？不——默认 auto，auto 对 memory 等价 pinned 但允许服务降级为 archived（超限时），见 02）`。为避免歧义，文档冻结：**memory 默认 `pin=pinned`**（用户记忆默认常驻，符合直觉；超限时服务自动截断并提示归档）。
- 其余字段与 `skill_tool` 同构（`action/scope/name/content/priority/enabled`）。

### 2.3 返回格式（冻结）

- `list` → 文本清单（`scope/name/pin/enabled/estTokens` 表格，超 20 条截断 + `…用 get 查详情`）；
- `get` → 全文（`readme.md` 去 frontmatter 后的正文 + 元数据头 3 行）；
- `create/update/delete/enable/disable` → 一句话确认（含下一轮生效提示 + 常驻 token 变化提示，如“已保存，下轮生效；常驻索引 +12 tokens”）。

## 3. 后端实现（精确到文件）

- 新增 `Tools/BuiltIn/SkillTool.cs`、`Tools/BuiltIn/MemoryTool.cs`（各 ~200 行，共享 `SkillMemoryService` + `SkillToolShared.ValidateName/ValidateContent/FormatList`）。
- `ToolRegistry` 构造新增 `Register(new SkillTool(skillMemoryService))` 等（无服务时构造空服务兜底，保证老单测 `new ToolRegistry()` 不崩）。
- `ToolContext` 新增 `SkillMemoryScopeDefault = "project"`（模型不填 scope 即写 Project——**默认写项目不写全局**，防模型污染全局；写 Global 需显式 `scope=global`，prompt 指南红字强调）。
- 双 Adapter 的 tools 排序：存量 `OrderBy(name)` 自动覆盖新工具（`ask_user < memory_tool < skill_tool < view_image` 的 ASCII 序，冻结后永不手动调序）。

## 4. 权限与安全（冻结，后端硬拦）

| 规则 | 行为 |
|---|---|
| 名称非法（01 §3.1） | `SKILL_VALIDATION` 拒绝 + 正则提示 |
| `../`、绝对路径、`.haloagent/_index.json` 直写 | 拒绝 `PATH_TRAVERSAL`（模型只能走 `name`，不能走路径；资产文件本期模型只读不写——写资产走 `write_file`，见 §6） |
| 跨工作区（`scope=project` 但 `name` 含 `/`） | 拒绝 |
| 配额超限（01 §6） | 拒绝 + 指明删哪个可腾出多少 |
| 删除 Global | 允许，但返回强提示“已删除全局 xxx，不可撤销（仅留 .bak）”+ `Warn` 日志；删除 Project 同理 |
| `pin=full`（skill 全文常驻） | 允许，但返回红字警告“每轮 +N tokens，确认常驻请保留，否则改回 auto” |

## 5. 热生效与提示词协同（02 联动）

- 写操作成功后：`SkillMemoryService` 重建索引 + 清 `GetMergedForPrompt` 快照 + `JsEventDispatcher.Publish("config:skill-memory-changed", {kind, scope, name})`（前端若有 Skill 面板则刷新，无面板则忽略）。
- 本轮不生效（`AgentOrchestrator` 轮首快照已取），工具返回文本必须带“**下轮生效**”后缀（冻结文案，避免模型以为即时生效而反复读）。

## 6. 明确不做（本期）

1. 模型直写 `assets/` 二进制（模型只能读写 `readme.md` 正文；资产读写走存量 `read_file/write_file` + 路径拼 `.haloagent/skills/<name>/assets/...`，复用沙箱）。
2. 按会话/模型分套覆盖（`scope` 只认 `global/project/all`，其余拒绝）。
3. 导入导出/市场/评分（`rating` 字段预留但忽略）。

## 7. 边角表

| # | 边角 | 预期 |
|---|---|---|
| E1 | 同名 Global/Project 写冲突 | 按 scope 精确写对应域；读合并时 Project 覆盖（01 E5） |
| E2 | 模型 `update` 不存在的 Skill | 拒绝 + 提示先 `list` 确认（不自动转 create，防拼写污染） |
| E3 | `content` 超 8000 字符 | 拒绝 + 指明截断到 8000 后的 token 估算 |
| E4 | 并发 `create` 同名 | `lock` 后写 wins，后写覆盖并提示覆盖 |
| E5 | `list scope=all` 无工作区（后台任务） | 只返 Global，Project 记 `(no workspace)` 空 |

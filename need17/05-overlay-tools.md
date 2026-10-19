# need17-05 · 叠加工具分册：ask / skill / memory / spawn / goal

> 前置：01（Exposure+preset）、02（码表）。对应 pi `toolDocs/00 §3`（曝光度）+ `10 §10.3-10.4`（简介规则/技能话术）。
> 冻结原则：overlay 工具**执行语义零变化**（鉴权、网关式拒绝、barrier 全部保持 need09/13/16 现状）；本分册只做**曝光度归位 + Schema 闭合 + 提示词动态装配输入**。

---

## 5.1 曝光度与 preset 归位

| 工具 | Exposure | Coding | ReadOnly | Full | 说明 |
|---|---|---|---|---|---|
| `ask_user` | Direct | — | — | ✓ | 编码任务默认不打扰；需要时 Full 或 `+ask_user` |
| `skill_tool` | Direct | — | — | ✓ | 同上（L0 索引仍常驻提示词，与声明解耦，保持 need09） |
| `memory_tool` | Direct | — | — | ✓ | 同上 |
| `spawn_subagent` | Direct | — | — | ✓ | 主包常显现状保持，仅声明层可裁剪；`ForSub` 仍用禁用桩 |
| `goal_tool` | Direct | — | — | ✓ | 同上；子只读鉴权不动 |
| `edit` | **Hidden** | — | — | — | 01 已定：可执行，不声明 |

* `Dispatch` 调被藏 overlay → `TOOL_NOT_ENABLED`（02），文案示例：`TOOL_NOT_ENABLED：工具 'skill_tool' 不在本轮激活集中（preset=Coding）。编码任务不需要它；如确需，请用 Full preset 或 +skill_tool。可用工具：read_file、exec_command、edit_file、edit_lines、write_file。`
* `ToolRegistryFactory.ForSub` 不动（子包裁剪逻辑已是事实 preset，声明侧同样走激活集过滤即可）。

### 验收

* `Overlay_HiddenInCoding`：Coding 声明 5 件；调 `skill_tool` 返回 `TOOL_NOT_ENABLED`；Full 下 15-1(edit)=14 件（含 ask/skill/memory/spawn/goal）。

## 5.2 Schema 补全（逐个，builder 生成）

* `ask_user`：required `[question]`（现状多为可选→冻结为必填，问题不能为空）；`options` array 可选；描述补：`Use when a choice blocks progress (destructive ops, ambiguous requirements). Do not use for status reports.` + `minLength:1` on question。
* `skill_tool`：`action` enum `[list,get]` 必填（现状字符串自由→枚举化，02 前置拒绝非法 action）；`scope/name` 可选；描述补：`Consult ## Available Skills index first; load body via skill_tool before applying.`。
* `memory_tool`：`action` enum `[list,get,set]` 必填；`name/body` 按 action 条件必填**无法用 JSON Schema 表达**→ Schema 保持可选，Validator 加一条业务级检查：`set` 缺 `name/body`→`MISSING_REQUIRED_PARAM`（Validator 支持按 action 的条件必填表，见实现注记）；描述写死条件。
* `spawn_subagent`：`task` string 必填 `minLength:1`；`label/role` 可选；描述补：`Include full background in task (sub sees no main history). Sub returns a summary; barrier waits for all.`。
* `goal_tool`：`action` enum `[get,set]` 必填；描述补：`Writes take effect next turn; sub sessions are read-only.`

实现注记：条件必填（memory set→name/body）放在 `ToolParameterShims` 同目录的 `ConditionalRequired.cs` 小表，Validator 第 3 步后执行；码复用 `MISSING_REQUIRED_PARAM`。

### 验收

* `Overlay_Schemas_Closed`：5 工具双协议解析通过；`skill_tool({action:"delete"})`→`ENUM_VIOLATION`；`memory_tool({action:"set"})` 无 body→`MISSING_REQUIRED_PARAM`。

## 5.3 提示词装配输入（供 06 消费，本分册只定数据）

* 每个 overlay 工具定 `PromptBrief`（一行）+ `PromptRules`（0-2 条），写入 `GetDefinition` 处的元数据（01 字段）：
  * ask：`Ask the user a blocking question` / 规则 `Ask only when blocked; never for progress updates.`
  * skill/memory/spawn/goal：brief 一行，规则 0-1 条（06 装配时按激活集逐行列出；Coding 下无这些行，提示词自动干净）。
* Skill L0 索引块、goal 块、sub 策略块（need09/13/16）保持现状，不动（声明裁剪不影响提示词常驻块）。

### 验收

* `Overlay_Briefs_Present`：Full 激活下 06 装配产物含 5 行简介；Coding 下 0 行。

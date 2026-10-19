# need17 总览：终结 LLM tool 调用失败与参数错误 — 工具体系全面重构

> 状态：施工方案（待实现）。前置：`docs/toolDocs/*`（pi-agent 设计基线）、`docs/need15/*`（错误码与备份已冻结）、`docs/need16/*`（goal/sub 已落地）。
> 目标：把“LLM 经常调错工具、传错参数”从模型运气问题，变为**声明强约束 + 中央校验 + 教学完备**的工程问题。
> 范围：只改工具声明/校验/注册/提示词装配/Agent 循环外围 + Python 兜底。不动业务源码语义，不做数据迁移，不改落盘算法（edit 匹配、write 覆盖语义保持现状）。

## 1. 问题地图（根因 → 方案文档 → 代码锚点）

| # | 症状 | 根因 | 方案文档 | 代码锚点 |
|---|---|---|---|---|
| 1 | 模型省略必填、多传字段、类型传错（`offset:"10"`）网关不拦 | Schema 无 `strict`/`additionalProperties:false`，裸透传 | [01](./01-declaration-and-activation.md) | `Tools/ToolDefinition.cs`、`LlmGateway/Adapters/ChatCompletionsAdapter.cs:171-189`、`ResponsesApiAdapter.cs:172-188` |
| 2 | 错类型静默走默认值（offset 字符串→回退 1），缺参只说“缺少 file_path” | 无中央校验，各工具手写 `TryGetProperty+ValueKind` 解析 | [02](./02-validation-pipeline.md) | `Tools/ToolRegistry.cs:140-229`（`DispatchAsync`）、各 `Tools/BuiltIn/*.cs:ExecuteAsync` |
| 3 | 选错工具（5 个写入口、edit mode 漏传） | 15 工具常驻全下发；`edit` 分发器多一层 mode；`file_path/path` 混用 | [01](./01-declaration-and-activation.md)、[03](./03-core-file-tools.md) | `Tools/ToolRegistry.cs:20-42`、`Tools/BuiltIn/EditTool.cs` |
| 4 | 模型靠猜上限/默认值/翻页（截断后才知道） | 描述一句话，上限只在执行层 | [03](./03-core-file-tools.md)、[04](./04-exec-search-tools.md) | `Tools/Safety/ToolOutputGuard.cs:12-16`、各 `GetDefinition()` |
| 5 | 叠加工具（skill/memory/ask/spawn/goal）污染编码任务、无 Schema 教学 | 无曝光度、无动态提示装配 | [05](./05-overlay-tools.md) | `Tools/ToolRegistryFactory.cs`、`Core/Models/PromptModels.cs` |
| 6 | 提示词只教 read/edit/write，其余 11 个工具无选型规则 | ToolGuide 静态 8 行 | [06](./06-prompt-and-loop.md) | `Core/Models/PromptModels.cs:49-58`、`Services/SystemPromptBuilder.cs` |
| 7 | 半截 JSON（截断回复）照样执行→“参数 JSON 解析失败” | 无 `finish_reason=length` 保护 | [06](./06-prompt-and-loop.md) | `Adapters/ChatCompletionsAdapter.cs:383-398`、`Services/AgentOrchestrator.cs:696-800` |
| 8 | 无 Python 的电脑上模型反复调 `runner:python` 烧 token | 启动无快照、拒绝文案英文、无安装引导 | [07](./07-python-fallback.md) | `Tools/BuiltIn/ExecCommandTool.cs:17-19,128-135`、`Tools/Process/CommandRunnerProbe.cs:113-142` |

## 2. 施工分期与硬顺序

```
P0 先行（必做）：01 声明扩展 + 02 中央校验 + 07 Python 兜底
  └─ 准出：畸形参数 100% 被结构化拒绝（单测），Python 缺失秒拒且文案逐字命中。

P1 紧接：03 + 04 + 05 各工具 Schema/描述重塑 + 06 提示词与循环加固
  └─ 准出：抽样 50 次真实任务，工具一次成功率 ≥90%，平均重试 ≤1 次。

P2 收尾：08 验收矩阵 + 灰度（strict Prefer→Require）+ 回滚开关验证。
  └─ 准出：回归全绿，灰度 7 天无网关 400 激增、无污染事故。
```

**硬约束**：02 的错误码是 03/04/05/06 自愈与 Hint 的前提，先冻结 02 附录码表，后续文档只追加不改码；07 可与 P0 并行（独立文件多）；06 的循环改动不得改工具参数语义。

## 3. 兼容承诺（冻结，全期有效）

1. 老调用零改动：只传老参数的调用行为不变；`path` 旧名继续有效（走别名搬运 + warning，不拒绝）；`edit` 分发器仍可执行（仅隐藏声明）。
2. 新增 Schema 约束只做“早拒绝 + 指明改法”，拒绝文案必带下一步；`write_file` 的 `allow_overwrite` 等 need15 语义保持。
3. 成功输出预算不变（`ToolOutputGuard` 16K / edit 回吐 2000 字符冻结）；`strict` 对不支持的第三方网关自动降级（Prefer 模式），不断连。
4. 唯一有意 breaking：`additionalProperties:false` 生效后，多传未知字段会被拒（此前静默忽略）——这是修 bug，文案会指明删掉哪个字段。

## 4. 文档索引

| 文档 | 内容 | 预计改动 |
|---|---|---|
| [01](./01-declaration-and-activation.md) | 声明层：ToolDefinition 扩展、strict 双协议、激活集 preset、edit 收敛、参数名统一 | 5 源码 + 1 测试 |
| [02](./02-validation-pipeline.md) | 中央校验管线：Validator+Shim+错误码表、Dispatch 接入 | 3 源码 + 1 测试 |
| [03](./03-core-file-tools.md) | 文件工具分册：read/write/edit_file/edit_lines/undo 的 Schema+描述+分工冻结 | 5 源码 + 1 测试 |
| [04](./04-exec-search-tools.md) | 执行搜索分册：exec/grep/list_dir/view_image 的上限声明化+截断教学 | 4 源码 + 1 测试 |
| [05](./05-overlay-tools.md) | 叠加工具分册：ask/skill/memory/spawn/goal 曝光度+Schema 补全+动态装配 | 4 源码 + 1 测试 |
| [06](./06-prompt-and-loop.md) | 提示词重写 + 工具段规则段动态装配 + 截断回复保护 + hooks 插槽 | 4 源码 + 1 测试 |
| [07](./07-python-fallback.md) | Python 缺失兜底专项：启动快照+后端秒拒+指定文案+设置引导 | 4 源码 + 1 测试 |
| [08](./08-test-rollout.md) | 验收矩阵、Stage17 测试清单、灰度与回滚 | — |

## 5. 施工纪律（所有文档通用）

* 每个文档独立分支、`Stage17*Tests.cs` 独立文件，合入前 `dotnet test tests/HaloAgent.Tests -c Debug` 全绿。
* 任何 Schema 文案改动必须双协议快照单测（`ToChatToolJson`/`ToResponsesToolJson` 可 `JsonDocument.Parse`，且 `required`/`additionalProperties` 断言）。
* 任何新增错误码必须：① 进 02 附录码表；② 进 `HintForTool`；③ 单测断言 `Error.StartsWith("CODE")` 且副作用未发生（文件不变/进程未起）。
* 测试不得依赖本机环境（Python/网关/大文件），一律用快照注入或假数据（07 的 `OverrideForTests` 为强制要求）。

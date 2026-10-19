# need15 总览：终结 `edit/write` 乱写 — P0→P2 施工路线图

> 状态：施工方案（待实现）。前置：`docs/need14/*`（R1–R5 已落地）。
> 目标：把“LLM 经常调错 edit、偶发写坏文件”从模型运气问题，变为**工具侧强约束 + 可验证 + 可回滚**的工程问题。
> 范围：只改网关工具层 + 提示词 + 测试 + 文档。不动业务源码，不做数据迁移。

## 1. 问题地图（对应代码锚点）

| 症状 | 根因 | 代码位置 | 归属 |
|---|---|---|---|
| 写 C# 插值/`$var`/bash 变量后内容被吞改 | `regex` 模式用 `Match.Result(newString)` 做 `$` 组展开 | `Tools/BuiltIn/EditFileTool.cs:270` | P0-1 |
| 模型用半截 `content` 调 `write_file` 直接覆盖整文件，不可逆 | 新建/覆盖同一工具，无 size/hash/备份 | `Tools/BuiltIn/WriteFileTool.cs:1-151` | P0-2 |
| 漏传 `limit` 就只换 1 行；`limit=0` 插入语义被误用 | `limit` 缺省 `=1` + 特殊值语义 | `Tools/BuiltIn/EditLinesTool.cs:108-118` | P0-3 |
| `},` 级短锚点批量污染（need13 污染 183 处复盘） | 仅拦 `replace_all + compact<8`，不拦模糊多命中 | `EditFileTool.cs:202-217` | P0-4 |
| 失配后模型盲猜重试，token 烧掉 | 错误是字符串，无机器可判 `code` | `Tools/ToolResult.cs` + `AgentOrchestrator.HintForTool` | P0-5 |
| 模型凭记忆拼 `old_string` 而不是复制 `read_file` | `ToolGuideTemplate` 只有 4 行软建议 | `Core/Models/PromptModels.cs:49-54` | P1 |
| 4 个归一开关模型乱调 | 参数空间过大 | `EditFileTool.cs:36-47` Schema | P1 |
| 大改不敢动，无预览无撤销 | 无 `dry_run`/备份/统一入口 | 新增 | P2 |

## 2. 三期划分与准入/准出

```
P0 止血（必做，先做）: 不改模型习惯也能防住丢数据/写坏。
  └─ 准出：Stage15 P0 全部单测绿 + 回归全绿 + 灰度 7 天零污染事故。

P1 加固（紧接）: 让“正确调用”变成最顺手的调用。
  └─ 准出：edit 一次成功率 ≥90%（抽样 50 次真实改码任务），重试 ≤1 次/任务。

P2 架构（最后）: 预览/撤销/统一入口，自愈闭环。
  └─ 准出：大文件（>2000 行）改动可 dry-run；误删可在 1 步内恢复。
```

**施工顺序硬约束**：P0-1 → P0-5 按序落地（P0-5 的错误码是 P1/P2 自愈的前提）；P1 不改 P0 已冻结的错误码字符串，只追加；P2 不改 P0/P1 的参数语义，只新增可选参数。

## 3. 兼容承诺（冻结）

1. 老调用（只传 `file_path/old_string/new_string[/replace_all]`）在 P0/P1 后行为不变，除两处安全收紧外：① `regex` 下 `$` 按字面写回（此前是展开，属于 bugfix，需在 CHANGELOG 声明）；② `write_file` 覆盖已存在文件需显式 `allow_overwrite:true`（此前无此参数，老调用第一次会收到 `NEED_CONFIRM` 而非直接覆盖——这是唯一的有意 breaking，需前端/编排层配合一次提示）。
2. 所有新增参数一律可选、有默认值；所有新增错误码以前缀 `大写下划线` 命名，且 `Error` 字符串首 token 即为码（如 `ANCHOR_TOO_SHORT：...`），方便正则判读。
3. 成功输出仍 `≤2000 字符`（need14 R4 冻结），P2 的 `diff` 扩展不得突破此预算，超限走 `truncated` + `read_file`。

## 4. 文档索引

| 文档 | 内容 | 预计改动文件数 |
|---|---|---|
| [`01-P0-critical-safety-fixes.md`](./01-P0-critical-safety-fixes.md) | P0-1 ~ P0-5 详细施工：$ 展开修复、write 护栏+备份、limit 陷阱、锚点拦截、结构化错误码 | 6 + 1 测试 |
| [`02-P1-schema-prompt-hardening.md`](./02-P1-schema-prompt-hardening.md) | P1：ToolGuide 硬规则、Schema 收敛+示例、回吐 hash/行号 | 4 + 1 测试 |
| [`03-P2-preview-undo-unified.md`](./03-P2-preview-undo-unified.md) | P2：dry_run、备份/撤销、统一 edit、自愈闭环 | 7 + 1 测试 |
| [`04-acceptance-test-plan.md`](./04-acceptance-test-plan.md) | 验收矩阵、性能预算、回归清单、灰度与回滚 | — |

## 5. 施工纪律（所有 P 通用）

* 每个 P 独立分支、独立 `Stage15*Tests.cs`，合入前 `dotnet test tests/HaloAgent.Tests -c Debug` 全绿。
* 任何落盘走 `FileToolShared.AtomicWriteAllText`，禁止直写 `File.WriteAllText`。
* 任何新增错误码必须同时：① 写进本文档附录表；② 写进 `HintForTool`；③ 有单测断言 `Error.StartsWith("CODE")` 且文件字节未变。
* 前端卡片（`ToolCallCard.vue`）缺失 `edit_lines/dry_run` 映射时，后端先行，允许前端通用卡片降级显示，不得阻塞网关合入（同 need14 §8.1 惯例）。

## 附录：错误码总表（P0 定稿，P1/P2 只追加）

| Code | 抛出点 | 含义 |
|---|---|---|
| `ANCHOR_TOO_SHORT` | edit_file | replace_all 锚点去空白 <8（存量，语义收紧见 01） |
| `ANCHOR_AMBIGUOUS` | edit_file | 非 replace_all 下多命中且相似度接近（新增） |
| `OLD_STRING_NOT_FOUND` | edit_file | 未命中（存量，改结构化负载） |
| `REGEX_ZERO_LENGTH` | edit_file | 正则零长命中（存量，改码名前缀统一） |
| `LITERAL_DOLLAR_NOTE` | edit_file | regex 下检测到 `$` 按字面处理时的回吐标记（回吐非错误） |
| `NEED_CONFIRM_OVERWRITE` | write_file | 覆盖已存在文件缺 `allow_overwrite:true`（新增） |
| `CONTENT_TOO_LARGE` | write_file | 全量内容超限（新增） |
| `EMPTY_NEW_TEXT` | edit_lines | 空文本未授权（存量） |
| `EXPECTED_HASH_MISMATCH` | edit_lines | 并发变化（存量） |
| `OUT_OF_RANGE` | edit_lines | offset 越界（存量） |
| `NO_CHANGE` | edit_lines | 无实质变化（存量） |
| `MISSING_LIMIT` | edit_lines | P0-3 后 limit 缺失（新增，替代隐式 =1） |

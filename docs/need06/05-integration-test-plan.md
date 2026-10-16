# 05 · 联调与测试计划（单测矩阵 · 端到端 · 合闸命令）

> 前置：`02+03+04` 完工。本篇是合闸唯一依据；任何一项红即不合闸。

## 1. 后端单测（新建 `tests/HaloAgent.Tests/Stage06PromptTests.cs`，≥10 个）

| # | 用例名 | 断言 |
|---|---|---|
| T1 | `Default_Render_Equals_Legacy_Template` | 同 workspace/py/date 下 `BuildPreview(default).AssembledText == legacy BuildAsync` 逐字节相等 |
| T2 | `MasterSwitch_Off_Returns_Empty` | `EnableHaloPrefix=false → AssembledText==""`，Blocks 全 `Enabled=false` |
| T3 | `Disable_CmdPython_Removes_Critical_Rule` | 输出不含 `CRITICAL RULE/runner:/powershell`，其余三块仍在 |
| T4 | `Disable_Identity_Removes_Halo_Line` | 不含 `You are Halo-Agent`，含 `## 1.` |
| T5 | `Disable_Ephemeral_Removes_Marker` | 不含 `## 4. Session Ephemeral`；`SplitSystem` 全归 stable |
| T6 | `Custom_Head_Prepends_And_Tail_Appends` | `head` 时 `StartsWith(custom)`；`tail` 时 `EndsWith(custom)` |
| T7 | `Unknown_Variable_Preserved_And_Reported` | `{{FooBar}}` 原样保留且进 `UnknownVariables` |
| T8 | `Save_Rejects_Oversize_Block` | 8001 字符单块 `Save` 抛 `PromptConfigValidationException(BlockId=custom)` |
| T9 | `Corrupt_Json_Falls_Back_Default` | 写坏 `prompt.json` 后 `GetPromptConfigInternal` 返回默认 + `FallbackReason` 非空 + 不删原文件（`.corrupt-` 留存） |
| T10 | `Adapters_Omit_System_When_Empty` | `SystemInstructions=""` 时 chat 无 `"role":"system"`，resp 无 `"instructions"` |
| T11 | `Preview_Equals_Orchestrator_Payload` | 同配置下 `PreviewPrompt(ws).AssembledText == Orchestrator 实发 SystemInstructions` |
| T12 | `Reset_Single_Block_Keeps_Others` | 改两块后 `Reset(identity)` 仅身份恢复，另一块保留 |

跑法：`dotnet build HaloAgent.sln` → `dotnet test tests/HaloAgent.Tests --filter Stage06Prompt` 全绿，且 `Stage05/Stage06` 老用例零修改通过。

## 2. 开关矩阵联调（真机，任选一工作区）

| 步骤 | 操作 | 预期 |
|---|---|---|
| M1 默认 | 打开 Tab，C 区刷新预览 | 预览 == 老模板观感；`prefixHash` 非空；无横幅 |
| M2 关 cmd-py | B 区关 `cmdPython` → 保存 → 刷新预览 → 发一轮对话 | 预览/实发均无 CRITICAL 段；`llm-trace [REQ]` 的 `prefixHash` 变化一次后稳定 |
| M3 总开关关 | 关总开关 → 保存 → 发一轮 | 抓包/`SerializeRequest` 无 system 字段；C 区空态文案；再打开恢复 |
| M4 自定义 | 开 custom(tail)+写 `> 团队规范：先读后改`+`{{WorkspaceName}}` → 保存 → 预览 | 尾部出现自定义段且变量已替换；token 行增长 |
| M5 回退演练 | 关应用 → 手工破坏 `config/prompt.json`（写 `{bad`）→ 启动 → 开 Tab + 发一轮 | Tab 顶部黄横幅 + 对话仍成功（默认模板） |
| M6 连续稳定 | 默认配置下连续 3 轮对话 | 3 轮 `[REQ] prefixHash` 相同（日期不变时），`hitRate` 持平或上升 |

## 3. 前端联调

1. `vue-tsc -b && vite build` 绿；mock 下走通 A→D 四区（保存/预览/复制/重置）。
2. 1020px 与 94vw 收缩双断点：Tab 内无横向滚动条；`pre` 换行正常。
3. `defaultTab="prompt"` 外部直达；脏检查弹窗出现一次。
4. 超限：custom 粘贴 8100 字符 → 计数变红 + 保存禁用；后端 `PROMPT_VALIDATION` 冒烟一次。

## 4. 合闸命令（按序，一条红即停）

```bash
dotnet build HaloAgent.sln
dotnet test tests/HaloAgent.Tests --filter Stage06Prompt
dotnet test tests/HaloAgent.Tests
cd src/Web.Client && npx vue-tsc -b && npx vite build
```

## 5. 合闸验收总表（签字用）

- [ ] T1-T12 全绿，老用例零修改通过。
- [ ] M1-M6 真机通过，`prefixHash` 变化仅发生在配置变更轮。
- [ ] AC1-AC5（01 §6）逐条演示通过。
- [ ] `prompt.json/.bak` 落盘位置正确（`{DataRoot}/config/`），损坏演练有日志。
- [ ] 文档与实现一致：块 ID/顺序/变量名/DTO 字段名与 02/03/04 无一字偏差。

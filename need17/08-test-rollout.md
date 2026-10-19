# need17-08 · 验收矩阵、测试清单、灰度与回滚

> 前置：01–07 全部合入。本篇无新功能，只定**做完的证明**与**出事的退路**。

---

## 8.1 测试文件清单（`tests/HaloAgent.Tests/`，每文档一文件）

| 文件 | 覆盖文档 | 最少用例 | 关键断言 |
|---|---|---|---|
| `Stage17DeclarationTests.cs` | 01 | 8 | strict 双协议、Schema 闭合、preset 隐藏、edit 隐藏可执行、别名 warning、非法 preset 阻断 |
| `Stage17ValidationTests.cs` | 02 | 12 | 宽容转换、4 码拒绝、未知字段点名、零执行调用、9 码冻结 |
| `Stage17CoreToolsTests.cs` | 03 | 10 | Schema 快照×5 工具、别名、MISSING_LIMIT、undo 闭合 |
| `Stage17ExecSearchTests.cs` | 04 | 8 | 上限同源（500/2000/100/300/10/30000）、runner 枚举、截断教学行存在 |
| `Stage17OverlayTests.cs` | 05 | 6 | Coding 隐藏、条件必填、brief 装配输入 |
| `Stage17PromptLoopTests.cs` | 06 | 8 | Guide 硬规则、动态工具段、截断丢弃零 Dispatch、hook 透传 |
| `Stage17PythonFallbackTests.cs` | 07 | 8 | 指定文案逐字、秒拒无进程、双保险、Schema/提示注记、CI 隔离 |

* 合入命令（每文件合入前）：`dotnet test tests/HaloAgent.Tests -c Debug` 全绿；新增文件失败即阻断合入。
* 老回归必绿：`Stage05ToolExecutionTests`、`Stage06*`、`Stage09*`、`Stage13*`、`Stage14*`、`Stage15*`、`Stage16*`（期望串一次性更新的在用例注释注明 need17）。

## 8.2 人工验收矩阵（抽样真实任务）

| 场景 | 操作 | 通过标准 |
|---|---|---|
| 缺参自愈 | 发“把 a.ts 第 3 行改成 x”观察首轮 edit_lines 缺 limit | 首轮 `MISSING_REQUIRED_PARAM`，次轮带 limit 一次成功 |
| 类型笔误 | 手工调 `read_file({file_path, offset:"10"})` | 宽容成功，无 warning 即无感，有 warning 即提示行 |
| 截断重发 | 超长文件 `read_file` 不带 limit | 回 truncation+落盘路径，次轮带 offset 翻页 |
| 无 Python | 快照 false 机器上让模型跑 python 脚本 | 首轮即中文秒拒，不再重试 python，自动转 cmd 或引导安装 |
| 预设干净 | Coding 下让模型列技能/问用户 | 模型不用被藏工具（声明里没有），任务仍完成 |
| 半截回复 | mock `finish_reason=length` | 零执行 + “调用未执行” tool 消息 |

* 目标：一次成功率 ≥90%（50 任务抽样），平均重试 ≤1 次/任务。

## 8.3 灰度（strict 是唯一外部风险）

1. 默认 `StrictToolsMode: Prefer` 合入，观察 7 天：网关 400 率、`strict` 降级重发次数（Warn 日志计数）、工具一次成功率。
2. 400 激增（降级路径也失败）→ 当天切 `Off`（配置热更新，无需发版），按 8.4 回滚。
3. 稳定后对主流供应商切 `Require`（开发联调确认各家 strict 兼容后再动生产）。

## 8.4 回滚开关（一键回到 need17 前行为）

* `StrictToolsMode: Off`：不发送 `strict`，Schema 去 `additionalProperties:false`（Validator 降级为 warning 不拒绝；`UNKNOWN_ARGUMENT` 码保留但只记 warning）。
* `toolsPreset: Full` + 01 §1.4 回退：`edit` 重新声明（Exposure 切回 Direct，需重启）。
* 以上两开关组合即等价 need17 前声明行为；02 校验/07 秒拒/06 截断保护保留（纯后端加固，无需回滚）。
* 回滚验证：切开关后 `Stage17DeclarationTests` 中 strict/闭合断言预期失败（已知），其余测试仍绿；发版说明标注。

## 8.5 施工顺序 checklist（合入即勾）

* [ ] 01：ToolDefinition 扩展 + builder + preset + edit 隐藏 + 别名决策
* [ ] 02：Validator + Shim + 码表冻结 + Dispatch 接入 + warnings 通道
* [ ] 07：快照 + 秒拒 + 指定文案 + Schema/提示/设置注记（可与 01/02 并行）
* [ ] 03 / 04 / 05：三工具分册 Schema+描述+brief/rules
* [ ] 06：ToolGuide 重写 + 动态装配 + Hint 补码 + 截断保护 + hooks 插槽
* [ ] 08：本篇验收执行 + 灰度观察 + 回滚演练一次

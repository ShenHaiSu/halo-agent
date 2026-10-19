# need07-01 需求分析（复制 · 重放 · 分支）

> 前置：`00-README.md`。本篇冻结后方可开工 02/03。

## 1. 背景与目标

对话历史主界面（`MessageTimeline`）当前只读：用户无法对单条 user / 单轮 assistant 做“再来一次”或“换条路走”。用户原话拆解：

1. 右键展开功能菜单：复制、重放、分支；
2. 复制 = 正文原文进剪切板；
3. 重放 user = 删除后续所有信息后重新发送生成；重放 assistant = 删除本条及后续后重发上一条 user；
4. 分支 = 上方历史复制继承建新会话并切换；右键 user 则原文进待发送区，右键模型消息则发送区留白。

## 2. 用户故事

| ID | 故事 | 验收口径 |
|----|------|---------|
| US-1 | 作为用户，我右键任意 user 气泡能复制原文 | 点击复制后剪切板与 `msg.content` 逐字一致（含换行），提示“已复制” |
| US-2 | 作为用户，我右键任意 assistant 轮次能复制正文 | 复制对象为该轮 `assistant.content` 原文（markdown 源码，非渲染后文本）；思考与工具输出不进剪切板 |
| US-3 | 作为用户，我对中段 user 点重放，能抹掉后面重出结果 | 后续全删 → 同文复发 → 新 assistant 落盘；轮次不翻倍（无重复 user） |
| US-4 | 作为用户，我对某轮 assistant 点重放，能回到它的上一条 user 重出 | 本轮（含工具）及后续全删 → 上一条 user 同文复发；无上一 user 则拒绝并提示 |
| US-5 | 作为用户，我对任意位置点分支，能开一条继承上文的新会话 | 新会话出现在侧栏顶部并自动切卡；历史前缀完整；user 分支草稿箱有原文，assistant 分支草稿箱空 |

## 3. 精确语义（冻结点，02/03 照此实现）

### 3.1 作用对象

- **user 目标**：`timelineItems[kind='user']` 单气泡，锚点 `msg.id`。
- **assistant 目标**：`timelineItems[kind='turn']` 整轮容器（含正文 + 思考 + 工具舱 + telemetry footer），锚点 `group.assistantId`（孤立 tool 组无右键）。
- **system 时间线**：无右键。

### 3.2 轮次展开规则（前后端同一条）

`messages.jsonl` 按序存放 `user / assistant / tool*`。任何切面先做展开：

- 锚点为 **assistant A**：切面覆盖 `A + 其后连续 tool*`（直到下一条 `user/assistant/system` 为止）。即同轮工具结果与 assistant 共进退。
- 锚点为 **user U**：切面即 U 单条（user 后不可能紧跟 tool* 而不经 assistant，天然原子）。
- 分支/截断的 `endIndex` 一律按展开后计算，永不劈开轮次。

### 3.3 三动作语义矩阵

| 动作 | 锚点 | 保留前缀 | 删除 | 后续动作 | 草稿箱 |
|------|------|---------|------|---------|-------|
| 复制 | user U | —（不写盘） | — | `clipboard.write(U.content)` | 不变 |
| 复制 | assistant A | — | — | `clipboard.write(A.content)`，空正文则复制 `reasoning`？**否：空正文提示“本轮无正文可复制”** | 不变 |
| 重放 | user U（索引 i） | `[0, i)`（U 亦删，复发时会重建，避免双 U） | `[i, end)` | `sendPrompt(U.content)` 自动复发 | 清空（复发即发送） |
| 重放 | assistant A（其 user 前驱为 U，索引 j） | `[0, j)`（U 亦删，同上） | `[j, end)` | `sendPrompt(U.content)` 自动复发；无前驱 U 则拒绝 `NO_PRIOR_USER` | 清空 |
| 分支 | user U（索引 i） | 新会话写入 `[0, i)` | 原会话不动 | 建新会话 → 切卡 → 草稿箱填入 `U.content`（不自动发送） | `U.content` |
| 分支 | assistant A（展开尾为 e） | 新会话写入 `[0, e]`（含整轮） | 原会话不动 | 建新会话 → 切卡 | 空 |

> “模型生成的图片”即 assistant 消息（含可能的 markdown 图片），分支后发送区空白——与上表 assistant 行一致。

### 3.4 互斥与态

- `isGenerating || streamingActive || compacting` 为真时：**复制可用，重放/分支禁用**（菜单置灰 + tooltip“生成中不可操作”)；后端同步以 `SESSION_BUSY` 拒绝，防竞态（用户切卡后点旧卡菜单等）。
- 空会话 / 锚点不存在 / 会话不存在：全部拒绝且不动盘（错误码见 02 §5）。

## 4. 边角表（联调按此逐行过）

| # | 场景 | 预期 |
|---|------|------|
| E1 | 空会话右键（无消息） | 无菜单（空态无锚点） |
| E2 | 重放首条 user（后有 N 轮） | 截断至空 → 同文复发 → 新首轮落盘 |
| E3 | 重放末条 user（后无消息） | 等价于“重新生成一次”：删 U 后同文复发 |
| E4 | 重放首轮 assistant（其前仅 1 条 user） | 删光 → 重发该 user |
| E5 | assistant 前无 user（如孤立 assistant/历史脏数据） | 拒绝 `NO_PRIOR_USER`，提示“找不到上一条用户消息，无法重放”，不动盘 |
| E6 | 分支首条 user | 新会话为空历史 + 草稿箱有文；标题继承原标题 `·分支` |
| E7 | 分支末轮 assistant | 新会话 = 全量复制；草稿箱空 |
| E8 | 生成中点重放/分支 | 前端禁用不可点；绕过前端直调后端返回 `SESSION_BUSY` |
| E9 | 复制空正文 assistant（纯工具轮） | 提示“本轮无正文可复制”，不写剪切板 |
| E10 | CefSharp 无 AsyncClipboard 权限 | 自动降级 `textarea + execCommand('copy')`，仍成功 |

## 5. 不做（冻结，防止蔓延）

1. 多选/批量删除、分支跨工作区、分支嵌套树可视化；
2. system 消息与单条 tool 结果右键；
3. 撤销栈（仅审计备份，不做 UI 撤销；见 02 §4）；
4. 重放时改模型/改温度（复发沿用当前 `activeProvider/Model`，与正常发送一致）；
5. 图片/附件消息（当前无附件模型，“图片”即 markdown 文本分支已覆盖）。

## 6. 文案与埋点

- 菜单项：`复制 / 重放 / 从这里分支`（icon：Copy / RotateCcw / GitBranch）。
- Toast：`已复制`、`已截断并重新发送`、`已创建分支会话`、`生成中不可操作`、`找不到上一条用户消息`。
- 日志：重放/分支成功写 `HaloLogger.App.Info`（含 sessionId/anchorId/removed/kept/newSessionId），失败写 Warn；诊断通道沿用 `app` 日志。

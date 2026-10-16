# 01 · 需求分析（前置提示词可配化 + 预发送预览 Tab）

> 前置：`00-README.md`。本篇冻结后才允许开工 02/03/04。

## 1. 现状与证据（为什么必须做）

| # | 现状 | 证据 | 痛点 |
|---|---|---|---|
| S1 | 系统前置写死在代码里，用户不可见不可改 | `Services/SystemPromptBuilder.cs:46-88`：`BuildAsync(workspace)` 全字符串拼接，无任何配置输入 | 想关 Halo 身份/想弱化 cmd-py 铁律/想加团队规范，无入口，只能改源码重编 |
| S2 | 每轮强制前置，无开关 | `Services/AgentOrchestrator.cs:153-156,193-197,225`：`BuildAsync → System(systemPrompt) → SystemInstructions` 三连写死 | 调试模型裸能力、对比开关效果、适配特殊网关时无法做 A/B |
| S3 | 设置与诊断中心无提示词 Tab | `components/settings/SettingsCenterDrawer.vue`：仅 5 个 `n-tab-pane`（appearance/providers/network/storage/runtime），`SettingsCenterTab` 联合类型无 prompt | 用户无法回答"上一轮到底发了什么 system" |
| S4 | 块粒度不可控 | 现模板实际 4 段（身份+环境/CRITICAL RULE/工具指南/尾部 Ephemeral），但无块 ID、无开关位 | 用户要求"关 cmd-py 优先但保留工具指南"做不到 |
| S5 | 稳定前缀与可配化存在张力 | `need05/02`：`stable/ephemeral` 切分 + 高低水位 `0.92/0.70` 保证前缀字节稳定 | 若允许任意编辑，前缀必然断裂；必须把"可配"与"命中率影响"同时呈现，否则用户误伤缓存 |

## 2. 用户故事（验收语言）

- **US1 总开关**：作为用户，我能一键关闭"发送 Halo-Agent 前置"，关闭后本轮起所有请求不再携带任何系统前置（双协议均无 `system/instructions` 字段），以便测试模型裸能力。
- **US2 分块开关**：作为用户，我能分别开关：①Halo 身份 ②运行环境事实 ③cmd/py 优先铁律 ④工具指南 ⑤尾部易变（日期/Python），关闭后输出中无该块标题/正文/残留空行。
- **US3 自定义块**：作为用户，我能填写一段 Markdown 自定义前置（团队规范/角色约束），可开关、可使用 `{{WorkspaceName}} {{WorkspaceRoot}} {{OsVersion}} {{PythonStatus}} {{CurrentDate}}` 变量，并选择放在系统前置之前还是之后。
- **US4 预发送预览**：作为用户，我在设置中心新 Tab 选择一个工作区，即可看到"按当前配置+该工作区上下文实际会发出的完整 system 文本"，含分块折叠视图、每块 token 估算、总 token、`stable/ephemeral` 分界线、chat 与 resp 两种渲染对照。
- **US5 诊断对照**：作为用户，我能看到"上轮实际发送 vs 当前配置预览"的差异提示、以及开关变更对 `prompt cache` 前缀命中率的影响提示（仅提示，不阻断）。
- **US6 安全兜底**：作为用户，即使我把配置改坏/文件删掉/写超长，下轮对话仍能正常发出（回退默认），并在 Tab 顶部看到明确横幅，而不是会话报错。

## 3. 功能清单（本期必须）

### 3.1 配置能力

| ID | 能力 | 说明 |
|---|---|---|
| F1 | `EnableHaloPrefix` 总开关 | `false` → `SystemInstructions=""`，适配器不写 system 字段；默认 `true` |
| F2 | 五块独立开关 | `EnableIdentity / EnableRuntime / EnableCmdPython / EnableToolGuide / EnableEphemeral`，默认全 `true` |
| F3 | 自定义块 | `EnableCustom + CustomText(markdown, ≤8000字符) + CustomPlacement(head/tail)`，默认关闭且空文本 |
| F4 | 块文本可编辑 | 身份/环境/cmd-py/工具指南/尾部模板文本可改（多行 textarea），"恢复默认"可单块/全部恢复 |
| F5 | 变量占位 | 自定义块 + 环境块支持 `{{…}}` 五变量；未知变量原样保留并在预览下标黄提示 |
| F6 | 全局生效 | 本期仅全局 `config/prompt.json`，工作区选择器仅用于预览上下文，不做按工作区覆盖 |

### 3.2 新 Tab「提示词与预发送」

位置：`SettingsCenterDrawer` 第六个 `n-tab-pane name="prompt"`，标题固定为 `提示词与预发送`。

Tab 内四区（自上而下，不可调序）：

1. **A. 总览与总开关区**：总开关 + 生效范围说明（全局/下轮生效）+ 配置异常横幅槽位 + `恢复全部默认` + `保存`（脏检查）。
2. **B. 分块开关与编辑区**：6 行（5 预置 + 1 自定义），每行 = `switch + 块名 + token 估算 + 折叠编辑`；自定义行另有 `head/tail` radio + 变量插入按钮。
3. **C. 预发送预览区**：工作区选择器 + `刷新预览` + 完整文本 `<pre>`（只读、可复制）+ 分块着色折叠 + 双协议对照折叠（chat 首条 system / resp 顶层 instructions 说明）+ token 与 `prefixHash/stable长度` 一行。
4. **D. 诊断区**：上轮实际发送摘要（workspace/session/reqId/prefixHash/长度/时间，若无则空态）+ 当前配置与上轮差异提示 + 缓存影响提示文案（固定话术，见 04）。

### 3.3 预览 API（后端）

| 方法 | 用途 |
|---|---|
| `GetPromptConfig` | 读当前配置（含 `isDefault/fallbackReason` 元信息） |
| `SavePromptConfig(configJson)` | 校验→原子写盘→返回生效后配置 |
| `ResetPromptConfig(block?)` | 单块或全部恢复默认 |
| `PreviewPrompt(workspaceId)` | 按当前配置 + 指定工作区真实上下文渲染完整文本 + 分块明细 + token + stable/ephemeral 切分点 + 双协议字段名对照 |

预览与实际发送必须同一 `BuildCore` 路径（03 强制），否则预览即谎言。

## 4. 非功能约束

| 类 | 约束 |
|---|---|
| 兼容 | 默认配置渲染与老模板逐字节一致；老 `BuildAsync(workspace)` 签名保留做兼容壳 |
| 关断语义 | 块关 = 该块标题+正文+多余空行全部消失，不留 `## 2.` 空壳；全关 = 空字符串 |
| 生效时机 | 保存后**下一轮 `RunAgentTurnAsync` 起**生效；流式中修改不中断本轮；预览即时渲染不落盘 |
| 长度上限 | 单块 ≤8000 字符，自定义 ≤8000，总拼装 ≤32000；超限后端截断失败→拒绝保存并回显哪块超限 |
| 稳定性 | 易变行（日期/Python）永远只出现在尾部 `ephemeral`；开关/文本变更允许前缀断裂，但必须在诊断区明示 |
| 安全 | 仅本地 `config/prompt.json`，不上传；`Save` 原子写盘 + `.bak`；损坏回退默认 + 日志 + 横幅 |
| 性能 | 预览 `<300ms`（Python 探测复用缓存，见 03）；`GetPythonStatus` 不得在 UI 线程同步阻塞 |
| 适配 | 仅 `Naive-UI 2.45.3 + CefSharp Chrome 109` 已验证组件（switch/input/textarea/select/collapse/pre/button/alert/copy）；1020px 与 94vw 无横向滚动 |

## 5. 明确不做（防蔓延，评审时逐条确认）

1. 不做按工作区/会话/模型/协议的多套覆盖；配置仅全局，`workspaceId` 只做预览上下文。（模型留 `Scope` 预留字段，不实现。）
2. 不做提示词版本历史/diff 时间线；仅 `prompt.json.bak` 一份覆盖式备份。
3. 不改压缩（`CompactionService.CompressorSystemPrompt`）与标题（`HostBridgeFacade.SessionTitle`）提示词语义；本期仅在 Tab 诊断区**只读预览**二者（标题/压缩各一段折叠），可开关都不给。
4. 不做"本轮热切换"；不做"按协议分别配两套 system"；不做导入/导出文件（复制粘贴即够用）。
5. 不做服务端下发/云端同步。

## 6. 验收口径（总闸，明细见 05）

- [ ] AC1：总开关关 → 双协议报文中无 `system/instructions` 字段（抓包/`SerializeRequest` 单测断言）。
- [ ] AC2：单关 `cmd-py` → 输出无 `CRITICAL RULE`/`runner:`/`powershell` 段落，其他块原样；单关身份 → 无 `You are Halo-Agent`。
- [ ] AC3：预览文本与随后一轮实际发送的 `SystemInstructions` 逐字节一致（同一 workspace+配置不变条件下）。
- [ ] AC4：`prompt.json` 删除/写坏/超限 → 下轮正常发送默认模板 + Tab 横幅 + 日志。
- [ ] AC5：新 Tab 四区齐备，双协议对照、token、prefixHash、上轮对照、缓存提示均可见；`vue-tsc/vite` 绿。

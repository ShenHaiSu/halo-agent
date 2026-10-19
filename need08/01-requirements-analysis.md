# need08-01 需求分析（命名 + 0=无限语义 + 预设 + 边角）

> 前置：`00-README.md`。本篇冻结后才允许开工 02/03/04。

## 1. 现状与证据（为什么必须做）

| # | 现状 | 证据 | 痛点 |
|---|------|------|------|
| S1 | 命令超时写死 `30s默认/300s封顶`，用户不可改 | `Tools/BuiltIn/ExecCommandTool.cs:14-15` 常量 + `:104 Math.Min钳制` + `:35` schema 文案 | 长构建/长脚本必被杀，只能拆命令绕行 |
| S2 | 轮次写死 `25`，构造注入但前端无入口 | `Services/AgentOrchestrator.cs:71,80` + `Bridge/HostBridgeFacade.cs:50` 默认构造 | 简单任务嫌多、复杂任务嫌少，都调不了 |
| S3 | 设置中心 6 Tab 均职责已满，无处放执行参数 | `SettingsCenterDrawer.vue:21-55` + `need06/01 §S3` | 硬塞任一 Tab 都会污染语义（见 §2） |
| S4 | `0/负数` 语义未定义 | `ExecCommandTool.cs:99-104`（`<=0→默认`）、`AgentOrchestrator.cs:80`（`>0?…:25`） | 用户自然会试 `0=无限`，当前是静默改写，最危险 |

## 2. Tab 命名决策（满足“后续杂项都能塞”）

约束：不能叫 `执行与工具`（太窄，后续杂项如自动继续、工具白名单、压缩阈值、标题温度等放不下）；不能叫 `系统/全局/高级设置`（与现有 `系统运行环境/网络全局` 重名混淆）；必须中英文 id 稳定、后续只加卡片不改名。

| 候选 | 结论 | 理由 |
|------|------|------|
| 执行与工具 | ❌ 否决（用户已明确禁用） | 覆盖面窄；杂项塞入即名不副实 |
| 模型与执行 | ❌ | 与 `模型与服务商` 重叠，用户分不清去哪调模型 |
| 系统行为 / 全局行为 | ❌ | `系统运行环境` 已占“系统”心智，会误以为只读遥测 |
 | **智能体行为（`name="agent"`）** | ✅ **已决策冻结** | “智能体”是行为主体：轮次、超时、后续自动继续/工具策略/压缩策略/安全确认全是它的行为；与 6 个旧 Tab 正交；英文 id `agent` 短且稳定 |
 | 备选：会话与执行 | ❌ 已否决 | 决策已定，不再保留备选；id 统一用 `agent` |

冻结项：Tab 标题 `智能体行为`，`SettingsCenterTab` 追加 `'agent'`，顺序**固定最后（prompt 之后）**，组件 `AgentBehaviorView.vue`，配置 `config/agent.json`。后续杂项一律以**新卡片**加入此 Tab，不新增 Tab、不改名。

## 3. 功能清单（本期必须）

| ID | 能力 | 说明 |
|----|------|------|
| F1 | 新 Tab 第 7 位 | `SettingsCenterDrawer` 新增 `n-tab-pane name="agent" tab="智能体行为"`；`defaultTab` 透传不动 |
| F2 | 命令超时可配 | `execDefaultTimeoutMs`（默认 30000）+ `execMaxTimeoutMs`（默认 300000，上限钳制）；`0=无限`（见 §4） |
| F3 | 工具轮次可配 | `maxIterations`（默认 25）；`0=无限`（见 §4） |
| F4 | 预设按钮 | 每个输入旁一排 `secondary tiny` 预设（见 §5），点击即填，不自动保存 |
 | F5 | 保存/按项恢复/全部恢复/脏检查 | 对标 `PromptSettingsView`：`保存`（脏才可点）+ 每项行内 `恢复本项默认`（`popconfirm` 二次确认）+ A 区 `恢复全部默认`（`dialog.error` 强确认）+ `尚未保存`提示；保存后**下一轮对话起生效** |
| F6 | 损坏回退+横幅 | 文件缺失/空/损坏/超限 → 回退默认 + 顶部 `n-alert warning` 显示 `fallbackReason`，会话不报错 |
| F7 | 全局生效 | 仅全局 `config/agent.json`；不做按工作区/会话/模型覆盖（`scope` 预留字段写死 `global`） |

## 4. `0=无限` 精确语义（后端必须逐字实现，测试逐行覆盖）

### 4.1 命令超时（毫秒）

设 `D=execDefaultTimeoutMs（配置默认值）`，`M=execMaxTimeoutMs（配置上限）`，`T=单次 tool call 的 timeout_ms（可缺省）`。

| 输入 | 有效值 `E` | 行为 |
|------|------------|------|
| `T` 缺省/非数字 | `E=D`；若 `D=0` 则无限 | 沿用旧“缺省走默认” |
| `T<0` | 视为缺省（`E=D`） | 负数永不直通到底层，前端也禁负 |
| `T=0` | `E=0` → **本 call 无限等待**，跳过 `CancelAfter`，仅用户取消才杀进程 | 显式无限优先于上限 `M` |
| `T>0, M=0` | `E=T`（上限关闭，不钳制；但受硬顶 §6 约束） | `M=0` 即“不限上限” |
| `T>0, M>0` | `E=min(T,M)` | 保留旧钳制语义 |
| `D=0` 且 `T` 缺省 | `E=0` → 无限 | 默认即无限，生而危险，UI 必须红字警告 |
| `D>0, M>0, D>M` | **拒绝保存**（`AGENT_VALIDATION`，指明 `execDefaultTimeoutMs <= execMaxTimeoutMs`） | 不做静默截断，逼用户显式修 |

无限分支实现要点（详情见 02）：不创建 `timeoutCts.CancelAfter`，直接 `await process.WaitForExitAsync(userToken)`；超时提示分支不进；`ToolOutputGuard` 照常；取消仍走 `job.Dispose()+TryKill`。

schema 文案必须同步改为：`Optional timeout in milliseconds. 0 = infinite (no timeout, cancellable by user). Defaults to <D>, capped by <M> (0 = no cap).`

### 4.2 工具轮次（整数）

| 输入 | 行为 |
|------|------|
| `N>0` | 旧语义：`for(iteration=1..N)`，`iteration>=N` 仍有 ToolCall → 落助理通知 + `TurnTruncated` + `Truncated(N,…)` |
| `N=0` | **无限轮次**：循环无上界，永不进截断分支；仅 `无ToolCall→Completed` / `取消→Cancelled` / `异常→自愈或Failed` 退出 |
| `N<0` | **拒绝保存**（前端禁负，后端再拦一道） |

无限轮次的安全声明（必须写进 UI 警告与 02 日志）：唯一刹车是**用户取消 + 模型自己停手（不再发 ToolCall）+ Token 预算裁剪**；后端在无限模式下打 `Warn` 日志并在 `IterationStarted` 事件中原样透出 `maxIterations:0`（前端渲染为 `∞`）。
 ### 4.3 按项恢复 ID（已决策：`reset` 按块/按项恢复）
 | 项 | `field` 主 ID | 兼容别名 | 恢复为 |
 |----|---------------|---------|--------|
 | 默认超时 | `defaultTimeout` | `execDefaultTimeoutMs/default/default-timeout` | `30000` |
 | 上限 | `maxTimeout` | `execMaxTimeoutMs/max/max-timeout` | `300000` |
 | 轮次 | `iterations` | `maxIterations/rounds/max-iterations` | `25` |
 | 全部 | 空/`all`/`default` | —— | 三项全回默认 |
 未知 `field` → `AGENT_VALIDATION` 拒绝（`未知恢复项：{field}`），大小写不敏感、前后空格容忍。

## 5. 预设按钮表（冻结，03 按此施工）

单位策略：超时输入框单位**毫秒**（与 tool schema 一致），旁注 human 文案 `=30s`；轮次无单位。

| 字段 | 预设（从左到右） | 说明 |
|------|-----------------|------|
| 超时默认 `D` | `30s / 60s / 120s / 300s / ∞` | 对应 `30000/60000/120000/300000/0`；沿用 `ProviderManagerView CONTEXT_PRESETS` 的 `tiny secondary + 高亮当前值` 范式 |
| 超时上限 `M` | `60s / 300s / 1h / 不限` | 对应 `60000/300000/3600000/0` |
| 轮次 `N` | `10 / 25 / 50 / 100 / ∞` | 对应 `10/25/50/100/0`；默认高亮 `25` |

交互：点击仅改本地 draft（脏检查变脏），不自动保存；当前值命中预设则该按钮 `type=primary`，`∞` 命中时红色描边以示危险。

## 6. 非功能约束（冻结）

| 类 | 约束 |
|----|------|
 | 硬顶（已决策确认，02 强制） | 有限超时 `1..3600000`（1h）；有限轮次 `1..500`；`0` 不受硬顶约束但 UI 二次确认 |
| 兼容 | `ExecCommandTool.DefaultTimeoutMs/MaxTimeoutMs` 常量保留，改为“配置缺失时的回退”；`AgentOrchestrator(maxIterations=25)` 签名保留 |
| 生效时机 | 保存后下一轮 `RunAgentTurnAsync` 起生效；执行中/流式中修改不中断本轮 |
| 安全 | 仅本地 `config/agent.json`；`Save` 原子写 + `.bak`；损坏保留 `agent.json.corrupt-<ts>` 后回退默认 |
| 性能 | `Get` 走内存快照+文件回源（对标 Prompt）；`RunAgentTurn` 每轮只读一次配置，不每 iteration 读文件 |
| 适配 | 仅 `Naive-UI 2.45.3 + CefSharp Chrome 109` 已验证组件（`input-number/select/button/alert/switch`）；1020px 与 94vw 无横向滚动 |

## 7. 明确不做（防蔓延，评审逐条确认）

1. 不做按工作区/会话/模型/协议的多套覆盖；`workspaceId` 与本 Tab 无关，不设工作区选择器。
2. 不做导入/导出文件、云端同步、版本历史/diff 时间线；仅 `agent.json.bak` 一份覆盖式备份。
3. 不改网络超时（`DefaultConnectTimeoutSeconds/DefaultRequestTimeoutSeconds`）与模型预算（`maxContext/maxOutput/temperature`）；Tab 内不设跳转链接，避免用户混淆两种“超时”。
4. 不做本轮热切换；不做“按 runner 分别配超时”（cmd/python 共用一套）。
5. 后续杂项（自动继续、工具白名单、压缩阈值等）**只留扩展位**（`02 §2` 的 `extensions` 占位与前端“预留卡片槽”注释），本期不实现任何一个。

## 8. 边角表（02/03/04 必须覆盖）

| # | 边角 | 预期 |
|---|------|------|
| E1 | 负数输入 | 前端 `n-input-number min=0` 禁掉；手改 Bridge JSON 仍被后端 `AGENT_VALIDATION` 拒绝 |
| E2 | `D>M>0` | 拒绝保存，错误定位到默认超时行并滚动 |
| E3 | `agent.json` 损坏/空/缺失 | 回退默认 + 横幅 `fallbackReason` + 写 `corrupt` 审计文件（损坏时）；会话可正常发起 |
| E4 | 有限→`0` | 弹 `dialog.warning` 二次确认（文案见 03 §4），确认后才变脏；`0`→有限不弹 |
| E5 | 保存时正有轮次在跑 | 不中断本轮；`config:agent-changed` 事件仅通知，不重载进行中循环的快照 |
| E6 | 存储迁移 | `ChangeStoragePath` 后 `EnsureInitialized` 重建默认，旧盘文件随迁移策略走（03/02 与 storage 同范式） |
| E7 | 并发保存 | 后端 `lock(_syncLock)` 串行化，后写 wins；前端 `saving` 期间禁用保存按钮 |
 | E8 | 未知恢复项 | `reset(field="bogus")` → `AGENT_VALIDATION` 拒绝，不写盘不改其他项 |

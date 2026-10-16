# need08-03 Bridge 与前端 Tab UI（智能体行为）

> 前置：`01` 冻结 + `02` 后端接口可用（至少 Bridge 方法签名先合入，单测可并行）。本篇按文件顺序施工。

## 1. 信息架构（Drawer 内位置与分区）

`components/settings/SettingsCenterDrawer.vue`（现 6 Tab，`SettingsCenterTab` 在同文件 `export type`）：

1. 新增第 7 `n-tab-pane name="agent" tab="智能体行为"`，**顺序固定最后（prompt 之后）**，避免打乱老用户肌肉记忆（沿用 `need06/04 §1` 规则）。
2. `SettingsCenterTab` 联合类型追加 `'agent'`；`defaultTab` 透传逻辑不动。
3. Tab 内容根组件：新建 `components/settings/AgentBehaviorView.vue`（500 行内），自上而下三区（不可调序，为后续杂项留“第 4 区以后的卡片槽”注释）：
    - **A. 说明与保存区**：`n-alert(异常横幅，仅 fallbackReason 非空显示)` + 生效范围文案（全局/下轮生效）+ `保存/按项恢复/全部恢复`按钮组 + `尚未保存`提示。
   - **B. 命令执行超时区**：`默认超时`输入 + 预设 + `上限`输入 + 预设 + `0=无限`红色警告（任一 `0` 即显示）。
   - **C. 工具轮次区**：`最大轮次`输入 + 预设 + `0=无限`红色警告 + 刹车说明（取消/模型停手/预算裁剪）。
   - 预留：`<!-- FUTURE: 杂项卡片槽：自动继续/工具白名单/压缩阈值等在此追加 n-card，不新增 Tab -->`。

## 2. 数据层（精确到文件）

### 2.1 `bridge/hostBridge.ts`（对标 `getPromptConfig/savePromptConfig`）

```ts
export interface AgentConfigDocument { version:number; execDefaultTimeoutMs:number; execMaxTimeoutMs:number; maxIterations:number; scope:string; updatedAtUtc:string; isDefault?:boolean; fallbackReason?:string|null; }
// HostBridge 声明追加：
 getAgentConfig: () => Promise<BridgeResponse<AgentConfigDocument>>;
 saveAgentConfig: (config: AgentConfigDocument) => Promise<BridgeResponse<AgentConfigDocument>>;
 resetAgentConfig: (field?: string) => Promise<BridgeResponse<AgentConfigDocument>>; // field 见 01 §4.3；空=全部
```

 命名一律小驼峰（C# 侧 `HaloJsonOptions.IndentedCamelCase`，与 Prompt 一致）。mock 实现（与 `getPromptConfig` mock 同区）：默认返回 `{version:1, execDefaultTimeoutMs:30000, execMaxTimeoutMs:300000, maxIterations:25, scope:'global', updatedAtUtc:now}`，`save` 回显 incoming，`reset(field?)` 按 01 §4.3 只回该项/全回（明确注释 `// mock 仅示意，联调以真后端为准`）。`IterationStarted` mock 的 `maxIterations:25` 保持不变；无限渲染由前端 `0→∞` 覆盖，无需改 mock。

`agentStore.ts`（已有 turn 编排）**不动**；`IterationStarted` 消费处加一行 `maxIterations===0?'∞':maxIterations` 显示即可（若该处无显示则跳过，不为本 Tab 新造显示）。

### 2.2 `stores/agentConfigStore.ts`（新建，对标 `promptStore.ts/configStore.ts`）

```ts
 ```ts
 state: config: AgentConfigDocument|null; loading/saving:boolean; dirty:boolean; lastSavedHash:string; lastError:{field,message}|null;
 actions: loadConfig() / saveConfig() / resetField(field) / resetAll() / touchDirty() / markSaved()
 dirty 判定：JSON.stringify(config) !== lastSavedHash；切换 Tab/关闭 Drawer 有脏时沿用 need01 抽屉交互（若 Drawer 已有全局脏拦截则复用，无则本 Tab 内 `尚未保存` 文案即可，不另造弹窗）。
 ```
 - `loadConfig()`：`getAgentConfig()` → `config=…; lastSavedHash=hash; dirty=false; lastError=null`。
 - `saveConfig()`：`saveAgentConfig(config)` → 成功 `markSaved()+message.success('智能体行为配置已保存，下一轮对话起生效')`；失败 `AGENT_VALIDATION` → `lastError={field,message}` + `message.error` + 返回 field 供视图滚动定位。
 - `resetField(field)`：行内 `n-popconfirm` 确认（文案见 §4）→ `resetAgentConfig(field)` → 成功同 save 成功路径；`field` 用 01 §4.3 主 ID（`defaultTimeout/maxTimeout/iterations`）。
 - `resetAll()`：`dialog.error` 强确认（文案见 §4）→ `resetAgentConfig()`（空参）→ 同 save 成功路径。
 - 监听 `haloEventBus 'storage:changed'` → `loadConfig()`（对标 `configStore.initialize`）。

## 3. 视图细节（`AgentBehaviorView.vue`，评审必查）

### 3.1 A 区：说明与保存（对标 `PromptSettingsView L4-43`）
 - 顶部 `n-alert warning` 仅 `config?.fallbackReason` 非空显示。
 - 文案固定：`生效范围：全局 config/agent.json，保存后下一轮对话起生效；执行中/流式中的本轮不受影响。超时单位为毫秒。`
 - 按钮组：`保存（primary, :disabled="!dirty||saving", data-testid=agent-save）` + `恢复全部默认（error ghost, dialog.error 强确认, data-testid=agent-reset-all）` + 脏提示 `尚未保存，离开将丢失`。

### 3.2 B/C 区：输入 + 预设（对标 `ProviderManagerView L429-468` 预设范式）

每个字段行结构：`label + n-input-number + human 换算 + 预设按钮组 + 字段错误行`。

| 字段 | 控件 | 预设（`tiny secondary`，命中 `primary`） | human 换算 |
|------|------|------------------------------------------|-----------|
| 默认超时 `execDefaultTimeoutMs` | `n-input-number :min=0 :max=3600000 :step=5000 :show-button=false` + `data-testid=agent-timeout-default-input` | `30s/60s/120s/300s/∞`→`30000/60000/120000/300000/0`，`data-testid=preset-timeout-default-*` | `=30s` / `=无限`（`0` 时红色） |
| 上限 `execMaxTimeoutMs` | 同上，`data-testid=agent-timeout-max-input` | `60s/300s/1h/不限`→`60000/300000/3600000/0` | `=5m` / `=不限上限` |
| 最大轮次 `maxIterations` | `n-input-number :min=0 :max=500 :step=1` + `data-testid=agent-iterations-input` | `10/25/50/100/∞`→`10/25/50/100/0` | `轮` / `=无限` |
 每个字段行尾加行内 `恢复本项默认（tiny, n-popconfirm, data-testid=agent-reset-<defaultTimeout|maxTimeout|iterations>）`，对标 `PromptSettingsView L117-122` 单块恢复。

- 预设点击仅改本地 `config` + `touchDirty()`，不自动保存。
- `∞/不限` 预设命中时按钮加 `danger` 描边（`type=primary` 保持，用 class 追加红色边框，避免引入新色板）。
- `0` 警告（B/C 各一，任一条件即显示，固定文案）：
  - B：`n-alert type=error`：`你设置了无限等待：命令永不超时，仅手动取消可终止。请确认长任务真的需要，并保持窗口开启。`
  - C：`n-alert type=error`：`你设置了无限轮次：永不截断，仅模型停手/手动取消可结束，Token 消耗无上限。请谨慎开启。`
- 字段错误行：`lastError.field` 命中该字段时红色文案 + 自动滚动定位（`scrollIntoView`，对标 04 I3）。

### 3.3 交互细节表

| # | 细节 | 做法 |
|---|------|------|
| I1 | 有限→`0` 确认 | 任一字段从 `>0` 变为 `0`（输入或预设）时弹 `dialog.warning`（标题 `确认为无限？`，内容按 B/C 警告文案，`正文=确认为无限/取消`）；点取消则回滚该字段为改前值；`0→有限`不弹。实现：视图层暂存改前值，确认后才 `touchDirty` |
| I2 | 单位防呆 | 超时输入框后缀 `ms`；human 换算函数 `fmtMs(v)= v===0?'∞':v<1000?v+'ms':(v/1000)+'s'`；轮次 `fmtIter(v)= v===0?'∞':v+'轮'` |
| I3 | 超限 | 前端软限（`>3600000/>500` 红字并禁保存，`min=0` 天然禁负）；后端硬限 `AGENT_VALIDATION` 回显 field，定位滚动到对应行 |
| I4 | `D>M` | 前端即时提示（`D>M>0` 时 B 区 warning 行 `默认超时不得大于上限，请先调大上限或调小默认`）并禁保存；后端再拦一道 |
 | I6 | 可测性 | `data-testid` 清单：`agent-behavior-tab/agent-save/agent-reset-all/agent-reset-defaultTimeout|agent-reset-maxTimeout|agent-reset-iterations/agent-timeout-default-input/agent-timeout-max-input/agent-iterations-input/preset-*` |
 | I7 | 空态 | 无（配置必有默认值；加载中显示 `n-spin`，失败显示重试按钮，不留白屏） |
 | I8 | 按项恢复 | 行内恢复只回该项（`resetField` 主 ID），不碰其他两项的本地未保存编辑；`resetField` 成功后 `markSaved` 全量（dirty=false），before/after 以 `message.success('已恢复本项默认')` 提示 |
 
 ## 4. 固定文案（不可自由发挥，评审逐字对）
 - Tab 标题：`智能体行为`。
 - A 区说明：`生效范围：全局 config/agent.json，保存后下一轮对话起生效；执行中/流式中的本轮不受影响。超时单位为毫秒，0 表示无限。`
 - 无限确认弹窗（B）：`确认为无限等待？命令将永不超时，仅手动取消可终止，误配可能导致进程挂起。请确认长任务真的需要。`
 - 无限确认弹窗（C）：`确认为无限轮次？循环将永不截断，仅模型停手或手动取消可结束，Token 消耗无上限。请谨慎开启。`
 - 按项恢复确认（行内 popconfirm）：`确认恢复本项为默认？` + 项名（`默认超时 30s / 上限 300s / 轮次 25` 三选一）。
 - 恢复全部默认确认：`确认将智能体行为 3 项全部恢复为默认（默认超时 30s / 上限 300s / 轮次 25）？当前编辑将丢失。`
 - 保存成功：`智能体行为配置已保存，下一轮对话起生效`。
 - 恢复成功：按项 `已恢复本项默认` / 全部 `已恢复全部默认`。
 
 ## 5. 本篇验收（给 04 用）
 - [ ] 第 7 Tab 位置/标题/无横溢；`SettingsCenterTab` 含 `'agent'`。
 - [ ] 三输入 + 三组预设点击即填、高亮正确、`∞` 红色描边。
 - [ ] 有限→`0` 弹确认，取消回滚；`0` 警告红字可见；`D>M` 禁保存。
 - [ ] 保存/按项恢复/全部恢复/`fallbackReason` 横幅/脏提示全可用；Mock 与真后端一致。

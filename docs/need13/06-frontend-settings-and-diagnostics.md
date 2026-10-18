# need13-06 前端：设置中心与诊断（智能体行为 D/E 卡 + 主会话子进度）

> 前置：`01/05` 已落地（Bridge 方法 + mock 可用）。本篇按文件顺序施工。

## 1. 信息架构（Drawer 内位置，分区不可调序）

`SettingsCenterDrawer.vue` 现 9 Tab（`appearance/providers/network/storage/runtime/prompt/agent/skill-memory/usage`），**不新增 Tab**。`AgentBehaviorView.vue` 自上而下五区：

- A. 说明与保存（存量）
- B. 命令执行超时（存量）
- C. 工具轮次（存量）
- **D. 子代理（新增卡）**
- **E. 子代理诊断（新增只读卡）**
- 预留：`<!-- FUTURE: 杂项卡片槽（自动继续/工具白名单/压缩阈值等在此追加 n-card，不新增 Tab） -->`

## 2. 数据层

### 2.1 `stores/agentConfigStore.ts`（对标存量，不改流程）

- `config` 类型换新 `AgentConfigDocument`（05 §5，7 字段）。
- `guessField()` 加映射：`submaxparallel→subMaxParallel`、`submaxiterations→subMaxIterations`、`subagentenabled→subAgentEnabled`、`suballow→subPerms`，供保存失败滚动定位。
- `load/save/reset` 逻辑不动；`storage:changed` 监听重载不动。

### 2.2 主会话子进度（改 `stores/agentStore.ts`）

- `AgentTurnState` 加 `subAgents:{total, done, waiting:boolean, items:[{subId,label,status}]}`（瞬态，不持久化）。
- `TurnEvent` 消费加 4 kind：`SubAgentsStarted→waiting=true`；`SubAgentProgress/agent:sub-event→更新 items`；`SubAgentsBarrier→waiting=true`；`SubAgentsBarrierReleased/TurnCompleted/TurnCancelled→waiting=false`。
- 等待中主输入框 `disabled + placeholder="子代理执行中（N/M），取消可掐断整树"`；取消按钮文案不变（底层已走 `CancelTree`）。

### 2.3 诊断 store（复用 `agentConfigStore` + 现场拉取，不新建 store）

- E 卡 `onMounted/refresh` 调 `getSubRuntime(ws, activeMainId)`；`activeMainId` 取 `workspaceStore.activeSessionId`。
- 无运行子显示空态（不留白屏）。

## 3. D 卡：子代理（控件清单）

| 控件 | 绑定 | 说明 |
|---|---|---|
| `n-switch` 总开关 | `subAgentEnabled` | 文案“允许主代理 spawn 子代理（上下文干净、寄宿主会话、barrier 等待）”；关时整卡下半置灰但保留诊断 |
| `n-input-number :min=1 :max=8` | `subMaxParallel` | 预设 `1/3/5/8`（`tiny secondary`，命中 `primary`）；human `=3并发` |
| `n-input-number :min=1 :max=50` | `subMaxIterations` | 预设 `5/15/30/50`；human `=15轮` |
| `n-switch` ×2（开） | `subAllowExecCommand/subAllowFileWrite` | 关则子只读；行尾小字说明影响面 |
| `n-switch` ×2（关） | `subAllowSkillWrite/subAllowMemoryWrite` | 默认关；从关→开弹 `dialog.warning`“子可改全局知识，确认？”；开态红色描边 |
| `n-switch`（置灰） | `subAllowAskUser` | `:disabled=true` + `tooltip="v1 锁定：子禁问，防 barrier 假死"` |
| 行内恢复 | `resetField('subEnabled'/'subParallel'/'subIterations'/'subPerms')` | `n-popconfirm` 二次确认，文案见 §6 |
| 警告 | `n-alert type=info` | `关闭总开关后 spawn 仍可见但调用被拒，主代理会转述原因`（info 非 error，不吓人） |

保存/脏检查/`fallbackReason` 横幅复用 A 区逻辑（`dirty` 即 JSON 比对，含新字段）。

## 4. E 卡：子代理诊断（只读）

| 块 | 来源 | 控件 |
|---|---|---|
| 运行表 | `getSubRuntime.subs` | `n-data-table` 列：label/subId(前8)/status/iteration/tool/耗时；空态“当前无子代理运行” |
| 策略快照 | `policy{enabled,maxParallel,...}` | `n-tag` 行展示本轮快照（非实时编辑值） |
| 最近拒绝 | `lastRejection{code,atUtc,callId}` | `n-alert type=warning`（无则隐藏） |
| 操作 | — | `刷新`（`getSubRuntime`）+ `掐断整树`（`cancelAgentTree`，`dialog.error` 强确认） |

子会话展开查看：主时间线 `spawn_subagent` 卡片“查看子会话”按钮 → 调 `getSubSessions` 列子 → 点子项拉该 `subId` 的 `messages`（复用现有历史接口，`sessionId=subId`）只读渲染。v1 不做子内继续发言（只读）。

用量 rollup（可选增强）：用量 Tab 在主会话行下加小字“（含子耗 +P/+C）”，数据取 `TurnCompleted.payload.subAgents` 或 `GetRollupAsync`（04 §6）；v1 无后端聚合时前端就地求和 `subs usage` 亦可。

## 5. 可测性（`data-testid` 清单，评审必查）

```
agent-sub-enabled / agent-sub-parallel-input / agent-sub-iterations-input
agent-sub-perm-exec / agent-sub-perm-file / agent-sub-perm-skill / agent-sub-perm-memory / agent-sub-perm-ask
agent-sub-reset-enabled|parallel|iterations|perms
preset-sub-parallel-* / preset-sub-iterations-*
agent-sub-runtime-table / agent-sub-refresh / agent-sub-cancel-tree
```

## 6. 固定文案（不可自由发挥，评审逐字对）

- D 卡标题：`子代理`；副文案：`主经 spawn_subagent 建上下文干净的子会话，寄宿主下，barrier 等全部完成；保存后下一轮起生效。`
- 关闭提示：`关闭总开关后 spawn 仍可见但调用被拒，主代理会转述原因。`
- skill 开警告：`子可改全局知识，确认允许子写 skill？`
- 掐断确认：`确认掐断整树？主与全部子即刻取消，残缺结果不回填。`
- 按项恢复：`确认恢复本项为默认？` + 项名（`总开关开/并发3/子轮次15/权限默认` 四选一）。
- 保存成功复用：`智能体行为配置已保存，下一轮对话起生效`。

## 7. 本篇验收（给 07 用）

- [ ] D/E 卡位置/标题/顺序正确；1020px 与 94vw 无横向滚动。
- [ ] 预设点击即填高亮对；有限→开 skill 写弹确认，取消回滚。
- [ ] `ask` 开关置灰不可点；关闭总开关 info 警告可见，保存禁用逻辑正确（关闭本身可保存）。
- [ ] E 卡空态/运行态/拒绝横幅三态正确；掐断整树二次确认有效。
- [ ] 等待中主输入框禁用 + 取消可点；`BarrierReleased` 后恢复。
- [ ] Mock 与真后端同文案同 `data-testid`。

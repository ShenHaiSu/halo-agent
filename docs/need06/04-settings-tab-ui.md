# 04 · 设置中心新 Tab「提示词与预发送」前端实现

> 前置：`02+03` 已冻结（含 Bridge 五方法签名与 `PromptPreviewDto` 字段名）。本篇按文件顺序施工。

## 1. 信息架构（Drawer 内位置与四区）

`components/settings/SettingsCenterDrawer.vue`（现 5 Tab，`SettingsCenterTab` 在同文件 `export type`）：

1. 新增第六 `n-tab-pane name="prompt" tab="提示词与预发送"`，顺序固定最后（runtime 之后），避免打乱老用户肌肉记忆。
2. `SettingsCenterTab` 联合类型追加 `'prompt'`；`defaultTab` 透传逻辑不动。
3. Tab 内容根组件：`components/settings/PromptSettingsView.vue`（新建，800 行内），四区自上而下（与 01 §3.2 同序）：
   - A 总览：`n-alert(异常横幅, 仅 FallbackReason 非空显示)` + `n-switch(总开关)` + 说明文案 + `保存/恢复全部默认` 按钮组。
   - B 分块：`n-collapse` 6 项（identity/runtime/cmdPython/toolGuide/ephemeral/custom），每项 header = `switch + 块名 + token徽标 + stable/ephemeral角标`，内容 = `n-input(type=textarea, autosize)` + 单块恢复 +（custom 独有 `n-radio-group head/tail` + 变量插入按钮组）。
   - C 预览：`n-select(工作区)` + `刷新预览` + `复制` + 只读 `n-input(pre)` + 分块折叠（着色：stable 蓝头 / ephemeral 黄头 / custom 紫头）+ 双协议对照折叠 + `共 X 字符 · 约 Y token · stable 约 Z token · prefixHash abc123` 一行。
   - D 诊断：`n-descriptions(上轮实际)` + `n-alert(差异/缓存提示)` + 附属提示词（压缩/标题）两个禁用折叠。

## 2. 数据层（精确到文件）

### 2.1 `bridge/hostBridge.ts`（对标 `getContextBudget:406/getProvidersConfig:366`）

```ts
export interface PromptConfigDocument { version:number; enableHaloPrefix:boolean; enableIdentity:boolean; enableRuntime:boolean; enableCmdPython:boolean; enableToolGuide:boolean; enableEphemeral:boolean; enableCustom:boolean; customPlacement:'head'|'tail'; customText:string; identityText:string; runtimeTemplate:string; cmdPythonText:string; toolGuideTemplate:string; ephemeralTemplate:string; scope:string; updatedAtUtc:string; isDefault?:boolean; fallbackReason?:string|null; }
export interface PromptBlockPreview { blockId:string; enabled:boolean; renderedText:string; estimatedTokens:number; isStable:boolean; }
export interface PromptPreviewDto { assembledText:string; blocks:PromptBlockPreview[]; totalEstimatedTokens:number; stableEstimatedTokens:number; prefixHash:string; workspaceId:string; unknownVariables:string[]; isDefault:boolean; fallbackReason?:string|null; }
// HostBridge 声明追加：
getPromptConfig: () => Promise<BridgeResponse<PromptConfigDocument>>;
savePromptConfig: (config: PromptConfigDocument) => Promise<BridgeResponse<PromptConfigDocument>>;
resetPromptConfig: (blockId?: string) => Promise<BridgeResponse<PromptConfigDocument>>;
previewPrompt: (workspaceId: string) => Promise<BridgeResponse<PromptPreviewDto>>;
getPromptDiagnostics: (workspaceId: string, sessionId?: string) => Promise<BridgeResponse<PromptDiagnosticsDto>>;
```

命名一律小驼峰 JSON（C# 侧配 `JsonSerializerOptions(PropertyNamingPolicy=CamelCase)`，与 `ProvidersConfigDocument` 一致）。mock 实现（`hostBridge.ts:1249 getContextBudget` 同区）：默认配置返回固定文本 + `prefixHash:"mock"`，`save` 回显 incoming，`preview` 按开关拼接示意文本（明确注释 `// mock 仅示意，联调以真后端为准`）。

### 2.2 `stores/promptStore.ts`（新建，对标 `configStore.ts/workspaceStore.ts`）

```ts
state: config: PromptConfigDocument|null; preview: PromptPreviewDto|null; diagnostics; loading/saving; dirty:boolean; lastSavedHash:string;
actions: loadConfig() / saveConfig() / resetBlock(id?) / resetAll() / refreshPreview(workspaceId) / loadDiagnostics(workspaceId, sessionId?)
dirty 判定：JSON.stringify(config) !== lastSavedHash；切换 Tab/关闭 Drawer 有脏时 `dialog.warning('尚未保存，离开将丢失')`（沿用 need01 抽屉交互）。
防抖：B 区 textarea 输入 500ms 后本地重算 token（`estimateTokens` 前端轻量版：ascii/3.6 + cjk*1.55，仅示意，标注 `≈`），真值以后端 preview 为准；`刷新预览` 手动触发后端渲染（避免 Python 探测频繁）。
```

## 3. 交互细节（评审必查）

| # | 细节 | 做法 |
|---|---|---|
| I1 | 开关即时性 | B 区开关只改本地 state + 本地 token 行，不自动保存；C 区 stale 横幅 `配置已更改，预览可能过期，请点刷新预览` + 预览置灰 |
| I2 | 变量插入 | custom 与 runtime/ephemeral 编辑区上方 6 个小按钮，点击在光标处插入 `{{X}}`；未知变量在预览中标黄 + `unknownVariables` 警告行 |
| I3 | 超限 | 前端软限（字符计数 `7800/8000` 黄、`>8000` 红并禁保存）；后端硬限（`PROMPT_VALIDATION` 回显 `blockId`，定位滚动到对应 collapse 项） |
| I4 | 恢复 | 单块恢复需二次确认（`popconfirm`）；全部恢复需 `dialog.error` 强确认 |
| I5 | 空态 | 总开关关时 C 区预览显示空态插画文案 `前置已关闭：本轮起不再发送任何系统提示词（双协议均无 system 字段）`，不显示空 pre |
| I6 | 复制 | 完整文本 + 单块文本均可 `copy`（沿用 `copySessionHash` 的 `navigator.clipboard + message.success` 范式） |
| I7 | 工作区联动 | C 区 `n-select` 选项复用 `workspaceStore.workspaces`；无工作区时用中性预览并注 `当前无工作区，为中性预览` |
| I8 | 滚动 | 四区纵向滚动，禁止横向滚动；`pre` 用 `white-space:pre-wrap; word-break:break-word`；1020px 与 94vw 双断点回归 |

## 4. 双协议对照（固定文案，不可自由发挥）

C 区折叠内两段说明（chat/resp 各一段，附字段名）：

- Chat：`请求体 messages[0] = { role:"system", content:"<上方便是此 content>" }；总开关关时 messages 无首条 system。`
- Resp：`请求体顶层 instructions = "<上方便是此 instructions>"；总开关关时无 instructions 字段。`

## 5. 诊断文案（固定话术，后端不配，前端写死）

- 差异一致：`当前配置预览与上轮实际发送一致（prefixHash 相同）。`
- 差异不一致：`当前配置与上轮实际发送不一致：预览 prefixHash=xxx，上轮=yyy。保存后下一轮起生效，流式中的本轮不受影响。`
- 缓存提示（常驻小字）：`提示：开关或文本变更会改变请求前缀字节，上游 prompt cache 需一轮预热；易变行（日期/Python）始终只在尾部，不影响 stable 段命中。`
- 无上轮：`暂无本会话发送记录：发起一轮对话后此处显示 reqId/prefixHash/长度/时间。`

## 6. `data-testid`（05 联调硬依赖）

`prompt-settings-tab / prompt-master-switch / prompt-block-{identity,runtime,cmdPython,toolGuide,ephemeral,custom} / prompt-custom-placement / prompt-preview-text / prompt-preview-refresh / prompt-preview-copy / prompt-diagnostics / prompt-save / prompt-reset-all`

## 7. 验收

- [ ] 第六 Tab 可点可切，`defaultTab="prompt"` 直达；四区顺序与文案与本篇一致。
- [ ] 6 开关 + 自定义 head/tail + 变量插入 + 超限禁存 + 脏检查齐备。
- [ ] 预览区含完整文本/分块/token/prefixHash/双协议对照/上轮对照/缓存提示。
- [ ] `vue-tsc -b && vite build` 绿；1020px/94vw 无横向滚动；mock 与真桥同签名。

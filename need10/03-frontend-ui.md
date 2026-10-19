# need10-03 前端 Tab UI（单 Tab 双类 + 原生 table + 卡片预览）

> 前置：`01` 冻结 + `02` Bridge 签名先合入（Mock 可并行）。本篇按文件顺序施工。

## 1. 信息架构与文件清单

1. `components/settings/SettingsCenterDrawer.vue`：追加第 8 `n-tab-pane name="skill-memory" tab="技能与记忆"`（agent 之后）；`SettingsCenterTab` 加 `'skill-memory'`；内容挂 `<SkillMemoryView />`。
2. 新建 `components/settings/SkillMemoryView.vue`（≤600 行）：A 说明筛选区 + B 原生表格列表 + C 预览弹窗（`n-modal>n-card` 只读）。
3. 新建 `stores/skillMemoryStore.ts`（对标 `promptStore.ts`）：无 `dirty`（无编辑，无脏检查），状态即筛选+列表+预览。
4. `bridge/hostBridge.ts`：追加 DTO + 5 方法声明 + Mock（与 `getPromptConfig` mock 同区，注释 `// mock 仅示意，联调以真后端为准`）。

## 2. 数据层

```ts
export interface SkillMemoryListItem { name:string; scope:'global'|'project'; title:string; summary:string; pin:string; enabled:boolean; priority:number; version:number; estTokens:number; chars:number; hash:string; hasAssets:boolean; updatedAtUtc:string; dirPath:string; }
export interface SkillMemoryDetail extends SkillMemoryListItem { bodyPreview:string; truncated:boolean; bodyTotalChars:number; }
// HostBridge 追加：
listSkillMemory: (kind:'skill'|'memory', scope:'global'|'project'|'all', workspaceId?:string) => Promise<BridgeResponse<SkillMemoryListItem[]>>;
getSkillMemoryDetail: (kind, scope, name, workspaceId?:string) => Promise<BridgeResponse<SkillMemoryDetail>>;
setSkillMemoryEnabled: (kind, scope, name, enabled:boolean, workspaceId?:string) => Promise<BridgeResponse<{ bakFileName?:string|null; notice:string }>>;
deleteSkillMemory: (kind, scope, name, workspaceId?:string) => Promise<BridgeResponse<{ bakFileName?:string|null; notice:string }>>;
openSkillMemoryFolder: (kind, scope, name, workspaceId?:string) => Promise<BridgeResponse<boolean>>;
```

`stores/skillMemoryStore.ts` 状态/动作：

```ts
state: kind:'skill'|'memory'('skill'); scopeFilter:'all'|'global'|'project'('all');
  keyword:''; workspaceId:''（默认当前工作区，复用 workspaceStore）;
  items:SkillMemoryListItem[]; loading:boolean; lastError:string|null;
  preview:SkillMemoryDetail|null; previewing:boolean;
actions: reload() / setKind() / setScope() / setKeyword() / openPreview(scope,name) / toggleEnabled(item) / removeItem(item) / openFolder(item)
```

- `reload()`：`listSkillMemory(kind,'all',workspaceId)` 一次拉全，`scopeFilter/keyword` 前端内存过滤（`name/title/summary` 大小写不敏感包含）。
- 监听 `haloEventBus 'config:skill-memory-changed' + 'storage:changed'` → `reload()`（对标 `promptStore/configStore`）。
- `toggleEnabled` 乐观更新：先翻 `enabled`，失败回滚 + `message.error`；成功 `message.success(01 §4 开关文案)`。
- `removeItem` 不乐观：`dialog.error(01 §4 删除文案)` → `deleteSkillMemory` → 成功 `message.success` + `reload()`。

## 3. 视图细节（评审必查）

### 3.1 A 区：说明与筛选（一行可换行，禁横溢）

- `n-alert info` 放 `01 §4` 说明文案；`n-segmented` 切 `技能|记忆`；`n-select` 选域（全部/全局/项目）；`n-select` 选工作区（`workspaceStore.workspaces`，无工作区显示 `全局模式`）；`n-input search` 关键字（300ms 防抖，不过滤即全量）；`n-button 刷新`。
- `scopeFilter=project/all` 且无工作区时行尾黄字：`当前无工作区，Project 为空，只显示 Global。`

### 3.2 B 区：原生表格（**禁用 `n-data-table`，必须原生 `<table>`**）

仿 `SystemRuntimePanel.vue` 表格策略：`table-layout:fixed; width:100%`，label 定宽，`white-space:normal; overflow-wrap:break-word`，最外层 `min-w-0 max-w-full overflow-x-hidden`：

| 列 | 宽 | 控件 |
|---|---|---|
| 状态 | 64px | `n-switch size=small :value=item.enabled`（`data-testid=sm-switch-{scope}-{name}`），`@update:value` 调 `toggleEnabled` |
| 名称/标题 | 自适应 | 主行 `n-button text` 显示 `name`（点击=预览，样式即卡片入口），副行 `title — summary` 小字 |
| 域 | 72px | `n-tag`（global 蓝/project 绿） |
| pin | 84px | 纯文本 `pin` |
| token | 76px | `~{estTokens}` mono |
| 更新 | 150px | `updatedAtUtc` 短格式 mono |
| 操作 | 168px | `预览`（text）+ `文件夹`（text）+ `删除`（text, danger） |

- 行点击≠整行可点：仅「名称按钮/预览按钮」进预览（防 Switch/删除误触）。
- 空态：`n-empty` + `01 §4` 空态文案；加载 `n-spin`；失败 `n-alert error + 重试`。
- 超 20 条不截断（与模型 `list` 截断不同，用户侧全量+纵向滚动）。

### 3.3 C 区：预览弹窗（i.e. 点击 card → 简短预览）

- `n-modal preset=card`（宽 640px/90vw），标题 `[{scope}] {name}`；内容 `n-card` 只读四段：`标题/摘要/pin·版本·token·资产` 元行 + `正文节选 pre（white-space:pre-wrap，最多 800 字）` + `truncated` 注脚（`01 §4`）+ `hasAssets` 提示（`含 assets/ 附件，正文引用 assets/...`）。
- **无任何 `n-input textarea` 可编辑控件**；正文用 `<pre>` 渲染。
- 弹窗底部按钮：`打开文件夹（primary ghost）` + `删除（error ghost，二次 dialog.error）` + `关闭`。行内「文件夹」按钮同 `openFolder` 直调（不经预览亦可）。

## 4. 交互细节表

| # | 细节 | 做法 |
|---|---|---|
| I1 | 无增改 | 全文件搜 `create/update/textarea+v-model=body` 应为 0；评审 grep 卡点 |
| I2 | 打开文件夹 | `openSkillMemoryFolder` → 后端 `OpenPathInExplorer(dirPath)`；失败 `message.error('目录已不存在，请刷新')` |
| I3 | 删除 | `dialog.error` 强确认（`01 §4` 文案）；成功提示含 `bakFileName`；失败不移除行 |
| I4 | Switch | 即时+乐观；`storage:changed` 后自动 `reload` 对齐真值 |
| I5 | 样式 | 表格样式复 `SystemRuntimePanel` 的 `.sys-runtime-telemetry-table` 命名另起 `.sm-native-table`，放 `<style scoped>`，不污染全局 |
| I6 | 可测性 | `data-testid` 见 `04 §3`；1020px/94vw 无横向滚动（沿用 need06 I8） |

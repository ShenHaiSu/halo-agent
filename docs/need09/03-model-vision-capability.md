# need09-03 模型多模态能力位（SupportsVision）— 前端可施工细化版

> 前置：`01/02` **已冻结（2026-10-16 用户确认）**。本篇为前端可施工细化，改动遵循 02 稳定前缀契约。
> 现状证据：`ModelConfig{Id/DisplayName/Protocol/MaxContextTokens/MaxOutputTokens/Temperature/Enabled}`（`ProviderModels.cs:50`）；前端 `ProviderManagerView.vue` 944 行（左侧服务商列表 + 右侧基础卡片/模型卡片/Header卡片）；`configStore.activeModel` 为发送模型唯一事实源；`hostBridge.ts` 含 `normalizeLlmProtocol/normalizeProvidersDocDto` 双向兼容层。

## 1. 存储与类型（前后端同构，精确到字段）

### 1.1 C#（`Core/Models/ProviderModels.cs`，在 `ModelConfig` 追加，默认值保证老文件可读）

```csharp
public bool SupportsVision { get; set; } = false;
public int MaxImagesPerTurn { get; set; } = 4;   // 0=默认4（见 §1.3，与 need08 的 0=无限不同）
public string? VisionDetail { get; set; } = "auto";
// 预留（本期声明但不使用、不校验、不进 UI）：
// public bool SupportsAudio { get; set; } = false;
// public bool SupportsVideo { get; set; } = false;
```

- `Clone()` 必须同步 3 字段，否则 `upsertProvider` 会丢能力位（施工 checklist 第一项）。
- `HaloJsonOptions` 已大小写不敏感，`providers.json` 缺字段即默认值，无需迁移脚本。

### 1.2 TS（`bridge/hostBridge.ts`，`ModelConfig` 接口追加 + 归一化）

```ts
export interface ModelConfig {
  id: string; displayName: string; protocol: LlmProtocolType;
  maxContextTokens: number; maxOutputTokens: number;
  temperature?: number | null; enabled: boolean;
  supportsVision: boolean;      // 新增，缺省 false
  maxImagesPerTurn: number;     // 新增，缺省 4
  visionDetail?: string | null; // 新增，缺省 'auto'
}
export function normalizeVisionFlag(m: any): void {
  if (typeof m.supportsVision !== 'boolean') m.supportsVision = false;
  if (!Number.isFinite(m.maxImagesPerTurn)) m.maxImagesPerTurn = 4;
  m.maxImagesPerTurn = Math.trunc(m.maxImagesPerTurn);
  if (!['auto','low','high'].includes(String(m.visionDetail||'auto'))) m.visionDetail = 'auto';
}
```

- 在 `normalizeProvidersDocDto` 的 `models.map` 内调用 `normalizeVisionFlag`（与 `normalizeLlmProtocol` 同位置）。
- Mock（`createBrowserMockBridge`）：`gpt-4o` 置 `supportsVision:true,maxImagesPerTurn:4`；`o3-mini` 置 `false`；`deepseek-chat` 置 `false`——保证浏览器调试能同时验两种分支。

### 1.3 语义冻结（施工无歧义）

| 字段 | 语义 | 超限 |
|---|---|---|
| `supportsVision=false` | 纯文本模型；发送含图轮次走 03 §2 降级（剥离图片+一次性通知+纯文本重发一次） | — |
| `maxImagesPerTurn=0` | **= 默认 4**（不是无限；图片无限会撑爆上下文，故与 need08 的 0=无限刻意不同，UI 文案必须写“0=默认4”） | `>16` 拒绝 `PROVIDER_VALIDATION`；`<0` 拒绝 |
| `visionDetail` | 仅透传 chat `image_url.detail`；resp 透传（部分网关忽略）；非法值网关回退 `auto` 不拒绝 | — |

## 2. 网关门控（前端只需知道契约，精确到行为）

- `AgentOrchestrator` 轮首快照模型能力；`Adapter.SerializeRequest` 首行计数图片；非视觉模型含图 → 抛 `NonVisionModelException` → 循环捕获后：落一条 assistant 通知 + 纯文本降级重发一次（仅一次）。
- 超 `MaxImagesPerTurn`：网关取**最新 N 张**（保 latest 丢最旧），`llm-trace` 记 `imgTruncated=N`。前端发送前也做同样取新丢旧（见 04 §4），双拦一致。
- 前端不直接调网关；前端职责仅：正确存能力位 + 发送前预警 + 超限取舍与后端一致。

## 3. 前端 UI（`ProviderManagerView.vue` 最小增量，精确到行）

### 3.1 位置（禁止新开 Tab/新开卡片）

在现有“模型卡片行”内追加一行（与 `protocol` 切换、`temperature` 输入同一行区，避免新增卡片导致 1020px 横溢）：

```
[模型行：id | displayName | protocol切换 | 温度]
[新增行：☑ 可读图片（多模态） [switch] | 每轮最多图片 [input-number 0..16] (0=默认4) | 清晰度 [select auto/low/high] ]
[灰字提示：关闭后发送图片将被拦截并转纯文本；图片仅支持 jpg/png/webp ≤8MB。]
```

### 3.2 组件约束（CefSharp Chrome109 + Naive 2.45.3 已验证）

- 只用 `n-switch / n-input-number(min=0,max=16) / n-select(auto/low/high) / n-alert`；禁用 `n-slider` 新交互。
- `supportsVision=false` 时 `maxImages/visionDetail` 置 `disabled` 但**保留值**（切换回 true 即恢复，避免来回丢配置）。
- 脏检查：纳入现有 `draft vs saved` 对比（与改名/改 URL 同一套保存按钮，不新增保存入口）；`reset` 按模型粒度恢复（复用现有按模型删除/恢复，不新增）。

### 3.3 文案冻结（避免施工各自发挥）

- switch 标签：`可读图片（多模态）`
- number 标签：`每轮最多图片（0=默认4，上限16）`
- 灰字：`关闭后发送图片将被拦截；图片按 jpg/png/webp ≤8MB 校验，超限自动取最新 N 张。`
- 非法输入报错（后端透传）：`字段 [maxImagesPerTurn]：允许 0 或 1..16`

### 3.4 testid（手工矩阵点检用）

`model-supports-vision-switch / model-max-images-input / model-vision-detail-select`

## 4. 与 04/05 的联动（精确到条件）

- 04 输入框读取 `configStore.activeModel`：
  - `supportsVision=false` 且草稿含图 → 发送按钮旁黄字（`n-alert warning tiny`）：`当前模型不支持看图，发送时将自动忽略图片（文字仍发送）` + 首次发送弹 `dialog.warning` 一次确认（`localStorage halo-skip-novision-warn=1` 记住，不反复弹）。
  - `supportsVision=true` → 无黄字；超 `maxImagesPerTurn` → 黄字 `图片超限，将只发送最新 N 张`。
- 05 持久化不受能力位影响（图片引用永远落盘；能力位只影响发送剥离），保证切换模型后历史图片可恢复。`MessageTimeline` 缩略图渲染与能力位无关（永远可看）。

## 5. 施工清单（按顺序打勾）

1. C# `ModelConfig` + `Clone()` + `VisionDefaults` + `ProviderConfigService` 校验。
2. TS `ModelConfig` + `normalizeVisionFlag` + Mock 三模型值。
3. `ProviderManagerView.vue` 新增行 + 脏检查 + testid。
4. `configStore` 无需改（透传），但联调需确认 `refreshProvidersConfig` 后 `activeModel.supportsVision` 实时更新。
5. 单测 `Stage09VisionModelTests`（老文件回退 + 超限拒绝 + Clone 不丢）。

## 6. 边角表（08 必须覆盖）

| # | 边角 | 预期 |
|---|---|---|
| E1 | 老 `providers.json` 无新字段 | 回退 `false/4/auto`，保存补写默认值 |
| E2 | `supportsVision=false` + 历史含图 | 会话可开可渲染；新一轮降级纯文本 + 通知 |
| E3 | `maxImagesPerTurn=0` | =默认4（文档/代码注释/UI三处一致写明） |
| E4 | `detail` 非法值 | 网关忽略用 `auto`，不拒绝 |
| E5 | 新前端+老后端 | 老后端忽略新字段（STJ 缺字段容忍），发送图片走老纯文本路径（图片占位符文本仍发送，不崩） |

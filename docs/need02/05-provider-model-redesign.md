# 05 · 「模型与服务商」Provider 重设计施工方案

对应需求 5a / 5b / 5c。主文件 `ProviderManagerView.vue`（848 行），后端 `ProviderConfigService.cs` / `HeaderVariableResolver.cs` / `ProviderModels.cs` 已通读。

## 1. 现状盘点

| 区域 | 现状行号 | 问题 |
|------|----------|------|
| 基础卡 `服务商网关、密钥与代理策略` | L80-213 | API Key（`col-span-7`）与代理策略（`col-span-5`）同行并排（L134-176）；独立代理子表单 `Custom` 时 `协议3/主机6/端口3` 挤一行（L179-211） |
| 自定义头卡 | L216-344 | `presetVariables` 8 个（L589-598）：`${session-hash/session-id/workspace-name/workspace-hash/model-id/provider-name/request-id/timestamp}`，无 `x-opencode-session`；`addCustomHeader` 默认 `X-Session-Hash=${session-hash}`（L676-686）；新建 Provider 默认头同式（L761-769） |
| 模型列表卡 | L346-470 | 每模型 `setting-inset` 全字段平铺可编辑：`id3/display3/protocol2/ctx2/out2`（L370-424）+ 底部协议 tag/统计/设默认/删除（L426-467）；模型一多即超长、无只读态、无预设按钮 |
| 后端头引擎 | `HeaderVariableResolver.cs` L99-169 | 通用 `${var}` 正则 + 白名单解析，`x-` 前缀透传（`TryAddWithoutValidation`），无需为新头加分支 |
| 预览 | `ProviderConfigService.PreviewProviderEndpointsAndHeaders` L339-375 | dummy request 解析 `EnableCustomHeaders` 全量头，天然支持新增头 |

## 2. 5a：API Key 占满一行、代理策略独占一行

### 目标布局（仍在同一 `n-card` 内，改 grid 不拆卡）

```
Row1: 服务商名称(5) + BaseURL(7)            ← 不变 L89-115
Row2: 端点预览条                             ← 不变 L118-131
Row3: API Key 全行(12)：label 左 + 掩码 tag 右 + password input 全宽  ← 由 col-7 改 col-12
Row4: 代理策略全行(12)：n-select 全宽 + 说明小字（继承全局→显示当前全局摘要） ← 由 col-5 改 col-12
Row5: Custom 子表单：协议(3)+主机(6)+端口(3)  ← 结构不变，仅因父行变宽而自然舒展；端口 input-number 加 w-full（已有 L208）
```

模板 diff 草案：

```diff
-          <div class="grid grid-cols-12 gap-3">
-            <div class="col-span-7 space-y-1">…API Key…</div>
-            <div class="col-span-5 space-y-1">…代理策略 select…</div>
-          </div>
+          <div class="space-y-3">
+            <div class="space-y-1" data-testid="provider-apikey-block">…API Key（label+tag 行，input 全宽）…</div>
+            <div class="space-y-1" data-testid="provider-proxy-block">
+              <label>服务商代理策略 (默认继承全局)</label>
+              <n-select … />
+              <div class="text-[11px] text-secondary font-mono">当前继承指向：{{ configStore.globalProxySummaryText }}</div>
+            </div>
+          </div>
```

- `data-testid` 保留 `provider-apikey-input/provider-proxy-mode-select` 不变，仅外层加 block id 供自动化。
- 校验逻辑（`handleSaveCurrentProvider` L791-813）不动；`handleProviderProxyModeChange`（L661-674）不动。

## 3. 5b：预设头新增 `x-opencode-session → 会话hash`

### 语义澄清

- 不是新增变量，而是新增一条**默认 Header 条目**：`Key: x-opencode-session`，`ValueTemplate: ${session-hash}`。后端引擎大小写无关透传（`Dictionary OrdinalIgnoreCase` + `TryAddWithoutValidation`），`session-hash` 恒定 16 位小写 hex（`ComputeSessionHash(workspaceId:sessionId)`），与 `X-Session-Hash` 同值、双发，兼容 opencode 生态网关。
- 头名大小写：需求原文小写 `x-opencode-session`，HTTP 头大小写无关，落盘保持小写原样，前端展示亦小写，避免与 `X-Session-Hash` 混淆。

### 三处必改（前端 only，后端零改）

1. `presetVariables`（L589-598）追加一条快捷胶囊？不——胶囊是变量 `${…}`，而本需求是头 `x-opencode-session`。正确做法是在 Header 卡工具条加第二个按钮：
   ```html
   <n-button size="tiny" secondary data-testid="add-opencode-session-header-btn" @click="addOpencodeSessionHeader">
     + x-opencode-session（会话亲和）
   </n-button>
   ```
   ```ts
   function addOpencodeSessionHeader() {
     editingProvider.value!.enableCustomHeaders = true;
     const exists = editingProvider.value!.customHeaders.some(h => h.key.toLowerCase() === 'x-opencode-session');
     if (!exists) editingProvider.value!.customHeaders.push({ key: 'x-opencode-session', valueTemplate: '${session-hash}', enabled: true, isSecret: false });
     refreshEndpointPreview();
   }
   ```
   去重用小写比较，避免双写。
2. `addCustomHeader()` 默认保持 `X-Session-Hash` 不变（存量兼容）；`handleAddNewProvider()`（L749-789）`customHeaders` 默认从 1 条变为 2 条：
   ```ts
   customHeaders: [
     { key: 'X-Session-Hash', valueTemplate: '${session-hash}', enabled: true, isSecret: false },
     { key: 'x-opencode-session', valueTemplate: '${session-hash}', enabled: true, isSecret: false },
   ]
   ```
3. `CreateDefaultProvidersDocument()`（`ProviderConfigService.cs` L419-470）openAiProvider 默认头同步加第二条（否则老用户重置/新机初始化缺失）；已落盘用户不做静默迁移（`NormalizeProviderForStorage` L542-551 只清洗不增补，避免覆盖用户删头意图），在 UI 空态提示行（L296-298）加一句「推荐同时保留 `x-opencode-session` 以兼容 opencode 网关」。
4. `hostBridge.ts` mock（L600-612 / L916-935）：mock 默认头 + 预览替换逻辑无需改（`${session-hash}` 已被替换），仅 mock `customHeaders` 数组加第二条以便浏览器 dev 联调；`normalizeProvidersDocDto` 不动。

验收：新建 Provider 自带双头；点按钮重复点不重复加；`resolvedHeaders` 预览区同时出现两键同值；日志脱敏（`isSecret`）行为不变。

## 4. 5c：Model 卡重设计——默认只读、点击排他展开编辑

### 4.1 交互总览

- 默认每模型一行**只读摘要行**（`setting-option`，可点击）：`[协议tag] 模型ID（displayName) …… Context 128K · Out 4K …… [默认徽] [chevron]` + 右侧小操作（设默认/删除 icon button，`@click.stop`）。
- 点击摘要行 → 展开**唯一编辑区**（`expandedModelIndex`，排他：点 B 自动收 A；再点自己则收起）。编辑区含 §4.2 网格 + §4.3 预设按钮 + 底部操作条（`设为默认模型/删除模型` 从原底部上移至此，保持 L442-466 能力）。
- 新增模型（`addModelRow` L712-723）：push 后 `expandedModelIndex = last`，新卡直接展开进入编辑，ID 默认 `model-N` 待用户改。

状态：

```ts
const expandedModelIndex = ref<number | null>(null);
function toggleModelExpand(idx: number) { expandedModelIndex.value = expandedModelIndex.value === idx ? null : idx; }
// 切换 Provider（selectProvider L616-625）与保存后必须 expandedModelIndex.value = null，避免越界
watch(() => editingProvider.value?.id, () => { expandedModelIndex.value = null; });
```

### 4.2 可编辑区排版（信息完整展示）

```
Row1(12): 模型ID(6,必填+placeholder gpt-4o/o3-mini) + 显示名称(6,placeholder 显示别名可选)
Row2(12): 通信协议(4,select chat/resp 全宽说明) + 启用开关(2,switch+文字) + 温度(6?可选折叠)
  └ 温度建议收进「高级折叠」(n-collapse)：temperature nullable，部分推理模型需留空——原 L714 默认 0.2 保留，但编辑区提供「清空（留空）」小按钮
Row3(12): 最大上下文(6) + 最大输出(6)：上方 label，下方 input-number 全宽 + 下方预设按钮条（§4.3）
Row4: 端点归一提示（该模型协议→ chat/resp 完整 URL，由 computedChatEndpoint/computedRespEndpoint 取值，text-[11px] font-mono break-all）
Row5(footer): [设为默认模型 primary/secondary] … [删除模型 error quaternary，length<=1 时 disabled，沿用 L459]
```

- 原 L401-423 两个 `col-span-2` 数字框在 600px 右列下过窄（`n-input-number` 箭头与数字挤叠）是本次要解决的核心——改为各占 6 列后全宽，Chrome109 无溢出。
- `displayName` 为空时摘要行显示 `id`；`protocol` tag 文案沿用 L434（`resp (/v1/responses)` / `chat (/v1/chat/completions)`）。

### 4.3 上下文/输出 Token 预设按钮（保持自定义 input）

```ts
const CONTEXT_PRESETS = [
  { label: '128k', value: 128_000 }, { label: '256k', value: 256_000 },
  { label: '512k', value: 512_000 }, { label: '1M', value: 1_000_000 },
];
const OUTPUT_PRESETS = [
  { label: '4k', value: 4_096 }, { label: '8k', value: 8_192 }, { label: '16k', value: 16_384 },
  { label: '50k', value: 51_200 }, { label: '64k', value: 65_536 }, { label: '128k', value: 131_072 },
];
```

- 需求原文输出预设为 `4k 8k 10k 50k 60k 128k`——`10k=10240/60k=61440` 非 2 的幂， histórico 模型多用 16k/64k；方案取并集 `4k/8k/16k/50k/64k/128k`（覆盖原文 4k/8k/50k/128k，10k→16k、60k→64k 向上兼容，并在文档注明映射；若产品坚持字面，则把 16k 换 10k、64k 换 60k，一行配置即可）。
- 渲染：`n-button tiny secondary` 一排，`:type="mdl.maxContextTokens===p.value?'primary':'default'"` 高亮命中；点击即赋值；`n-input-number` 保留（min/max/step 沿用 L403-410/L415-422：ctx 1024~2000000 step4096；out 256~256000 step1024——1M ctx 与 128k out 在界内，无需改 clamp；但 `NormalizeProviderForStorage` L560-561 非正回退 128000/4096 保持）。
- 人性化显示：摘要行 `formatK(128000)='128K'`（`Math.round(v/1024)+'K'`，1M 显示 `1024K` 或特判 `1M`——建议特判 `>=1048576 → (v/1048576)+'M'`）。

### 4.4 只读摘要行模板草案

```html
<div class="setting-option p-2.5 cursor-pointer min-w-0" :class="{'setting-option-active': expandedModelIndex===mIdx}"
  :data-testid="`model-row-${mIdx}`" @click="toggleModelExpand(mIdx)">
  <div class="flex items-center justify-between gap-2 min-w-0">
    <div class="flex items-center gap-2 min-w-0">
      <ChevronDown class="w-3.5 h-3.5 transition-transform shrink-0" :class="{'rotate-180': expandedModelIndex===mIdx}" />
      <span class="text-xs font-semibold font-mono truncate">{{ mdl.id || '(未命名模型)' }}</span>
      <span v-if="mdl.displayName" class="text-[11px] text-secondary truncate">({{ mdl.displayName }})</span>
      <n-tag :type="mdl.protocol==='resp'?'info':'success'" size="small" :bordered="false" class="font-mono shrink-0">{{ mdl.protocol==='resp'?'resp':'chat' }}</n-tag>
    </div>
    <div class="flex items-center gap-2 shrink-0" @click.stop>
      <span class="text-[11px] font-mono text-secondary hidden sm:inline">{{ formatK(mdl.maxContextTokens) }} / Out {{ formatK(mdl.maxOutputTokens) }}</span>
      <n-tag v-if="isModelActive(editingProvider.id, mdl.id)" type="primary" size="small" :bordered="false">默认</n-tag>
      <n-button size="tiny" secondary @click="handleSetActiveModel(editingProvider.id, mdl.id)">设为默认</n-button>
    </div>
  </div>
  <n-collapse-transition :show="expandedModelIndex===mIdx">
    <div @click.stop class="pt-2.5 mt-2 border-t border-subtle/60 space-y-2.5">…§4.2 编辑网格…</div>
  </n-collapse-transition>
</div>
```

- `@click.stop` 在编辑区阻止冒泡收起；操作按钮亦 `@click.stop`。
- 删除按钮放编辑区 footer（避免摘要行误删），与 04 篇原地确认心智一致但此处仍用直接删除（模型删除已有 `length<=1` 保护）。

## 5. 文件级施工步骤

1. `ProviderManagerView.vue` 模板：
   - L134-176 → §2 diff（Key 全行 + 代理独占行 + 全局摘要小字）。
   - L300-311 工具条：`添加自定义 Header` 旁加 `addOpencodeSessionHeader` 按钮（§3）。
   - L363-469 模型列表：整体替换为 §4.4（摘要行 + `n-collapse-transition` 编辑区 + §4.2 网格 + §4.3 预设条）；`v-for` key 建议 `mdl.id + mIdx`（防 ID 重复时复用错乱）。
2. script：
   - 新增 `expandedModelIndex/CONTEXT_PRESETS/OUTPUT_PRESETS/formatK/toggleModelExpand/addOpencodeSessionHeader/setPreset()`；`selectProvider/addModelRow/removeModelRow/handleSaveCurrentProvider` 末尾复位 `expandedModelIndex`；`handleAddNewProvider` 默认双头（§3）。
   - `protocolOptions/proxySchemeOptions` 不动；`presetVariables` 不动（变量胶囊与新头按钮并存）。
3. C#：`ProviderConfigService.CreateDefaultProvidersDocument` openAi 默认头 +1（§3.3）；其余 C# 零改。
4. `hostBridge.ts` mock 默认头 +1（dev 联调用）。

## 6. 联合回归注意

- 02 篇的 `table fixed` 加固如落地，provider 预览区（L118-131 端点条 / L324-342 resolvedHeaders）自动受益，不重复写 scoped。
- Drawer 右列 8/12 宽下 Row3 各 6 列仍 ≥280px，`n-input-number` 不再挤叠；小窗（<lg 单列）grid 自动堆叠（`grid-cols-12` + `col-span-12 sm:…` 建议补 `col-span-12` 保底）。

## 7. 验收清单

- [ ] 5a：Key 独占一行全宽；代理策略独占一行（含全局摘要）；Custom 子表单三框舒展无挤叠；`data-testid` 旧 id 仍可定位。
- [ ] 5b：新建 Provider 自带 `X-Session-Hash` + `x-opencode-session`（同值 `${session-hash}`）；按钮重复点不 дубли；预览区双键同值；C# 默认文档同步。
- [ ] 5c：默认只读摘要（ID/别名/协议/128K/Out/默认徽）；同时最多展开一个；点击摘要/chevron 展开收起；编辑区 ID/别名/协议/开关/温度高级/双 token（预设+手输）/端点提示/设默认/删除完整可用；新增模型直接展开。
- [ ] 预设：上下文 `128k/256k/512k/1M`，输出 `4k/8k/16k/50k/64k/128k`（与原文差异见 §4.3 注明）；手输任意合法值可保存（`NormalizeProviderForStorage` 不 clamp 上限，仅正数校验）。
- [ ] 暗/亮各一全卡截图；`vue-tsc -b && vite build` 通过；`grep -n "col-span-7.*apiKey\|col-span-5.*proxy" ProviderManagerView.vue` 旧并排零残留。

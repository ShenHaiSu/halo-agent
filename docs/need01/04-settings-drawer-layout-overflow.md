# 任务3附属A：Drawer 尺寸选型与内部溢出根治（痛点a+b）

> 对应痛点：a. Dialog 太小且居中不好用；b. 内部组件超出 Dialog 宽度。
> 前置：已阅读 `03` 总览。本文只给“量好的尺寸 + 逐文件溢出清单”。

## 1. Drawer 尺寸与结构定稿

### 1.1 为什么是 `width=1020 + max-width:94vw`

| 方案 | 可用宽（估算） | 否决/采纳理由 |
|------|---------------|--------------|
| 现状 Modal 960 居中 | 约 820px（扣纵 Tab 110 + padding 48 + pl-2） | 太窄，且纵 Tab 在小高度下还要纵滚 |
| Modal 放大到 1280 居中 | 约 1140px，但高度仍只有视口 70%，且遮挡主工作台 | 否决：宽了但依然不好“做事” |
| **Drawer 1020 右侧全高（采纳）** | **约 972px（扣 drawer padding 约 48，无纵 Tab 开销）**，高度=100vh 可纵滚 | 右侧滑入不挡左侧会话/顶栏水位；全高使长表单（Provider 三卡片叠加约 1400px）一次纵滚看完，无需二级滚动 |

写法（chrome109 安全）：

```vue
<n-drawer v-model:show="visible" placement="right" :width="1020" style="max-width: 94vw;">
  <n-drawer-content title="设置与诊断中心" closable :native-scrollbar="false">
```

- 小窗（如 1280×720 CefSharp）自动收为 94vw≈1203→受 1020 上限约束，实际 1020；更窄的 Win7 小屏则按 94vw 收缩，不会出现横向滚动条。
- 禁止使用百分比字符串以外的响应式 JS 计算宽度（无需监听 resize，保持简单）。
- `n-tabs` 改为 `type="line" placement="top"`，去掉 `style="min-height:520px"` 与每个 pane 外的 `<div class="pl-2">`（共 4 处），换成 `<div class="pt-3 min-w-0 max-w-full overflow-x-hidden">`。

### 1.2 滚动规则（一句话）

- 纵向：只允许 Drawer 自身滚动。各面板现有的 `max-h-[540px]/max-h-[580px] overflow-y-auto`（`ProviderManagerView.vue` L23/L82）全部删除，改为自然高度；`n-drawer-content` 的 `native-scrollbar=false` 启用 Naive 内置细滚动条，与主界面 6px 滚动条视觉统一。
- 横向：Drawer 内容区及每个面板根节点统一加 `min-w-0 max-w-full overflow-x-hidden`；任何横向溢出都视为 bug，不允许出现横向滚动条。

## 2. 溢出清单（逐文件、逐行给改法）

### 2.1 `ProviderManagerView.vue`（重灾区，5 处）

根：`L2 <div class="grid grid-cols-12 gap-4">` → 改为：

```diff
-<div class="grid grid-cols-12 gap-4" data-testid="provider-manager-panel">
+<div class="grid grid-cols-1 lg:grid-cols-12 gap-4 min-w-0 max-w-full overflow-x-hidden" data-testid="provider-manager-panel">
+  <!-- 左列表：col-span-4 → lg:col-span-4；右详情：col-span-8 → lg:col-span-8，均加 min-w-0 -->
```

- 理由：Drawer 在窄窗下不足 900px 时强制 12 列会把右详情压到 ~600px；`lg:` 断点以下上下堆叠最稳。`min-w-0` 是 grid 子项可压缩的关键（grid 默认 `min-width:auto` 会撑破）。

1. **左列表项 L24-L35**：`:class` 选中态换语义类（见 `05`），另给整个 item 加 `min-w-0`，`baseUrl` 行（L60-L62）已有 `truncate`，再加 `min-w-0` 父约束即可。
2. **端点预览条 L122-L135**：两个 `flex justify-between` 行内右侧 URL `<span class="... select-all">` 追加 `min-w-0 flex-1 text-right truncate`，左侧 label 加 `shrink-0 mr-2`；外层再加 `overflow-hidden`。长网关 URL（如 `https://corp-gateway…/v1/chat/completions`）从“撑破”变为“省略+悬停可选”，`data-testid="preview-chat-endpoint/preview-resp-endpoint"` 保留。
3. **独立代理小网格 L185 `grid-cols-12`**：子项全部已有 `col-span-*`，追加每个直接子 `min-w-0`；`n-input-number`（端口）加 `w-full max-w-full`（已有一半，需补 `max-w-full` 与 `:show-button="false"` 可选，避免上下按钮挤宽）。
4. **Header 列表行 L252-L297 `grid-cols-12`**：`col-span-1/4/5/2` 在 900px 下操作列（`col-span-2`≈140px）塞 checkbox+删除按钮必溢出。改法：行容器加 `min-w-0`，`col-span-2` 操作列改为 `flex items-center justify-end gap-1 min-w-[86px]`，`n-input` 两个各加 `w-full`；或在小屏下将该行改为 `grid-cols-1 sm:grid-cols-12`。推荐后者，一行代码解决。
5. **模型行 L373 `grid-cols-12`（3+3+2+2+2）**：同理，每个 `col-span-*` 子项加 `min-w-0`，`n-input-number` 两个加 `w-full max-w-full`；底部信息条 L429 `flex justify-between` 加 `flex-wrap gap-2`，左侧 tag+mono 文案加 `min-w-0 break-all`，右侧按钮组加 `flex-wrap`。`data-testid="model-row-*"` 保留。

### 2.2 `NetworkProxySettings.vue`（3 处）

1. **三模式卡 L22 `grid-cols-3`** → `grid-cols-1 xl:grid-cols-3`。Drawer 1020 下三列每列约 300px，`n-radio`+图标+描述（`pl-6`）刚好塞满；窄窗三列必溢出。断点堆叠后每块独占一行，最稳。
2. **系统代理快照 L92-L143**：`n-descriptions :column=2` 在窄窗下 `effectiveHttpProxyUri` 长 URI 会撑列。改法：`:column="1"`（或保留 2 但给每个 value span 加 `break-all`）；推荐改为 1 列纵排，诊断信息本就适合纵读。`bypassOverride` 已有 `truncate`，需补 `max-w-full`。
3. **手动代理表单 L150-L181 `grid-cols-12`**：同 Provider 处理，每个子项加 `min-w-0`；`code SocketsHttpHandler.UseProxy=false`（L243）加 `break-all`。

### 2.3 `StorageSettingsPanel.vue`（2 处）

1. **日志监控 `n-descriptions :column=3`（L132）**：三通道 `filePath`（如 `C:\HaloAgentData\logs\llm-trace-20260101.log`）在三列下必溢出。改为 `:column="1"` 纵排（或 `:column="2"` + `label-placement="top"`），每个 `filePath` 行已是 `truncate`，补 `max-w-full` 即可。`header-extra` 的三个按钮 `n-space` 加 `flex-wrap`（窄窗下“写入测试日志/清理/打开目录”会超出卡头）。
2. **迁移确认 `n-modal preset=dialog style=width:540px`（L154-L161）**：这是 drawer 内的二级 dialog，保留 dialog 形态，但宽度改为 `width: min(540px, 90vw)`，避免小屏下 dialog 比 drawer 还宽的荒诞。

### 2.4 `SystemRuntimePanel.vue`（1 处）

- `n-descriptions :column=2`（L4）+ 合规清单 `grid-cols-2`（L61）：前者长 mono（架构、TLS、目录）加 `break-all`；后者改为 `grid-cols-1 xl:grid-cols-2`，每个 item（L62-L72）加 `min-w-0`，右侧 `value` span 加 `text-right break-all`。

### 2.5 全局防线（`main.css` 追加 8 行，与主题无关）

```css
/* Drawer 内容永不横溢：grid/flex 子项默认 min-width:auto 是元凶 */
.settings-drawer-body, .settings-drawer-body * { min-width: 0; }
.settings-drawer-body { overflow-x: hidden; }
.settings-drawer-body code, .settings-drawer-body .font-mono {
  overflow-wrap: anywhere; word-break: break-all;
}
```

- 作用域限定 `.settings-drawer-body`（给 `n-drawer-content` 包一层该类），不污染主工作台。
- 全部 chrome109 安全，无需前缀。

## 3. 验收（痛点a+b）

- [ ] 设置中心从右侧滑入，全高可见；关闭后主工作台无遮挡残留；Esc/遮罩点击关闭正常。
- [ ] 1020px 与 94vw 收缩两种宽度下，四个 Tab 均无横向滚动条；用最长 BaseURL（40+ 字符）+ 开启独立代理 + 3 个自定义 Header + 3 个模型时仍不溢出。
- [ ] `n-descriptions` 在窄窗下纵排可读，长路径/URI 自动换行或省略。
- [ ] `vue-tsc -b && vite build` 通过；所有 `data-testid` 原名保留。

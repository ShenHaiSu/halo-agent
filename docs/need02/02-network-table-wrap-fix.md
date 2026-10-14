# 02 · 「网络与代理」系统代理快照表格换行修复方案

## 1. 故障复现（对照截图）

截图（`pasted-8838a51a…image.png`）：「网络与代理」→ 已选「自动检测系统代理」→「Windows 系统代理实时探测快照」卡内 `n-descriptions bordered :column=1` 四行中：

- `label` 列（深色底）文字被竖排挤压：`注册表` / `ProxyServer`、`PAC` / `自动脚本地址`、`绕过列表` / `(ProxyOverride)` 逐字换行；
- `content` 列长串（`http://127.0.0.1:7890`、`localhost;127.*;…;<local>`）溢出或把 label 列挤窄；
- 本质是 **table 列宽分配失败 + label 不换行策略错误**，不是数据错误。

## 2. 根因定位（已读代码）

文件 `NetworkProxySettings.vue` L109-130：

```html
<n-descriptions bordered :column="1" size="small" label-placement="left">
  <n-descriptions-item label="生效代理 URI"><span class="font-mono text-xs text-emerald-400 min-w-0 break-all">…</span></n-descriptions-item>
  <n-descriptions-item label="注册表 ProxyServer">…break-all…</n-descriptions-item>
  <n-descriptions-item label="PAC 自动脚本地址">…</n-descriptions-item>
  <n-descriptions-item label="绕过列表 (ProxyOverride)">
    <span class="font-mono text-xs truncate max-w-full min-w-0 break-all">…</span>  <!-- ← truncate 是元凶之一 -->
  </n-descriptions-item>
</n-descriptions>
```

1. **Naive `n-descriptions` 默认 `table-layout: auto` + label `white-space: nowrap`？** 实际上 Naive 对 `bordered` 表格 `th(label)` 宽度由内容决定，长中文 label（7~13 字）在 1020px Drawer 右侧列（约 600px 可用）下与长 content 争宽 → table 自动布局把 label 压成窄列，中文逐字断行。
2. **`truncate`（= `overflow:hidden + text-overflow:ellipsis + white-space:nowrap`）与 `break-all` 同写在绕过列表 span 上**（L126）：`truncate` 的 nowrap 覆盖换行意图，长 bypass 字符串单行溢出，视觉即「不能自动换行」。
3. 全局 `.settings-drawer-body code/.font-mono{word-break:break-all}`（`main.css` L88-90）只管断词，不管 table 的 `table-layout` 与 `th` 白空格，所以修不彻底。
4. 外层已有 `min-w-0 max-w-full overflow-x-hidden`（L2/L82），横溢被裁而非换行，用户感知为「内容被切」。

## 3. 修复目标

- 四行 label 完整横排展示、不竖排；content 长串（URI/注册表/PAC/绕过列表）自动换行、无横向滚动、无省略截断。
- 暗/亮两主题一致；窄窗（Drawer `max-width:94vw`）同样成立；不动任何 TS 逻辑与 bridge。

## 4. 方案（纯 CSS + 微调模板，推荐 A）

### 方案 A（采用）：scoped `:deep` 覆盖 + 去掉 `truncate`

模板微调（仅 `NetworkProxySettings.vue` L109-130）：

```diff
-<n-descriptions bordered :column="1" size="small" label-placement="left">
+<n-descriptions bordered :column="1" size="small" label-placement="left" class="sys-proxy-snapshot">
   <n-descriptions-item label="生效代理 URI">…（不变，已有 break-all）
   <n-descriptions-item label="注册表 ProxyServer">…（不变）
   <n-descriptions-item label="PAC 自动脚本地址">…（不变）
   <n-descriptions-item label="绕过列表 (ProxyOverride)">
-    <span class="font-mono text-xs truncate max-w-full min-w-0 break-all">
+    <span class="font-mono text-xs min-w-0 break-all">
```

`NetworkProxySettings.vue` `<style scoped>` 新增（Chrome109 安全，无新语法）：

```css
/* 表格固定布局：label 定宽，content 占剩余并强制换行 */
.sys-proxy-snapshot :deep(.n-descriptions-table) { table-layout: fixed; width: 100%; }
.sys-proxy-snapshot :deep(.n-descriptions-table th) {
  width: 168px; min-width: 168px; max-width: 168px;
  white-space: normal; overflow-wrap: anywhere; word-break: break-all;
  line-height: 1.5; vertical-align: top;
}
.sys-proxy-snapshot :deep(.n-descriptions-table td) {
  white-space: normal; overflow-wrap: anywhere; word-break: break-all;
  line-height: 1.6; vertical-align: top; min-width: 0;
}
.sys-proxy-snapshot :deep(.font-mono) {
  white-space: normal; overflow-wrap: anywhere; word-break: break-all;
}
```

- `168px` 依据：最长 label「绕过列表 (ProxyOverride)」约 13 字符 × 12px ≈ 156px + padding；Drawer 窄至 360px 时仍可用（content 剩 ~190px 照样换行）。如需更稳可用 `width: 34%; max-width: 200px`，但定值在 `column=1` 下最可预测，推荐定值。
- 为何不用 `label-style/content-style` prop：Naive 允许 `:label-style="{width:'168px'}"`，但 `th white-space` 仍需 CSS 覆盖，一次写 scoped 更彻底；且后续 05 篇 provider 预览区可复用同一类。

### 方案 B（备选，不推荐首选）：换 `n-descriptions` 为 `div` 描述列表

用 `setting-inset` + `grid grid-cols-[168px_1fr]` 手写四行，完全避开 table 布局。优点是零 `:deep`；缺点是丢失 `bordered` 原生边框语义、改动行数更多。仅当 Naive 版本升级导致 `:deep` 类名变化时启用。

## 5. 施工步骤

1. `NetworkProxySettings.vue`：L109 加 `class="sys-proxy-snapshot"`；L126 删 `truncate max-w-full`（保留 `min-w-0 break-all`）；确认四行 content span 均有 `break-all`（L111/L116/L121 已有，补 L126 后的即可）。
2. 同文件追加 `<style scoped>`（§4 代码块原样粘贴）。
3. 可选加固 `main.css`（一行，惠及 provider 预览区）：
   ```css
   .settings-drawer-body table { table-layout: fixed; width: 100%; }
   .settings-drawer-body th, .settings-drawer-body td { white-space: normal; overflow-wrap: anywhere; word-break: break-all; }
   ```
   注意：此为全局兜底，优先级低于 scoped，不会污染工作台 `MessageTimeline`（其不在 `.settings-drawer-body` 内）。
4. 同屏检查：`NetworkProxySettings.vue` L349-352 探测结果两行 `flex justify-between` 长 URL 在窄窗下可能同病——顺手给两 `span` 加 `min-w-0 break-all` + `flex-wrap`，不单独立项。

## 6. 验收清单

- [ ] 复现数据：系统代理开启（`127.0.0.1:7890`）+ 绕过列表含 `<local>` 长串时，四行 label 横排完整，content 自动换行，Drawer 内无横向滚动条（DevTools 检查 `scrollWidth === clientWidth`）。
- [ ] 窄窗：将浏览器缩至 900px（Drawer `94vw` 生效），同样无挤压竖排。
- [ ] 暗/亮各一截图；` trunk truncate` 在该文件零残留（`grep -n truncate NetworkProxySettings.vue` 应只剩 0 或无关行）。
- [ ] 回归：切换 Custom/None 模式表单无样式漂移；`test-proxy-connectivity` 结果区不断行异常。
- [ ] `vue-tsc -b && vite build` 通过。

## 7. 风险与回退

- Naive 内部类名（`.n-descriptions-table`）跨版本改名风险：已 pin `naive-ui 2.45.3`，升级时重测本节即可；回退即删 scoped 块 + 恢复 `truncate`，一分钟内可逆。
- `table-layout:fixed` 后超长无空格串（如 200 字符 URI）依赖 `break-all` 断行，已在 `main.css` 全局兜底，无 Chrome109 兼容问题。

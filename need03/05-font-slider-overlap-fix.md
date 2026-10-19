# 05 · 全局字号滑块与中文字体 label 纵向重叠：成因与修复方案

## 1. 现状（已读代码）

`AppearanceSettingsPanel.vue` L68-82 字体卡第一块：
```html
<div class="space-y-1.5">
  <div class="flex items-center justify-between">
    <span class="text-xs text-secondary">全局字号</span>
    <span class="text-xs font-mono">…px</span>
  </div>
  <n-slider :value="settings.fontSize" :min="12" :max="16" :step="1" :marks="fontSizeMarks" ... />
</div>
<div class="grid grid-cols-1 gap-2.5">  <!-- 中文字体 -->
  <div class="space-y-1"><span class="text-xs text-secondary">中文字体</span><n-select .../></div>
```
- `fontSizeMarks = {12:'12',13:'13',14:'14',15:'15',16:'16'}`（L207-213）：5 个刻度全显。
- 外层 `space-y-3`（L67）：滑块块与中文块之间仅 12px 间距。
- `n-slider` 带 `marks` 时，Naive 会在轨道下方渲染一排 mark label（绝对定位，约 14–18px 高），**该高度不计入 slider 本体高度**（Naive slider 本体 ~20px，marks 溢出到下方）。于是 marks 文字（12/13/14/15/16）与下方 `中文字体` label 在 12px 间距里相撞 → 纵向重叠。

## 2. 根因（为什么会重叠 + 为什么之前没发现）

1. **直接原因**：`marks` 溢出高度未预留。`space-y-1.5` 只管“标题行—滑块”间距，不管“滑块—下一块”间距；`space-y-3` 的 12px < marks 溢出 14–18px → 必然重叠。Drawer 窄（~320px）时 5 个 mark 挤在一起更明显。
2. **放大器**：全局 `font-size: var(--app-font-size)`（`main.css` L96-98）会放大 marks 字体（marks 继承 body 字号，14–16px 档下更大），重叠随字号档位加重——用户调大字号越重叠，负反馈循环。
3. **防复发缺口**：项目无 “带 marks 的 slider 下方必须留白” 规范；need02-01 只验了功能未验 5 档 marks 在窄 Drawer 下的盒模型。

## 3. 修复设计（三选一，推荐 A）

- **A（推荐）：去 marks + 留刻度感**：删 `:marks`，改用 `show-tooltip` 或下方自定义 12/16 端点文案。slider 本体无溢出，重叠根除；5 档步进（step=1）本身已有段落感，marks 信息密度低可舍。`data-testid` 不变。
```html
<n-slider :value="..." :min="12" :max="16" :step="1" />
<div class="flex justify-between text-[11px] text-secondary font-mono"><span>12 小</span><span>16 大</span></div>
```
下方端点行是正常流高度，永不溢出。
- **B（保留 marks，加垫）**：保留 marks，外层 `space-y-1.5 → space-y-0` 并给 slider 包一层 `pb-6`（预留 24px 给 marks）。优点零信息损失；缺点窄 Drawer 下 5 个 mark 仍拥挤，且字号放大后仍可能擦边。
- **C（marks 精简）**：marks 只留两端 `{12:'12',16:'16'}` + `pb-5`。折中，信息与空间兼顾。

推荐 **A**（最彻底），若产品坚持刻度可读则退到 **C**。无论选哪个，**禁止再用“5 全量 marks + space-y-3”组合**（写入 §4 规范）。

## 4. 施工步骤 + 防复发规范

1. 按 A（或 C）改 L73-81；下方中文块 `grid gap-2.5` 不动（间距已够，问题不在它）。
2. `main.css` `.settings-drawer-body` 加一条兜底（即使未来有人再加 marks 也不重叠）：
```css
/* 带刻度的 slider 必须预留下方空间：marks 溢出约 18px */
.settings-drawer-body .n-slider--with-marks { margin-bottom: 18px; }
```
（Naive slider 有 marks 时根节点带 `--with-marks` 修饰类；若类名版本差异，改用属性选择器或直接给使用方包 `slider-with-marks` 类，二选一施工时实测确定。）
3. 验证 12/13/14/15/16 五档逐一切换，每档截一图（或至少 12/16 两端 + 14 中间三档），确认滑块区与 `中文字体` label 间隙 ≥8px。
4. 规范入库：在 `00-README` 或本篇留下红线——Drawer 内 `n-slider` 若带 `marks` 必须同时加下垫（`pb-5` 或 `margin-bottom:18px`）；CodeReview checklist 加一项。

## 5. 验收清单

- [ ] 12–16 五档下，滑块刻度区与“中文字体” label 无任何像素重叠（DevTools 盒模型截图或目测）。
- [ ] Drawer 在 1020px 与窄窗（<900px）下均无横向滚动条；`settings-drawer-body *{min-width:0}` 不被破坏。
- [ ] `data-testid="appearance-font-size"` 与 `appearance-font-size-value` 行为不变（拖动即时改 `--app-font-size` + 预览区同步）。
- [ ] `vue-tsc -b && vite build` 通过；暗/亮各一图（marks/端点文案在亮色下可读）。

## 6. 风险与回退

- 纯展示层，删 marks 不影响 `appearanceStore.update({fontSize})` 数据流；回退即恢复 `:marks="fontSizeMarks"` 一行。
- 若 Naive 版本升级改了 marks 类名，§4 的全局兜底选择器需同步更新（以 A 方案为主则兜底只是双保险，不阻塞）。

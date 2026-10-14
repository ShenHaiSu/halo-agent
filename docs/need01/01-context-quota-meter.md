# 任务1：上下文预算监控——删除左侧大号百分比，只留右侧

> 文件：`src/Web.Client/src/components/workbench/WorkbenchTopBar.vue`
> 范围：主界面右上角常态徽标（`data-testid="context-quota-meter-badge"`），不含 hover 弹出的详情卡。

## 1. 现状代码（已通读）

```vue
<!-- L37-L52，常态徽标 trigger -->
<div class="flex items-center gap-2 px-2.5 py-1 rounded ... cursor-pointer text-xs"
     data-testid="context-quota-meter-badge">
  <n-progress
    type="circle"
    :percentage="usagePercentage"
    :stroke-width="12"
    :color="statusColor"
    style="width: 16px; height: 16px" />
  <span class="font-mono text-[11px] font-medium" :class="statusTextColor">
    {{ formatTokens(usedTokens) }} / {{ formatTokens(totalTokens) }}
    <span class="opacity-80">({{ usagePercentage }}%)</span>
  </span>
</div>
```

详情卡内部（L62-L64）还有一个 `{{ usagePercentage }}% 已用` 的 `n-tag`，属于 hover 后才可见的内容。

## 2. 根因（为什么会出现“两个百分比、一大一小、大的溢出”）

1. `n-progress` 的 `show-indicator` 默认为 `true`。`type="circle"` 时会在圆环正中渲染百分比文本。
2. 该圆环被强制压到 `16×16px`，内置文本（约 12px 字号）根本放不下，只能以大号字体浮动溢出到容器之外——这就是“左边那个大个的百分比”。
3. 右侧 `<span>` 是手写的 `({{ usagePercentage }}%)`，字号 `11px`，与徽标对齐良好——这就是要保留的“右边这个”。
4. 同文件 L69-L75 的横向 `n-progress[type=line]` 已经正确写了 `:show-indicator="false"`，唯独 circle 漏了，属于遗漏而非设计。

结论：不是要删 DOM 节点，而是**关掉 circle 的内置指示器**。删整个 `n-progress` 会丢失圆环水位语义，不符合“只需要显示右边这个”的最小改动预期（圆环保留、文字去重）。

## 3. 施工方案（优雅且最小）

### 3.1 唯一必需改动：1 个 prop

```diff
 <n-progress
   type="circle"
   :percentage="usagePercentage"
   :stroke-width="12"
   :color="statusColor"
+  :show-indicator="false"
   style="width: 16px; height: 16px"
 />
```

- 位置：`WorkbenchTopBar.vue` L41-L47。
- 效果：左侧只剩 16px 纯圆环（颜色仍随 `statusColor` 70/90 阈值变蓝/黄/红），右侧保留 `12K / 128K (9%)` 文本。常态下视觉只剩一个百分比。
- 不动：`statusColor / statusTextColor / usageTagType` 计算、`formatTokens`、`n-popover` 详情卡、`usagePercentage` 钳制逻辑。

### 3.2 可选加固（建议顺手做，均 1 行）

| 项 | 做法 | 理由 |
|----|------|------|
| 防抖动 | 给右侧 `<span>` 加 `tabular-nums`（或 `font-variant-numeric: tabular-nums`） | 百分比 9%→10% 跳变时宽度稳定，徽标不抖 |
| 可访问性 | 给 circle 包一层 `title`/`aria-label`，如 `:title="\`已用 ${usagePercentage}%`"` | 关掉内置文本后，读屏/悬停仍可获知语义；且与 popover 不冲突 |
| 对齐 | circle 的 `style` 追加 `flex-shrink:0` | 极窄窗口下徽标被挤压时圆环不变形 |

三项都不改变布局结构，可与 3.1 同一次提交。

### 3.3 明确不改

- hover 详情卡 L56-L135 全保留：标题 `上下文预算监控`、`n-tag {{ usagePercentage }}% 已用`、横向进度条、Used/Reserve/Safety/Available 四行、Session Hash、协议标识——这些是诊断信息，不是“常态下第二个百分比”。
- 若产品最终连详情卡的 `% 已用`  tag 也想去，可单另提需求，本次不动，避免误伤诊断能力。

## 4. 验收

- [ ] 常态徽标：只见圆环 + `xxK / xxK (xx%)` 一处百分比；窗口缩放到 1100px 无溢出、无浮动大字。
- [ ] hover 仍能打开 330px 详情卡，内部水位、数值、Hash、协议均正常。
- [ ] 切换模型（`handleSelectModel`）后圆环颜色与右侧百分比同步变化；>90% 红、≥70% 黄、其余蓝。
- [ ] `vue-tsc -b` 通过；`data-testid="context-quota-meter-badge"` 未改名。

## 5. 回归风险

- 风险≈0。`show-indicator` 是 Naive UI `n-progress` 公开 prop（line 用法已在同文件验证），无样式副作用，无需改 `naiveTheme.ts` 与 C# 侧 `TokenBudgetService`。

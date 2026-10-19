# 04 用量分析前端设计（第 9 Tab + ECharts 板式）

## 4.1 图表库选型（冻结结论：Apache ECharts 5.5.x）

| 候选 | 结论 | 理由（按本项目约束打分） |
|---|---|---|
| **Apache ECharts `5.5.x`（采纳，canvas 渲染、按需引入）** | ✅ | 堆叠柱 + 折线双轴 + 环形 + rich hover 一套全包；canvas 在 CefSharp109/Chromium109 成熟；`echarts/core` 按需引（Bar/Line/Pie+Tooltip/Legend/Grid）可把增量控制在 ~350KB（gzip 前），在 `chunkSizeWarningLimit=2500` 内；主题可代码注入跟随暗黑/浅色；中英文档全，hover 图例开箱即用。锁 `^5.5.0` 不追 6（6 弃旧浏览器风险）。 |
| Chart.js 4.x | ❌ | 堆叠 + 双轴需手配插件，环形/图例联动弱；tree-shaking 粒度不如 ECharts core；无 dataset  transform，聚合要前端手写。 |
| uPlot / lightweight-charts | ❌ | 极快但只擅长时序折线，堆叠柱+占比+环形要自造轮子；API 低级，工期翻倍。 |
| AntV G2Plot / S2 | ❌ | 包体大（G2 全量 >1MB）、对 chrome109 兼容声明弱、样式与 Naive UI 融合成本高。 |
| 自研 SVG/Canvas | ❌ | hover 十字线/图例联动/堆叠圆角全手写，测试地狱。 |

引入方式（`package.json` 加 `echarts@5.5.1` + `@types/echarts` 可选）：
```ts
// views/components/usageCharts.ts —— 只引用的才打包
import * as echarts from 'echarts/core';
import { BarChart, LineChart, PieChart } from 'echarts/charts';
import { GridComponent, TooltipComponent, LegendComponent, DatasetComponent } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
echarts.use([BarChart, LineChart, PieChart, GridComponent, TooltipComponent, LegendComponent, DatasetComponent, CanvasRenderer]);
```
- 禁用 SVG 渲染器（Win7 下 SVG 滤镜 + 大数据量卡顿）；`animationThreshold` 设 500 点以上自动关动画；`resize` 用 `ResizeObserver` + Tab 激活时 `chart.resize()`（抽屉懒挂载宽度为 0 是经典坑，见 4.4）。
- 构建 target 保持 `chrome109`；ECharts 5 官方 browserslist 覆盖 chrome>80，无需 transpile 特例。

## 4.2 Tab 板式（ASCII 线框，Drawer 宽 1020 内）

```
┌─ 设置与诊断中心 ─────────────────────────────┐
│ 左Tab(第9个): [用量分析]                       │
│ ┌─ 筛选条（sticky） ────────────────────────┐ │
│ │ [今日][昨日][近3日][近7日][近30日][本周]  │ │
│ │ [本月][全部] [自定义日期____]              │ │
│ │ Provider[多选▾] Model[多选▾](按P联动) [查] │ │
│ └──────────────────────────────────────────┘ │
│ ┌─ 瓦片行（6块，n-grid 3×2 / 6×1自适应） ──┐ │
│ │总请求 │总输入 │总输出 │命中缓存│综合命中│模│ │
│ │ 1,234 │ 8.2M  │ 1.1M  │ 5.6M   │ 68.4%  │数│ │
│ └──────────────────────────────────────────┘ │
│ ┌─ 图1: 用量趋势（堆叠柱，按模型占比） ────┐ │
│ │ ECharts bar stack, x=日期, hover明细     │ │
│ └──────────────────────────────────────────┘ │
│ ┌─ 图2+图3 双列 ───────────────────────────┐ │
│ │命中率趋势(折线+柱) │ Provider占比(环形)  │ │
│ └──────────────────────────────────────────┘ │
│ ┌─ 明细表（原生table，分页） ──────────────┐ │
│ │日期时间│Provider│Model│in│out│cached│命中│ │
│ └──────────────────────────────────────────┘ │
└──────────────────────────────────────────────┘
```

- 文件：新建 `components/settings/UsageAnalyticsView.vue` + `stores/usageAnalyticsStore.ts` + `utils/usagePreset.ts`（预设→日期换算仅用于自定义回显，真实换算以后端为准）；`SettingsCenterDrawer.vue` 加第 9 pane（`name="usage" tab="用量分析"`），`SettingsCenterTab` 联合类型加 `'usage'`，组件懒加载（`defineAsyncComponent` 或 `v-if="activeTab==='usage'"`，避免首屏多 350KB）。
- 全部文本 `data-testid`：`usage-tab / usage-preset-* / usage-provider-select / usage-model-select / usage-tile-* / usage-chart-stacked / usage-chart-hit / usage-chart-provider / usage-table`（见 06）。

## 4.3 各区详细设计

### (1) 筛选条
- 预设 8 个：今日 / 昨日 / 近3日 / 近7日 / 近30日 / 本周（周一 00:00 起）/ 本月 / 全部；单选 pills（`n-button-group`），默认 `近7日`。
- 自定义：`n-date-picker type=daterange`（chrome109 原生日期兼容已验证），选后自动切为 `custom`。
- Provider：`n-select multiple`（选项来自 `UsageGetFilterOptions`）；Model：`n-select multiple`，选项按已选 Provider 联动过滤（`provider/name → models` 映射前端做；“不同 Provider 同名模型”以 `provider/name/model` 三段 label 区分，如 `OpenAI/gpt-4o` vs `DeepSeek/deepseek-chat`，value 用 `provider_id\x1fmodel_id`）。
- 查询按钮 + 自动查（预设/下拉变化防抖 300ms 自动重查）；空数据给空态插画（Naive `n-empty`），不留白屏。

### (2) 瓦片行（6 瓦）
| 瓦 | 值 | 小字 |
|---|---|---|
| 总请求数 | `1,234` | 所选范围内记录数 |
| 总输入 | `8.24 M`（短格式）hover 显示精确 | tokens |
| 总生成 | 同上 | tokens |
| 命中缓存 | 同上 + 占比小字 | tokens |
| 综合命中率 | `68.4%` 大字 + 进度环/条 | `Σcached/Σinput` |
| 活跃模型 | `5` | `provider/model` 去重数 |
- 实现：`n-card`/`n-statistic` 或轻量 div（推荐后者，[end10] 原生化精神，减少 Naive 嵌套）；数字千分位 + 短单位（`formatSmartBytesShort` 复用 02 的工具）；加载骨架 `n-skeleton`。

### (3) 图1 用量趋势堆叠柱（核心交付）
- ECharts `series: bar stack='total'`，每 `provider/model` 一个系列，颜色按哈希分配 + 图例可点灭（`legend.selectedMode` 默认全开，hover 联动高亮 `emphasis.focus='series'`）。
- x 轴日期（缺天补 0，后端已补零，前端再保险一次）；y 轴 tokens（短单位 formatter）；tooltip `axis` 模式：`日期 + 各模型 in/out/cached + 当日合计 + 当日命中率`（`formatter` 自定义 HTML，chrome109 安全写法，无 `?.` 之外的超新语法——走构建降级即可）。
- 柱内占比：总量大时开 `label.show=false`（防重叠），靠 hover 看占比；提供右上切换 `输入/输出/总量` 三档（`n-radio-group`，切 series 数据不重查）。
- 双列小图：命中率折线（y 0–100%，`markLine` 平均线）+ Provider 环形（`pie radius 50-70%`，hover 显示 token 与占比）。

### (4) 明细表
- 原生 `<table>`（沿用 need10 禁令，不用 `n-data-table`），列：日期时间 / Provider / Model / 输入 / 输出 / 命中缓存 / 命中率（进度条 mini）/ 状态；分页 `n-pagination`（200/页，可选 50/100/200）；行 hover 高亮；命中率 ≥60% 绿、30–60% 黄、<30% 灰（与折线同色系）。
- 大数据：只取当前页（`UsageQueryPage`），不前端分页；导出按钮本期不做（置灰 + tooltip“二期”）。

## 4.4 性能与坑位

1. **服务端聚合优先**：趋势/堆叠/瓦片/环形全是聚合接口，单次 payload <50KB；明细页 <200 行。前端不做 `GROUP BY`。
2. **ECharts 体积**：按需引入 + `vite build` 验证增量 <400KB；抽屉关闭时 `dispose()` 图表实例（防 CefSharp 内存爬升）。
3. **抽屉懒挂载坑**：`n-drawer` 首次 `show` 前内容宽度 0 → 图表 `init` 必须在 `nextTick + activeTab==='usage'` 且容器有宽度后执行，并监听 `ResizeObserver`；Tab 切换回来 `resize()`。
4. **主题跟随**：`appearanceStore` 暗黑切换时 `setOption({backgroundColor:'transparent', textStyle...})` 重刷，不重建实例。
5. **Win7 字体**：图表字体栈 `'"Microsoft YaHei", sans-serif'`，数字 `tabular-nums`；动画超 500 点自动关（`animation:false` 阈值）。

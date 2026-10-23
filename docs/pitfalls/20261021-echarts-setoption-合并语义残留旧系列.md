# ECharts setOption 合并语义：筛选后图表残留旧系列（旧数据不消失）

- 日期：2026-10-21
- 环境：Win10 x64 / .NET6 / Node 22 / Vue 3.5.43 / ECharts 5.5.1
- 现象：
  设置与诊断中心 → 用量分析。筛选条件（预设 / Provider / Model / 来源 / 实测估算）确实发到了后端，
  表格与瓦片数字都按筛选结果更新了，但**堆叠趋势图仍显示已被筛掉的模型和它的旧数值**，
  legend 也还挂着旧模型名。切"今日"后 x 轴只剩 1 天，柱子里却还有上一次（近 7 日）的旧柱子。
- 复现步骤：
  1. 打开 设置与诊断中心 → 用量分析，等首次加载完成（近 7 日，多模型 → 堆叠图 N 个系列）。
  2. 点预设"今日"（或只保留一个 Provider）。
  3. 观察"用量趋势（按模型堆叠）"：系列数没变，被筛掉的模型仍在图上。
- 根因：
  ECharts `setOption` 默认是**合并**语义，不是"替换"：
  - `echarts/lib/core/echarts.js` `setOption(option, notMerge, lazyUpdate)`：`notMerge` 默认 false；
  - `model/Global.js` mergeOption → `mergeMode = 'normalMerge'` → `util/model.js` `mappingToExists`；
  - `util/model.js` `prepareResult` 注释直说：`Mode "normalMerge": all existings kept.`；
  - 新 option 里没有的旧系列 `newOption === null` → `Global.js` 走 `componentModel.mergeOption({}, this)`
    → **旧系列连同旧 data 原样保留并继续渲染**。
  饼图不受影响：`series[0].data` 是数组，zrender `merge`（`zrender/lib/core/util.js`）对数组走
  `clone` 整体替换；折线图只有 1 个 series 也不受影响。**只有"系列数会变"的图（堆叠/多系列）会中招。**
- 解法（最小改动）：
  1. 统一封装整体替换：`chart.setOption(option, { notMerge: true })`
     （本次落在 `src/Web.Client/src/features/settings/components/UsageAnalyticsView.vue` 的 `setOptionReplace`）。
  2. `notMerge` 会重建整个 model → 同一份数据被重复 `setOption` 会**重播动画**（筛选后图表闪一下）。
     本次同时加了幂等守卫：只有 (trend, share, metric, mask, theme) 变化才重绘；
     抽屉 `resize()` 走 `renderAll(true)` 强制重绘一次。
- 关联提交 / PR：`fix(web): 修复用量分析图表筛选后残留旧系列`、`fix(web): 修复用量分析筛选控件与后端查询口径不一致`、
  `fix(web): 修复用量分析刷新并发竞态导致图表回退旧数据`、`test(web): 新增用量分析筛选与图表刷新回归用例`
- 教训（下次如何避开）：
  - 本仓库里凡是"筛选/分页/切换口径"驱动的 ECharts 图，`setOption` 一律显式 `notMerge: true`；
    千万别依赖默认合并语义。
  - 排查"图表没更新"时，先分清是**没触发重绘**还是**重绘了但旧内容没被移除**——
    后者用 `chart.getOption()` 数 series 就能一眼看出。
  - 回归测试要用**真实 ECharts 实例**断言 `getOption().series.length`，
    纯 mock `setOption` 的断言只能证明"调用了"，证明不了"图对了"。

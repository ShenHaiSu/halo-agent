// src/Web.Client/src/components/usage/charts/useUsageCharts.ts
// P17 图表重构：主图（token 三段堆叠柱 + 命中率折线双轴）+ 模型分布饼图。
// echarts 按需引入，禁全量。口径：miss = max(0, input - cached)，后端 prompt 语义不变。
import * as echarts from 'echarts/core';
import { BarChart, LineChart, PieChart } from 'echarts/charts';
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import { formatNumber, formatZhCount } from '@/utils/format';
import { formatHitRate, type UsageByDay, type UsageByModel } from '../usageTypes';

echarts.use([
  BarChart,
  LineChart,
  PieChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  CanvasRenderer,
]);

export type EChartsInstance = ReturnType<typeof echarts.init>;
/** 展示层唯一公式（后端不动）：未命中输入 = 输入 − 命中缓存，脏数据截零。 */
export const missOf = (input: number, cached: number): number => Math.max(0, input - cached);

/** 主题文字色：运行时解析 --fg-muted（HSL 三元组），跟随浅色/深色主题；canvas 不解析 var()，必须事先求值。 */
export function themeAxisColor(el: HTMLElement | null): string {
  try {
    const v = el ? getComputedStyle(el).getPropertyValue('--fg-muted').trim() : '';
    if (v) return `hsl(${v})`; // @allow-hardcoded-color：运行时由 --fg-muted 求值，非字面量，跟随主题
  } catch {
    /* 忽略，走回落 */
  }
  return 'gray';
}

/** 折线 accent 色：运行时解析 --accent（HSL 三元组），与按钮/链接同色。 */
export function themeAccentColor(el: HTMLElement | null): string {
  try {
    const v = el ? getComputedStyle(el).getPropertyValue('--accent').trim() : '';
    if (v) return `hsl(${v})`; // @allow-hardcoded-color：运行时由 --accent 求值，非字面量，跟随主题
  } catch {
    /* 忽略，走回落 */
  }
  return '#3b82f6'; // @allow-hardcoded-color：无 DOM 时的回落（测试/jsdom），真机走 --accent 求值
}

function baseTextStyle(el: HTMLElement | null): Record<string, unknown> {
  return { color: themeAxisColor(el), fontSize: 11 };
}

/** 三段 tooltip 行：精确值 + 括号紧凑值。 */
function seg(label: string, v: number): string {
  return `${label} ${formatNumber(v)}（${formatZhCount(v)}）`;
}

/** 主图：x=byDay.day，左轴 token（中文单位），右轴命中率 0–100%。bar 三段 miss/cached/output + line 命中率。 */
export function renderTrend(
  chart: EChartsInstance,
  days: UsageByDay[],
  el: HTMLElement | null = null
): void {
  chart.setOption(
    {
      animation: false,
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (ps: Array<{ dataIndex: number }>): string => {
          const d = days[ps[0]?.dataIndex ?? -1];
          if (!d) return '';
          const miss = missOf(d.input, d.cached);
          let html = `${d.day}<br/>${seg('未命中输入', miss)} · ${seg('命中缓存', d.cached)} · ${seg('输出', d.output)}`;
          html += `<br/>命中率 ${d.input > 0 ? `${((d.cached / d.input) * 100).toFixed(1)}%` : '—'}`;
          if (d.cached > d.input) html += '（缓存＞输入，脏数据，已截零）';
          return html;
        },
      },
      legend: { data: ['未命中输入', '命中缓存', '输出', '命中率'], textStyle: baseTextStyle(el) },
      grid: { left: 48, right: 48, top: 32, bottom: 24 },
      xAxis: {
        type: 'category',
        data: days.map((d) => d.day),
        axisLabel: baseTextStyle(el),
      },
      yAxis: [
        {
          type: 'value',
          axisLabel: { ...baseTextStyle(el), formatter: (v: number) => formatZhCount(v) },
        },
        {
          type: 'value',
          min: 0,
          max: 100,
          axisLabel: { ...baseTextStyle(el), formatter: '{value}%' },
        },
      ],
      series: [
        {
          name: '未命中输入',
          type: 'bar',
          stack: 't',
          data: days.map((d) => missOf(d.input, d.cached)),
        },
        { name: '命中缓存', type: 'bar', stack: 't', data: days.map((d) => d.cached) },
        { name: '输出', type: 'bar', stack: 't', data: days.map((d) => d.output) },
        {
          name: '命中率',
          type: 'line',
          yAxisIndex: 1,
          // 可视限幅 clamp 到 [0,100]；input=0 用 null 断点。
          data: days.map((d) =>
            d.input > 0 ? Math.min(100, Math.max(0, (d.cached / d.input) * 100)) : null
          ),
          lineStyle: { color: themeAccentColor(el) },
          itemStyle: { color: themeAccentColor(el) },
        },
      ],
    },
    { notMerge: true }
  );
}

/** 分布饼图：一切片 = 一 byModel 行，切片值 = input + output（后端 total 口径）。空数据由页容器显示 HEmpty，不 init 空饼。 */
export function renderModelPie(
  chart: EChartsInstance,
  models: UsageByModel[],
  el: HTMLElement | null = null,
  opts: { resolve: (pid: string) => string } = { resolve: (pid) => pid }
): void {
  chart.setOption(
    {
      animation: false,
      tooltip: {
        trigger: 'item',
        formatter: (p: { dataIndex: number }): string => {
          const m = models[p.dataIndex];
          if (!m) return '';
          const name = `${opts.resolve(m.providerId)}/${m.modelId}`;
          const total = m.input + m.output;
          return (
            `${name}<br/>${seg('总计', total)}` +
            `<br/>${seg('未命中输入', missOf(m.input, m.cached))} · ${seg('命中缓存', m.cached)} · ${seg('输出', m.output)}` +
            `<br/>命中率 ${formatHitRate(m.input, m.hitRate)} · 请求数 ${formatNumber(m.requests)}（${formatZhCount(m.requests)}）`
          );
        },
      },
      legend: { textStyle: baseTextStyle(el) },
      series: [
        {
          type: 'pie',
          radius: ['45%', '70%'],
          label: {
            ...baseTextStyle(el),
            formatter: '{b}\n{d}%',
            overflow: 'truncate',
          },
          data: models.map((m) => ({
            name: `${opts.resolve(m.providerId)}/${m.modelId}`,
            value: m.input + m.output,
          })),
        },
      ],
    },
    { notMerge: true }
  );
}

/** 创建图表：ResizeObserver + rAF 节流 resize；返回 dispose。 */
export function createChart(el: HTMLElement): { chart: EChartsInstance; dispose: () => void } {
  const chart = echarts.init(el, null, { renderer: 'canvas' });
  let raf = 0;
  const ro = new ResizeObserver(() => {
    cancelAnimationFrame(raf);
    raf = requestAnimationFrame(() => chart.resize());
  });
  ro.observe(el);
  return {
    chart,
    dispose: () => {
      cancelAnimationFrame(raf);
      ro.disconnect();
      chart.dispose();
    },
  };
}

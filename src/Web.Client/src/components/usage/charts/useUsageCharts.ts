// src/Web.Client/src/components/usage/charts/useUsageCharts.ts
// P16 用量三图：趋势堆叠柱 + 分布横向条 + 命中率独立折线（echarts 按需引入，禁全量）。
import * as echarts from 'echarts/core';
import { LineChart, BarChart } from 'echarts/charts';
import { GridComponent, TooltipComponent, LegendComponent } from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import type { UsageByDay, UsageByModel } from '../usageTypes';

echarts.use([LineChart, BarChart, GridComponent, TooltipComponent, LegendComponent, CanvasRenderer]);

export type EChartsInstance = ReturnType<typeof echarts.init>;

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

function baseTextStyle(el: HTMLElement | null): Record<string, unknown> {
  return { color: themeAxisColor(el), fontSize: 11 };
}

/** 趋势：x=byDay.day，input/cached/output 堆叠柱（空桶 0 不断线）。 */
export function renderTrend(
  chart: EChartsInstance,
  days: UsageByDay[],
  el: HTMLElement | null = null,
): void {
  chart.setOption(
    {
      animation: false,
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { data: ['输入', '缓存', '输出'], textStyle: baseTextStyle(el) },
      grid: { left: 48, right: 12, top: 32, bottom: 24 },
      xAxis: {
        type: 'category',
        data: days.map((d) => d.day),
        axisLabel: baseTextStyle(el),
      },
      yAxis: { type: 'value', axisLabel: baseTextStyle(el) },
      series: [
        { name: '输入', type: 'bar', stack: 't', data: days.map((d) => d.input) },
        { name: '缓存', type: 'bar', stack: 't', data: days.map((d) => d.cached) },
        { name: '输出', type: 'bar', stack: 't', data: days.map((d) => d.output) },
      ],
    },
    { notMerge: true },
  );
}

/** 分布：横向条形，y=provider/model，tooltip 含命中率。 */
export function renderDist(
  chart: EChartsInstance,
  models: UsageByModel[],
  el: HTMLElement | null = null,
): void {
  const names = models.map((m) => `${m.providerId}/${m.modelId}`);
  chart.setOption(
    {
      animation: false,
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (ps: Array<{ dataIndex: number; seriesName: string; value: number }>): string => {
          const m = models[ps[0]?.dataIndex ?? -1];
          if (!m) return '';
          const hit = m.input > 0 ? (m.hitRate * 100).toFixed(1) : '—';
          return `${m.providerId}/${m.modelId}<br/>输入 ${m.input} · 缓存 ${m.cached} · 输出 ${m.output}<br/>命中率 ${m.input > 0 ? `${hit}%` : '—'}`;
        },
      },
      legend: { data: ['输入', '输出'], textStyle: baseTextStyle(el) },
      grid: { left: 120, right: 12, top: 32, bottom: 24 },
      xAxis: { type: 'value', axisLabel: baseTextStyle(el) },
      yAxis: { type: 'category', data: names, axisLabel: baseTextStyle(el) },
      series: [
        { name: '输入', type: 'bar', data: models.map((m) => m.input) },
        { name: '输出', type: 'bar', data: models.map((m) => m.output) },
      ],
    },
    { notMerge: true },
  );
}

/** 命中率：独立折线，0~100%。 */
export function renderHitRate(
  chart: EChartsInstance,
  days: UsageByDay[],
  el: HTMLElement | null = null,
): void {
  chart.setOption(
    {
      animation: false,
      tooltip: {
        trigger: 'axis',
        formatter: (ps: Array<{ dataIndex: number; value: number }>): string => {
          const i = ps[0]?.dataIndex ?? -1;
          const d = days[i];
          if (!d) return '';
          const hit = d.input > 0 ? ((d.cached / d.input) * 100).toFixed(1) : '—';
          return `${d.day}<br/>命中率 ${d.input > 0 ? `${hit}%` : '—'}`;
        },
      },
      grid: { left: 48, right: 12, top: 32, bottom: 24 },
      xAxis: { type: 'category', data: days.map((d) => d.day), axisLabel: baseTextStyle(el) },
      yAxis: {
        type: 'value',
        min: 0,
        max: 100,
        axisLabel: { ...baseTextStyle(el), formatter: '{value}%' },
      },
      series: [
        {
          name: '命中率',
          type: 'line',
          data: days.map((d) => (d.input > 0 ? Number(((d.cached / d.input) * 100).toFixed(1)) : 0)),
        },
      ],
    },
    { notMerge: true },
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

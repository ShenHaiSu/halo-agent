// src/Web.Client/src/components/settings/providerFormat.ts
// P27 Provider/Model 双区块版的纯展示/纯计算逻辑（无 Vue 依赖，可单测）。
// 依据：docs/P27-FrontEnd-Enhance/定稿-Provider-Model双区块版.md §4–§9。

import type { ProviderEdit, ProvidersTree } from './providerEditing';

/** 密度偏好：紧凑 28px / 舒适 32px，唯一高度变量 ——ih。 */
export type Density = 'dense' | 'roomy';
export const DENSITY_STORAGE_KEY = 'halo.settings.density';

export function normalizeDensity(v: unknown): Density {
  return v === 'roomy' ? 'roomy' : 'dense';
}

// ============================================================
// 单位格式化（§6.2）：上下文 M/K，输出统一 K
// ============================================================

/** 上下文：≥1M → 1M / 1.5M；≥1000 → 128K；null → —。 */
export function formatCtxLabel(n: number | null | undefined): string {
  if (n === null || n === undefined || !Number.isFinite(n)) return '—';
  if (n >= 1000000) {
    const m = n / 1000000;
    return `${Number.isInteger(m) ? m : m.toFixed(1)}M`;
  }
  if (n >= 1000) return `${Math.round(n / 1000)}K`;
  return String(n);
}

/** 输出上限：≥1000 → 20K / 1.5K；<1000 原数；null → —。 */
export function formatOutLabel(n: number | null | undefined): string {
  if (n === null || n === undefined || !Number.isFinite(n)) return '—';
  if (n >= 1000) {
    const k = n / 1000;
    return `${Number.isInteger(k) ? k : k.toFixed(1)}K`;
  }
  return String(n);
}

/** 按 id/别称实时过滤（不区分大小写，空串=不过滤）。 */
export function filterProviders(
  providers: readonly ProviderEdit[],
  query: string
): ProviderEdit[] {
  const q = query.trim().toLowerCase();
  if (!q) return providers.slice();
  return providers.filter(
    (p) => p.id.toLowerCase().includes(q) || p.displayName.toLowerCase().includes(q)
  );
}

/** 按 id/别称实时过滤模型。 */
export function filterModels(
  models: readonly { id: string; displayName: string }[],
  query: string
): number[] {
  const q = query.trim().toLowerCase();
  const out: number[] = [];
  models.forEach((m, i) => {
    if (!q || m.id.toLowerCase().includes(q) || m.displayName.toLowerCase().includes(q)) out.push(i);
  });
  return out;
}

/** 该 Provider 是否为使用中（activeProvider）。 */
export function isActiveProvider(tree: ProvidersTree, pid: string): boolean {
  return !!pid && tree.activeProvider === pid;
}

/** 该模型是否为使用中的模型。 */
export function isActiveModel(tree: ProvidersTree, pid: string, mid: string): boolean {
  return !!pid && tree.activeProvider === pid && tree.activeModel === mid;
}

/** 空 Provider 不允许设为使用中（与 validateProvidersTree 的 active 规则同口径）。 */
export function canSetActive(p: ProviderEdit | undefined | null): boolean {
  return !!p && p.models.length > 0;
}

/** 请求头摘要：`无` 或 `X-Title · X-Env`（空 key 过滤，与保存口径一致）。 */
export function headersSummary(headers: readonly { key: string; value: string }[]): string {
  const keys = headers.map((h) => h.key.trim()).filter((k) => k !== '');
  return keys.length === 0 ? '无' : keys.join(' · ');
}

/** 有效请求头数量（空 key 不计，与保存口径一致）。 */
export function headersCount(headers: readonly { key: string; value: string }[]): number {
  return headers.filter((h) => h.key.trim() !== '').length;
}

/** 模型 id 是否已在列表（精确 Ordinal 匹配）；exclude 用于编辑自身时排除自己。 */
export function hasModelId(
  models: readonly { id: string }[],
  id: string,
  excludeIndex = -1
): boolean {
  return models.some((m, i) => i !== excludeIndex && m.id === id);
}

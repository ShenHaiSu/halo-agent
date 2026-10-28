// src/Web.Client/src/components/settings/modelProviders.ts
// 模型页服务商数据的归一化与切换语义（纯函数，可单测）。
//
// 背景：settings.get 的 providers[] 契约是 {name, model(单数), baseUrl}
//（见 contract/agent.schema.json 与 SettingsContracts.ProviderEntry），
//历史前端曾按 {name, baseUrl, models[](复数)} 消费，导致切换服务商时
//裸索引 p.models[0] 抛 TypeError。归一化层同时兼容两种形状，杜绝复发。

export interface ProviderInfo {
  name: string;
  baseUrl: string;
  /** 真实契约字段：该服务商的默认模型（单数）。 */
  model?: string;
  /** 兼容字段：旧引擎/旧缓存可能下发 models 数组，有则优先取 [0]。 */
  models?: string[];
}

/** 取某服务商的默认模型：models[0] 优先，回落 model，缺失为空串（永不抛）。 */
export function defaultModelOf(p: ProviderInfo | undefined | null): string {
  if (!p || typeof p !== 'object') return '';
  const fromList = Array.isArray(p.models) ? p.models[0] : undefined;
  if (typeof fromList === 'string' && fromList.trim()) return fromList;
  if (typeof p.model === 'string' && p.model.trim()) return p.model;
  return '';
}

/** 把 settings.get 下发的 providers 原始值归一化为 ProviderInfo[]（永不抛）。 */
export function normalizeProviders(raw: unknown): ProviderInfo[] {
  if (!Array.isArray(raw)) return [];
  const out: ProviderInfo[] = [];
  for (const item of raw) {
    if (!item || typeof item !== 'object') continue;
    const rec = item as Record<string, unknown>;
    const name = typeof rec['name'] === 'string' ? rec['name'].trim() : '';
    if (!name) continue;
    const baseUrl = typeof rec['baseUrl'] === 'string' ? rec['baseUrl'] : '';
    const model = typeof rec['model'] === 'string' ? rec['model'] : undefined;
    const models = Array.isArray(rec['models'])
      ? (rec['models'] as unknown[]).filter((x): x is string => typeof x === 'string')
      : undefined;
    out.push({ name, baseUrl, model, models });
  }
  return out;
}

export interface ProviderSwitchResult {
  baseUrl: string;
  /** 要写入表单的 model；为 '' 表示保持原值（无可用默认值）。 */
  model: string;
  /** model 是否发生了跟随变化（用于调用方判断，可选）。 */
  followed: boolean;
}

/**
 * 服务商切换语义（推荐方案）：“跟随但尊重手填”。
 * - 当前 model 为空 → 跟随新默认值；
 * - 当前 model 正好等于旧服务商默认值 → 视为“没手改过”，跟随新默认值；
 * - 当前为用户手填（与旧默认不同）→ 保留手填，不覆盖；
 * - 新服务商无默认值 → 保持原值（只换 baseUrl）。
 * 永不抛；找不到新服务商返回 null（调用方仅标 dirty）。
 */
export function resolveProviderSwitch(
  providers: ProviderInfo[],
  prevName: string,
  nextName: string,
  curModel: string,
): ProviderSwitchResult | null {
  const next = providers.find((x) => x.name === nextName);
  if (!next) return null;
  const prev = providers.find((x) => x.name === prevName);
  const nextDef = defaultModelOf(next);
  const cur = (curModel ?? '').trim();
  const prevDef = prev ? defaultModelOf(prev) : '';
  const shouldFollow = !cur || (prevDef !== '' && cur === prevDef);
  return {
    baseUrl: next.baseUrl ?? '',
    model: shouldFollow && nextDef !== '' ? nextDef : cur,
    followed: shouldFollow && nextDef !== '' && cur !== nextDef,
  };
}

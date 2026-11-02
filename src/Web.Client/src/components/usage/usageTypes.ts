// src/Web.Client/src/components/usage/usageTypes.ts
// P16 用量前端类型（与 contract/agent.schema.json 的 usage.* 对齐）。

export interface UsageRow {
  tsMs: number;
  sessionId: string;
  turnId: string;
  providerId: string;
  providerDisplay?: string;
  modelId: string;
  modelDisplay?: string;
  protocol?: string;
  prompt: number;
  cached: number;
  output: number;
  total: number;
  hitRate: number;
  truncated?: number;
}

export interface UsageByDay {
  day: string;
  input: number;
  cached: number;
  output: number;
}

export interface UsageByModel {
  providerId: string;
  providerDisplay?: string;
  modelId: string;
  modelDisplay?: string;
  input: number;
  cached: number;
  output: number;
  /** 一行=一回合，非网络请求次数 */
  requests: number;
  hitRate: number;
}

export interface UsageSummary {
  input: number;
  cached: number;
  output: number;
  total: number;
  /** 一行=一回合，非网络请求次数 */
  requests: number;
  hitRate: number;
  byDay: UsageByDay[];
  byModel: UsageByModel[];
  degraded?: boolean;
}

export interface UsageQueryResult {
  items: UsageRow[];
  total: number;
  degraded?: boolean;
}

/** 全量 provider/model 词典（首次无过滤 summary 构建并缓存）。 */
export interface ProviderDict {
  [providerId: string]: {
    display: string;
    models: Array<{ id: string; display: string }>;
  };
}

export function buildProviderDict(byModel: UsageByModel[]): ProviderDict {
  const dict: ProviderDict = {};
  for (const m of byModel) {
    let e = dict[m.providerId];
    if (!e) {
      e = { display: m.providerDisplay || m.providerId, models: [] };
      dict[m.providerId] = e;
    }
    if (!e.models.some((x) => x.id === m.modelId)) {
      e.models.push({ id: m.modelId, display: m.modelDisplay || m.modelId });
    }
  }
  return dict;
}

/** 命中率展示：prompt=0 显示 —，否则 x.x%。 */
export function formatHitRate(prompt: number, hitRate: number): string {
  if (prompt <= 0) return '—';
  return `${(hitRate * 100).toFixed(1)}%`;
}

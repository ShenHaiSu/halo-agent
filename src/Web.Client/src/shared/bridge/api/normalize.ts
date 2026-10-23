/** need21-F02: 桥接归一化（旧 client.ts normalize* + payload 归一原样迁移，行为不变）。 */
import type { BridgeResponse } from '../protocol/types';
import type {
  GlobalNetworkSettings,
  GlobalProxyMode,
  LlmProtocolType,
  ProviderProxyMode,
  ProvidersConfigDocument,
} from '../protocol/types';
import { normalizeVisionFlag } from '../protocol/types';
import type { AgentConfigDocument, PromptConfigDocument } from '../protocol/types';

export function normalizeGlobalProxyMode(raw: unknown): GlobalProxyMode {
  if (raw === 1 || String(raw).toLowerCase() === 'custom') return 'Custom';
  if (raw === 2 || String(raw).toLowerCase() === 'none') return 'None';
  return 'System';
}

export function normalizeProviderProxyMode(raw: unknown): ProviderProxyMode {
  if (raw === 1 || String(raw).toLowerCase() === 'none') return 'None';
  if (raw === 2 || String(raw).toLowerCase() === 'custom') return 'Custom';
  return 'Inherit';
}

export function normalizeLlmProtocol(raw: unknown): LlmProtocolType {
  if (raw === 1 || String(raw).toLowerCase() === 'resp' || String(raw).toLowerCase() === 'responses') {
    return 'resp';
  }
  return 'chat';
}

export function normalizeNetworkSettingsDto(res: BridgeResponse<GlobalNetworkSettings>): BridgeResponse<GlobalNetworkSettings> {
  if (res.data) {
    res.data.mode = normalizeGlobalProxyMode(res.data.mode);
    if (!res.data.customProxy) {
      res.data.customProxy = {
        scheme: 'http',
        host: '127.0.0.1',
        port: 7890,
        requiresAuth: false,
        bypassList: 'localhost;127.*;10.*;192.168.*',
        bypassOnLocal: true,
      };
    }
  }
  return res;
}

export function normalizeProvidersDocDto(res: BridgeResponse<ProvidersConfigDocument>): BridgeResponse<ProvidersConfigDocument> {
  if (res.data && Array.isArray(res.data.providers)) {
    res.data.providers = res.data.providers.map((p) => ({
      ...p,
      proxyMode: normalizeProviderProxyMode(p.proxyMode),
      customHeaders: Array.isArray(p.customHeaders) ? p.customHeaders : [],
      models: Array.isArray(p.models)
        ? p.models.map((m) => {
            const mm = { ...m, protocol: normalizeLlmProtocol(m.protocol) };
            normalizeVisionFlag(mm as unknown as Record<string, unknown>);
            return mm;
          })
        : [],
    }));
  }
  return res;
}

export function normalizeVersionedBaseUrlClient(baseUrl: string): string {
  const trimmed = (baseUrl || 'https://api.openai.com/v1').trim();
  const withScheme = trimmed.includes('://') ? trimmed : `https://${trimmed}`;
  if (withScheme.endsWith('#')) {
    return withScheme.slice(0, -1).replace(/\/+$/, '');
  }
  const clean = withScheme.replace(/\/+$/, '');
  if (clean.toLowerCase().endsWith('/v1')) {
    return clean;
  }
  return `${clean}/v1`;
}

function toCamelKey(key: string): string {
  if (!key) return key;
  return key.charAt(0).toLowerCase() + key.slice(1);
}

export function normalizeBridgePayload<T>(input: any, preserveDictionaryKeys = false): T {
  // CefSharp 会把 C# DateTime 转成 JS Date（typeof object）。旧实现直接 Object.entries(Date)
  // → {}，导致 updatedAtUtc 回传变成 {"updatedAtUtc":{}}，后端 STJ 报 Unsupported token StartObject。
  // 此处优先还原为 ISO 字符串，保证往返契约始终是 string。
  if (input instanceof Date) {
    return (Number.isNaN(input.getTime()) ? new Date().toISOString() : input.toISOString()) as unknown as T;
  }
  if (input === null || input === undefined || typeof input !== 'object') {
    return input as T;
  }
  if (Array.isArray(input)) {
    return input.map((item) => normalizeBridgePayload(item, false)) as unknown as T;
  }
  const result: Record<string, any> = {};
  for (const [key, value] of Object.entries(input)) {
    const outKey = preserveDictionaryKeys ? key : toCamelKey(key);
    const isStringDictionary = outKey === 'resolvedHeaders';
    result[outKey] = normalizeBridgePayload(value, isStringDictionary);
  }
  return result as T;
}

/** need06：保存前把 updatedAtUtc 归一为 ISO 字符串。CefSharp 回传的 Date / 已损坏的 {} / 数字时间戳全部在此消化，后端只收干净 string。 */
export function sanitizePromptConfigForSave(config: PromptConfigDocument | string): string {
  if (typeof config === 'string') {
    return config;
  }
  const raw = config as unknown as Record<string, unknown>;
  const copy: Record<string, unknown> = { ...raw };
  copy.updatedAtUtc = toIsoDateString(raw.updatedAtUtc);
  return JSON.stringify(copy);
}
/** need08：同 need06 归一逻辑，智能体行为配置保存前把 updatedAtUtc 归一为 ISO 字符串。 */
export function sanitizeAgentConfigForSave(config: AgentConfigDocument | string): string {
  if (typeof config === 'string') {
    return config;
  }
  const raw = config as unknown as Record<string, unknown>;
  const copy: Record<string, unknown> = { ...raw };
  copy.updatedAtUtc = toIsoDateString(raw.updatedAtUtc);
  return JSON.stringify(copy);
}

function toIsoDateString(v: unknown): string {
  if (typeof v === 'string' && v.trim()) {
    const t = Date.parse(v);
    // 能解析就原样保留（后端 STJ 只认 ISO）；解析不了就用现在时间兜底，避免把垃圾发给后端。
    return Number.isNaN(t) ? new Date().toISOString() : v;
  }
  if (v instanceof Date && !Number.isNaN(v.getTime())) {
    return v.toISOString();
  }
  if (typeof v === 'number' && Number.isFinite(v)) {
    // 10 位按秒、13 位按毫秒换算成 ISO
    const ms = v >= 100_000_000_000 ? v : v * 1000;
    const d = new Date(ms);
    return Number.isNaN(d.getTime()) ? new Date().toISOString() : d.toISOString();
  }
  // {} / null / undefined / 其他：直接用当前时间，后端 Save 会再归一为 UtcNow
  return new Date().toISOString();
}

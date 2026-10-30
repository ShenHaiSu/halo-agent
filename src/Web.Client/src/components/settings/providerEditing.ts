// src/Web.Client/src/components/settings/providerEditing.ts
// P10 Provider 1—N Model 全量树纯函数（归一化/校验/dirty），可单测。
import { isSupportedModelProtocol } from './validators';

export interface ModelEdit {
  id: string;
  displayName: string;
  protocol: string;
  contextWindow: number | null;
  maxTokens: number | null;
  acceptsImages: boolean;
  includeReasoningHistory: boolean;
  expanded?: boolean;
}

export interface ProviderEdit {
  id: string;
  displayName: string;
  baseUrl: string;
  apiKeySet: boolean;
  newKey: string;
  clearKey: boolean;
  headersEnabled: boolean;
  headers: Array<{ key: string; value: string }>;
  useProxy: boolean;
  models: ModelEdit[];
  expanded?: boolean;
}

export interface ProvidersTree {
  activeProvider: string;
  activeModel: string;
  providers: ProviderEdit[];
}

export const PROVIDER_ID_RE = /^[a-z0-9][a-z0-9-_]{0,63}$/;

export function normalizeProvidersTree(raw: unknown): ProvidersTree {
  const r = (raw ?? {}) as Record<string, unknown>;
  const providers: ProviderEdit[] = [];
  const list = r['providers'];
  if (Array.isArray(list)) {
    for (const item of list) {
      const o = (item ?? {}) as Record<string, unknown>;
      const id = typeof o['id'] === 'string' ? (o['id'] as string) : '';
      if (!id) continue;
      const modelsRaw = Array.isArray(o['models']) ? (o['models'] as unknown[]) : [];
      const models: ModelEdit[] = [];
      for (const m of modelsRaw) {
        const mo = (m ?? {}) as Record<string, unknown>;
        models.push({
          id: typeof mo['id'] === 'string' ? (mo['id'] as string) : '',
          displayName: typeof mo['displayName'] === 'string' ? (mo['displayName'] as string) : '',
          protocol: typeof mo['protocol'] === 'string' ? (mo['protocol'] as string) : 'chat',
          contextWindow: typeof mo['contextWindow'] === 'number' ? (mo['contextWindow'] as number) : null,
          maxTokens: typeof mo['maxTokens'] === 'number' ? (mo['maxTokens'] as number) : null,
          acceptsImages: mo['acceptsImages'] === true,
          includeReasoningHistory: mo['includeReasoningHistory'] !== false,
        });
      }
      const headersRaw = Array.isArray(o['headers']) ? (o['headers'] as unknown[]) : [];
      providers.push({
        id,
        displayName: typeof o['displayName'] === 'string' ? (o['displayName'] as string) : '',
        baseUrl: typeof o['baseUrl'] === 'string' ? (o['baseUrl'] as string) : '',
        apiKeySet: o['apiKeySet'] === true,
        newKey: '',
        clearKey: false,
        headersEnabled: o['headersEnabled'] !== false,
        headers: headersRaw.map((h) => {
          const ho = (h ?? {}) as Record<string, unknown>;
          const key = typeof ho['key'] === 'string' ? ho['key'] : typeof ho['name'] === 'string' ? (ho['name'] as string) : '';
          return { key, value: typeof ho['value'] === 'string' ? (ho['value'] as string) : '' };
        }),
        useProxy: o['useProxy'] !== false,
        models,
      });
    }
  }
  return {
    activeProvider: typeof r['activeProvider'] === 'string' ? (r['activeProvider'] as string) : '',
    activeModel: typeof r['activeModel'] === 'string' ? (r['activeModel'] as string) : '',
    providers,
  };
}

export interface TreeError {
  provider: string;
  model?: string;
  field: string;
  message: string;
}

export function validateProvidersTree(t: ProvidersTree): TreeError[] {
  const errs: TreeError[] = [];
  const seenP = new Set<string>();
  for (const p of t.providers) {
    if (!PROVIDER_ID_RE.test(p.id)) errs.push({ provider: p.id, field: 'id', message: 'provider.id 非法' });
    if (seenP.has(p.id)) errs.push({ provider: p.id, field: 'id', message: 'provider.id 重复' });
    seenP.add(p.id);
    if (p.displayName.length > 64) errs.push({ provider: p.id, field: 'displayName', message: '别称不超过 64 字' });
    if (!p.baseUrl.trim()) errs.push({ provider: p.id, field: 'baseUrl', message: 'baseUrl 必填' });
    else {
      try {
        const u = new URL(p.baseUrl.trim());
        if (u.protocol !== 'http:' && u.protocol !== 'https:') errs.push({ provider: p.id, field: 'baseUrl', message: 'baseUrl 必须是 http(s)' });
      } catch {
        errs.push({ provider: p.id, field: 'baseUrl', message: 'baseUrl 非法' });
      }
    }
    const seenM = new Set<string>();
    for (const m of p.models) {
      if (!m.id.trim()) errs.push({ provider: p.id, model: m.id, field: 'id', message: 'model.id 非空' });
      if (seenM.has(m.id)) errs.push({ provider: p.id, model: m.id, field: 'id', message: 'model.id 重复' });
      seenM.add(m.id);
      if (!isSupportedModelProtocol(m.protocol ?? 'chat')) errs.push({ provider: p.id, model: m.id, field: 'protocol', message: '仅支持 chat/responses 协议' });
      if (m.contextWindow !== null && !(Number.isInteger(m.contextWindow) && m.contextWindow >= 1000 && m.contextWindow <= 2000000))
        errs.push({ provider: p.id, model: m.id, field: 'contextWindow', message: 'contextWindow 须为 null 或 1000~2000000' });
      if (m.maxTokens !== null && !(Number.isInteger(m.maxTokens) && m.maxTokens >= 1 && m.maxTokens <= 128000))
        errs.push({ provider: p.id, model: m.id, field: 'maxTokens', message: 'maxTokens 须为 null 或 1~128000' });
    }
  }
  const ap = (t.activeProvider ?? '').trim();
  const am = (t.activeModel ?? '').trim();
  if (!ap && !am) return errs;
  if (!ap || !am) {
    errs.push({ provider: ap, model: am, field: 'active', message: 'active 必须同时存在或同时为空' });
    return errs;
  }
  const p = t.providers.find((x) => x.id === ap);
  if (!p) errs.push({ provider: ap, model: am, field: 'active', message: 'active provider 不存在' });
  else if (p.models.length === 0) errs.push({ provider: ap, model: am, field: 'active', message: 'active 不可指向空 provider' });
  else if (!p.models.some((m) => m.id === am)) errs.push({ provider: ap, model: am, field: 'active', message: 'active model 不存在' });
  return errs;
}

export function diffActive(a: ProvidersTree, b: ProvidersTree): boolean {
  return a.activeProvider !== b.activeProvider || a.activeModel !== b.activeModel;
}

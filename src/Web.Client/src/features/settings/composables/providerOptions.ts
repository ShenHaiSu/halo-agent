import type { ModelConfig, ProviderConfig } from '@shared/bridge/hostBridge';

export const visionDetailOptions = [
  { label: 'high（高清·默认）', value: 'high' },
  { label: 'auto（自动）', value: 'auto' },
  { label: 'low（省流）', value: 'low' },
];
export const providerProxyModeOptions = [
  { label: '继承全局代理设置 (Inherit)', value: 'Inherit' },
  { label: '不使用代理 / 强制直连 (None)', value: 'None' },
  { label: '使用独立自定义代理 (Custom)', value: 'Custom' },
];
export const proxySchemeOptions = [
  { label: 'HTTP', value: 'http' },
  { label: 'HTTPS', value: 'https' },
  { label: 'SOCKS5', value: 'socks5' },
];
export const protocolOptions = [
  { label: 'chat (/v1/chat/completions)', value: 'chat' },
  { label: 'resp (/v1/responses)', value: 'resp' },
];
export const presetVariables = [
  { token: '${session-hash}', desc: '当前会话 16 位确定性 SHA-256 哈希' },
  { token: '${session-id}', desc: '当前会话原始 ID' },
  { token: '${workspace-name}', desc: '当前工作区文件夹名' },
  { token: '${workspace-hash}', desc: '当前工作区路径 16 位确定性哈希' },
  { token: '${model-id}', desc: '当前调用的模型 ID' },
  { token: '${provider-name}', desc: '当前服务商名称' },
  { token: '${request-id}', desc: '单次 HTTP 请求唯一 UUID' },
  { token: '${timestamp}', desc: '当前 UTC Unix 秒级时间戳' },
];
export const CONTEXT_PRESETS = [
  { label: '128k', value: 128000 },
  { label: '256k', value: 256000 },
  { label: '512k', value: 512000 },
  { label: '1M', value: 1000000 },
];
export const OUTPUT_PRESETS = [
  { label: '4k', value: 4096 },
  { label: '8k', value: 8192 },
  { label: '16k', value: 16384 },
  { label: '50k', value: 51200 },
  { label: '64k', value: 65536 },
  { label: '128k', value: 131072 },
];
export interface VisionFieldView {
  supportsVision?: boolean;
  maxImagesPerTurn?: number | null;
  visionDetail?: string | null;
  includeReasoningInHistory?: boolean;
}
export function visionOf(mdl: ModelConfig): VisionFieldView {
  return mdl as unknown as VisionFieldView;
}
export function normalizeVisionFields(mdl: ModelConfig): void {
  const m = mdl as unknown as Record<string, unknown>;
  if (typeof m['supportsVision'] !== 'boolean') m['supportsVision'] = false;
  if (typeof m['includeReasoningInHistory'] !== 'boolean') m['includeReasoningInHistory'] = true;
  if (!Number.isFinite(m['maxImagesPerTurn'] as number)) m['maxImagesPerTurn'] = 4;
  m['maxImagesPerTurn'] = Math.trunc(m['maxImagesPerTurn'] as number);
  if (m['visionDetail'] == null || m['visionDetail'] === '') m['visionDetail'] = 'high';
  if (!['auto', 'low', 'high'].includes(String(m['visionDetail']))) m['visionDetail'] = 'high';
}
export function normalizeAllVisionFields(provider: ProviderConfig | null): void {
  if (!provider) return;
  for (const mdl of provider.models) normalizeVisionFields(mdl);
}
export type VisionField = 'supportsVision' | 'maxImagesPerTurn' | 'visionDetail' | 'includeReasoningInHistory';
export function setVisionField(mdl: ModelConfig, field: VisionField, value: unknown): void {
  const m = mdl as unknown as Record<string, unknown>;
  if (field === 'supportsVision') m['supportsVision'] = value === true;
  else if (field === 'includeReasoningInHistory') m['includeReasoningInHistory'] = value !== false;
  else if (field === 'maxImagesPerTurn') {
    const n = typeof value === 'number' ? value : Number(value);
    m['maxImagesPerTurn'] = !Number.isFinite(n) ? 4 : Math.min(16, Math.max(0, Math.trunc(n)));
  } else {
    const s = String(value ?? 'high');
    m['visionDetail'] = ['auto', 'low', 'high'].includes(s) ? s : 'high';
  }
  normalizeVisionFields(mdl);
}
export function clampVisionForSave(mdl: ModelConfig): void {
  const m = mdl as unknown as Record<string, unknown>;
  if (typeof m['supportsVision'] !== 'boolean') m['supportsVision'] = false;
  if (typeof m['includeReasoningInHistory'] !== 'boolean') m['includeReasoningInHistory'] = true;
  let n = Number(m['maxImagesPerTurn']);
  if (!Number.isFinite(n)) n = 4;
  m['maxImagesPerTurn'] = Math.min(16, Math.max(0, Math.trunc(n)));
  if (!['auto', 'low', 'high'].includes(String(m['visionDetail'] ?? 'high'))) m['visionDetail'] = 'high';
}
export function formatK(v: number | null | undefined): string {
  if (v == null || Number.isNaN(v)) return '-';
  if (v >= 1048576) {
    const m = v / 1048576;
    return `${Number.isInteger(m) ? m : m.toFixed(1)}M`;
  }
  return `${Math.round(v / 1024)}K`;
}
export function versionPrefixOf(baseUrl: string): string {
  const raw = (baseUrl || 'https://api.openai.com/v1').trim();
  const withScheme = raw.includes('://') ? raw : `https://${raw}`;
  if (withScheme.endsWith('#')) return withScheme.slice(0, -1).replace(/\/+$/, '');
  const clean = withScheme.replace(/\/+$/, '');
  return clean.toLowerCase().endsWith('/v1') ? clean : `${clean}/v1`;
}

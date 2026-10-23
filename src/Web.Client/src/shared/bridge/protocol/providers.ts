/** need21-F02: 桥协议类型（单源：旧 src/bridge/types.ts 按域拆分，行为不变）。 */
// =========================================================================
// Stage 02: 全局网络代理中枢 (ProxyHub) 与多服务商配置引擎模型定义
// =========================================================================

export type GlobalProxyMode = 'System' | 'Custom' | 'None';
export type ProviderProxyMode = 'Inherit' | 'None' | 'Custom';
export type LlmProtocolType = 'chat' | 'resp';

export interface ManualProxyConfig {
  scheme: string;
  host: string;
  port: number;
  requiresAuth: boolean;
  username?: string | null;
  passwordEncrypted?: string | null;
  password?: string | null;
  passwordMasked?: string | null;
  bypassList: string;
  bypassOnLocal: boolean;
}

export interface GlobalNetworkSettings {
  mode: GlobalProxyMode;
  customProxy: ManualProxyConfig;
  defaultConnectTimeoutSeconds: number;
  defaultRequestTimeoutSeconds: number;
}

export interface SystemProxySnapshot {
  isEnabled: boolean;
  proxyServerRaw?: string | null;
  effectiveHttpProxyUri?: string | null;
  autoConfigUrl?: string | null;
  bypassOverride?: string | null;
  inspectedAtUtc: string;
}

export interface ProxyConnectivityTestResult {
  success: boolean;
  statusCode?: number | null;
  elapsedMs: number;
  effectiveProxyUsed: string;
  targetUrl: string;
  errorMessage?: string | null;
  diagnosticCategory: string;
}

export interface ModelConfig {
  id: string;
  displayName: string;
  protocol: LlmProtocolType;
  maxContextTokens: number;
  maxOutputTokens: number;
  temperature?: number | null;
  enabled: boolean;
  supportsVision?: boolean;
  maxImagesPerTurn?: number;
  visionDetail?: string | null;
  includeReasoningInHistory?: boolean;
}

// need09-05：聊天图片引用（缺字段/null/空 = 纯文本）
export interface ChatImageRef {
  imageId: string;
  fileName: string;
  mime: string;
  relPath: string;
  width: number;
  height: number;
  sizeBytes: number;
  sha16: string;
  lost?: boolean;
}

// need09-03：模型多模态能力位归一化（缺省 false/4/high；maxImagesPerTurn 0=默认4；显式 auto 保留，缺失/非法回 high）
export function normalizeVisionFlag(m: Record<string, unknown>): void {
  const r = m as unknown as {
    supportsVision?: unknown;
    maxImagesPerTurn?: unknown;
    visionDetail?: unknown;
    includeReasoningInHistory?: unknown;
  };
  if (typeof r.supportsVision !== 'boolean') r.supportsVision = false;
  if (!Number.isFinite(r.maxImagesPerTurn as number)) r.maxImagesPerTurn = 4;
  r.maxImagesPerTurn = Math.trunc(r.maxImagesPerTurn as number);
  if (r.maxImagesPerTurn === 0) r.maxImagesPerTurn = 4;
  if (r.visionDetail == null || r.visionDetail === '') r.visionDetail = 'high';
  if (!['auto', 'low', 'high'].includes(String(r.visionDetail))) r.visionDetail = 'high';
  // 历史思考链默认随上下文发出（老 providers.json 缺字段回退 true）
  if (typeof r.includeReasoningInHistory !== 'boolean') r.includeReasoningInHistory = true;
}

export interface CustomHeaderEntry {
  key: string;
  valueTemplate: string;
  enabled: boolean;
  isSecret: boolean;
}

export interface ProviderConfig {
  id: string;
  name: string;
  enabled: boolean;
  baseUrl: string;
  apiKeyEncrypted: string;
  apiKey?: string | null;
  apiKeyMasked?: string | null;
  hasApiKey?: boolean | null;
  proxyMode: ProviderProxyMode;
  customProxy?: ManualProxyConfig | null;
  enableCustomHeaders: boolean;
  customHeaders: CustomHeaderEntry[];
  models: ModelConfig[];
}

export interface ProvidersConfigDocument {
  activeProviderId: string;
  activeModelId: string;
  providers: ProviderConfig[];
}

export interface ProviderEndpointPreviewDto {
  baseUrl: string;
  normalizedVersionPrefix: string;
  chatEndpointUrl: string;
  respEndpointUrl: string;
  modelsEndpointUrl: string;
  effectiveProxySummary: string;
  sessionId: string;
  sessionHash: string;
  workspaceName: string;
  workspaceHash: string;
  resolvedHeaders: Record<string, string>;
}

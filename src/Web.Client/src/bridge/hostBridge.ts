export interface BridgeResponse<T> {
  ok: boolean;
  code?: string;
  message?: string;
  data?: T;
}

export interface SystemRuntimeInfo {
  osDescription: string;
  osVersionString: string;
  isWin7: boolean;
  architecture: string;
  processArchitecture: string;
  dotnetVersion: string;
  cefSharpVersion: string;
  currentDataRoot: string;
  bootstrapFilePath: string;
  logsDirectory: string;
  gpuAccelerationDisabled: boolean;
  tlsProtocols: string;
}

export interface LogChannelStatus {
  channel: string;
  label: string;
  fileName: string;
  filePath: string;
  sizeBytes: number;
  exists: boolean;
  lastWriteTimeUtc?: string | null;
}

export interface StorageConfigInfo {
  dataRootPath: string;
  defaultPath: string;
  fallbackPath: string;
  bootstrapFilePath: string;
  previousDataRootPath?: string | null;
  lastModifiedUtc: string;
  isUsingFallback: boolean;
  isWritable: boolean;
  diskFreeSpaceMb: number;
  diskTotalSpaceMb: number;
  logRetentionDays: number;
  logChannels: LogChannelStatus[];
}

export interface StorageValidationResult {
  isValid: boolean;
  normalizedPath: string;
  reasonCode?: string | null;
  message: string;
  availableFreeSpaceMb: number;
}

export interface StorageMigrationResult {
  Success?: boolean;
  success: boolean;
  oldDataRootPath: string;
  newDataRootPath: string;
  migratedData: boolean;
  migratedFileCount: number;
  migratedTotalBytes: number;
  message: string;
}

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

export interface HostEventEnvelope<T = unknown> {
  type: string;
  payload: T;
  timestamp: number;
}

export interface HostBridgeApi {
  // Stage 01
  getSystemInfo: () => Promise<BridgeResponse<SystemRuntimeInfo>>;
  getStorageConfig: () => Promise<BridgeResponse<StorageConfigInfo>>;
  validateStoragePath: (targetPath: string) => Promise<BridgeResponse<StorageValidationResult>>;
  selectFolderDialog: (initialPath?: string) => Promise<BridgeResponse<string | null>>;
  changeStoragePath: (newPath: string, migrateData: boolean) => Promise<BridgeResponse<StorageMigrationResult>>;
  resetStoragePathToDefault: (migrateData: boolean) => Promise<BridgeResponse<StorageMigrationResult>>;
  openPathInExplorer: (targetPath: string) => Promise<BridgeResponse<boolean>>;
  cleanupExpiredLogs: (retentionDays?: number) => Promise<BridgeResponse<number>>;
  writeDiagnosticLog: (channel: string, message: string) => Promise<BridgeResponse<boolean>>;
  setWindowTitle: (title: string) => Promise<BridgeResponse<boolean>>;

  // Stage 02
  getNetworkSettings: () => Promise<BridgeResponse<GlobalNetworkSettings>>;
  saveNetworkSettings: (settings: GlobalNetworkSettings | string) => Promise<BridgeResponse<GlobalNetworkSettings>>;
  inspectSystemProxy: (probeTargetUrl?: string) => Promise<BridgeResponse<SystemProxySnapshot>>;
  testProxyConnectivity: (
    targetUrl?: string,
    overrideSettings?: GlobalNetworkSettings | string | null,
    provider?: ProviderConfig | string | null
  ) => Promise<BridgeResponse<ProxyConnectivityTestResult>>;
  getProvidersConfig: () => Promise<BridgeResponse<ProvidersConfigDocument>>;
  saveProvidersConfig: (doc: ProvidersConfigDocument | string) => Promise<BridgeResponse<ProvidersConfigDocument>>;
  upsertProvider: (provider: ProviderConfig | string) => Promise<BridgeResponse<ProvidersConfigDocument>>;
  deleteProvider: (providerId: string) => Promise<BridgeResponse<ProvidersConfigDocument>>;
  setActiveModel: (providerId: string, modelId: string) => Promise<BridgeResponse<ProvidersConfigDocument>>;
  previewProviderResolution: (
    provider: ProviderConfig | string,
    modelId?: string,
    sessionId?: string,
    workspacePath?: string
  ) => Promise<BridgeResponse<ProviderEndpointPreviewDto>>;
}

declare global {
  interface Window {
    CefSharp?: {
      BindObjectAsync: (name: string) => Promise<void>;
    };
    hostBridge?: HostBridgeApi;
    __haloReceiveHostEvents?: (events: Array<HostEventEnvelope>) => void;
    __haloEventBus?: {
      on: <T = unknown>(eventType: string, handler: (payload: T, timestamp: number) => void) => () => void;
      emitLocal: (eventType: string, payload: unknown) => void;
    };
  }
}

type EventListener = (payload: any, timestamp: number) => void;
const listeners = new Map<string, Set<EventListener>>();

function dispatchIncomingEvents(events: Array<HostEventEnvelope>): void {
  if (!Array.isArray(events)) {
    return;
  }

  for (const evt of events) {
    if (!evt || typeof evt.type !== 'string') {
      continue;
    }

    const bucket = listeners.get(evt.type);
    if (bucket) {
      for (const fn of bucket) {
        try {
          fn(evt.payload, evt.timestamp);
        } catch (err) {
          console.error('[haloEventBus] handler error:', err);
        }
      }
    }

    const wildcardBucket = listeners.get('*');
    if (wildcardBucket) {
      for (const fn of wildcardBucket) {
        try {
          fn(evt, evt.timestamp);
        } catch (err) {
          console.error('[haloEventBus] wildcard handler error:', err);
        }
      }
    }
  }
}

export const haloEventBus = {
  on<T = unknown>(eventType: string, handler: (payload: T, timestamp: number) => void): () => void {
    let set = listeners.get(eventType);
    if (!set) {
      set = new Set();
      listeners.set(eventType, set);
    }
    set.add(handler as EventListener);
    return () => {
      set?.delete(handler as EventListener);
    };
  },
  emitLocal(eventType: string, payload: unknown): void {
    dispatchIncomingEvents([
      {
        type: eventType,
        payload,
        timestamp: Date.now(),
      },
    ]);
  },
};

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

function normalizeNetworkSettingsDto(res: BridgeResponse<GlobalNetworkSettings>): BridgeResponse<GlobalNetworkSettings> {
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

function normalizeProvidersDocDto(res: BridgeResponse<ProvidersConfigDocument>): BridgeResponse<ProvidersConfigDocument> {
  if (res.data && Array.isArray(res.data.providers)) {
    res.data.providers = res.data.providers.map((p) => ({
      ...p,
      proxyMode: normalizeProviderProxyMode(p.proxyMode),
      customHeaders: Array.isArray(p.customHeaders) ? p.customHeaders : [],
      models: Array.isArray(p.models)
        ? p.models.map((m) => ({
            ...m,
            protocol: normalizeLlmProtocol(m.protocol),
          }))
        : [],
    }));
  }
  return res;
}

function normalizeVersionedBaseUrlClient(baseUrl: string): string {
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

function createBrowserMockBridge(): HostBridgeApi {
  let mockDataRoot = 'C:\\HaloAgentData';
  const today = new Date().toISOString().slice(0, 10).replace(/-/g, '');

  let mockNetworkSettings: GlobalNetworkSettings = {
    mode: 'System',
    customProxy: {
      scheme: 'http',
      host: '127.0.0.1',
      port: 7890,
      requiresAuth: false,
      username: '',
      passwordEncrypted: '',
      passwordMasked: '',
      bypassList: 'localhost;127.*;10.*;192.168.*',
      bypassOnLocal: true,
    },
    defaultConnectTimeoutSeconds: 15,
    defaultRequestTimeoutSeconds: 300,
  };

  let mockProvidersDoc: ProvidersConfigDocument = {
    activeProviderId: 'provider-openai',
    activeModelId: 'gpt-4o',
    providers: [
      {
        id: 'provider-openai',
        name: 'OpenAI / 兼容网关',
        enabled: true,
        baseUrl: 'https://api.openai.com/v1',
        apiKeyEncrypted: 'DPAPI_MOCK_CIPHER_1',
        apiKeyMasked: 'sk-p****9876',
        hasApiKey: true,
        proxyMode: 'Inherit',
        customProxy: null,
        enableCustomHeaders: true,
        customHeaders: [
          {
            key: 'X-Session-Hash',
            valueTemplate: '${session-hash}',
            enabled: true,
            isSecret: false,
          },
          {
            key: 'X-Client-Workspace',
            valueTemplate: '${workspace-name}',
            enabled: true,
            isSecret: false,
          },
        ],
        models: [
          {
            id: 'gpt-4o',
            displayName: 'GPT-4o (Chat Completions)',
            protocol: 'chat',
            maxContextTokens: 128000,
            maxOutputTokens: 4096,
            temperature: 0.2,
            enabled: true,
          },
          {
            id: 'o3-mini',
            displayName: 'o3-mini (Responses API)',
            protocol: 'resp',
            maxContextTokens: 200000,
            maxOutputTokens: 16384,
            temperature: null,
            enabled: true,
          },
        ],
      },
      {
        id: 'provider-deepseek',
        name: 'DeepSeek Official',
        enabled: true,
        baseUrl: 'https://api.deepseek.com/v1',
        apiKeyEncrypted: '',
        apiKeyMasked: '',
        hasApiKey: false,
        proxyMode: 'Inherit',
        customProxy: null,
        enableCustomHeaders: false,
        customHeaders: [],
        models: [
          {
            id: 'deepseek-chat',
            displayName: 'DeepSeek-V3 (Chat)',
            protocol: 'chat',
            maxContextTokens: 64000,
            maxOutputTokens: 8192,
            temperature: 0.1,
            enabled: true,
          },
        ],
      },
    ],
  };

  return {
    async getSystemInfo() {
      return {
        ok: true,
        data: {
          osDescription: 'Windows 10/11 (Web Dev Mock)',
          osVersionString: 'Microsoft Windows NT 10.0.22631.0',
          isWin7: false,
          architecture: 'X64',
          processArchitecture: 'X64',
          dotnetVersion: '.NET 6.0.36 (Mock)',
          cefSharpVersion: '109.1.110.0',
          currentDataRoot: mockDataRoot,
          bootstrapFilePath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\bootstrap.json',
          logsDirectory: `${mockDataRoot}\\logs`,
          gpuAccelerationDisabled: false,
          tlsProtocols: 'Tls12, Tls13',
        },
      };
    },
    async getStorageConfig() {
      return {
        ok: true,
        data: {
          dataRootPath: mockDataRoot,
          defaultPath: 'C:\\HaloAgentData',
          fallbackPath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\Data',
          bootstrapFilePath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\bootstrap.json',
          previousDataRootPath: null,
          lastModifiedUtc: new Date().toISOString(),
          isUsingFallback: false,
          isWritable: true,
          diskFreeSpaceMb: 131480,
          diskTotalSpaceMb: 512000,
          logRetentionDays: 14,
          logChannels: [
            {
              channel: 'app',
              label: '系统运行日志 (App)',
              fileName: `app-${today}.log`,
              filePath: `${mockDataRoot}\\logs\\app-${today}.log`,
              sizeBytes: 4096,
              exists: true,
              lastWriteTimeUtc: new Date().toISOString(),
            },
            {
              channel: 'llm',
              label: '大模型请求审计 (LLM Trace)',
              fileName: `llm-trace-${today}.log`,
              filePath: `${mockDataRoot}\\logs\\llm-trace-${today}.log`,
              sizeBytes: 1024,
              exists: true,
              lastWriteTimeUtc: new Date().toISOString(),
            },
            {
              channel: 'tool',
              label: '工具执行日志 (Tool Exec)',
              fileName: `tool-exec-${today}.log`,
              filePath: `${mockDataRoot}\\logs\\tool-exec-${today}.log`,
              sizeBytes: 512,
              exists: true,
              lastWriteTimeUtc: new Date().toISOString(),
            },
          ],
        },
      };
    },
    async validateStoragePath(targetPath: string) {
      return {
        ok: true,
        data: {
          isValid: !!targetPath.trim(),
          normalizedPath: targetPath.trim(),
          message: '路径校验通过 (Mock)',
          availableFreeSpaceMb: 131480,
        },
      };
    },
    async selectFolderDialog(initialPath?: string) {
      const input = window.prompt('请输入模拟选择的新存储目录路径：', initialPath || 'C:\\HaloAgentData_Test');
      return {
        ok: true,
        data: input ? input.trim() : null,
      };
    },
    async changeStoragePath(newPath: string, migrateData: boolean) {
      const oldRoot = mockDataRoot;
      haloEventBus.emitLocal('storage:migration-progress', {
        percent: 50,
        message: '正在复制配置文件与日志...',
        targetPath: newPath,
      });
      await new Promise((r) => setTimeout(r, 250));
      mockDataRoot = newPath;
      haloEventBus.emitLocal('storage:migration-progress', {
        percent: 100,
        message: '存储路径切换完成。',
        targetPath: newPath,
      });
      return {
        ok: true,
        message: `已切换至 ${newPath}`,
        data: {
          success: true,
          oldDataRootPath: oldRoot,
          newDataRootPath: newPath,
          migratedData: migrateData,
          migratedFileCount: migrateData ? 4 : 0,
          migratedTotalBytes: migrateData ? 5632 : 0,
          message: `已切换至 ${newPath}`,
        },
      };
    },
    async resetStoragePathToDefault(migrateData: boolean) {
      return this.changeStoragePath('C:\\HaloAgentData', migrateData);
    },
    async openPathInExplorer() {
      return { ok: true, data: true };
    },
    async cleanupExpiredLogs() {
      return { ok: true, data: 0, message: '清理完成，共移除 0 个过期日志文件。' };
    },
    async writeDiagnosticLog() {
      return { ok: true, data: true };
    },
    async setWindowTitle(title: string) {
      document.title = title;
      return { ok: true, data: true };
    },

    // Stage 02 Mock Implementations
    async getNetworkSettings() {
      return {
        ok: true,
        data: JSON.parse(JSON.stringify(mockNetworkSettings)),
      };
    },
    async saveNetworkSettings(settings: GlobalNetworkSettings | string) {
      const parsed: GlobalNetworkSettings =
        typeof settings === 'string' ? JSON.parse(settings) : JSON.parse(JSON.stringify(settings));
      if (parsed.customProxy?.password && !parsed.customProxy.password.includes('****')) {
        parsed.customProxy.passwordEncrypted = 'DPAPI_MOCK_PROXY_PWD';
        parsed.customProxy.passwordMasked = '********';
      }
      if (parsed.customProxy) {
        delete parsed.customProxy.password;
      }
      mockNetworkSettings = parsed;
      return {
        ok: true,
        message: '网络与代理设置已保存并立即生效 (Mock)',
        data: JSON.parse(JSON.stringify(mockNetworkSettings)),
      };
    },
    async inspectSystemProxy() {
      return {
        ok: true,
        data: {
          isEnabled: true,
          proxyServerRaw: '127.0.0.1:7890',
          effectiveHttpProxyUri: 'http://127.0.0.1:7890',
          autoConfigUrl: null,
          bypassOverride: 'localhost;127.*;10.*;192.168.*;<local>',
          inspectedAtUtc: new Date().toISOString(),
        },
      };
    },
    async testProxyConnectivity(targetUrl?: string, overrideSettings?: GlobalNetworkSettings | string | null) {
      const parsedSettings: GlobalNetworkSettings = overrideSettings
        ? typeof overrideSettings === 'string'
          ? JSON.parse(overrideSettings)
          : overrideSettings
        : mockNetworkSettings;

      const effectiveSummary =
        parsedSettings.mode === 'None'
          ? 'DIRECT (No Proxy)'
          : parsedSettings.mode === 'Custom'
          ? `CUSTOM (${parsedSettings.customProxy.scheme}://${parsedSettings.customProxy.host}:${parsedSettings.customProxy.port})`
          : 'SYSTEM (http://127.0.0.1:7890)';

      return {
        ok: true,
        message: `连通测试成功 (42 ms · ${effectiveSummary})`,
        data: {
          success: true,
          statusCode: 200,
          elapsedMs: 42,
          effectiveProxyUsed: effectiveSummary,
          targetUrl: targetUrl || 'https://api.openai.com/v1/models',
          errorMessage: null,
          diagnosticCategory: 'OK',
        },
      };
    },
    async getProvidersConfig() {
      return {
        ok: true,
        data: JSON.parse(JSON.stringify(mockProvidersDoc)),
      };
    },
    async saveProvidersConfig(doc: ProvidersConfigDocument | string) {
      const parsed: ProvidersConfigDocument =
        typeof doc === 'string' ? JSON.parse(doc) : JSON.parse(JSON.stringify(doc));
      mockProvidersDoc = parsed;
      return {
        ok: true,
        message: '大模型服务商与模型配置已安全加密落盘 (Mock)',
        data: JSON.parse(JSON.stringify(mockProvidersDoc)),
      };
    },
    async upsertProvider(provider: ProviderConfig | string) {
      const parsed: ProviderConfig =
        typeof provider === 'string' ? JSON.parse(provider) : JSON.parse(JSON.stringify(provider));
      if (parsed.apiKey && !parsed.apiKey.includes('****')) {
        parsed.apiKeyEncrypted = 'DPAPI_MOCK_CIPHER';
        parsed.apiKeyMasked =
          parsed.apiKey.length > 8 ? `${parsed.apiKey.slice(0, 4)}****${parsed.apiKey.slice(-4)}` : '********';
        parsed.hasApiKey = true;
      }
      delete parsed.apiKey;
      const idx = mockProvidersDoc.providers.findIndex((p) => p.id === parsed.id);
      if (idx >= 0) {
        mockProvidersDoc.providers[idx] = parsed;
      } else {
        mockProvidersDoc.providers.push(parsed);
      }
      return {
        ok: true,
        message: `服务商 [${parsed.name}] 已保存 (Mock)`,
        data: JSON.parse(JSON.stringify(mockProvidersDoc)),
      };
    },
    async deleteProvider(providerId: string) {
      mockProvidersDoc.providers = mockProvidersDoc.providers.filter((p) => p.id !== providerId);
      if (mockProvidersDoc.activeProviderId === providerId) {
        mockProvidersDoc.activeProviderId = mockProvidersDoc.providers[0]?.id || '';
        mockProvidersDoc.activeModelId = mockProvidersDoc.providers[0]?.models[0]?.id || '';
      }
      return {
        ok: true,
        message: '服务商已删除 (Mock)',
        data: JSON.parse(JSON.stringify(mockProvidersDoc)),
      };
    },
    async setActiveModel(providerId: string, modelId: string) {
      mockProvidersDoc.activeProviderId = providerId;
      mockProvidersDoc.activeModelId = modelId;
      return {
        ok: true,
        message: `已切换至模型 ${modelId}`,
        data: JSON.parse(JSON.stringify(mockProvidersDoc)),
      };
    },
    async previewProviderResolution(provider: ProviderConfig | string, modelId?: string) {
      const parsed: ProviderConfig =
        typeof provider === 'string' ? JSON.parse(provider) : provider;
      const prefix = normalizeVersionedBaseUrlClient(parsed.baseUrl);
      const sessionHash = '9f86d081884c7d65';
      const resolvedHeaders: Record<string, string> = {};
      if (parsed.enableCustomHeaders && Array.isArray(parsed.customHeaders)) {
        for (const h of parsed.customHeaders) {
          if (h.enabled && h.key.trim()) {
            resolvedHeaders[h.key.trim()] = (h.valueTemplate || '')
              .replace(/\$\{session-hash\}/gi, sessionHash)
              .replace(/\$\{session-id\}/gi, 'sess_stage02_preview')
              .replace(/\$\{workspace-name\}/gi, 'halo-agent')
              .replace(/\$\{workspace-hash\}/gi, 'a1b2c3d4e5f60718')
              .replace(/\$\{model-id\}/gi, modelId || parsed.models[0]?.id || 'gpt-4o')
              .replace(/\$\{provider-name\}/gi, encodeURIComponent(parsed.name))
              .replace(/\$\{provider-id\}/gi, encodeURIComponent(parsed.id));
          }
        }
      }
      return {
        ok: true,
        data: {
          baseUrl: parsed.baseUrl,
          normalizedVersionPrefix: prefix,
          chatEndpointUrl: `${prefix}/chat/completions`,
          respEndpointUrl: `${prefix}/responses`,
          modelsEndpointUrl: `${prefix}/models`,
          effectiveProxySummary:
            parsed.proxyMode === 'None'
              ? 'DIRECT (No Proxy)'
              : parsed.proxyMode === 'Custom' && parsed.customProxy
              ? `CUSTOM (${parsed.customProxy.scheme}://${parsed.customProxy.host}:${parsed.customProxy.port})`
              : 'SYSTEM (http://127.0.0.1:7890)',
          sessionId: 'sess_stage02_preview',
          sessionHash,
          workspaceName: 'halo-agent',
          workspaceHash: 'a1b2c3d4e5f60718',
          resolvedHeaders,
        },
      };
    },
  };
}

function toCamelKey(key: string): string {
  if (!key) return key;
  return key.charAt(0).toLowerCase() + key.slice(1);
}

function normalizeBridgePayload<T>(input: any, preserveDictionaryKeys = false): T {
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

function wrapNativeHostBridge(rawBridge: any): HostBridgeApi {
  return {
    async getSystemInfo() {
      return normalizeBridgePayload<BridgeResponse<SystemRuntimeInfo>>(await rawBridge.getSystemInfo());
    },
    async getStorageConfig() {
      return normalizeBridgePayload<BridgeResponse<StorageConfigInfo>>(await rawBridge.getStorageConfig());
    },
    async validateStoragePath(targetPath: string) {
      return normalizeBridgePayload<BridgeResponse<StorageValidationResult>>(await rawBridge.validateStoragePath(targetPath));
    },
    async selectFolderDialog(initialPath?: string) {
      return normalizeBridgePayload<BridgeResponse<string | null>>(await rawBridge.selectFolderDialog(initialPath ?? ''));
    },
    async changeStoragePath(newPath: string, migrateData: boolean) {
      return normalizeBridgePayload<BridgeResponse<StorageMigrationResult>>(await rawBridge.changeStoragePath(newPath, migrateData));
    },
    async resetStoragePathToDefault(migrateData: boolean) {
      return normalizeBridgePayload<BridgeResponse<StorageMigrationResult>>(await rawBridge.resetStoragePathToDefault(migrateData));
    },
    async openPathInExplorer(targetPath: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.openPathInExplorer(targetPath));
    },
    async cleanupExpiredLogs(retentionDays = 14) {
      return normalizeBridgePayload<BridgeResponse<number>>(await rawBridge.cleanupExpiredLogs(retentionDays));
    },
    async writeDiagnosticLog(channel: string, message: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.writeDiagnosticLog(channel, message));
    },
    async setWindowTitle(title: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.setWindowTitle(title));
    },

    // Stage 02 Native Bridge Wrappers
    async getNetworkSettings() {
      const res = normalizeBridgePayload<BridgeResponse<GlobalNetworkSettings>>(await rawBridge.getNetworkSettings());
      return normalizeNetworkSettingsDto(res);
    },
    async saveNetworkSettings(settings: GlobalNetworkSettings | string) {
      const payload = typeof settings === 'string' ? settings : JSON.stringify(settings);
      const res = normalizeBridgePayload<BridgeResponse<GlobalNetworkSettings>>(await rawBridge.saveNetworkSettings(payload));
      return normalizeNetworkSettingsDto(res);
    },
    async inspectSystemProxy(probeTargetUrl?: string) {
      return normalizeBridgePayload<BridgeResponse<SystemProxySnapshot>>(
        await rawBridge.inspectSystemProxy(probeTargetUrl ?? 'https://api.openai.com')
      );
    },
    async testProxyConnectivity(
      targetUrl?: string,
      overrideSettings?: GlobalNetworkSettings | string | null,
      provider?: ProviderConfig | string | null
    ) {
      const settingsStr =
        overrideSettings === null || overrideSettings === undefined
          ? ''
          : typeof overrideSettings === 'string'
          ? overrideSettings
          : JSON.stringify(overrideSettings);
      const providerStr =
        provider === null || provider === undefined
          ? ''
          : typeof provider === 'string'
          ? provider
          : JSON.stringify(provider);

      return normalizeBridgePayload<BridgeResponse<ProxyConnectivityTestResult>>(
        await rawBridge.testProxyConnectivity(targetUrl ?? '', settingsStr, providerStr)
      );
    },
    async getProvidersConfig() {
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await rawBridge.getProvidersConfig());
      return normalizeProvidersDocDto(res);
    },
    async saveProvidersConfig(doc: ProvidersConfigDocument | string) {
      const payload = typeof doc === 'string' ? doc : JSON.stringify(doc);
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await rawBridge.saveProvidersConfig(payload));
      return normalizeProvidersDocDto(res);
    },
    async upsertProvider(provider: ProviderConfig | string) {
      const payload = typeof provider === 'string' ? provider : JSON.stringify(provider);
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await rawBridge.upsertProvider(payload));
      return normalizeProvidersDocDto(res);
    },
    async deleteProvider(providerId: string) {
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(await rawBridge.deleteProvider(providerId));
      return normalizeProvidersDocDto(res);
    },
    async setActiveModel(providerId: string, modelId: string) {
      const res = normalizeBridgePayload<BridgeResponse<ProvidersConfigDocument>>(
        await rawBridge.setActiveModel(providerId, modelId)
      );
      return normalizeProvidersDocDto(res);
    },
    async previewProviderResolution(
      provider: ProviderConfig | string,
      modelId?: string,
      sessionId?: string,
      workspacePath?: string
    ) {
      const payload = typeof provider === 'string' ? provider : JSON.stringify(provider);
      return normalizeBridgePayload<BridgeResponse<ProviderEndpointPreviewDto>>(
        await rawBridge.previewProviderResolution(payload, modelId ?? '', sessionId ?? '', workspacePath ?? '')
      );
    },
  };
}

let resolvedBridge: HostBridgeApi | null = null;

export async function initHostBridge(): Promise<HostBridgeApi> {
  window.__haloReceiveHostEvents = dispatchIncomingEvents;
  window.__haloEventBus = haloEventBus;

  if (resolvedBridge) {
    return resolvedBridge;
  }

  if (window.CefSharp && !window.hostBridge) {
    try {
      await window.CefSharp.BindObjectAsync('hostBridge');
    } catch (err) {
      console.error('[hostBridge] BindObjectAsync failed:', err);
    }
  }

  if (window.hostBridge) {
    resolvedBridge = wrapNativeHostBridge(window.hostBridge);
  } else {
    resolvedBridge = createBrowserMockBridge();
  }

  window.hostBridge = resolvedBridge;
  return resolvedBridge;
}

export function getHostBridge(): HostBridgeApi {
  if (!resolvedBridge) {
    if (window.hostBridge) {
      resolvedBridge = wrapNativeHostBridge(window.hostBridge);
    } else {
      resolvedBridge = createBrowserMockBridge();
    }
    window.hostBridge = resolvedBridge;
  }
  return resolvedBridge;
}

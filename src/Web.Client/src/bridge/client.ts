/** need22-P3 G5：真实 CefSharp 调用（四模块之 client）。
 * - negotiate() 版本握手：失败直接抛，不静默。
 * - startAgentTurn 参数收敛到本处（7→6→5 参回退），业务代码不再 try/catch 兼容。 */
import { BRIDGE_VERSION, reportBridgeError, serializeWithDates, toBridgeError, normalizeVisionFlag } from './types';

export interface StartAgentTurnParams {
  workspaceId: string;
  sessionId: string;
  userText: string;
  providerId?: string;
  modelId?: string;
  attachmentsJson?: string;
  requestId?: string;
}

/** 握手：后端版本号 === BRIDGE_VERSION 则通过；显式不一致直接抛；无版本接口按 legacy(0) 显式 warn 继续。 */
export async function negotiate(rawBridge: unknown): Promise<{ version: number }> {
  try {
    const raw = rawBridge as Record<string, unknown>;
    const getVersion = raw['getBridgeVersion'] ?? raw['getVersion'];
    if (typeof getVersion === 'function') {
      const v = await (getVersion as () => Promise<unknown>).call(rawBridge);
      const num = typeof v === 'number' ? v : Number((v as { version?: unknown })?.version ?? v);
      if (num === BRIDGE_VERSION) return { version: num };
      const err = toBridgeError('BRIDGE_VERSION_MISMATCH', `桥接版本不一致：前端=${BRIDGE_VERSION}，后端=${String(num)}`);
      reportBridgeError(err);
      throw Object.assign(new Error(err.message), err);
    }
    // 旧后端无版本接口：显式 warn 标记 legacy（version 0），不断连、不静默吞掉
    console.warn(`[bridge] BRIDGE_VERSION_MISMATCH: 后端未上报桥接版本（期望 BRIDGE_VERSION=${BRIDGE_VERSION}），按 legacy(0) 继续，请升级后端`);
    return { version: 0 };
  } catch (e) {
    if (e instanceof Error && (e as Error & { code?: string }).code) throw e;
    const err = toBridgeError('BRIDGE_HANDSHAKE_FAILED', `桥接握手失败：${e instanceof Error ? e.message : String(e)}`);
    reportBridgeError(err);
    throw Object.assign(new Error(err.message), err);
  }
}

/** startAgentTurn 参数收敛点：7 参→6 参→5 参回退，每次回退 warn + code，不吞错。 */
export async function callStartAgentTurn(
  rawBridge: unknown,
  params: StartAgentTurnParams,
): Promise<unknown> {
  const p = serializeWithDates(params);
  const raw = rawBridge as {
    startAgentTurn: (...args: string[]) => Promise<unknown>;
  };
  const base5 = [p.workspaceId, p.sessionId, p.userText, p.providerId ?? '', p.modelId ?? ''];
  const extra = p.attachmentsJson ?? '';
  const reqId = p.requestId ?? '';
  // 7 参（含 requestId）
  try {
    return await raw.startAgentTurn(...base5, extra, reqId);
  } catch (e) {
    console.warn('[bridge] startAgentTurn 7-arg failed, fallback 6-arg:', (e as Error)?.message ?? e);
  }
  // 6 参（含 attachments）
  try {
    return await raw.startAgentTurn(...base5, extra);
  } catch (e) {
    console.warn('[bridge] startAgentTurn 6-arg failed, fallback 5-arg:', (e as Error)?.message ?? e);
  }
  // 5 参（最后手段；再失败直接抛 {code,message}）
  try {
    return await raw.startAgentTurn(...base5);
  } catch (e) {
    const err = toBridgeError('AGENT_START_ERROR', `startAgentTurn 失败：${e instanceof Error ? e.message : String(e)}`);
    reportBridgeError(err);
    throw Object.assign(new Error(err.message), err);
  }
}

import type { AgentConfigDocument, BridgeResponse, CompactionResultDto, ContextBudgetStatusDto, GlobalNetworkSettings, GlobalProxyMode, HistoryDeleteResultDto, HistoryHealResultDto, HistoryTruncateResultDto, HostBridgeApi, LlmProtocolType, PromptConfigDocument, PromptDiagnosticsDto, PromptPreviewDto, ProviderConfig, ProviderEndpointPreviewDto, ProviderProxyMode, ProvidersConfigDocument, ProxyConnectivityTestResult, SessionGoalDto, SessionMetadata, SkillMemoryDetail, SkillMemoryKind, SkillMemoryListItem, SkillMemoryOpResult, SkillMemoryScope, SkillMemoryScopeFilter, StorageConfigInfo, StorageMigrationResult, StorageValidationResult, SubRuntimeInfo, SubSessionDetailDto, SystemProxySnapshot, SystemRuntimeInfo, UnifiedChatMessage, UnifiedLlmRequestDto, UsageDbStatus, UsageFilter, UsageFilterOptions, UsageOverview, UsagePage, UsageProviderShareItem, UsageTrend, WorkspaceInfo } from './types';

/** need22-P5 J4：以下自 hostBridge.ts 迁入（normalize* + payload 归一 + wrapNativeHostBridge），行为不变。 */
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

 function normalizeBridgePayload<T>(input: any, preserveDictionaryKeys = false): T {
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
 function sanitizePromptConfigForSave(config: PromptConfigDocument | string): string {
   if (typeof config === 'string') {
     return config;
   }
   const raw = config as unknown as Record<string, unknown>;
   const copy: Record<string, unknown> = { ...raw };
   copy.updatedAtUtc = toIsoDateString(raw.updatedAtUtc);
   return JSON.stringify(copy);
 }
/** need08：同 need06 归一逻辑，智能体行为配置保存前把 updatedAtUtc 归一为 ISO 字符串。 */
function sanitizeAgentConfigForSave(config: AgentConfigDocument | string): string {
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

export function wrapNativeHostBridge(rawBridge: any): HostBridgeApi {
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
     async openExternalLink(href: string, workspaceRoot?: string) {
       const raw = rawBridge as unknown as { openExternalLink?: (h: string, w: string) => Promise<unknown> };
       if (typeof raw.openExternalLink === 'function') {
         return normalizeBridgePayload<BridgeResponse<boolean>>(await raw.openExternalLink(href, workspaceRoot ?? ''));
       }
       // 旧后端兼容：http(s) 用默认方式打开，本地路径退化为 explorer 定位
       if (/^https?:\/\//i.test(String(href || ''))) {
         try { window.open(href, '_blank', 'noopener'); } catch (e) { console.warn('[bridge] window.open fallback failed:', (e as Error)?.message ?? e); }
         return { ok: true, data: true };
       }
       return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.openPathInExplorer(href));
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
    async startSessionRun(request: UnifiedLlmRequestDto | string) {
      const payload = typeof request === 'string' ? request : JSON.stringify(request);
      return normalizeBridgePayload<BridgeResponse<string>>(await rawBridge.startSessionRun(payload));
    },
    async cancelSessionRun(sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.cancelSessionRun(sessionId));
    },
     async startAgentTurn(workspaceId: string, sessionId: string, userText: string, providerId?: string, modelId?: string, attachmentsJson?: string, requestId?: string) {
       const extra = typeof attachmentsJson === 'string' ? attachmentsJson : '';
       const reqId = typeof requestId === 'string' ? requestId : '';
       // need22-P2 F2：requestId 透传（新后端 7 参回显去重）；老后端逐级回退 6→5 参，不崩
       if (reqId) {
         try {
           return normalizeBridgePayload<BridgeResponse<string>>(
             await rawBridge.startAgentTurn(workspaceId, sessionId, userText, providerId ?? '', modelId ?? '', extra, reqId)
           );
         } catch (e6) { console.warn('[bridge] startAgentTurn 7-arg unsupported, fallback 6/5-arg [AGENT_START_ERROR]:', (e6 as Error)?.message ?? e6); }
       }
       // 老后端仅 5 参：有附件时尝试 6 参，失败回退 5 参（图片占位符文本仍发送，不崩）
       if (extra) {
         try {
           return normalizeBridgePayload<BridgeResponse<string>>(
             await rawBridge.startAgentTurn(workspaceId, sessionId, userText, providerId ?? '', modelId ?? '', extra)
           );
         } catch (e6) { console.warn('[bridge] startAgentTurn 6-arg unsupported, fallback 5-arg [AGENT_START_ERROR]:', (e6 as Error)?.message ?? e6); }
       }
       return normalizeBridgePayload<BridgeResponse<string>>(
         await rawBridge.startAgentTurn(workspaceId, sessionId, userText, providerId ?? '', modelId ?? '')
       );
     },
     async cancelAgentTurn(sessionId: string) {
       return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.cancelAgentTurn(sessionId));
     },
     async getImageThumbnail(workspaceId: string, sessionId: string, imageId: string) {
       try {
         if (typeof rawBridge.getImageThumbnail === 'function') {
           return normalizeBridgePayload<BridgeResponse<string>>(
             await rawBridge.getImageThumbnail(workspaceId, sessionId, imageId)
           );
         }
       } catch (e) { console.warn('[bridge] native call failed, fallback empty state:', (e as Error)?.message ?? e); }
       return { ok: false, code: 'THUMB_UNAVAILABLE', message: '缩略图接口不可用' };
     },
    // need09-06：问询回答提交（老后端无此方法时报 ASK_UNSUPPORTED，不丢草稿）
    async resolveAskUser(sessionId: string, ticketId: string, answersJson: string) {
      try {
        if (typeof rawBridge.resolveAskUser === 'function') {
          return normalizeBridgePayload<BridgeResponse<boolean>>(
            await rawBridge.resolveAskUser(sessionId ?? '', ticketId ?? '', answersJson ?? '')
          );
        }
      } catch (e) { console.warn('[bridge] native call failed, fallback empty state:', (e as Error)?.message ?? e); }
      return { ok: false, code: 'ASK_UNSUPPORTED', message: '问询提交通道不可用' };
    },

    // Stage 04 Native Wrappers
    async getWorkspaces() {
      return normalizeBridgePayload<BridgeResponse<WorkspaceInfo[]>>(await rawBridge.getWorkspaces());
    },
    async addWorkspace(rootPath: string, name?: string) {
      return normalizeBridgePayload<BridgeResponse<WorkspaceInfo>>(await rawBridge.addWorkspace(rootPath, name ?? ''));
    },
    async removeWorkspace(workspaceId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.removeWorkspace(workspaceId));
    },
    async browseWorkspaceFolder(initialPath?: string) {
      return normalizeBridgePayload<BridgeResponse<string | null>>(await rawBridge.browseWorkspaceFolder(initialPath ?? ''));
    },
    async getSessions(workspaceId: string) {
      return normalizeBridgePayload<BridgeResponse<SessionMetadata[]>>(await rawBridge.getSessions(workspaceId));
    },
    async createSession(workspaceId: string, title?: string, providerId?: string, modelId?: string) {
      return normalizeBridgePayload<BridgeResponse<SessionMetadata>>(
        await rawBridge.createSession(workspaceId, title ?? '', providerId ?? '', modelId ?? '')
      );
    },
    async getSessionMessages(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<UnifiedChatMessage[]>>(
        await rawBridge.getSessionMessages(workspaceId, sessionId)
      );
    },
    async appendSessionMessage(workspaceId: string, sessionId: string, message: UnifiedChatMessage | string) {
      const payload = typeof message === 'string' ? message : JSON.stringify(message);
      return normalizeBridgePayload<BridgeResponse<boolean>>(
        await rawBridge.appendSessionMessage(workspaceId, sessionId, payload)
      );
    },
    async updateSessionTitle(workspaceId: string, sessionId: string, title: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(
        await rawBridge.updateSessionTitle(workspaceId, sessionId, title)
      );
    },
    async generateSessionTitle(workspaceId: string, sessionId: string, providerId?: string, modelId?: string) {
      return normalizeBridgePayload<BridgeResponse<string>>(
        await rawBridge.generateSessionTitle(workspaceId, sessionId, providerId ?? '', modelId ?? '')
      );
    },
    async setSessionPinned(workspaceId: string, sessionId: string, isPinned: boolean) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(
        await rawBridge.setSessionPinned(workspaceId, sessionId, isPinned)
      );
    },
    async deleteSession(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(
        await rawBridge.deleteSession(workspaceId, sessionId)
      );
    },
    async exportSessionMarkdown(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<string>>(
        await rawBridge.exportSessionMarkdown(workspaceId, sessionId)
      );
    },
    async getContextBudget(providerId: string, modelId: string, workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<ContextBudgetStatusDto>>(
        await rawBridge.getContextBudget(providerId, modelId, workspaceId, sessionId)
      );
    },
    async compactSession(workspaceId: string, sessionId: string, providerId?: string, modelId?: string, keepLastGroups?: number) {
      return normalizeBridgePayload<BridgeResponse<CompactionResultDto>>(
        await rawBridge.compactSession(workspaceId, sessionId, providerId ?? '', modelId ?? '', keepLastGroups ?? 6)
      );
    },
     async getCompactBackupList(workspaceId: string, sessionId: string) {
       return normalizeBridgePayload<BridgeResponse<string>>(
         await rawBridge.getCompactBackupList(workspaceId, sessionId)
       );
     },
     async getPromptConfig() {
       return normalizeBridgePayload<BridgeResponse<PromptConfigDocument>>(await rawBridge.getPromptConfig());
     },
     async savePromptConfig(config: PromptConfigDocument | string) {
       const payload = sanitizePromptConfigForSave(config);
       return normalizeBridgePayload<BridgeResponse<PromptConfigDocument>>(await rawBridge.savePromptConfig(payload));
     },
     async resetPromptConfig(blockId?: string) {
       return normalizeBridgePayload<BridgeResponse<PromptConfigDocument>>(await rawBridge.resetPromptConfig(blockId ?? ''));
     },
     async getAgentConfig() {
       return normalizeBridgePayload<BridgeResponse<AgentConfigDocument>>(await rawBridge.getAgentConfig());
     },
     async saveAgentConfig(config: AgentConfigDocument | string) {
       const payload = sanitizeAgentConfigForSave(config);
       return normalizeBridgePayload<BridgeResponse<AgentConfigDocument>>(await rawBridge.saveAgentConfig(payload));
     },
      async resetAgentConfig(field?: string) {
        return normalizeBridgePayload<BridgeResponse<AgentConfigDocument>>(await rawBridge.resetAgentConfig(field ?? ''));
      },
      // need13：子代理直通（老后端缺方法时降级空态，不炸面板）
      async getSubSessions(ws: string, parent: string) {
        try {
          if (typeof rawBridge.getSubSessions !== 'function') return { ok: true, data: [] as SessionMetadata[] };
          return normalizeBridgePayload<BridgeResponse<SessionMetadata[]>>(await rawBridge.getSubSessions(ws ?? '', parent ?? ''));
        } catch (e) { console.warn('[bridge] native unavailable, fallback empty state:', (e as Error)?.message ?? e); return { ok: true, data: [] as SessionMetadata[] }; }
      },
      async cancelSubAgent(ws: string, sub: string) {
        try {
          if (typeof rawBridge.cancelSubAgent !== 'function') return { ok: false, code: 'SUB_UNSUPPORTED', message: '子代理通道不可用' };
          return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.cancelSubAgent(ws ?? '', sub ?? ''));
        } catch (e) { console.warn('[bridge] SUB_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'SUB_UNSUPPORTED', message: '子代理通道不可用' }; }
      },
      async cancelAgentTree(main: string) {
        try {
          if (typeof rawBridge.cancelAgentTree !== 'function') return rawBridge.cancelAgentTurn(main ?? '');
          return normalizeBridgePayload<BridgeResponse<boolean>>(await rawBridge.cancelAgentTree(main ?? ''));
        } catch (e) { console.warn('[bridge] SUB_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'SUB_UNSUPPORTED', message: '子代理通道不可用' }; }
      },
      async getSubRuntime(ws: string, parent: string) {
        try {
          if (typeof rawBridge.getSubRuntime !== 'function') return { ok: true, data: { subs: [], policy: { enabled: true, maxParallel: 3, maxIterations: 15, exec: true, file: true, skill: false, memory: false }, lastRejection: null } as SubRuntimeInfo };
          return normalizeBridgePayload<BridgeResponse<SubRuntimeInfo>>(await rawBridge.getSubRuntime(ws ?? '', parent ?? ''));
        } catch (e) { console.warn('[bridge] native unavailable, fallback empty state:', (e as Error)?.message ?? e); return { ok: true, data: { subs: [], policy: { enabled: true, maxParallel: 3, maxIterations: 15, exec: true, file: true, skill: false, memory: false }, lastRejection: null } as SubRuntimeInfo }; }
      },
       async getGoal(ws: string, sess: string) {
         try {
           if (typeof (rawBridge as unknown as Record<string, unknown>).getGoal !== 'function') return { ok: true, data: { title: '', description: '', milestones: [], percent: 0, note: '', updatedAtUtc: new Date().toISOString() } as SessionGoalDto };
           return normalizeBridgePayload<BridgeResponse<SessionGoalDto>>(await (rawBridge as unknown as { getGoal: (a: string, b: string) => Promise<BridgeResponse<SessionGoalDto>> }).getGoal(ws ?? '', sess ?? ''));
         } catch (e) { console.warn('[bridge] native unavailable, fallback empty state:', (e as Error)?.message ?? e); return { ok: true, data: { title: '', description: '', milestones: [], percent: 0, note: '', updatedAtUtc: new Date().toISOString() } as SessionGoalDto }; }
       },
       async saveGoal(ws: string, sess: string, goalJson: string) {
         try {
           if (typeof (rawBridge as unknown as Record<string, unknown>).saveGoal !== 'function') return { ok: true, data: { title: '', description: '', milestones: [], percent: 0, note: '', updatedAtUtc: new Date().toISOString() } as SessionGoalDto };
           return normalizeBridgePayload<BridgeResponse<SessionGoalDto>>(await (rawBridge as unknown as { saveGoal: (a: string, b: string, c: string) => Promise<BridgeResponse<SessionGoalDto>> }).saveGoal(ws ?? '', sess ?? '', goalJson ?? ''));
         } catch (e) { return { ok: false, code: 'GOAL_SAVE_ERROR', message: String(e) }; }
       },
       async clearGoal(ws: string, sess: string) {
         try {
           if (typeof (rawBridge as unknown as Record<string, unknown>).clearGoal !== 'function') return { ok: true, data: true };
           return normalizeBridgePayload<BridgeResponse<boolean>>(await (rawBridge as unknown as { clearGoal: (a: string, b: string) => Promise<BridgeResponse<boolean>> }).clearGoal(ws ?? '', sess ?? ''));
         } catch (e) { return { ok: false, code: 'GOAL_CLEAR_ERROR', message: String(e) }; }
       },
       async getSubSessionDetail(ws: string, sub: string) {
         try {
           if (typeof (rawBridge as unknown as Record<string, unknown>).getSubSessionDetail !== 'function') return { ok: false, code: 'SUB_NOT_FOUND', message: '子会话不存在' };
           return normalizeBridgePayload<BridgeResponse<SubSessionDetailDto>>(await (rawBridge as unknown as { getSubSessionDetail: (a: string, b: string) => Promise<BridgeResponse<SubSessionDetailDto>> }).getSubSessionDetail(ws ?? '', sub ?? ''));
         } catch (e) { return { ok: false, code: 'SUB_LIST_ERROR', message: String(e) }; }
       },
       async getSubSessionMessages(ws: string, sub: string, limit?: number, beforeMessageId?: string) {
         try {
           if (typeof (rawBridge as unknown as Record<string, unknown>).getSubSessionMessages !== 'function') return { ok: true, data: [] as UnifiedChatMessage[] };
           return normalizeBridgePayload<BridgeResponse<UnifiedChatMessage[]>>(await (rawBridge as unknown as { getSubSessionMessages: (a: string, b: string, c?: number, d?: string) => Promise<BridgeResponse<UnifiedChatMessage[]>> }).getSubSessionMessages(ws ?? '', sub ?? '', limit, beforeMessageId ?? ''));
         } catch (e) { console.warn('[bridge] native unavailable, fallback empty state:', (e as Error)?.message ?? e); return { ok: true, data: [] as UnifiedChatMessage[] }; }
       },
     async previewPrompt(workspaceId: string) {
       return normalizeBridgePayload<BridgeResponse<PromptPreviewDto>>(await rawBridge.previewPrompt(workspaceId ?? ''));
     },
     async getPromptDiagnostics(workspaceId: string, sessionId?: string) {
       return normalizeBridgePayload<BridgeResponse<PromptDiagnosticsDto>>(
         await rawBridge.getPromptDiagnostics(workspaceId ?? '', sessionId ?? '')
       );
     },
     async truncateSessionHistory(workspaceId: string, sessionId: string, anchorMessageId: string, removeAnchor: boolean) {
       return normalizeBridgePayload<BridgeResponse<HistoryTruncateResultDto>>(
         await rawBridge.truncateSessionHistory(workspaceId ?? '', sessionId ?? '', anchorMessageId ?? '', removeAnchor ?? true)
       );
     },
     async branchSession(workspaceId: string, sourceSessionId: string, anchorMessageId: string, includeAnchor: boolean) {
       return normalizeBridgePayload<BridgeResponse<SessionMetadata>>(
         await rawBridge.branchSession(workspaceId ?? '', sourceSessionId ?? '', anchorMessageId ?? '', includeAnchor ?? true)
       );
     },
     async healSessionHistory(workspaceId: string, sessionId: string) {
       return normalizeBridgePayload<BridgeResponse<HistoryHealResultDto>>(
         await rawBridge.healSessionHistory(workspaceId ?? '', sessionId ?? '')
       );
     },
     async deleteSessionMessage(workspaceId: string, sessionId: string, messageId: string) {
       return normalizeBridgePayload<BridgeResponse<HistoryDeleteResultDto>>(
         await rawBridge.deleteSessionMessage(workspaceId ?? '', sessionId ?? '', messageId ?? '')
       );
     },
     async listSkillMemory(kind: SkillMemoryKind, scope: SkillMemoryScopeFilter, workspaceId?: string) {
       try {
         if (typeof rawBridge.listSkillMemory !== 'function') return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
         return normalizeBridgePayload<BridgeResponse<SkillMemoryListItem[]>>(
           await rawBridge.listSkillMemory(kind ?? 'skill', scope ?? 'all', workspaceId ?? '')
         );
       } catch (e) { console.warn('[bridge] SKILL_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' }; }
     },
     async getSkillMemoryDetail(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string, workspaceId?: string) {
       try {
         if (typeof rawBridge.getSkillMemoryDetail !== 'function') return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
         return normalizeBridgePayload<BridgeResponse<SkillMemoryDetail>>(
           await rawBridge.getSkillMemoryDetail(kind ?? 'skill', scope ?? 'global', name ?? '', workspaceId ?? '')
         );
       } catch (e) { console.warn('[bridge] SKILL_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' }; }
     },
     async setSkillMemoryEnabled(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string, enabled: boolean, workspaceId?: string) {
       try {
         if (typeof rawBridge.setSkillMemoryEnabled !== 'function') return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
         return normalizeBridgePayload<BridgeResponse<SkillMemoryOpResult>>(
           await rawBridge.setSkillMemoryEnabled(kind ?? 'skill', scope ?? 'global', name ?? '', enabled ?? true, workspaceId ?? '')
         );
       } catch (e) { console.warn('[bridge] SKILL_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' }; }
     },
     async deleteSkillMemory(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string, workspaceId?: string) {
       try {
         if (typeof rawBridge.deleteSkillMemory !== 'function') return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
         return normalizeBridgePayload<BridgeResponse<SkillMemoryOpResult>>(
           await rawBridge.deleteSkillMemory(kind ?? 'skill', scope ?? 'global', name ?? '', workspaceId ?? '')
         );
       } catch (e) { console.warn('[bridge] SKILL_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' }; }
     },
     async openSkillMemoryFolder(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string, workspaceId?: string) {
       try {
         if (typeof rawBridge.openSkillMemoryFolder !== 'function') return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' };
         return normalizeBridgePayload<BridgeResponse<boolean>>(
           await rawBridge.openSkillMemoryFolder(kind ?? 'skill', scope ?? 'global', name ?? '', workspaceId ?? '')
         );
       } catch (e) { console.warn('[bridge] SKILL_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'SKILL_UNSUPPORTED', message: '技能记忆通道不可用' }; }
     },
     async getUsageFilterOptions() {
       try {
         if (typeof rawBridge.getUsageFilterOptions !== 'function') return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
         return normalizeBridgePayload<BridgeResponse<UsageFilterOptions>>(await rawBridge.getUsageFilterOptions());
       } catch (e) { console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' }; }
     },
     async getUsageOverview(filter: UsageFilter | string) {
       try {
         if (typeof rawBridge.getUsageOverview !== 'function') return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
         const payload = typeof filter === 'string' ? filter : JSON.stringify(filter ?? {});
         return normalizeBridgePayload<BridgeResponse<UsageOverview>>(await rawBridge.getUsageOverview(payload));
       } catch (e) { console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' }; }
     },
     async getUsageTrend(filter: UsageFilter | string, metric?: string) {
       try {
         if (typeof rawBridge.getUsageTrend !== 'function') return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
         const payload = typeof filter === 'string' ? filter : JSON.stringify(filter ?? {});
         return normalizeBridgePayload<BridgeResponse<UsageTrend>>(await rawBridge.getUsageTrend(payload, metric ?? 'total'));
       } catch (e) { console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' }; }
     },
     async getUsageProviderShare(filter: UsageFilter | string) {
       try {
         if (typeof rawBridge.getUsageProviderShare !== 'function') return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
         const payload = typeof filter === 'string' ? filter : JSON.stringify(filter ?? {});
         return normalizeBridgePayload<BridgeResponse<UsageProviderShareItem[]>>(await rawBridge.getUsageProviderShare(payload));
       } catch (e) { console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' }; }
     },
     async queryUsagePage(filter: UsageFilter | string, page: number, pageSize: number) {
       try {
         if (typeof rawBridge.queryUsagePage !== 'function') return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
         const payload = typeof filter === 'string' ? filter : JSON.stringify(filter ?? {});
         return normalizeBridgePayload<BridgeResponse<UsagePage>>(await rawBridge.queryUsagePage(payload, page ?? 1, pageSize ?? 10));
       } catch (e) { console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' }; }
     },
     async getUsageDbStatus() {
       try {
         if (typeof rawBridge.getUsageDbStatus !== 'function') return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' };
         return normalizeBridgePayload<BridgeResponse<UsageDbStatus>>(await rawBridge.getUsageDbStatus());
       } catch (e) { console.warn('[bridge] USAGE_UNSUPPORTED:', (e as Error)?.message ?? e); return { ok: false, code: 'USAGE_UNSUPPORTED', message: '用量通道不可用' }; }
     },
   };
}

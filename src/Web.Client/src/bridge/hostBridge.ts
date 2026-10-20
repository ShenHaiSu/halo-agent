/** need22-P5 J4：兼容垫片（实现已迁至 types/client/mock/mockState/mockSystem/mockWorkspace/events/proxy）。
 * 本文件仅保留生命周期（init/get/mock 标记）+ 重导出，禁止新增逻辑；存量 import '@/bridge/hostBridge' 无需改动。 */
import type { HostBridgeApi } from './types';
import { dispatchIncomingEvents, haloEventBus } from './events';
import { wrapNativeHostBridge } from './client';
import { createBrowserMockBridge } from './mock';

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
    markBridgeMock(false);
  } else {
    // need22-P3 G5：Mock 绝不静默——显式标记，UI 红色横幅“桥接未连接（Mock数据）”+重试
    resolvedBridge = createBrowserMockBridge();
    markBridgeMock(true);
    console.warn('[hostBridge] BRIDGE_NOT_CONNECTED: 桥接未连接（Mock数据），已降级 Mock（显式标记，非静默）');
  }

  window.hostBridge = resolvedBridge;
  return resolvedBridge;
}

export function getHostBridge(): HostBridgeApi {
  if (!resolvedBridge) {
    if (window.hostBridge) {
      resolvedBridge = wrapNativeHostBridge(window.hostBridge);
      markBridgeMock(false);
    } else {
      resolvedBridge = createBrowserMockBridge();
      markBridgeMock(true);
    }
    window.hostBridge = resolvedBridge;
  }
  return resolvedBridge;
}

/** need22-P3 G5：版本重导出（proxy negotiate 握手用，BRIDGE_VERSION=2）+ Mock 显式标记（横幅用，不静默）。 */

function markBridgeMock(on: boolean): void {
  try {
    (window as unknown as { __haloBridgeMock?: boolean }).__haloBridgeMock = on;
    if (typeof sessionStorage !== 'undefined') {
      if (on) sessionStorage.setItem('halo-bridge-mock', '1');
      else sessionStorage.removeItem('halo-bridge-mock');
    }
  } catch (e) { console.warn('[hostBridge] mock flag failed:', e); }
}
/** Mock 是否生效（App.vue 红色横幅“桥接未连接（Mock数据）”+重试按钮依据）。 */
export function isBridgeMock(): boolean {
  try {
    if (typeof sessionStorage !== 'undefined' && sessionStorage.getItem('halo-bridge-mock') === '1') return true;
  } catch (e) { console.warn('[hostBridge] mock flag read failed:', e); }
  return (window as unknown as { __haloBridgeMock?: boolean }).__haloBridgeMock === true;
}

export { haloEventBus } from './events';
export { wrapNativeHostBridge } from './client';
export { normalizeGlobalProxyMode, normalizeProviderProxyMode, normalizeLlmProtocol } from './client';
export { createBrowserMockBridge, isMockMode, setMockMode, shouldShowMockBanner } from './mock';
export { normalizeVisionFlag } from './types';
export type { AgentConfigDocument, BridgeResponse, ChatImageRef, CompactionResultDto, ContextBudgetStatusDto, CustomHeaderEntry, GlobalNetworkSettings, GlobalProxyMode, GoalMilestoneDto, HeaderResolutionContextDto, HistoryDeleteResultDto, HistoryHealResultDto, HistoryTruncateResultDto, HostBridgeApi, HostEventEnvelope, LastSentPromptSnapshot, LlmProtocolType, LogChannelStatus, ManualProxyConfig, ModelConfig, PromptBlockPreview, PromptConfigDocument, PromptDiagnosticsDto, PromptPreviewDto, ProviderConfig, ProviderEndpointPreviewDto, ProviderProxyMode, ProvidersConfigDocument, ProxyConnectivityTestResult, SessionGoalDto, SessionMetadata, SessionStreamEventDto, SkillMemoryDetail, SkillMemoryKind, SkillMemoryListItem, SkillMemoryOpResult, SkillMemoryScope, SkillMemoryScopeFilter, StorageConfigInfo, StorageMigrationResult, StorageValidationResult, SubPolicyDto, SubRejectionInfo, SubRuntimeInfo, SubRuntimeItem, SubSessionDetailDto, SystemProxySnapshot, SystemRuntimeInfo, UnifiedChatMessage, UnifiedChatMessageDto, UnifiedLlmRequestDto, UnifiedStreamEventKind, UnifiedTokenUsageDto, UnifiedToolCallDto, UnifiedToolDefinitionDto, UsageDbStatus, UsageFilter, UsageFilterOptions, UsageModelOption, UsageOverview, UsagePage, UsagePreset, UsageProviderOption, UsageProviderShareItem, UsageRowDto, UsageSourceOption, UsageTrend, UsageTrendSeries, WorkspaceInfo } from './types';

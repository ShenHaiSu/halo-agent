/** need21-F02: 宿主桥生命周期（旧 src/bridge/hostBridge.ts 迁移）。
 * 本文件只做三件事：init/getHostBridge 门面（签名不变）+ Transport 选择（DEV&&?mock→Mock，否则 Cef）+ 版本断言。
 * Mock 实现常驻 @/mocks，仅 DEV 分支动态加载（@vite-ignore + DEV 死分支消除，生产包零 mock）。 */
import type { HostBridgeApi } from './protocol/types';
import { assertBridgeVersion, BRIDGE_VERSION } from './protocol/version';
import { MOCK_BANNER_TEXT, reportBridgeError, toBridgeError } from './protocol/errors';
import { dispatchIncomingEvents, haloEventBus } from './events';
import { CefTransport } from './transport/cef';
import { MockTransport } from './transport/mock';
import type { BridgeTransport } from './transport/transport';
import { createHostBridgeApi } from './api';

export { haloEventBus };
export { MOCK_BANNER_TEXT, BRIDGE_VERSION };
export { CefTransport, MockTransport };
export type { BridgeTransport };

const MOCK_FLAG_KEY = 'halo-bridge-mock';
const WRAPPED_MARK = '__haloWrappedApi';

let resolvedBridge: HostBridgeApi | null = null;
let activeTransport: BridgeTransport | null = null;

/** 是否显式要求 Mock（仅 DEV 生效）：URL 含 ?mock。 */
function wantsMock(): boolean {
  try {
    if (typeof window === 'undefined' || typeof window.location === 'undefined') return false;
    return new URLSearchParams(window.location.search).has('mock');
  } catch {
    return false;
  }
}

/** Mock 工厂动态加载（@vite-ignore 使生产构建不收录该 chunk；DEV=false 死分支由压缩器消除）。 */
async function loadMockApi(): Promise<HostBridgeApi> {
  const mod = await import(/* @vite-ignore */ '../../mocks/mock.ts');
  return mod.createBrowserMockBridge() as HostBridgeApi;
}

/** 是否为已组装的 HostBridgeApi（init 后回填到 window.hostBridge 的标记），区别于 Cef 原生对象。 */
export function isWrappedHostBridge(v: unknown): boolean {
  return !!v && (v as Record<string, unknown>)[WRAPPED_MARK] === true;
}

function getNativeRaw(): unknown {
  try {
    const cur = window.hostBridge as unknown;
    if (cur && !isWrappedHostBridge(cur)) return cur;
  } catch {
    /* ignore */
  }
  return null;
}

function markWrapped(api: HostBridgeApi): void {
  try {
    (api as unknown as Record<string, unknown>)[WRAPPED_MARK] = true;
  } catch {
    /* ignore */
  }
}

function markBridgeMock(on: boolean): void {
  try {
    (window as unknown as { __haloBridgeMock?: boolean }).__haloBridgeMock = on;
    if (typeof sessionStorage !== 'undefined') {
      if (on) sessionStorage.setItem(MOCK_FLAG_KEY, '1');
      else sessionStorage.removeItem(MOCK_FLAG_KEY);
    }
  } catch (e) {
    console.warn('[hostBridge] mock flag failed:', e);
  }
}

/** 是否处于 Mock 模式（localStorage/session 标记 + window 标记，横幅依据）。 */
export function isMockMode(): boolean {
  try {
    if (typeof sessionStorage !== 'undefined' && sessionStorage.getItem(MOCK_FLAG_KEY) === '1') return true;
  } catch (e) {
    console.warn('[hostBridge] mock flag read failed:', e);
  }
  return (window as unknown as { __haloBridgeMock?: boolean }).__haloBridgeMock === true;
}

export function setMockMode(on: boolean): void {
  markBridgeMock(on);
}

/** Mock 横幅是否可见 = Mock 模式。调用方（App.vue）据此渲染红色横幅 + 重试按钮。 */
export function shouldShowMockBanner(): boolean {
  return isMockMode();
}

/** Mock 是否生效（App.vue 红色横幅“桥接未连接（Mock数据）”+重试按钮依据）。 */
export function isBridgeMock(): boolean {
  return isMockMode();
}

export async function initHostBridge(): Promise<HostBridgeApi> {
  window.__haloReceiveHostEvents = dispatchIncomingEvents;
  window.__haloEventBus = haloEventBus;

  if (resolvedBridge) {
    return resolvedBridge;
  }

  if (window.CefSharp && !getNativeRaw()) {
    try {
      await window.CefSharp.BindObjectAsync('hostBridge');
    } catch (err) {
      console.error('[hostBridge] BindObjectAsync failed:', err);
    }
  }

  const nativeRaw = getNativeRaw();

  if (import.meta.env.DEV && wantsMock()) {
    // 显式 ?mock：即使有原生对象也走 Mock（联调复现用）。
    const mockApi = await loadMockApi();
    activeTransport = new MockTransport(mockApi);
    resolvedBridge = createHostBridgeApi(activeTransport);
    assertBridgeVersion(await activeTransport.version());
    markBridgeMock(true);
    console.warn('[hostBridge] BRIDGE_NOT_CONNECTED: 桥接未连接（Mock数据），已降级 Mock（显式标记，非静默）');
  } else if (nativeRaw) {
    activeTransport = new CefTransport(nativeRaw);
    resolvedBridge = createHostBridgeApi(activeTransport);
    assertBridgeVersion(await activeTransport.version());
    markBridgeMock(false);
  } else if (import.meta.env.DEV) {
    // DEV 无原生对象：动态 Mock + 显式标记（横幅），不断连。
    const mockApi = await loadMockApi();
    activeTransport = new MockTransport(mockApi);
    resolvedBridge = createHostBridgeApi(activeTransport);
    markBridgeMock(true);
    console.warn('[hostBridge] BRIDGE_NOT_CONNECTED: 桥接未连接（Mock数据），已降级 Mock（显式标记，非静默）');
  } else {
    // 生产无原生对象：无 Mock 可退，显式抛错（禁止静默），由调用方展示横幅 + 重试。
    markBridgeMock(true);
    const err = toBridgeError('BRIDGE_NOT_CONNECTED', `${MOCK_BANNER_TEXT}：未检测到 CefSharp hostBridge`);
    reportBridgeError(err);
    throw Object.assign(new Error(err.message), err);
  }

  markWrapped(resolvedBridge);
  window.hostBridge = resolvedBridge;
  return resolvedBridge;
}

export function getHostBridge(): HostBridgeApi {
  if (!resolvedBridge) {
    const nativeRaw = getNativeRaw();
    if (nativeRaw) {
      // 同步懒加载仅组装 Cef 通道（版本断言在 initHostBridge 内完成）。
      activeTransport = new CefTransport(nativeRaw);
      resolvedBridge = createHostBridgeApi(activeTransport);
      markWrapped(resolvedBridge);
      markBridgeMock(false);
    } else {
      // Mock 只能经异步 initHostBridge 动态加载；此处显式抛错，禁止静默降级。
      markBridgeMock(true);
      const err = toBridgeError('BRIDGE_NOT_CONNECTED', '桥接未初始化：请先 await initHostBridge()（Mock 仅 DEV&&?mock 经异步加载）');
      reportBridgeError(err);
      throw Object.assign(new Error(err.message), err);
    }
    window.hostBridge = resolvedBridge;
  }
  return resolvedBridge;
}

/** need21-F03：暴露当前传输层（agent turn 经 api/agent 对象参直调；与 getHostBridge 同源，调用前确保已 init）。 */
export function getBridgeTransport(): BridgeTransport {
  getHostBridge();
  if (!activeTransport) {
    const err = toBridgeError('BRIDGE_NOT_CONNECTED', '传输层未就绪：请先 await initHostBridge()');
    reportBridgeError(err);
    throw Object.assign(new Error(err.message), err);
  }
  return activeTransport;
}

/** 兼容旧 import（新代码请用 createHostBridgeApi + Transport）。 */
export function wrapNativeHostBridge(rawBridge: unknown): HostBridgeApi {
  return createHostBridgeApi(new CefTransport(rawBridge));
}

export { createHostBridgeApi } from './api';
export { normalizeGlobalProxyMode, normalizeProviderProxyMode, normalizeLlmProtocol } from './api/normalize';
export { normalizeVisionFlag } from './protocol/types';
export type {
  AgentConfigDocument,
  BridgeResponse,
  ChatImageRef,
  CompactionResultDto,
  ContextBudgetStatusDto,
  CustomHeaderEntry,
  GlobalNetworkSettings,
  GlobalProxyMode,
  GoalMilestoneDto,
  HeaderResolutionContextDto,
  HistoryDeleteResultDto,
  HistoryHealResultDto,
  HistoryTruncateResultDto,
  HostBridgeApi,
  HostEventEnvelope,
  LastSentPromptSnapshot,
  LlmProtocolType,
  LogChannelStatus,
  ManualProxyConfig,
  ModelConfig,
  PromptBlockPreview,
  PromptConfigDocument,
  PromptDiagnosticsDto,
  PromptPreviewDto,
  ProviderConfig,
  ProviderEndpointPreviewDto,
  ProviderProxyMode,
  ProvidersConfigDocument,
  ProxyConnectivityTestResult,
  SessionGoalDto,
  SessionMetadata,
  SessionStreamEventDto,
  SkillMemoryDetail,
  SkillMemoryKind,
  SkillMemoryListItem,
  SkillMemoryOpResult,
  SkillMemoryScope,
  SkillMemoryScopeFilter,
  StorageConfigInfo,
  StorageMigrationResult,
  StorageValidationResult,
  SubPolicyDto,
  SubRejectionInfo,
  SubRuntimeInfo,
  SubSessionDetailDto,
  SystemProxySnapshot,
  SystemRuntimeInfo,
  UiActiveState,
  UnifiedChatMessage,
  UnifiedChatMessageDto,
  UnifiedLlmRequestDto,
  UnifiedStreamEventKind,
  UnifiedTokenUsageDto,
  UnifiedToolCallDto,
  UnifiedToolDefinitionDto,
  UsageDbStatus,
  UsageFilter,
  UsageFilterOptions,
  UsageModelOption,
  UsageOverview,
  UsagePage,
  UsagePreset,
  UsageProviderOption,
  UsageProviderShareItem,
  UsageRowDto,
  UsageSourceOption,
  UsageTrend,
  UsageTrendSeries,
  WorkspaceInfo,
} from './protocol/types';
export type { BridgeError } from './protocol/errors';

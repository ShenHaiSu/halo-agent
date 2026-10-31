// src/Web.Client/src/stores/session.ts
// P10 扩展：provider/providerDisplay/model/modelDisplay/protocol/baseUrl/contextWindow/maxTokens/acceptsImages/includeReasoning/proxyInUse/configured。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';

export type PermissionMode = 'ask' | 'acceptEdits' | 'bypassPermissions' | 'yolo';

const MODE_KEY = 'haloagent.mode';

export const useSessionStore = defineStore('session', () => {
  const id = ref<string>('');
  const title = ref<string>('新会话');
  const cwd = ref<string>('');
  const historyPath = ref<string>('');
  const mode = ref<PermissionMode>('ask');
  const persona = ref<string>('');
  const maxTurns = ref<number>(200);
  const retry = ref<number>(0);
  const usage = ref({ input: 0, output: 0 });
  const windowMaximized = ref(false);
  const configured = ref(false);
  const provider = ref('');
  const providerDisplay = ref('');
  const model = ref('');
  const modelDisplay = ref('');
  const protocol = ref('chat');
  const baseUrl = ref('');
  const contextWindow = ref<number | null>(null);
  const maxTokens = ref<number | null>(null);
  const acceptsImages = ref(false);
  const includeReasoning = ref(true);
  const proxyInUse = ref(false);
  const compactionSummary = ref<string | null>(null);

  const isYolo = computed(() => mode.value === 'yolo');
  const modeLabel = computed(() => {
    switch (mode.value) {
      case 'ask':
        return '询问模式';
      case 'acceptEdits':
        return '自动接受编辑';
      case 'bypassPermissions':
        return '完全放行';
      case 'yolo':
        return '完全放行';
      default:
        return String(mode.value);
    }
  });

  function applyInfo(p: Record<string, unknown>): void {
    if (typeof p.sessionId === 'string') id.value = p.sessionId;
    if (typeof p.id === 'string' && !id.value) id.value = p.id;
    if (typeof p.title === 'string') title.value = p.title;
    if (typeof p.cwd === 'string') cwd.value = p.cwd;
    if (typeof p.historyPath === 'string') historyPath.value = p.historyPath;
    if (typeof p.mode === 'string') mode.value = p.mode as PermissionMode;
    if (typeof p.persona === 'string') persona.value = p.persona;
    if (typeof p.maxTurns === 'number') maxTurns.value = p.maxTurns;
    if (typeof p.retry === 'number') retry.value = p.retry;
    if (typeof p.provider === 'string') provider.value = p.provider;
    if (typeof p.providerDisplay === 'string') providerDisplay.value = p.providerDisplay;
    if (typeof p.model === 'string') model.value = p.model;
    if (typeof p.modelDisplay === 'string') modelDisplay.value = p.modelDisplay;
    if (typeof p.protocol === 'string') protocol.value = p.protocol;
    if (typeof p.baseUrl === 'string') baseUrl.value = p.baseUrl;
    if (typeof p.contextWindow === 'number' || p.contextWindow === null) contextWindow.value = p.contextWindow as number | null;
    if (typeof p.maxTokens === 'number' || p.maxTokens === null) maxTokens.value = p.maxTokens as number | null;
    if (typeof p.acceptsImages === 'boolean') acceptsImages.value = p.acceptsImages;
    if (typeof p.includeReasoning === 'boolean') includeReasoning.value = p.includeReasoning;
    if (typeof p.proxyInUse === 'boolean') proxyInUse.value = p.proxyInUse;
    if (typeof p.configured === 'boolean') configured.value = p.configured;
    const u = p.usage as { input?: number; output?: number } | undefined;
    if (u) usage.value = { input: u.input ?? 0, output: u.output ?? 0 };
  }

  function applySnapshotAgent(a: Record<string, unknown>): void {
    applyInfo(a);
  }

  async function setMode(m: PermissionMode): Promise<void> {
    const prev = mode.value;
    mode.value = m;
    try {
      localStorage.setItem(MODE_KEY, m);
    } catch {
      /* ignore */
    }
    try {
      await transport.request('state.setMode', { mode: m });
    } catch (e) {
      mode.value = prev;
      console.error('[session] 切换权限模式失败', e);
    }
  }

  async function setPersona(name: string): Promise<void> {
    const prev = persona.value;
    persona.value = name;
    try {
      await transport.request('state.setPersona', { name });
    } catch (e) {
      persona.value = prev;
      throw e;
    }
  }

  async function setMaxTurns(n: number): Promise<void> {
    const prev = maxTurns.value;
    maxTurns.value = n;
    try {
      await transport.request('state.setMaxTurns', { value: n });
    } catch (e) {
      maxTurns.value = prev;
      throw e;
    }
  }

  async function setRetry(n: number): Promise<void> {
    const prev = retry.value;
    retry.value = n;
    try {
      await transport.request('state.setRetry', { value: n });
    } catch (e) {
      retry.value = prev;
      throw e;
    }
  }

   async function setCwd(path: string): Promise<void> {
     const prev = cwd.value;
     cwd.value = path;
     try {
       const r = await transport.request<{ cwd?: string }>('session.setCwd', { path });
       // P10-fix3 02：用引擎返回的规范化路径回填，而非输入原文。
       if (r && typeof r.cwd === 'string' && r.cwd) cwd.value = r.cwd;
     } catch (e) {
       cwd.value = prev;
       throw e;
     }
   }

  function restoreMode(): void {
    try {
      const saved = localStorage.getItem(MODE_KEY) as PermissionMode | null;
      if (saved) mode.value = saved;
    } catch {
      /* ignore */
    }
  }

  function setWindowMaximized(v: boolean): void {
    windowMaximized.value = v;
  }

  return {
    id,
    title,
    cwd,
    historyPath,
    mode,
    persona,
    maxTurns,
    retry,
    usage,
    windowMaximized,
    isYolo,
    modeLabel,
    configured,
    provider,
    providerDisplay,
    model,
    modelDisplay,
    protocol,
    baseUrl,
    contextWindow,
    maxTokens,
    acceptsImages,
    includeReasoning,
    proxyInUse,
    compactionSummary,
    applyInfo,
    applySnapshotAgent,
    addUsage: (input: number, output: number): void => {
      usage.value.input += input;
      usage.value.output += output;
    },
    setMode,
    setPersona,
    setMaxTurns,
    setRetry,
    setCwd,
    restoreMode,
    setWindowMaximized,
  };
});

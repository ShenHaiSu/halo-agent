// src/Web.Client/src/stores/session.ts
// P5 扩展：configured / provider / model / contextWindow / compactionSummary / modeLabel。
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
  const model = ref('');
  const baseUrl = ref('');
  const contextWindow = ref(128000);
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
    if (typeof p.model === 'string') model.value = p.model;
    if (typeof p.baseUrl === 'string') baseUrl.value = p.baseUrl;
    if (typeof p.contextWindow === 'number') contextWindow.value = p.contextWindow;
    if (typeof p.configured === 'boolean') configured.value = p.configured;
    const u = p.usage as { input?: number; output?: number } | undefined;
    if (u) usage.value = { input: u.input ?? 0, output: u.output ?? 0 };
  }

  function addUsage(input: number, output: number): void {
    usage.value.input += input;
    usage.value.output += output;
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
      await transport.request('session.setCwd', { path });
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
    model,
    baseUrl,
    contextWindow,
    compactionSummary,
    applyInfo,
    addUsage,
    setMode,
    setPersona,
    setMaxTurns,
    setRetry,
    setCwd,
    restoreMode,
    setWindowMaximized,
  };
});

// src/Web.Client/src/stores/session.ts
// D4-8 当前会话元信息 store。
//
// 分层注意（A-7.5）：本文件不直接 import bridge/host，只经 transport 通信。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';

export type PermissionMode = 'ask' | 'acceptEdits' | 'bypassPermissions' | 'yolo';

const MODE_KEY = 'haloagent.mode';

/**
 * 当前会话元信息 store。
 *
 * ============ 它不管消息内容 ============
 *  消息内容在 messages store。这里只管"会话级配置与统计"：
 *    cwd / mode / persona / maxTurns / retry / usage
 * ============ 权限模式的持久化 ============
 *  权限模式是"用户偏好"，应该跨会话保持。
 *  但**不能**存在 config.json 里（那是引擎的配置），
 *  所以用 localStorage，并在切换时同时通知引擎。
 * ============ ============
 */
export const useSessionStore = defineStore('session', () => {
  const id = ref<string>('');
  const title = ref<string>('新会话');
  const cwd = ref<string>('');
  const mode = ref<PermissionMode>('ask');
  const persona = ref<string>('');
  const maxTurns = ref<number>(200);
  const retry = ref<number>(0);
  const usage = ref({ input: 0, output: 0 });
  const windowMaximized = ref(false);

  const isYolo = computed(() => mode.value === 'yolo');

  /** 从 state.snapshot / session.info 事件刷新 */
  function applyInfo(p: Record<string, unknown>): void {
    if (typeof p.sessionId === 'string') id.value = p.sessionId;
    if (typeof p.id === 'string' && !id.value) id.value = p.id;
    if (typeof p.title === 'string') title.value = p.title;
    if (typeof p.cwd === 'string') cwd.value = p.cwd;
    if (typeof p.mode === 'string') mode.value = p.mode as PermissionMode;
    if (typeof p.persona === 'string') persona.value = p.persona;
    if (typeof p.maxTurns === 'number') maxTurns.value = p.maxTurns;
    if (typeof p.retry === 'number') retry.value = p.retry;

    const u = p.usage as { input?: number; output?: number } | undefined;
    if (u) {
      usage.value = { input: u.input ?? 0, output: u.output ?? 0 };
    }
  }

  function addUsage(input: number, output: number): void {
    usage.value.input += input;
    usage.value.output += output;
  }

  /** 切换权限模式（同时通知引擎 + 持久化） */
  async function setMode(m: PermissionMode): Promise<void> {
    const prev = mode.value;
    mode.value = m;
    try {
      localStorage.setItem(MODE_KEY, m);
    } catch {
      /* 忽略 */
    }
    try {
      await transport.request('state.setMode', { mode: m });
    } catch (e) {
      // 失败回滚 UI（避免显示与实际不一致）
      mode.value = prev;
      console.error('[session] 切换权限模式失败', e);
    }
  }

  async function setPersona(name: string): Promise<void> {
    persona.value = name;
    await transport.request('state.setPersona', { name });
  }

  async function setMaxTurns(n: number): Promise<void> {
    maxTurns.value = n;
    await transport.request('state.setMaxTurns', { value: n });
  }

  async function setRetry(n: number): Promise<void> {
    retry.value = n;
    await transport.request('state.setRetry', { value: n });
  }

  async function setCwd(path: string): Promise<void> {
    cwd.value = path;
    await transport.request('session.setCwd', { path });
  }

  /** 恢复上次的权限模式 */
  function restoreMode(): void {
    try {
      const saved = localStorage.getItem(MODE_KEY) as PermissionMode | null;
      if (saved) mode.value = saved;
    } catch {
      /* 忽略 */
    }
  }

  /** 同步窗口最大化状态（由 TitleBar / host store 调用） */
  function setWindowMaximized(v: boolean): void {
    windowMaximized.value = v;
  }

  return {
    id,
    title,
    cwd,
    mode,
    persona,
    maxTurns,
    retry,
    usage,
    windowMaximized,
    isYolo,
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

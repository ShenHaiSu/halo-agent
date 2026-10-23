import { computed, onMounted, onUnmounted } from 'vue';
import { useDialog, useMessage } from 'naive-ui';
import { haloEventBus } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useSubSessionStore } from '@/stores/subSessionStore';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useAgentStream } from '@features/agent/stores/useAgentStream';
import { useAgentCards } from '@features/agent/stores/useAgentCards';
import { useAgentTelemetry } from '@features/agent/stores/useAgentTelemetry';
import { toSendPayload } from '@shared/utils/composerModel';
import type { ComposerApi } from './useComposer';

export function useSendTurn(c: ComposerApi) {
  const message = useMessage();
  const dialog = useDialog();
  const workspaceStore = useWorkspaceStore();
  const turn = useAgentTurn();
  const stream = useAgentStream();
  const cards = useAgentCards();
  const telemetry = useAgentTelemetry();
  const canSend = computed(() => !!(workspaceStore.activeSessionId || workspaceStore.activeSession?.id));
  const subWaiting = computed(() => cards.subAgents?.waiting === true);
  const subDone = computed(() => cards.subAgents?.done ?? 0);
  const subTotal = computed(() => cards.subAgents?.total ?? 0);
  async function openFirstSub(): Promise<void> {
    try {
      const s = useSubSessionStore();
      const wsId = workspaceStore.activeWorkspaceId || '';
      const parent = workspaceStore.activeSessionId || '';
      if (!wsId || !parent) return;
      await s.refreshList(wsId, parent);
      const first = s.subs[0];
      if (first) await s.enterSub(wsId, parent, first.id);
    } catch {
      /* ignore */
    }
  }
  async function handleCompactCommand(): Promise<void> {
    const sid = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
    c.clearDraft();
    if (!sid) return;
    workspaceStore.touchSessionTime(sid);
    await workspaceStore.compactSession(sid);
  }
  async function handleSend(): Promise<void> {
    if (stream.isGenerating || c.askMasked.value) return;
    if (!canSend.value) return;
    c.readEditorToDraft();
    // need22-F02/F03：仅含 skill 段（无任何文字）也算非空，可发送；
    // text 里带 [[skill:<scope>/<name>]] marker，原样发给后端（后端在发包前展开正文）。
    if (c.isEmptyDraft()) return;
    const payload = await toSendPayload(c.getDraft());
    const textOnly = payload.text.replace(/\[image:[^\]]+\]/g, '').trim();
    if (textOnly === '/compact') {
      await handleCompactCommand();
      return;
    }
    let attachments = payload.attachments;
    let text = payload.text;
    const hasInvalid = c.attachmentList.value.some((a) => a.status !== 'ready');
    if (attachments.length > c.maxImages.value) {
      const kept = attachments.slice(attachments.length - c.maxImages.value);
      const keptIds = new Set(kept.map((a) => a.attId));
      for (const a of attachments.filter((x) => !keptIds.has(x.attId))) text = text.split(`[image:${a.attId}]`).join('');
      attachments = kept;
    }
    if (!text.trim() && attachments.length === 0) {
      if (hasInvalid) message.warning('图片不可用（超限或需重新添加），且无文字可发送');
      return;
    }
    if (hasInvalid && attachments.length === 0 && text.trim()) message.warning('不可用的图片已跳过，仅发送文字');
    if (attachments.length > 0 && !c.supportsVision.value) {
      try {
        if (window.localStorage.getItem('halo-skip-novision-warn') !== '1') {
          const confirmed = await new Promise<boolean>((resolve) => {
            dialog.warning({
              title: '当前模型不支持看图',
              content: '发送时将自动忽略图片（文字仍发送），是否继续？',
              positiveText: '仍发送',
              negativeText: '取消',
              onPositiveClick: () => {
                try {
                  window.localStorage.setItem('halo-skip-novision-warn', '1');
                } catch {
                  /* ignore */
                }
                resolve(true);
                return true;
              },
              onNegativeClick: () => {
                resolve(false);
                return true;
              },
              onClose: () => {
                resolve(false);
                return true;
              },
            });
          });
          if (!confirmed) return;
        }
      } catch {
        /* 确认框异常则继续发送 */
      }
    }
    if (await turn.sendPrompt(text, attachments)) c.clearDraft();
  }
  function handleStop(): void {
    void turn.stopGenerating();
  }
  function handleGlobalKeydown(e: KeyboardEvent): void {
    if (e.key === 'Escape' && stream.isGenerating) handleStop();
  }
  function focusInput(): void {
    c.focusInput();
  }
  let unsubs: Array<() => void> = [];
  onMounted(() => {
    turn.bindTurn();
    stream.bindStream();
    cards.bindCards();
    telemetry.bindTelemetry();
    window.addEventListener('keydown', handleGlobalKeydown);
    c.restoreForSession();
    unsubs.push(
      haloEventBus.on<{ text?: string; sessionId?: string }>('chat:prefill-draft', (payload) => {
        try {
          const sid = String(payload?.sessionId || '');
          if (sid && sid !== (workspaceStore.activeSessionId || '')) return;
          c.insertTextAtCaret(String(payload?.text || ''));
          focusInput();
        } catch {
          /* ignore */
        }
      })
    );
    unsubs.push(
      haloEventBus.on<{ sessionId?: string }>('agent:ask-user-requested', (payload) => {
        try {
          const sid = String(payload?.sessionId || '');
          if (sid && sid !== (workspaceStore.activeSessionId || '')) return;
          c.setAskMasked(true);
        } catch {
          /* ignore */
        }
      })
    );
    const unmask = (payload: { sessionId?: string }) => {
      try {
        const sid = String(payload?.sessionId || '');
        if (sid && sid !== (workspaceStore.activeSessionId || '')) return;
        c.setAskMasked(false);
      } catch {
        /* ignore */
      }
    };
    unsubs.push(haloEventBus.on<{ sessionId?: string }>('agent:ask-user-resolved', unmask));
    unsubs.push(haloEventBus.on<{ sessionId?: string }>('agent:ask-user-dismissed', unmask));
    unsubs.push(
      haloEventBus.on<{ sessionId?: string }>('agent:turn-event', (payload) => {
        try {
          const p = payload as unknown as { sessionId?: string; kind?: string };
          if (p && (p.kind === 'TurnCompleted' || p.kind === 'TurnCancelled' || p.kind === 'TurnError')) unmask(p);
        } catch {
          /* ignore */
        }
      })
    );
  });
  onUnmounted(() => {
    window.removeEventListener('keydown', handleGlobalKeydown);
    for (const u of unsubs) {
      try {
        u();
      } catch {
        /* ignore */
      }
    }
    unsubs = [];
  });
  return { canSend, subWaiting, subDone, subTotal, openFirstSub, handleSend, handleStop };
}
export type SendTurn = ReturnType<typeof useSendTurn>;

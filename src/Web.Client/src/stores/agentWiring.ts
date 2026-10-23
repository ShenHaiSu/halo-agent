/** need21-F05: agent 域跨 store 接线（旧 stores/agentStore.ts 转发部分的唯一继承者，位于 src/stores 接线层）。
 * 旧转发删除后仍需有人做两件事（否则发送/会话同步链路断）：
 * 1. 向新 store 注入 workspace/session/config/askUser 运行时适配（registerAgentRuntimeContext）。
 * 2. session 切换同步 + persistedMessages 投影 + session:switched 事件桥接。
 * 由 App.vue setup 调用一次（幂等）；新代码禁止再建组合转发 store。
 * 注意：本文件是唯一允许直 import 各 store 的 agent 接线点；features/agent/stores 下新 store 仍禁止直调其他 store（eslint 卡死）。
 */
import { watch } from 'vue';
import { getHostBridge, haloEventBus } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useSessionStore } from '@/stores/sessionStore';
import { useConfigStore } from '@/stores/configStore';
import { useAskUserStore } from '@/stores/askUserStore';
import { setActiveSessionId, setActiveWorkspaceId, setFallbackProtocol, setPersistedMessages } from '@features/agent/stores/model';
import { registerAgentRuntimeContext } from '@features/agent/stores/runtime';

let wiringDone = false;

/** 注册 agent 运行时接线（幂等；需在 pinia 安装后调用）。 */
export function ensureAgentWiring(): void {
  const ws = useWorkspaceStore();
  const session = useSessionStore();
  const config = useConfigStore();
  const ask = useAskUserStore();
  const wid = (): string => ws.activeWorkspaceId || ws.activeWorkspace?.id || '';
  registerAgentRuntimeContext({
    workspaceId: wid,
    providerId: () => config.activeProvider?.id || '',
    modelId: () => config.activeModel?.id || '',
    maxImages: () => Math.max(1, Math.min(16, Math.trunc(Number(config.activeModel?.maxImagesPerTurn) || 4))),
    findMessageContent: (_sid, msgId) => String(ws.sessionMessages.find((m) => String(m.id) === String(msgId))?.content || ''),
    truncateHistory: async (w, s, mid, rm) => {
      const r = await getHostBridge().truncateSessionHistory(w, s, mid, rm);
      return { ok: r.ok && !!r.data, replayText: String((r.data as unknown as { replayText?: unknown })?.replayText || '') };
    },
    loadSessionMessages: (sid) => ws.loadSessionMessages(sid),
    refreshBudget: () => ws.refreshBudget(),
    touchSessionTime: (sid, iso) => ws.touchSessionTime(sid, iso),
    generateTitle: (sid) => {
      void ws.generateTitleAfterFirstRound(sid).catch(() => undefined);
    },
    reconcileTimes: () => {
      void (ws as unknown as { reconcileSessionTimes?: () => Promise<void> }).reconcileSessionTimes?.().catch(() => undefined);
    },
    askPending: (sid) => ask.hasPending(sid),
    clearAskPending: (sid) => ask.clearPending(sid),
    // need23-R1：把后端已落盘的单条 tool 消息增量合并进视图（避免全量重拉）
    mergePersistedMessage: (sid, msg) => ws.mergePersistedMessage(sid, msg as import('@shared/bridge/protocol/domain').UnifiedChatMessage),
  });
  if (wiringDone) return;
  wiringDone = true;
  const syncSession = (): void => {
    const sid = session.activeSessionId || ws.activeSessionId || ws.activeSession?.id || '';
    setActiveSessionId(sid);
    setActiveWorkspaceId(wid());
    setFallbackProtocol(config.activeModel?.protocol || 'chat');
    haloEventBus.emitLocal('session:switched', { sessionId: sid, workspaceId: wid() });
  };
  syncSession();
  setPersistedMessages([...(ws.sessionMessages || [])]);
  watch(() => session.activeSessionId, syncSession);
  watch(() => ws.activeSessionId, syncSession);
  watch(
    () => ws.sessionMessages,
    (msgs) => setPersistedMessages(msgs || [])
  );
}

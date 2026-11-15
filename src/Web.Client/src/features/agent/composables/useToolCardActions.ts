import { computed } from 'vue';
import type { ComputedRef } from 'vue';
import { useMessage } from 'naive-ui';
import type { AgentToolCard } from '@features/agent/stores/model';
import { getHostBridge } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useSubSessionStore } from '@/stores/subSessionStore';

/**
 * 工具卡副作用收敛点：终止进程、复制输出、打开原图、进入子会话。
 * 视图层只调这里的方法，不再直连桥接、会话 store 与消息反馈。
 */
export function useToolCardActions(getCard: () => AgentToolCard) {
  const message = useMessage();
  const workspaceStore = useWorkspaceStore();

  const displayOutput: ComputedRef<string> = computed(() => getCard().resultText || '');

  async function handleKill(): Promise<void> {
    const sessionId = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
    if (!sessionId) return;
    try {
      await getHostBridge().cancelAgentTurn(sessionId);
      message.info('已发送终止指令');
    } catch {
      message.error('终止指令发送失败');
    }
  }

  async function handleCopyText(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      message.success('已复制终端输出');
    } catch {
      message.info('复制失败，请手动选择文本');
    }
  }

  async function handleOpenOrigin(target: string): Promise<void> {
    if (!target) {
      message.warning('无可用原图路径');
      return;
    }
    try {
      await getHostBridge().openPathInExplorer(target);
    } catch {
      message.error('打开原图失败');
    }
  }

  // 子会话定位：结果文本里只认约定的 subSessionId 形态。
  // TODO(bridge): 后端给出结构化子会话字段后删掉这组正则。
  const subId: ComputedRef<string> = computed(() => {
    try {
      const out = displayOutput.value || '';
      const m =
        out.match(/"subSessionId"\s*:\s*"([^"]+)"/) ||
        out.match(/subSessionId[:=]\s*([A-Za-z0-9_-]+)/) ||
        out.match(/(sess_[A-Za-z0-9_-]+)/);
      if (m && m[1]) return m[1];
    } catch (e) {
      console.warn('[tool-card] sub session id parse failed:', e);
    }
    return '';
  });

  async function openSub(): Promise<void> {
    if (!subId.value) return;
    try {
      const s = useSubSessionStore();
      const wsId = workspaceStore.activeWorkspaceId || '';
      const parent = workspaceStore.activeSessionId || '';
      if (wsId && parent) await s.enterSub(wsId, parent, subId.value);
    } catch (e) {
      console.warn('[tool-card] open sub session failed:', e);
    }
  }

  return { displayOutput, handleKill, handleCopyText, handleOpenOrigin, subId, openSub };
}

export type ToolCardActions = ReturnType<typeof useToolCardActions>;

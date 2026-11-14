import { ref } from 'vue';
import type { ChatImageRef, UnifiedChatMessage } from '@shared/bridge/hostBridge';
import { getHostBridge } from '@shared/bridge/hostBridge';
import { orderedImagesOf } from '@features/session/utils/turnGroup';
import { pendingPreviewOf } from '@features/session/utils/pendingUser';
import { useAgentTurn } from '@features/agent/stores/useAgentTurn';
import { useWorkspaceStore } from '@/stores/workspaceStore';

/**
 * 图片派生：消息图片行排序 + 乐观预览直出 + 缩略图合批懒取。
 * 失败进丢失集合，不阻塞渲染。
 */
export function useTimelineImages() {
  const turn = useAgentTurn();
  const workspaceStore = useWorkspaceStore();

  const thumbnailCache = ref<Record<string, string>>({});
  const lostImageIds = ref<Record<string, boolean>>({});
  const thumbPending = new Set<string>();
  const thumbQueue: string[] = [];
  let thumbIdleScheduled = false;
  const THUMB_BATCH = 6;

  const msgImages = (msg: UnifiedChatMessage | undefined): ChatImageRef[] => orderedImagesOf(msg);

  const isPendingMessage = (msg: UnifiedChatMessage | undefined): boolean => {
    const id = String(msg?.id || '');
    return id.startsWith('pending-');
  };

  const pendingStatusOf = (msg: UnifiedChatMessage | undefined): string => {
    try {
      if (!isPendingMessage(msg)) return '';
      const pending = turn.activeTurn?.pendingUser;
      if (pending && String(pending.key || '') === String(msg?.id || '')) return String(pending.status || 'sending');
      return 'sending';
    } catch (e) {
      console.warn('[timeline:images] pending status failed:', e);
      return '';
    }
  };

  const hasLostImage = (msg: UnifiedChatMessage | undefined): boolean => {
    if (isPendingMessage(msg)) return false;
    return msgImages(msg).some((i) => lostImageIds.value[i.imageId] || i.lost === true);
  };

  const isImageLost = (img: ChatImageRef): boolean => {
    try {
      const pending = turn.activeTurn?.pendingUser;
      if (pending && pending.previews && String(pending.previews[String(img?.imageId || '')] || '')) return false;
    } catch (e) {
      console.warn('[timeline:images] preview check failed:', e);
    }
    return img.lost === true || lostImageIds.value[img.imageId] === true;
  };

  function scheduleThumbIdle(): void {
    if (thumbIdleScheduled) return;
    thumbIdleScheduled = true;
    const run = (): void => {
      thumbIdleScheduled = false;
      const batch = thumbQueue.splice(0, THUMB_BATCH);
      for (const id of batch) void fetchThumb(id);
      if (thumbQueue.length > 0) scheduleThumbIdle();
    };
    try {
      const idle = (window as unknown as { requestIdleCallback?: (cb: () => void, opts?: { timeout: number }) => number })
        .requestIdleCallback;
      if (typeof idle === 'function') idle.call(window, run, { timeout: 300 });
      else window.setTimeout(run, 0);
    } catch (e) {
      console.warn('[timeline:images] idle schedule failed:', e);
      window.setTimeout(run, 0);
    }
  }

  function enqueueThumb(imageId: string): void {
    if (!thumbQueue.includes(imageId)) thumbQueue.push(imageId);
    scheduleThumbIdle();
  }

  async function fetchThumb(imageId: string): Promise<void> {
    try {
      const ws = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
      const sid = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
      const res = await getHostBridge().getImageThumbnail(ws, sid, imageId);
      if (res.ok && typeof res.data === 'string' && res.data) {
        thumbnailCache.value = { ...thumbnailCache.value, [imageId]: res.data };
      } else {
        lostImageIds.value = { ...lostImageIds.value, [imageId]: true };
      }
    } catch (e) {
      console.warn('[timeline:images] thumb fetch failed:', e);
      lostImageIds.value = { ...lostImageIds.value, [imageId]: true };
    } finally {
      thumbPending.delete(imageId);
    }
  }

  function thumbOf(img: ChatImageRef): string {
    try {
      const pending = turn.activeTurn?.pendingUser;
      if (pending) {
        const preview = pendingPreviewOf(pending, String(img?.imageId || ''));
        if (preview) return preview;
      }
    } catch (e) {
      console.warn('[timeline:images] preview read failed:', e);
    }
    if (thumbnailCache.value[img.imageId] || img.lost === true || lostImageIds.value[img.imageId]) {
      return thumbnailCache.value[img.imageId] || '';
    }
    if (thumbPending.has(img.imageId)) return '';
    thumbPending.add(img.imageId);
    enqueueThumb(img.imageId);
    return '';
  }

  return {
    thumbnailCache,
    lostImageIds,
    msgImages,
    isPendingMessage,
    pendingStatusOf,
    hasLostImage,
    isImageLost,
    thumbOf,
    fetchThumb,
  };
}

export type TimelineImages = ReturnType<typeof useTimelineImages>;

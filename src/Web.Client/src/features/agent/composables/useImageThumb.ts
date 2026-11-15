import { ref } from 'vue';
import type { ChatImageRef } from '@shared/bridge/hostBridge';
import { getHostBridge } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';

/**
 * 缩略图共享通道：工具卡图片行与时间轴图片行共用同一桥接懒取路径。
 * 命中缓存直出，未命中后台单取；失败只记空，调用方按文件名 chip 降级，不崩渲染。
 */
async function fetchImageThumb(workspaceId: string, sessionId: string, imageId: string): Promise<string | null> {
  try {
    const res = await getHostBridge().getImageThumbnail(workspaceId, sessionId, imageId);
    if (res.ok && typeof res.data === 'string' && res.data) return res.data;
  } catch (e) {
    console.warn('[image-thumb] fetch failed:', imageId, e);
  }
  return null;
}

export function useImageThumb() {
  const workspaceStore = useWorkspaceStore();
  const thumbCache = ref<Record<string, string>>({});
  const thumbPending = new Set<string>();

  function thumbOf(img: ChatImageRef): string {
    const hit = thumbCache.value[img.imageId];
    if (hit) return hit;
    if (thumbPending.has(img.imageId)) return '';
    thumbPending.add(img.imageId);
    void (async () => {
      try {
        const ws = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
        const sid = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
        const data = await fetchImageThumb(ws, sid, img.imageId);
        if (data) thumbCache.value = { ...thumbCache.value, [img.imageId]: data };
      } finally {
        thumbPending.delete(img.imageId);
      }
    })();
    return '';
  }

  return { thumbCache, thumbOf };
}

export type ImageThumb = ReturnType<typeof useImageThumb>;

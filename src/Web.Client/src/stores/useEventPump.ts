// src/Web.Client/src/stores/useEventPump.ts
// D4-8 事件拉取循环（P1 决策的 pull 模式）。
import { onBeforeUnmount, onMounted } from 'vue';
import { transport } from '@/transport';
import { useMessagesStore } from '@/stores/messages';
import { dispatchFrames } from '@/stores/eventBridge';

/**
 * 事件拉取循环（P1 决策的 pull 模式）。
 *
 * ============ 为什么用 pull 而不是 push ============
 *  ① 天然背压：前端处理慢了就少拉几次
 *  ② 不会重入：一次 call 只处理一批
 *  ③ 崩溃恢复简单：只需保存 lastSeq
 * ============ 间隔为什么是"自适应" ============
 *  空闲时 200ms 一次（省 CPU），有事件到达后 30ms 再拉（低延迟）。
 *  固定 16ms 会让空闲时也每秒 60 次跨进程调用，浪费。
 * ============ 失败退避 ============
 *  连续失败指数退避（400/800/1600…），封顶 2s；卸载时停止。
 * ============ ============
 */
export const IDLE_INTERVAL = 200;
export const BUSY_INTERVAL = 30;
export const MAX_BACKOFF = 2000;

export function useEventPump(): void {
  const messages = useMessagesStore();
  let stopped = false;
  let timer: number | null = null;
  let consecutiveErrors = 0;

  async function tick(): Promise<void> {
    if (stopped) return;

    try {
      const res = await transport.pollEvents(messages.lastSeq);
      consecutiveErrors = 0;

      if (res.events.length > 0) {
        dispatchFrames(res.events);
      }

      // 有事件 → 立刻再拉（低延迟）；无事件 → 空闲间隔
      const delay = res.events.length > 0 ? BUSY_INTERVAL : IDLE_INTERVAL;
      timer = window.setTimeout(() => void tick(), delay);
    } catch (e) {
      consecutiveErrors++;
      // 指数退避，最多 2s
      const backoff = Math.min(IDLE_INTERVAL * 2 ** consecutiveErrors, MAX_BACKOFF);
      console.warn(`[pump] pollEvents 失败（第 ${consecutiveErrors} 次），${backoff}ms 后重试`, e);
      timer = window.setTimeout(() => void tick(), backoff);
    }
  }

  onMounted(() => {
    void tick();
  });

  onBeforeUnmount(() => {
    stopped = true;
    if (timer !== null) window.clearTimeout(timer);
  });
}

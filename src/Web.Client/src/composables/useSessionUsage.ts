// src/Web.Client/src/composables/useSessionUsage.ts
// P23：右抽屉本会话用量（复用 usage.summary({sessionId})，不新增 RPC）。
// llmCalls/turns 另从 session/messages store 直接读，不进本 composable 的 loading 态。
import { getCurrentInstance, onBeforeUnmount, ref, watch } from 'vue';
import { transport } from '@/transport';
import { useMessagesStore } from '@/stores/messages';
import { useSessionStore } from '@/stores/session';
import type { UsageSummary } from '@/components/usage/usageTypes';

export function useSessionUsage() {
  const session = useSessionStore();
  const messages = useMessagesStore();

  const summary = ref<UsageSummary | null>(null);
  const loading = ref(false);
  const failed = ref(false);
  const degraded = ref(false);

  // 旧响应丢弃（沿用 UsageAnalyticsPage queryId 模式）+ 500ms 防抖。
  let queryId = 0;
  let timer: ReturnType<typeof setTimeout> | null = null;

  async function refresh(): Promise<void> {
    const sid = session.id;
    // session.id 为空不发请求，直接零态。
    if (!sid) {
      summary.value = null;
      failed.value = false;
      loading.value = false;
      return;
    }
    const id = ++queryId;
    loading.value = true;
    try {
      const s = await transport.request<UsageSummary>('usage.summary', { sessionId: sid });
      if (id !== queryId) return; // 旧响应丢弃（含切会话后的过期响应）
      summary.value = s;
      failed.value = false;
      degraded.value = s.degraded === true;
    } catch {
      if (id !== queryId) return;
      failed.value = true;
    } finally {
      if (id === queryId) loading.value = false;
    }
  }

  function scheduleRefresh(ms: number): void {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      timer = null;
      void refresh();
    }, ms);
  }

  // 切会话：禁止复用上一会话 summary，先清零再重查。
  const stopId = watch(
    () => session.id,
    () => {
      summary.value = null;
      failed.value = false;
      degraded.value = false;
      void refresh();
    },
  );
  // 主刷新点：turn.finished（turns.length 变化）/ compact / contextCleared（turns 清零）。
  const stopTurns = watch(
    () => messages.turns.length,
    () => scheduleRefresh(400),
  );
  // 辅刷新点：usage 事件（每 round 到达，DB 落盘在 turn 结束；防抖避免高频刷库）。
  const stopCalls = watch(
    () => session.llmCalls,
    () => scheduleRefresh(500),
  );

  function dispose(): void {
    stopId();
    stopTurns();
    stopCalls();
    if (timer) clearTimeout(timer);
    // 丢弃飞行中的旧响应
    queryId++;
  }

  // 组件外调用（如单测）时无实例可绑，跳过自动卸载（调用方自行管理）。
  if (getCurrentInstance()) onBeforeUnmount(dispose);

  return { summary, loading, failed, degraded, refresh, dispose };
}

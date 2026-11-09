import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import type { ContractEventFrame } from '@/contract/agent';
import type { FrameTransport } from '@/transport/types';

/**
 * Usage view over the frame boundary. Totals come from `usage.query` and
 * accumulate `usage.tick` deltas between queries; the snapshot water mark
 * stays comparable with the queried totals. Draft form state lives with the
 * caller, never here.
 *
 * Display rule: frame numbers are estimates and never drive the budget bar.
 * The bar keeps reading the legacy budget source; frame values only reach
 * console telemetry plus the degraded flag below.
 */
export const useFrameUsageStore = defineStore('frameUsage', () => {
  const transport = ref<FrameTransport | null>(null);
  const inputTokens = ref(0);
  const outputTokens = ref(0);
  const cachedTokens = ref(0);
  const retryCount = ref(0);
  const maxTokens = ref(128000);
  const usagePercentage = ref(0);
  const occupancyState = ref('normal');
  const usedIsEstimated = ref(true);
  const degraded = ref(false);
  const lastError = ref('');
  /** Monotonic journal cursor; stale or replayed frames are dropped. */
  const lastSeq = ref(0);
  /** Per-turn high-water marks so a tick racing a query counts once. */
  const waters = ref(new Map<string, number>());

  /** Budget bar source stays on the legacy chain by design. */
  const budgetSource = 'legacy' as const;

  const totalTokens = computed(() => inputTokens.value + outputTokens.value);

  function bind(frameTransport: FrameTransport): void {
    transport.value = frameTransport;
  }

  function reset(): void {
    inputTokens.value = 0;
    outputTokens.value = 0;
    cachedTokens.value = 0;
    retryCount.value = 0;
    maxTokens.value = 128000;
    usagePercentage.value = 0;
    occupancyState.value = 'normal';
    usedIsEstimated.value = true;
    degraded.value = false;
    lastError.value = '';
    lastSeq.value = 0;
    waters.value = new Map<string, number>();
  }

  function readNumber(record: Record<string, unknown>, key: string): number {
    const value = record[key];
    return typeof value === 'number' && Number.isFinite(value) ? Math.floor(value) : 0;
  }

  function readText(record: Record<string, unknown>, key: string): string {
    const value = record[key];
    return typeof value === 'string' ? value : '';
  }

  /**
   * Fold one `usage.tick` delta into the running totals.
   * Returns false when the frame is stale or a duplicate.
   */
  function observe(frame: ContractEventFrame): boolean {
    if (!frame || frame.t !== 'evt' || frame.k !== 'usage.tick') return false;
    const seq = typeof frame.seq === 'number' ? Math.floor(frame.seq) : 0;
    if (seq > 0 && seq <= lastSeq.value) return false;
    const params = (frame.p ?? {}) as Record<string, unknown>;
    const turnId = readText(params, 'turnId');
    const input = readNumber(params, 'inputTokens') + readNumber(params, 'input');
    const output = readNumber(params, 'outputTokens') + readNumber(params, 'output');
    if (turnId) {
      const seen = waters.value.get(turnId) ?? 0;
      const stamp = input + output + readNumber(params, 'cachedTokens');
      if (stamp <= seen) {
        if (seq > 0) lastSeq.value = Math.max(lastSeq.value, seq);
        return false;
      }
      waters.value.set(turnId, stamp);
    }
    if (seq > 0) lastSeq.value = Math.max(lastSeq.value, seq);
    inputTokens.value += input;
    outputTokens.value += output;
    cachedTokens.value += readNumber(params, 'cachedTokens');
    retryCount.value += readNumber(params, 'retryCount');
    return true;
  }

  async function refresh(): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    const reply = await channel.invoke('usage.query', {});
    if (!reply.ok) {
      lastError.value = reply.error?.msg || 'Usage query failed.';
      degraded.value = true;
      return false;
    }
    const result = reply.result ?? {};
    const nextInput = readNumber(result, 'inputTokens') + readNumber(result, 'input');
    const nextOutput = readNumber(result, 'outputTokens') + readNumber(result, 'output');
    const nextCached = readNumber(result, 'cachedTokens');
    const nextRetry = readNumber(result, 'retryCount');
    const drift = Math.abs(nextInput + nextOutput - totalTokens.value);
    if (drift > 0 && totalTokens.value > 0 && drift / Math.max(1, totalTokens.value) > 0.05) {
      degraded.value = true;
      console.info('[frameUsage] query drift over 5 percent, keeping legacy bar', {
        drift,
        frameTotal: nextInput + nextOutput,
        localTotal: totalTokens.value,
      });
    } else if (!reply.ok) {
      degraded.value = true;
    } else {
      degraded.value = false;
    }
    inputTokens.value = nextInput;
    outputTokens.value = nextOutput;
    cachedTokens.value = nextCached;
    retryCount.value = nextRetry;
    maxTokens.value = readNumber(result, 'maxTokens') || 128000;
    usagePercentage.value =
      typeof result['usagePercentage'] === 'number' && Number.isFinite(result['usagePercentage'])
        ? (result['usagePercentage'] as number)
        : 0;
    occupancyState.value = readText(result, 'occupancyState') || 'normal';
    usedIsEstimated.value = typeof result['usedIsEstimated'] === 'boolean' ? (result['usedIsEstimated'] as boolean) : true;
    waters.value = new Map<string, number>();
    lastError.value = '';
    return true;
  }

  async function clear(): Promise<boolean> {
    const channel = transport.value;
    if (!channel) {
      lastError.value = 'Frame transport is not bound.';
      return false;
    }
    const reply = await channel.invoke('usage.clear', {});
    if (!reply.ok) {
      lastError.value = reply.error?.msg || 'Usage clear failed.';
      return false;
    }
    reset();
    return true;
  }

  return {
    inputTokens,
    outputTokens,
    cachedTokens,
    retryCount,
    maxTokens,
    usagePercentage,
    occupancyState,
    usedIsEstimated,
    degraded,
    lastError,
    lastSeq,
    waters,
    budgetSource,
    totalTokens,
    bind,
    reset,
    observe,
    refresh,
    clear,
  };
});

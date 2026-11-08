import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import type { ContractEventFrame } from '@/contract/agent';
import type { FrameTransport } from '@/transport/types';

/**
 * Usage view over the frame boundary. Totals come from `usage.query` and
 * accumulate `usage.tick` deltas between queries; the snapshot water mark
 * stays comparable with the queried totals. Draft form state lives with the
 * caller, never here.
 */
export const useFrameUsageStore = defineStore('frameUsage', () => {
  const transport = ref<FrameTransport | null>(null);
  const inputTokens = ref(0);
  const outputTokens = ref(0);
  const cachedTokens = ref(0);
  const retryCount = ref(0);
  const lastError = ref('');

  const totalTokens = computed(() => inputTokens.value + outputTokens.value);

  function bind(frameTransport: FrameTransport): void {
    transport.value = frameTransport;
  }

  function reset(): void {
    inputTokens.value = 0;
    outputTokens.value = 0;
    cachedTokens.value = 0;
    retryCount.value = 0;
    lastError.value = '';
  }

  function readNumber(record: Record<string, unknown>, key: string): number {
    const value = record[key];
    return typeof value === 'number' && Number.isFinite(value) ? Math.floor(value) : 0;
  }

  /** Fold one `usage.tick` delta into the running totals. */
  function observe(frame: ContractEventFrame): void {
    if (!frame || frame.t !== 'evt' || frame.k !== 'usage.tick') return;
    const params = (frame.p ?? {}) as Record<string, unknown>;
    inputTokens.value += readNumber(params, 'inputTokens') + readNumber(params, 'input');
    outputTokens.value += readNumber(params, 'outputTokens') + readNumber(params, 'output');
    cachedTokens.value += readNumber(params, 'cachedTokens');
    retryCount.value += readNumber(params, 'retryCount');
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
      return false;
    }
    const result = reply.result ?? {};
    inputTokens.value = readNumber(result, 'inputTokens') + readNumber(result, 'input');
    outputTokens.value = readNumber(result, 'outputTokens') + readNumber(result, 'output');
    cachedTokens.value = readNumber(result, 'cachedTokens');
    retryCount.value = readNumber(result, 'retryCount');
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
    lastError,
    totalTokens,
    bind,
    reset,
    observe,
    refresh,
    clear,
  };
});

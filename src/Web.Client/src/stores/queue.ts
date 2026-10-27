// src/Web.Client/src/stores/queue.ts
// P5：排队消息（引擎权威对齐）。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';

const MAX_QUEUE = 5;

export const useQueueStore = defineStore('queue', () => {
  const items = ref<string[]>([]);
  const authoritative = ref<number | null>(null);
  const full = computed(() => items.value.length >= MAX_QUEUE);

  function syncFromEngine(queueLength: number): void {
    authoritative.value = queueLength;
    const m = items.value.length;
    if (m > queueLength) items.value = items.value.slice(m - queueLength);
    else if (m < queueLength) console.debug('[queue] 本地队列少于引擎', m, queueLength);
  }

  async function push(text: string): Promise<boolean> {
    if (full.value) return false;
    const r = await transport.request<{ queued?: boolean }>('agent.queue', { text });
    if (!r.queued && r.queued !== undefined) return false;
    items.value.push(text);
    return true;
  }

  async function remove(index: number): Promise<boolean> {
    const t = items.value[index];
    if (t === undefined) return false;
    const r = await transport.request<{ removed?: boolean }>('agent.unqueue', { text: t });
    if (r.removed ?? true) items.value.splice(index, 1);
    return true;
  }

  function clear(): void {
    items.value = [];
    authoritative.value = null;
  }

  return { items, authoritative, full, max: MAX_QUEUE, syncFromEngine, push, remove, clear };
});

// src/Web.Client/src/stores/permissions.ts
// P5 扩展：queue / draftAfterDeny / clear 别名。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';

export interface PendingAsk {
  askId: string;
  kind: string;
  title: string;
  body: string;
  warning: string | null;
  allowAlways: boolean;
  used?: number | null;
}

export const usePermissionsStore = defineStore('permissions', () => {
  const pending = ref<PendingAsk | null>(null);
  const answering = ref(false);
  const queue = ref<string[]>([]);
  const draftAfterDeny = ref<string | null>(null);

  const hasPending = computed(() => pending.value !== null);
  const isHighRisk = computed(() => Boolean(pending.value?.warning));
  const canAlways = computed(() => Boolean(pending.value?.allowAlways) && !pending.value?.warning);

  function setPending(p: PendingAsk): void {
    pending.value = p;
  }
  function setAsk(p: PendingAsk): void {
    pending.value = p;
  }
  function clearPending(): void {
    pending.value = null;
    answering.value = false;
  }
  function clear(): void {
    pending.value = null;
    answering.value = false;
  }

  async function answer(allow: boolean, always = false): Promise<void> {
    const p = pending.value;
    if (!p || answering.value) return;
    answering.value = true;
    try {
      const effectiveAlways = canAlways.value ? always : false;
      await transport.request('agent.answer', { askId: p.askId, allow, always: effectiveAlways });
      pending.value = null;
    } catch (e) {
      console.error('[permissions] 回答失败', e);
      throw e;
    } finally {
      answering.value = false;
    }
  }

  function allow(): Promise<void> {
    return answer(true, false);
  }
  function allowAlways(): Promise<void> {
    return answer(true, true);
  }
  function deny(): Promise<void> {
    return answer(false, false);
  }

  return {
    pending,
    answering,
    queue,
    draftAfterDeny,
    hasPending,
    isHighRisk,
    canAlways,
    setPending,
    setAsk,
    clearPending,
    clear,
    answer,
    allow,
    allowAlways,
    deny,
  };
});

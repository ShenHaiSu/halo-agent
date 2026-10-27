// src/Web.Client/src/stores/permissions.ts
// D4-8 权限询问 store。
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { transport } from '@/transport';

export interface PendingAsk {
  askId: string;
  kind: string;
  title: string;
  body: string;
  /** 高危警示（非空时"总是允许"必须被禁用） */
  warning: string | null;
  allowAlways: boolean;
}

/**
 * 权限询问 store。
 *
 * ============ 单待答询问（P2 已定的设计） ============
 *  同一时刻只允许一个待答询问。新的来临时旧的作废。
 *  理由：多个并发询问会让用户不知道在答哪个；
 *  而且 agent 循环本来就是逐个工具执行，不会真的并发询问。
 * ============ warning 存在时禁用 allowAlways ============
 *  这是安全兜底：高危命令（如 rm -rf /）不允许"总是允许"。
 *  即使前端传了 always=true，也要忽略。
 * ============ ============
 */
export const usePermissionsStore = defineStore('permissions', () => {
  const pending = ref<PendingAsk | null>(null);
  const answering = ref(false);

  const hasPending = computed(() => pending.value !== null);
  const isHighRisk = computed(() => Boolean(pending.value?.warning));
  /** "总是允许"是否可用 */
  const canAlways = computed(() => Boolean(pending.value?.allowAlways) && !pending.value?.warning);

  function setPending(p: PendingAsk): void {
    pending.value = p;
  }

  function clearPending(): void {
    pending.value = null;
    answering.value = false;
  }

  /**
   * 回答询问。
   * @param allow 是否允许
   * @param always 是否"总是允许"（仅在 canAlways 时生效）
   */
  async function answer(allow: boolean, always = false): Promise<void> {
    const p = pending.value;
    if (!p || answering.value) return;

    answering.value = true;
    try {
      // 安全降级：高危时忽略 always
      const effectiveAlways = canAlways.value ? always : false;
      await transport.request('agent.answer', {
        askId: p.askId,
        allow,
        always: effectiveAlways,
      });
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
    hasPending,
    isHighRisk,
    canAlways,
    setPending,
    clearPending,
    answer,
    allow,
    allowAlways,
    deny,
  };
});

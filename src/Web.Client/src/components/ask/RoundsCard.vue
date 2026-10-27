<!-- src/Web.Client/src/components/ask/RoundsCard.vue -->
<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue';
import { usePermissionsStore } from '@/stores/permissions';
import { useSessionStore } from '@/stores/session';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import HButton from '@/components/base/HButton.vue';

/**
 * 轮次询问卡（见 01 §7.5）。
 * 三按钮：停止 / 再继续 200 轮 / 继续，焦点默认在"停止"（防失控）。
 */
const perms = usePermissionsStore();
const session = useSessionStore();
const toast = useToast();

const stopBtn = ref<InstanceType<typeof HButton> | null>(null);

const used = computed(() => {
  const n = Number(perms.pending?.body ?? 0);
  return Number.isFinite(n) && n > 0 ? n : session.maxTurns;
});

onMounted(() => {
  void nextTick(() => {
    const el = stopBtn.value?.$el;
    if (el instanceof HTMLElement) el.focus();
  });
});

async function stop(): Promise<void> {
  if (!perms.pending || perms.answering) return;
  try {
    await perms.answer(false, false);
  } catch (e) {
    toast.error(`操作失败：${(e as Error).message}`);
  }
}

async function cont(): Promise<void> {
  if (!perms.pending || perms.answering) return;
  try {
    await perms.answer(true, false);
  } catch (e) {
    toast.error(`操作失败：${(e as Error).message}`);
  }
}

async function extend(): Promise<void> {
  if (!perms.pending || perms.answering) return;
  try {
    await perms.answer(true, false);
    await transport.request('state.setMaxTurns', { value: used.value + 200 });
  } catch (e) {
    toast.error(`操作失败：${(e as Error).message}`);
  }
}
</script>

<template>
  <div v-if="perms.pending" class="rounds-card" role="alertdialog" aria-labelledby="rounds-title">
    <div class="rounds-head">
      <HaloIcon name="refresh" :size="16" />
      <h3 id="rounds-title">已进行 {{ used }} 轮，是否继续？</h3>
    </div>
    <p class="rounds-desc">模型已连续执行多轮工具调用。继续可能产生大量改动。</p>
    <div class="rounds-actions">
      <span class="spacer" />
      <HButton ref="stopBtn" variant="secondary" size="sm" :disabled="perms.answering" @click="stop"
        >停止</HButton
      >
      <HButton variant="ghost" size="sm" :disabled="perms.answering" @click="extend"
        >再继续 200 轮</HButton
      >
      <HButton variant="primary" size="sm" :disabled="perms.answering" @click="cont">继续</HButton>
    </div>
  </div>
</template>

<style scoped>
.rounds-card {
  border: 1px solid hsl(var(--warning) / 0.6);
  border-radius: var(--radius-md);
  background: hsl(var(--bg-elevated));
  padding: var(--space-3);
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  box-shadow: var(--shadow-md);
}
.rounds-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  color: hsl(var(--warning));
}
.rounds-head h3 {
  font-size: var(--text-base);
  font-weight: 600;
  margin: 0;
  color: hsl(var(--fg));
}
.rounds-desc {
  margin: 0;
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
.rounds-actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}
.spacer {
  flex: 1;
}
</style>

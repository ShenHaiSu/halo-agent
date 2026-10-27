<!-- src/Web.Client/src/components/ask/PermissionCard.vue -->
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { usePermissionsStore } from '@/stores/permissions';
import { useAskFocus, type AskFocusTarget } from './useAskFocus';
import { askTitle, askIcon } from './askFormat';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import HButton from '@/components/base/HButton.vue';
import HSpinner from '@/components/base/HSpinner.vue';

const perms = usePermissionsStore();

const allowBtn = ref<InstanceType<typeof HButton> | null>(null);
const denyBtn = ref<InstanceType<typeof HButton> | null>(null);
const stopBtn = ref<InstanceType<typeof HButton> | null>(null);

const ask = computed(() => perms.pending);
const title = computed(() => askTitle(ask.value?.kind ?? '', ask.value?.title ?? ''));
const icon = computed(() => askIcon(ask.value?.kind ?? ''));
const hasWarning = computed(() => !!ask.value?.warning);
/** * warning 存在时绝不显示"总是允许"（P2 二次降级在 UI 对齐） */
const canAlways = computed(() => ask.value?.allowAlways === true && !hasWarning.value);

/** A-2：有 warning → 拒绝；否则允许一次 */
const focusTarget = computed<AskFocusTarget>(() => (hasWarning.value ? 'deny' : 'allow'));

useAskFocus(
  computed(() => ask.value?.askId ?? null),
  focusTarget,
  { allow: allowBtn, deny: denyBtn, stop: stopBtn }
);

async function answer(allow: boolean, always = false): Promise<void> {
  if (!ask.value || perms.answering) return;
  await perms.answer(allow, always);
}

/**
 * 询问出现后焦点在按钮上，不在 textarea 里，
 * 所以 Esc/Enter 必须在 document 级监听（见 04 §4.4）。
 * 注意：焦点已在某个按钮上时不拦截 Enter（避免与按钮原生行为双触发）。
 */
function onDocKeydown(e: KeyboardEvent): void {
  if (e.isComposing) return;
  if (e.key === 'Escape') {
    e.preventDefault();
    void answer(false);
  } else if (e.key === 'Enter') {
    const active = document.activeElement;
    const isButton = active instanceof HTMLButtonElement;
    if (isButton) return;
    e.preventDefault();
    void answer(true);
  }
}

onMounted(() => document.addEventListener('keydown', onDocKeydown, { capture: true }));
onBeforeUnmount(() => document.removeEventListener('keydown', onDocKeydown, { capture: true }));

/** 供 Composer 的 keydown 转发（Esc / Enter） */
defineExpose({
  deny: () => answer(false),
  allowOnce: () => answer(true),
  hasWarning,
});
</script>

<template>
  <div
    v-if="ask"
    class="ask-card"
    :class="{ 'has-warning': hasWarning }"
    role="alertdialog"
    aria-modal="false"
    aria-labelledby="ask-title"
    aria-describedby="ask-body"
    data-ask-card
  >
    <div class="ask-head">
      <HaloIcon :name="icon" :size="16" class="ask-icon" />
      <h3 id="ask-title" class="ask-title">{{ title }}</h3>
      <span class="ask-id" :title="`询问 ID：${ask.askId}`">{{ ask.askId }}</span>
    </div>

    <pre id="ask-body" class="ask-body">{{ ask.body }}</pre>

    <div v-if="hasWarning" class="ask-warning" role="note">
      <HaloIcon name="alert" :size="14" />
      <span>{{ ask.warning }}</span>
    </div>

    <div class="ask-actions">
      <span class="ask-hint">
        <template v-if="perms.answering"><HSpinner size="xs" /> 正在提交…</template>
        <template v-else>Esc 拒绝 · Enter 允许</template>
      </span>
      <span class="ask-spacer" />
      <HButton
        ref="denyBtn"
        variant="secondary"
        size="sm"
        :disabled="perms.answering"
        @click="answer(false)"
      >
        拒绝
      </HButton>
      <HButton
        v-if="canAlways"
        variant="ghost"
        size="sm"
        :disabled="perms.answering"
        @click="answer(true, true)"
        >总是允许</HButton
      >
      <HButton
        ref="allowBtn"
        variant="primary"
        size="sm"
        :disabled="perms.answering"
        @click="answer(true)"
        >允许一次</HButton
      >
    </div>
  </div>
</template>

<style scoped>
.ask-card {
  border: 1px solid hsl(var(--warning) / 0.6);
  border-radius: var(--radius-md);
  background: hsl(var(--bg-elevated));
  padding: var(--space-3);
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  box-shadow: var(--shadow-md);
  min-width: 0;
}
.ask-card.has-warning {
  border-color: hsl(var(--danger) / 0.7);
}
.ask-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
}
.ask-icon {
  color: hsl(var(--warning));
  flex: none;
}
.has-warning .ask-icon {
  color: hsl(var(--danger));
}
.ask-title {
  font-size: var(--text-base);
  font-weight: 600;
  margin: 0;
  min-width: 0;
}
.ask-id {
  margin-left: auto;
  font-family: var(--font-mono);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 120px;
}
.ask-body {
  margin: 0;
  padding: var(--space-2);
  background: hsl(var(--bg-subtle));
  border-radius: var(--radius-sm);
  font-family: var(--font-mono);
  font-size: var(--text-xs);
  max-height: 180px;
  overflow: auto;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
.ask-warning {
  display: flex;
  align-items: flex-start;
  gap: var(--space-2);
  padding: var(--space-2);
  border-radius: var(--radius-sm);
  background: hsl(var(--danger-subtle));
  color: hsl(var(--danger));
  font-size: var(--text-xs);
  line-height: 1.5;
}
.ask-actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
}
.ask-hint {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
.ask-spacer {
  flex: 1;
  min-width: 0;
}
</style>

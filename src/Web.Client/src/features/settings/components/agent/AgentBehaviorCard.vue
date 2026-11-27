<template>
  <n-card
    size="small"
    :bordered="true"
    :class="['agent-behavior-card', { 'is-open': open, 'is-disabled': disabled }]"
    :data-testid="testId"
  >
    <template #header>
      <div class="agent-card-header">
        <button
          ref="toggleRef"
          class="agent-card-toggle"
          type="button"
          :aria-expanded="open"
          :aria-controls="bodyId"
          :aria-label="toggleLabel"
          :disabled="disabled"
          :data-testid="toggleTestId"
          @click="emit('update:open', !open)"
        >
          <span class="agent-card-title">{{ title }}</span>
          <span
            v-if="dirty"
            class="agent-card-dirty"
            role="img"
            aria-label="有未保存更改"
            title="有未保存更改"
            :data-testid="dirtyTestId"
          />
          <span class="agent-card-summary">{{ summary }}</span>
          <ChevronDown class="agent-card-arrow" aria-hidden="true" />
        </button>
        <div class="agent-card-extra" @click.stop>
          <slot name="header-extra" />
        </div>
      </div>
    </template>

    <div v-show="open" :id="bodyId" ref="bodyRef" :aria-hidden="!open" class="agent-card-body" v-bind="inertAttrs">
      <slot />
    </div>
  </n-card>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, useId, watch } from 'vue';
import { ChevronDown } from 'lucide-vue-next';

const props = defineProps<{
  title: string;
  summary?: string;
  open: boolean;
  dirty?: boolean;
  disabled?: boolean;
  testId?: string;
}>();

const emit = defineEmits<{
  (e: 'update:open', value: boolean): void;
}>();

const instanceId = useId();
const toggleRef = ref<HTMLButtonElement | null>(null);
const bodyRef = ref<HTMLDivElement | null>(null);

const bodyId = computed(() => `${props.testId?.trim() || `agent-card-${instanceId}`}-body`);
const toggleLabel = computed(() => `${props.open ? '折叠' : '展开'}${props.title}${props.dirty ? '，有未保存更改' : ''}`);

/** inert 是布尔属性：必须折叠时才出现，展开时渲染 inert="false" 反而会锁死正文交互。 */
const inertAttrs = computed(() => (props.open ? {} : { inert: true }));

function deriveTestId(kind: 'toggle' | 'dirty'): string | undefined {
  const testId = props.testId?.trim();
  if (!testId) return undefined;
  const semanticPrefix = `agent-card-${kind}-`;
  return testId.startsWith('agent-card-') ? `${semanticPrefix}${testId.slice('agent-card-'.length)}` : `${testId}-${kind}`;
}

const toggleTestId = computed(() => deriveTestId('toggle'));
const dirtyTestId = computed(() => deriveTestId('dirty'));

watch(
  () => props.open,
  (isOpen, wasOpen) => {
    if (isOpen || !wasOpen) return;
    const body = bodyRef.value;
    if (!body?.contains(document.activeElement)) return;

    void nextTick(() => {
      if (!props.open) toggleRef.value?.focus();
    });
  }
);
</script>

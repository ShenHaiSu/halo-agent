<!-- src/Web.Client/src/components/base/HRadio.vue -->
<script setup lang="ts">
import { computed, inject, type Ref } from 'vue';

interface GroupCtx {
  name: Ref<string>;
  modelValue: Ref<string | number | null | undefined>;
  disabled: Ref<boolean>;
  select(v: string | number): void;
}

const props = defineProps<{
  value: string | number;
  disabled?: boolean;
  label?: string;
}>();

const group = inject<GroupCtx | null>('hRadioGroup', null);
if (!group) {
  // 开发期用错时的明确报错
  throw new Error('<HRadio> 必须放在 <HRadioGroup> 内使用');
}

// throw 守卫之后收窄为非空：后文闭包/函数内直接用 ctx，避免 TS18047。
const ctx: GroupCtx = group;
const checked = computed(() => ctx.modelValue.value === props.value);
const isDisabled = computed(() => props.disabled || ctx.disabled.value);

function onChange() {
  if (isDisabled.value) return;
  ctx.select(props.value);
}
</script>

<template>
  <label
    class="inline-flex cursor-pointer items-center gap-2 select-none"
    :class="isDisabled ? 'cursor-not-allowed opacity-60' : ''"
  >
    <span class="relative flex h-4 w-4 shrink-0 items-center justify-center">
      <input
        type="radio"
        class="peer absolute inset-0 h-full w-full cursor-inherit opacity-0"
        :name="ctx.name.value"
        :value="String(value)"
        :checked="checked"
        :disabled="isDisabled"
        @change="onChange"
      />
      <span
        class="pointer-events-none flex h-4 w-4 items-center justify-center rounded-full border transition-colors duration-fast ease-out peer-focus-visible:ring-2 peer-focus-visible:ring-accent/40 peer-focus-visible:ring-offset-1 peer-focus-visible:ring-offset-bg"
        :class="checked ? 'border-accent' : 'border-border-strong'"
      >
        <span v-if="checked" class="h-2 w-2 rounded-full bg-accent" />
      </span>
    </span>
    <span v-if="label || $slots.default" class="text-sm text-fg">
      <slot>{{ label }}</slot>
    </span>
  </label>
</template>

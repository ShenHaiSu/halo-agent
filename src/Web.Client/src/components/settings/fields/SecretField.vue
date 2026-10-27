<!-- src/Web.Client/src/components/settings/fields/SecretField.vue -->
<!-- API Key 字段（01 §9.3）：password 默认可切明文；已设置显示占位；保存后由父级清空。 -->
<script setup lang="ts">
import { ref } from 'vue';
import HInput from '@/components/base/HInput.vue';
import HIconButton from '@/components/base/HIconButton.vue';

const props = defineProps<{
  modelValue: string;
  /** 后端只给布尔（永不回显明文） */
  apiKeySet: boolean;
}>();

const emit = defineEmits<{ 'update:modelValue': [string] }>();

const reveal = ref(false);

function onInput(v: string): void {
  emit('update:modelValue', v);
}
</script>

<template>
  <div class="secret">
    <HInput
      :model-value="props.modelValue"
      :type="reveal ? 'text' : 'password'"
      :placeholder="props.apiKeySet ? '••••••••（已设置，留空不修改）' : '请输入 API Key'"
      autocomplete="off"
      spellcheck="false"
      @update:model-value="onInput"
    />
    <HIconButton
      :icon="'eye'"
      :label="reveal ? '隐藏明文' : '显示明文'"
      size="sm"
      :pressed="reveal"
      @click="reveal = !reveal"
    />
  </div>
  <p v-if="props.apiKeySet && !props.modelValue" class="secret-hint">
    已设置（为安全起见不显示明文）。
  </p>
</template>

<style scoped>
.secret {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  min-width: 0;
}
.secret-hint {
  margin: 0;
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
</style>

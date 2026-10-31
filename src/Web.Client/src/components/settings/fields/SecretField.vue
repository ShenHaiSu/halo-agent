<!-- API Key 字段（01 §9.3）：password 默认可切明文；已设置显示占位；保存后由父级清空。 -->
<!-- 横向组合：HInput + 明文切换 + 清除（清除仅已设置时显示，由父级经 clearKey 受控）。 -->
<script setup lang="ts">
import { ref } from 'vue';
import HButton from '@/components/base/HButton.vue';
import HIconButton from '@/components/base/HIconButton.vue';
import HInput from '@/components/base/HInput.vue';

const props = defineProps<{
  modelValue: string;
  /** 后端只给布尔（永不回显明文） */
  apiKeySet: boolean;
  /** 父级受控：是否已标记清除 */
  clearKey?: boolean;
  /** 是否显示清除按钮（通常 apiKeySet 才给 true） */
  showClear?: boolean;
}>();

const emit = defineEmits<{ 'update:modelValue': [string]; 'update:clearKey': [boolean] }>();

function onToggleClear(): void {
  emit('update:clearKey', !props.clearKey);
}

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
    <HButton
      v-if="props.showClear"
      size="sm"
      variant="ghost"
      class="clear-btn"
      :aria-pressed="props.clearKey ? 'true' : undefined"
      @click="onToggleClear"
      >{{ props.clearKey ? '取消清除' : '清除' }}</HButton
    >
  </div>
</template>

<style scoped>
.secret {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  min-width: 0;
}
.secret > :first-child {
  flex: 1;
  min-width: 0;
}
.clear-btn {
  flex: none;
  white-space: nowrap;
}
</style>

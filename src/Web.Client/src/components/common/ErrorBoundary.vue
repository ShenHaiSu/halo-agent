<!-- src/Web.Client/src/components/common/ErrorBoundary.vue -->
<!-- 局部错误边界（05 §8.1）：onErrorCaptured 阻止冒泡 + 重试（重挂载）+ 复制错误详情。 -->
<script setup lang="ts">
import { onErrorCaptured, ref } from 'vue';
import HEmpty from '@/components/base/HEmpty.vue';
import HButton from '@/components/base/HButton.vue';
import { hostBridge } from '@/bridge/host';
import { useToast } from '@/composables/useToast';

const toast = useToast();
const err = ref<{ message: string; stack: string; at: string } | null>(null);
/** 重挂载 key：重试时递增，强制 slot 内容重建。 */
const retryKey = ref(0);

onErrorCaptured((e) => {
  const error = e as Error;
  err.value = {
    message: error.message || String(error),
    stack: error.stack ?? '(无堆栈)',
    at: new Date().toISOString(),
  };
  console.error('[ErrorBoundary]', e);
  return false;
});

async function copyDetail(): Promise<void> {
  if (!err.value) return;
  const text = [
    `时间：${err.value.at}`,
    `消息：${err.value.message}`,
    '',
    '堆栈：',
    err.value.stack,
    '',
    `UA：${navigator.userAgent}`,
  ].join('\n');
  try {
    await hostBridge.copyText(text);
    toast.success('错误详情已复制');
  } catch {
    toast.error('复制失败');
  }
}

function retry(): void {
  err.value = null;
  retryKey.value++;
}
</script>

<template>
  <HEmpty v-if="err" icon="alert" title="这一部分出错了" :description="err.message">
    <template #action>
      <div class="eb-actions">
        <HButton size="sm" variant="secondary" @click="retry">重试</HButton>
        <HButton size="sm" variant="ghost" @click="copyDetail">复制错误详情</HButton>
      </div>
    </template>
  </HEmpty>
  <slot v-else :key="retryKey" />
</template>

<style scoped>
.eb-actions {
  display: flex;
  gap: var(--space-2);
  margin-top: var(--space-2);
  justify-content: center;
}
</style>

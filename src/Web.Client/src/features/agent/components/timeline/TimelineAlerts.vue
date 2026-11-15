<template>
  <div class="mt-1.5 space-y-1.5" data-testid="user-turn-notice">
    <n-alert v-if="notice" type="info" :show-icon="false" class="text-xs">{{ notice }}</n-alert>
    <n-alert v-if="error" type="error" class="text-xs" data-testid="turn-error-alert">{{ error }}</n-alert>
    <div v-if="showRetry" class="flex items-center gap-2">
      <n-button size="small" type="error" ghost data-testid="turn-retry-btn" @click="emit('retry')">重试本轮</n-button>
      <n-button size="small" quaternary data-testid="turn-error-dismiss" @click="emit('dismiss')">忽略</n-button>
    </div>
  </div>
</template>

<script setup lang="ts">
/** 轮内通知区：行内 notice / 末轮错误 / 重试入口，收敛两处重复模板。 */
defineProps<{ notice: string; error: string; showRetry: boolean }>();

const emit = defineEmits<{ retry: []; dismiss: [] }>();
</script>

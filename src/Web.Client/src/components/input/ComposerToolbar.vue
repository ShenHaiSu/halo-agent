<!-- src/Web.Client/src/components/input/ComposerToolbar.vue -->
<script setup lang="ts">
import HButton from '@/components/base/HButton.vue';
import HIconButton from '@/components/base/HIconButton.vue';
import StopButton from './StopButton.vue';

defineProps<{
  busy: boolean;
  canSend: boolean;
  askPending: boolean;
  cwd: string;
}>();

defineEmits<{
  mention: [];
  slash: [];
  submit: [];
  cancel: [];
}>();
</script>

<template>
  <div class="composer-bottom">
    <HIconButton
      icon="at"
      label="引用文件"
      size="sm"
      :disabled="askPending"
      @click="$emit('mention')"
    />
    <HIconButton
      icon="slash"
      label="斜杠命令"
      size="sm"
      :disabled="askPending"
      @click="$emit('slash')"
    />
    <span class="cwd" :title="cwd">{{ cwd || '未选择工作目录' }}</span>
    <span class="bottom-spacer" />
    <StopButton v-if="busy" @click="$emit('cancel')" />
    <HButton
      variant="primary"
      size="sm"
      icon="send"
      :disabled="!canSend"
      aria-label="发送消息"
      @click="$emit('submit')"
      >{{ busy ? '排队' : '发送' }}</HButton
    >
  </div>
</template>

<style scoped>
.composer-bottom {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  min-width: 0;
}
.cwd {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-family: var(--font-mono);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 40%;
}
.bottom-spacer {
  flex: 1;
  min-width: 0;
}
</style>

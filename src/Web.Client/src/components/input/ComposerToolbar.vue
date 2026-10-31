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
   pickCwd: [];
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
     <button type="button" class="cwd" :title="cwd || '未选择工作目录'" :disabled="busy || askPending" @click="$emit('pickCwd')">{{ cwd || '未选择工作目录' }}</button>
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
   background: transparent;
   border: 1px solid transparent;
   border-radius: var(--radius-sm);
   padding: 2px 6px;
   cursor: pointer;
   text-align: left;
 }
 .cwd:hover:not(:disabled) {
   border-color: hsl(var(--border-strong));
   color: hsl(var(--fg));
 }
 .cwd:disabled {
   cursor: not-allowed;
   opacity: 0.6;
 }
  .cwd:focus-visible {
    outline: 2px solid hsl(var(--accent));
    outline-offset: 1px;
  }
.bottom-spacer {
  flex: 1;
  min-width: 0;
}
</style>

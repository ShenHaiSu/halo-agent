<!-- src/Web.Client/src/components/settings/fields/DangerZone.vue -->
<!-- 危险操作区：高权限确认 / 清除历史，全部经 HDialog 二次确认（I-6）。 -->
<script setup lang="ts">
import { ref } from 'vue';
import HButton from '@/components/base/HButton.vue';
import HDialog from '@/components/base/HDialog.vue';
import HCheckbox from '@/components/base/HCheckbox.vue';

const emit = defineEmits<{
  'enable-bypass': [];
  'clear-history': [];
}>();

const bypassOpen = ref(false);
const ackRisk = ref(false);
const clearOpen = ref(false);

function openBypass(): void {
  ackRisk.value = false;
  bypassOpen.value = true;
}

function confirmBypass(): void {
  if (!ackRisk.value) return;
  bypassOpen.value = false;
  emit('enable-bypass');
}

function confirmClear(): void {
  clearOpen.value = false;
  emit('clear-history');
}
</script>

<template>
  <div class="danger" role="group" aria-label="危险操作">
    <div class="drow">
      <div class="dinfo">
        <span class="dtitle">完全放行模式</span>
        <span class="ddesc">所有工具调用不再询问。模型可以删除你的文件。</span>
      </div>
      <HButton size="sm" variant="danger" @click="openBypass">启用</HButton>
    </div>
    <div class="drow">
      <div class="dinfo">
        <span class="dtitle">清除会话历史</span>
        <span class="ddesc">仅清除界面与上下文，历史文件保留。</span>
      </div>
      <HButton size="sm" variant="secondary" @click="clearOpen = true">清除</HButton>
    </div>

    <HDialog
      :open="bypassOpen"
      title="确认启用完全放行？"
      width="sm"
      @update:open="bypassOpen = $event"
    >
      <p class="dp">
        启用后，所有工具调用<strong>不再询问</strong>，模型可以直接读写、删除文件并执行命令。
      </p>
      <label class="ack">
        <HCheckbox v-model="ackRisk" />
        <span>我已了解风险，并确认当前目录中没有不可恢复的数据</span>
      </label>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="bypassOpen = false">取消</HButton>
        <HButton variant="danger" size="sm" :disabled="!ackRisk" @click="confirmBypass"
          >确认启用</HButton
        >
      </template>
    </HDialog>

    <HDialog :open="clearOpen" title="清除会话历史？" width="sm" @update:open="clearOpen = $event">
      <p class="dp">仅清除界面与上下文，历史文件保留。当前未发送的内容不会被清除。</p>
      <template #footer>
        <HButton variant="secondary" size="sm" @click="clearOpen = false">取消</HButton>
        <HButton variant="danger" size="sm" @click="confirmClear">清除</HButton>
      </template>
    </HDialog>
  </div>
</template>

<style scoped>
.danger {
  border: 1px solid hsl(var(--danger) / 0.4);
  border-radius: var(--radius-md);
  padding: var(--space-2) var(--space-3);
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}
.drow {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-width: 0;
}
.dinfo {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 1px;
}
.dtitle {
  font-size: var(--text-sm);
  font-weight: 500;
  color: hsl(var(--fg));
}
.ddesc {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
}
.dp {
  margin: 0 0 var(--space-2);
  font-size: var(--text-sm);
  line-height: 1.6;
}
.ack {
  display: flex;
  align-items: flex-start;
  gap: var(--space-2);
  font-size: var(--text-sm);
  cursor: pointer;
}
</style>

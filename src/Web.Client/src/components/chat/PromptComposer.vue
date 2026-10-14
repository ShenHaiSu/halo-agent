<template>
  <div class="shrink-0 px-6 pb-4 pt-2" data-testid="prompt-composer">
    <div
      class="max-w-4xl mx-auto rounded-xl border border-subtle bg-surface focus-within:border-blue-500/80 focus-within:ring-2 focus-within:ring-blue-500/15 transition-all shadow-lg p-2.5 space-y-2"
    >
      <n-input
        ref="inputRef"
        v-model:value="draft"
        type="textarea"
        :autosize="{ minRows: 2, maxRows: 8 }"
        :bordered="false"
        placeholder="输入任务指令，Enter 发送，Shift+Enter 换行..."
        class="!bg-transparent text-xs"
        :disabled="agentStore.isGenerating"
        @keydown="handleKeydown"
      />
      <div class="flex items-center justify-end gap-2">

        <n-button
          v-if="!agentStore.isGenerating"
          type="primary"
          size="small"
          :disabled="!draft.trim() || !canSend"
          @click="handleSend"
        >
          <template #icon>
            <SendHorizontal class="w-3.5 h-3.5" />
          </template>
          发送 (Enter)
        </n-button>
        <n-button v-else type="error" size="small" @click="handleStop">
          <template #icon>
            <Square class="w-3.5 h-3.5" />
          </template>
          停止生成 (Esc)
        </n-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { SendHorizontal, Square } from 'lucide-vue-next';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentStore } from '@/stores/agentStore';

const workspaceStore = useWorkspaceStore();
const agentStore = useAgentStore();

const draft = ref<string>('');
const inputRef = ref<unknown>(null);

const canSend = computed(() => !!(workspaceStore.activeSessionId || workspaceStore.activeSession?.id));

function handleSend(): void {
  const text = draft.value.trim();
  if (!text || agentStore.isGenerating) return;
  draft.value = '';
  void agentStore.sendPrompt(text);
}

function handleStop(): void {
  void agentStore.stopGenerating();
}

function handleKeydown(e: KeyboardEvent): void {
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    handleSend();
  } else if (e.key === 'Escape' && agentStore.isGenerating) {
    e.preventDefault();
    handleStop();
  }
}

function handleGlobalKeydown(e: KeyboardEvent): void {
  if (e.key === 'Escape' && agentStore.isGenerating) {
    handleStop();
  }
}

onMounted(() => {
  agentStore.bind();
  window.addEventListener('keydown', handleGlobalKeydown);
});

onUnmounted(() => {
  window.removeEventListener('keydown', handleGlobalKeydown);
});
</script>

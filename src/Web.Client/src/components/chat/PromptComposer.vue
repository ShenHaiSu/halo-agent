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
      <div class="flex items-center justify-between gap-2">
        <div class="flex items-center gap-2 text-[11px] text-secondary min-w-0">
          <span class="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-elevated font-mono truncate" :title="workspacePath">
            <FolderGit2 class="w-3 h-3 text-accent shrink-0" />
            <span class="truncate">{{ workspaceName }}</span>
          </span>
          <span class="hidden sm:inline-flex items-center gap-1 px-2 py-0.5 rounded bg-elevated font-mono shrink-0">
            <Terminal class="w-3 h-3 text-emerald-400" />
            默认Runner: CMD优先/PY次之
          </span>
        </div>

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
import { storeToRefs } from 'pinia';
import { FolderGit2, SendHorizontal, Square, Terminal } from 'lucide-vue-next';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentStore } from '@/stores/agentStore';

const workspaceStore = useWorkspaceStore();
const agentStore = useAgentStore();
const { activeWorkspace } = storeToRefs(workspaceStore);

const draft = ref<string>('');
const inputRef = ref<unknown>(null);

const workspaceName = computed(() => activeWorkspace.value?.name || '未绑定工作区');
const workspacePath = computed(() => activeWorkspace.value?.rootPath || '');
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

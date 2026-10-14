<template>
  <div class="flex-1 min-h-0 overflow-y-auto" data-testid="message-timeline">
    <div :class="chatWidthMode === 'full' ? 'chat-timeline-full px-4 py-4 space-y-4' : 'chat-timeline-centered px-4 py-4 space-y-4'">
      <!-- 空态 -->
      <div v-if="persistedMessages.length === 0 && !agentStore.hasStreamingContent && agentStore.toolCards.length === 0" class="py-10 text-center space-y-2">
        <div class="flex justify-center">
          <Bot class="w-8 h-8 text-accent" />
        </div>
        <p class="text-sm text-primary font-medium">在下方输入任务指令，Agent 将自动编排工具完成</p>
        <p class="text-xs text-secondary font-mono">默认 Runner：CMD 优先 / PYTHON 次之 · 禁用 PowerShell 专属语法</p>
      </div>

      <!-- 持久化消息 -->
      <template v-for="msg in persistedMessages" :key="msg.id">
        <!-- User -->
        <div v-if="msg.role === 'user'" class="flex justify-end">
          <div class="max-w-[85%] rounded-lg bg-elevated border border-subtle px-3.5 py-2.5 text-[13px] text-primary whitespace-pre-wrap break-words select-text">
            {{ msg.content }}
          </div>
        </div>

        <!-- Assistant -->
        <div v-else-if="msg.role === 'assistant'" class="space-y-2">
          <!-- 思考折叠舱 -->
          <div v-if="msg.reasoningContent" class="rounded-r-md my-2 bg-slate-900/60 border-l-2 border-indigo-500/70 overflow-hidden">
            <div class="flex items-center justify-between px-2.5 h-7 cursor-pointer select-none" @click="toggleReasoning(msg.id)">
              <span class="flex items-center gap-1.5 text-[11px] text-slate-300">
                <BrainCircuit class="w-3.5 h-3.5 text-indigo-400" />
                <span>深度思考过程</span>
              </span>
              <span class="flex items-center gap-1 text-[11px] text-slate-400">
                <span>{{ collapsedReasoning[msg.id] ? '点击展开' : '点击折叠' }}</span>
                <ChevronDown class="w-3 h-3 transition-transform" :class="collapsedReasoning[msg.id] ? '' : 'rotate-180'" />
              </span>
            </div>
            <n-collapse-transition :show="!collapsedReasoning[msg.id]">
              <div class="px-3 pb-2 text-[12px] text-slate-300 whitespace-pre-wrap break-words select-text max-h-64 overflow-y-auto">
                {{ msg.reasoningContent }}
              </div>
            </n-collapse-transition>
          </div>

          <!-- 正文 -->
          <div
            v-if="msg.content"
            class="text-[13px] leading-relaxed text-primary break-words select-text markdown-body"
            v-html="renderMarkdown(msg.content)"
          />

          <!-- ToolCall 提示（持久化消息中的工具调用名） -->
          <div v-if="msg.toolCalls && msg.toolCalls.length > 0" class="flex flex-wrap gap-1.5">
            <n-tag v-for="tc in msg.toolCalls" :key="tc.id" size="tiny" :bordered="false" type="info" class="font-mono">
              {{ toolNameOf(tc) }}
            </n-tag>
          </div>

          <!-- Telemetry footer -->
          <div v-if="hasTelemetry(msg)" class="text-[11px] text-secondary font-mono flex flex-wrap items-center gap-x-3 gap-y-0.5">
            <span>协议: {{ protocolOf(msg) }}</span>
            <span>In: {{ msg.inputTokens }} (Cache {{ msg.cachedTokens }})</span>
            <span>Out: {{ msg.outputTokens }}</span>
          </div>
        </div>

        <!-- Tool result -->
        <div v-else-if="msg.role === 'tool'" class="rounded-md border border-subtle bg-surface px-3 py-2 text-[12px]">
          <div class="flex items-center gap-1.5 text-[11px] text-secondary font-mono mb-1">
            <Wrench class="w-3 h-3" />
            <span>{{ msg.name || 'tool' }}</span>
            <span class="opacity-60">{{ msg.toolCallId }}</span>
          </div>
          <div class="font-mono text-[11px] text-slate-300 whitespace-pre-wrap break-all max-h-48 overflow-y-auto select-text">
            {{ truncateToolText(msg.content || '') }}
          </div>
        </div>

        <!-- System -->
        <div v-else class="text-[11px] text-secondary text-center font-mono px-4">
          {{ msg.content }}
        </div>
      </template>

      <!-- 流式思考折叠舱 -->
      <div v-if="agentStore.streamingReasoning" class="rounded-r-md my-2 bg-slate-900/60 border-l-2 border-indigo-500/70 overflow-hidden">
        <div class="flex items-center justify-between px-2.5 h-7 select-none">
          <span class="flex items-center gap-1.5 text-[11px] text-slate-300">
            <BrainCircuit class="w-3.5 h-3.5 text-indigo-400" />
            <span>思考中...</span>
          </span>
          <Loader2 class="w-3.5 h-3.5 text-indigo-400 animate-spin" />
        </div>
        <div class="px-3 pb-2 text-[12px] text-slate-300 whitespace-pre-wrap break-words select-text max-h-64 overflow-y-auto">
          {{ agentStore.streamingReasoning }}
        </div>
      </div>

      <!-- 流式工具卡片组 -->
      <div v-if="agentStore.toolCards.length > 0" class="space-y-2">
        <ToolCallCard v-for="card in agentStore.toolCards" :key="card.callId" :card="card" />
      </div>

      <!-- 流式正文 -->
      <div
        v-if="agentStore.streamingText"
        class="text-[13px] leading-relaxed text-primary break-words select-text markdown-body"
        v-html="renderMarkdown(agentStore.streamingText)"
      />
      <div v-else-if="agentStore.streamingActive && agentStore.isGenerating" class="flex items-center gap-2 text-xs text-secondary">
        <Loader2 class="w-3.5 h-3.5 animate-spin" />
        <span>Agent 推理中...</span>
      </div>

      <!-- 中断/截断/错误提示 -->
      <n-alert v-if="agentStore.turnNotice" type="info" :show-icon="false" class="text-xs">
        {{ agentStore.turnNotice }}
      </n-alert>
      <n-alert v-if="agentStore.lastError" type="error" class="text-xs">
        {{ agentStore.lastError }}
      </n-alert>

      <!-- Telemetry footer（本轮） -->
      <div v-if="showLiveTelemetry" class="text-[11px] text-secondary font-mono flex flex-wrap items-center gap-x-3 gap-y-0.5 border-t border-subtle pt-2">
        <span>协议: {{ agentStore.telemetry.protocol === 'resp' ? 'RESP (/v1/responses)' : 'CHAT (/v1/chat/completions)' }}</span>
        <span v-if="agentStore.telemetry.ttftMs !== null">首字: {{ agentStore.telemetry.ttftMs }}ms</span>
        <span v-if="agentStore.telemetry.totalMs !== null">总耗时: {{ (agentStore.telemetry.totalMs / 1000).toFixed(1) }}s</span>
        <span>In: {{ agentStore.telemetry.inputTokens }} (Cache {{ cachePercent }})</span>
        <span>Out: {{ agentStore.telemetry.outputTokens }}</span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue';
import { Bot, BrainCircuit, ChevronDown, Loader2, Wrench } from 'lucide-vue-next';
import MarkdownIt from 'markdown-it';
import hljs from 'highlight.js';
import { storeToRefs } from 'pinia';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentStore } from '@/stores/agentStore';
import { useAppearanceStore } from '@/stores/appearanceStore';
import ToolCallCard from '@/components/chat/ToolCallCard.vue';
import type { UnifiedChatMessage } from '@/bridge/hostBridge';

const workspaceStore = useWorkspaceStore();
const agentStore = useAgentStore();
const appearanceStore = useAppearanceStore();
const { sessionMessages } = storeToRefs(workspaceStore);
const chatWidthMode = computed(() => appearanceStore.chatWidthMode);

const persistedMessages = computed<UnifiedChatMessage[]>(() => sessionMessages.value || []);
const collapsedReasoning = reactive<Record<string, boolean>>({});

function toggleReasoning(id: string): void {
  collapsedReasoning[id] = !collapsedReasoning[id];
}

const md: MarkdownIt = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
  highlight: (code: string, lang: string): string => {
    try {
      if (lang && hljs.getLanguage(lang)) {
        return hljs.highlight(code, { language: lang }).value;
      }
      return hljs.highlightAuto(code).value;
    } catch {
      return '';
    }
  },
});

function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function renderMarkdown(content: string): string {
  try {
    return md.render(content || '');
  } catch {
    return `<pre>${escapeHtml(content || '')}</pre>`;
  }
}

function toolNameOf(tc: { function?: { name?: string } }): string {
  return (tc.function && tc.function.name) || 'tool';
}

function hasTelemetry(msg: UnifiedChatMessage): boolean {
  return (msg.inputTokens !== null && msg.inputTokens !== undefined) || (msg.outputTokens !== null && msg.outputTokens !== undefined);
}

function protocolOf(msg: UnifiedChatMessage): string {
  void msg;
  return agentStore.telemetry.protocol === 'resp' ? 'RESP' : 'CHAT';
}

function truncateToolText(s: string): string {
  if (s.length > 4000) return s.slice(0, 4000) + '\n...（输出过长已截断）';
  return s;
}

const showLiveTelemetry = computed(() => {
  const t = agentStore.telemetry;
  return t.ttftMs !== null || t.totalMs !== null || t.inputTokens > 0 || t.outputTokens > 0;
});

const cachePercent = computed(() => {
  const t = agentStore.telemetry;
  if (t.inputTokens <= 0) return '0%';
  return `${Math.round((t.cachedTokens / t.inputTokens) * 100)}%`;
});
</script>

<style scoped>
.markdown-body :deep(pre) {
  background: #090d12;
  border-radius: 6px;
  padding: 10px 12px;
  overflow-x: auto;
  font-size: 12px;
  line-height: 1.6;
}
.markdown-body :deep(code) {
  font-family: ui-monospace, Consolas, monospace;
}
.markdown-body :deep(p) {
  margin: 4px 0;
}
</style>

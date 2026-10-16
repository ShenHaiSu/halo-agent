<template>
  <div ref="scrollContainerRef" class="flex-1 min-h-0 overflow-y-auto min-w-0 max-w-full overflow-x-hidden" data-testid="message-timeline">
    <div :class="chatWidthMode === 'full' ? 'chat-timeline-full px-4 py-4 space-y-4' : 'chat-timeline-centered px-4 py-4 space-y-4'">
      <!-- 空态 -->
      <div v-if="persistedMessages.length === 0 && !agentStore.hasActiveTurn" class="py-10 text-center space-y-2">
        <div class="flex justify-center">
          <Bot class="w-8 h-8 text-accent" />
        </div>
        <p class="text-sm text-primary font-medium">在下方输入任务指令，Agent 将自动编排工具完成</p>
        <p class="text-xs text-secondary font-mono">默认 Runner：CMD 优先 / PYTHON 次之 · 禁用 PowerShell 专属语法</p>
      </div>

      <!-- 持久化时间线：user/system 顶层 + 轮次归并容器 -->
      <template v-for="item in timelineItems" :key="item.key">
        <!-- User -->
        <div v-if="item.kind === 'user'" class="flex justify-end">
          <div class="max-w-[85%] rounded-lg bg-elevated border border-subtle px-3.5 py-2.5 app-text-body text-primary whitespace-pre-wrap break-words select-text min-w-0">
            {{ item.msg?.content }}
          </div>
        </div>
        <!-- System -->
        <div v-else-if="item.kind === 'system'" class="text-[11px] text-secondary text-center font-mono px-4">
          {{ item.msg?.content }}
        </div>
        <!-- 轮次容器：正文常显 + 思考+工具归并舱（默认收缩） -->
        <div v-else class="space-y-2 min-w-0">
          <div
            v-if="item.group?.content"
            class="app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0"
            v-html="renderMarkdown(item.group?.content || '')"
          />
          <div v-if="item.group?.reasoning || (item.group?.toolMessages?.length || 0) > 0" class="rounded-r-md bg-[var(--reasoning-bg)] border-l-2 border-indigo-500/70 overflow-hidden" data-testid="turn-thinking-tools">
            <div class="flex items-center justify-between px-2.5 h-7 cursor-pointer select-none" @click="toggleTurn(item.group?.key || item.key)">
              <span class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] min-w-0">
                <BrainCircuit class="w-3.5 h-3.5 text-indigo-400 shrink-0" />
                <span class="truncate">🧠 思考与工具调用 · {{ item.group?.toolMessages.length || 0 }} 个工具{{ turnDuration(item.group!) }}</span>
                <span v-if="hasFailedTool(item.group!)" class="shrink-0 font-mono text-[10px] px-1.5 py-px rounded-full bg-rose-500/20 text-rose-400 border border-rose-500/40">有失败</span>
              </span>
              <span class="flex items-center gap-1 text-[11px] text-[var(--reasoning-text)] opacity-80 shrink-0">
                <span>{{ isTurnExpanded(item.group?.key || item.key) ? '点击折叠' : '点击展开' }}</span>
                <ChevronDown class="w-3 h-3 transition-transform" :class="isTurnExpanded(item.group?.key || item.key) ? 'rotate-180' : ''" />
              </span>
            </div>
            <n-collapse-transition :show="isTurnExpanded(item.group?.key || item.key)">
              <div class="px-3 pb-2 space-y-2 max-h-72 overflow-y-auto min-w-0">
                <div v-if="item.group?.reasoning" class="whitespace-pre-wrap break-words select-text app-text-small text-[var(--reasoning-text)]">{{ item.group?.reasoning }}</div>
                <ToolCallCard v-for="t in (item.group?.toolMessages || [])" :key="toolKeyOf(t)" :card="toCard(t, item.group!)" :default-collapsed="true" />
              </div>
            </n-collapse-transition>
          </div>
          <div
            v-if="item.group && hasTelemetryGroup(item.group)"
            class="text-[11px] text-secondary font-mono flex flex-wrap items-center gap-x-3 gap-y-0.5"
            title="IN为末轮快照，多轮工具调用轮内多次请求只记末次占用，累计见顶栏Σ"
          >
            <span>协议: {{ protocolOfGroup(item.group!) }}</span>
            <span>IN {{ item.group?.telemetry?.inputTokens ?? '-' }} · Cache {{ item.group?.telemetry?.cachedTokens ?? 0 }} · {{ cachePercentOf(item.group?.telemetry?.cachedTokens ?? 0, item.group?.telemetry?.inputTokens ?? 0) }}</span>
            <span>OUT {{ item.group?.telemetry?.outputTokens ?? '-' }}</span>
          </div>
        </div>
      </template>

      <!-- need04-03：一轮一容器（流式期间唯一容器，顺序恒为 思考→工具→正文） -->
      <div v-if="agentStore.hasActiveTurn" data-testid="active-turn-container" class="rounded-lg border border-subtle bg-surface/40 px-3 py-2 space-y-2 min-w-0">
        <div class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] select-none">
          <BrainCircuit class="w-3.5 h-3.5 text-indigo-400 shrink-0" />
          <span v-if="agentStore.streamingReasoning">思考中… · 已调用 {{ agentStore.toolCards.length }} 个工具</span>
          <span v-else>思考中… · 已调用 {{ agentStore.toolCards.length }} 个工具</span>
          <Loader2 v-if="agentStore.isGenerating" class="w-3 h-3 text-indigo-400 animate-spin shrink-0" />
        </div>
        <div v-if="agentStore.toolCards.length" class="space-y-1.5" data-testid="active-turn-tools">
          <ToolCallCard v-for="card in agentStore.toolCards" :key="card.callId" :card="card" :default-collapsed="true" />
        </div>
        <div
          v-if="agentStore.streamingText"
          class="app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0"
          v-html="renderMarkdown(agentStore.streamingText)"
        />
        <div v-else-if="agentStore.streamingActive && agentStore.isGenerating" class="flex items-center gap-2 text-xs text-secondary">
          <Loader2 class="w-3.5 h-3.5 animate-spin" />
          <span>Agent 推理中...</span>
        </div>
      </div>

      <!-- 中断/截断/错误提示 -->
      <n-alert v-if="agentStore.turnNotice" type="info" :show-icon="false" class="text-xs">
        {{ agentStore.turnNotice }}
      </n-alert>
      <n-alert v-if="agentStore.lastError" type="error" class="text-xs">
        {{ agentStore.lastError }}
      </n-alert>

      <!-- Telemetry footer（本轮快照语义：IN=总量快照，OUT=本轮生成） -->
      <div
        v-if="showLiveTelemetry"
        class="text-[11px] text-secondary font-mono flex flex-wrap items-center gap-x-3 gap-y-0.5 border-t border-subtle pt-2"
        title="IN为末轮快照，多轮工具调用轮内多次请求只记末次占用，累计见顶栏Σ"
      >
        <span>协议: {{ agentStore.telemetry.protocol === 'resp' ? 'RESP (/v1/responses)' : 'CHAT (/v1/chat/completions)' }}</span>
        <span v-if="agentStore.telemetry.ttftMs !== null">首字: {{ agentStore.telemetry.ttftMs }}ms</span>
        <span v-if="agentStore.telemetry.totalMs !== null">总耗时: {{ (agentStore.telemetry.totalMs / 1000).toFixed(1) }}s</span>
        <span>In: {{ agentStore.telemetry.inputTokens }} (Cache {{ agentStore.telemetry.cachedTokens }} · {{ cachePercent }})</span>
        <span>Out: {{ agentStore.telemetry.outputTokens }}</span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { Bot, BrainCircuit, ChevronDown, Loader2 } from 'lucide-vue-next';
import MarkdownIt from 'markdown-it';
import hljs from 'highlight.js';
import { storeToRefs } from 'pinia';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAgentStore, type AgentToolCard } from '@/stores/agentStore';
import { useAppearanceStore } from '@/stores/appearanceStore';
import ToolCallCard from '@/components/chat/ToolCallCard.vue';
import type { UnifiedChatMessage } from '@/bridge/hostBridge';
import { buildTurnGroups, type TurnGroup } from '@/utils/turnGroup';

const workspaceStore = useWorkspaceStore();
const agentStore = useAgentStore();
const appearanceStore = useAppearanceStore();
const { sessionMessages, budgetStatus } = storeToRefs(workspaceStore);
const chatWidthMode = computed(() => appearanceStore.chatWidthMode);

const persistedMessages = computed<UnifiedChatMessage[]>(() => sessionMessages.value || []);
// need04-04：默认全部收缩（与旧 collapsedReasoning 默认展开相反）；切卡保留在内存
const expandedTurns = reactive<Record<string, boolean>>({});

function isTurnExpanded(key: string): boolean {
  return expandedTurns[key] === true;
}
function toggleTurn(key: string): void {
  expandedTurns[key] = !isTurnExpanded(key);
}

// need04-03：持久 tool 灰块与流式卡去重（同 callId 本轮隐藏持久侧）
const allTurnGroups = computed<TurnGroup[]>(() => buildTurnGroups(persistedMessages.value));
const visibleTurnGroups = computed<TurnGroup[]>(() => {
  const hidden = new Set<string>((agentStore.hiddenToolCallIds as unknown as Set<string> | string[]) instanceof Set
    ? [...(agentStore.hiddenToolCallIds as unknown as Set<string>)]
    : [...(agentStore.hiddenToolCallIds as unknown as string[])]);
  if (hidden.size === 0) return allTurnGroups.value;
  const out: TurnGroup[] = [];
  for (const g of allTurnGroups.value) {
    const kept = g.toolMessages.filter((m) => !hidden.has(String((m as unknown as { toolCallId?: string }).toolCallId || '')));
    if (!g.content && !g.reasoning && kept.length === 0) continue;
    out.push({ ...g, toolMessages: kept });
  }
  return out;
});

interface TimelineItem {
  key: string;
  kind: 'user' | 'system' | 'turn';
  msg?: UnifiedChatMessage;
  group?: TurnGroup;
}
// 按 jsonl 顺序：user/system 顶层直渲，assistant+tool 聚合为 turn（顺序与落盘一致）
const timelineItems = computed<TimelineItem[]>(() => {
  const items: TimelineItem[] = [];
  const groupsByAssistant = new Map<string, TurnGroup>();
  for (const g of visibleTurnGroups.value) {
    if (g.assistantId) groupsByAssistant.set(g.assistantId, g);
    else if (g.toolMessages.length) {
      // 孤立 tool 组：直接展开为 turn 项
      items.push({ key: g.key, kind: 'turn', group: g });
    }
  }
  const emitted = new Set<string>();
  for (const msg of persistedMessages.value) {
    const role = String(msg.role || '').toLowerCase();
    if (role === 'user') items.push({ key: `u-${msg.id}`, kind: 'user', msg });
    else if (role !== 'assistant' && role !== 'tool') items.push({ key: `s-${msg.id}`, kind: 'system', msg });
    else if (role === 'assistant') {
      const g = groupsByAssistant.get(String(msg.id));
      if (g && !emitted.has(g.key)) { emitted.add(g.key); items.push({ key: g.key, kind: 'turn', group: g }); }
    }
    // role==='tool' 跳过（已并入所属 turn 组）
  }
  // 兜底：未被顺序命中的组追加末尾
  for (const g of visibleTurnGroups.value) {
    if (g.assistantId && !emitted.has(g.key)) items.push({ key: g.key, kind: 'turn', group: g });
  }
  return items;
});

function toolKeyOf(m: UnifiedChatMessage): string {
  return String((m as unknown as { toolCallId?: string }).toolCallId || m.id);
}

/** need04-04：持久 tool 消息 → ToolCallCard 富展示（参数从同组 assistant.toolCalls 同 id 取）。 */
function toCard(m: UnifiedChatMessage, group: TurnGroup): AgentToolCard {
  const callId = String((m as unknown as { toolCallId?: string }).toolCallId || m.id);
  const ref = group.toolCalls.find((t) => t.id === callId);
  // 兼容后端 UnifiedToolCall 形状：function.arguments / argumentsJson
  let argsJson = '';
  const rawAssistant = persistedMessages.value.find((x) => String(x.id) === group.assistantId);
  const rawCalls = (rawAssistant as unknown as { toolCalls?: Array<{ id?: string; callId?: string; argumentsJson?: string; function?: { arguments?: string; name?: string }; functionName?: string }> }).toolCalls;
  if (Array.isArray(rawCalls)) {
    const hit = rawCalls.find((c) => String(c.id || c.callId) === callId);
    if (hit) argsJson = String(hit.argumentsJson || hit.function?.arguments || '');
  }
  const content = String(m.content || '');
  const isErr = content.includes('[Tool Error');
  return {
    callId,
    functionName: ref?.name || (m.name as string) || 'tool',
    argsJson,
    status: isErr ? 'error' : 'done',
    resultText: content.length > 6000 ? content.slice(0, 6000) + '\n...（输出过长已截断）' : content,
    exitCode: null,
    durationMs: null,
    iteration: 0,
  };
}

function hasFailedTool(g: TurnGroup): boolean {
  return g.toolMessages.some((m) => String(m.content || '').includes('[Tool Error'));
}
function turnDuration(_g: TurnGroup): string {
  void _g;
  return '';
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

function hasTelemetryGroup(g: TurnGroup): boolean {
  return !!g.telemetry && (g.telemetry.inputTokens != null || g.telemetry.outputTokens != null);
}

function protocolOfGroup(_g: TurnGroup): string {
  void _g;
  const p = (budgetStatus.value?.protocol || agentStore.telemetry.protocol || 'chat').toLowerCase();
  return p === 'resp' ? 'RESP' : 'CHAT';
}

// need05-01：持久 footer 文案冻结为 IN {input} · Cache {cached} · {p}%；分母 0 显示 - 而非 0%（区分未知）
function cachePercentOf(cached: number, input: number): string {
  if (!input || input <= 0) return '-';
  return `${Math.round((cached / input) * 100)}%`;
}

function truncateToolText(s: string): string {
  void s;
  return '';
}

const showLiveTelemetry = computed(() => {
  const t = agentStore.telemetry;
  return t.ttftMs !== null || t.totalMs !== null || t.inputTokens > 0 || t.outputTokens > 0;
});

const cachePercent = computed(() => {
  const t = agentStore.telemetry;
  if (t.inputTokens <= 0) return '-';
  return `${Math.round((t.cachedTokens / t.inputTokens) * 100)}%`;
});
// 进入会话自动滚到底：主界面渲染完毕后检测纵向滚动条位置与可滚动高度，有向下空间即滚到底。
const scrollContainerRef = ref<HTMLElement | null>(null);
let scrollBottomRaf = 0;
let scrollBottomTimer: number | null = null;
function scrollTimelineToBottomIfNeeded(): boolean {
  const el = scrollContainerRef.value;
  if (!el) return false;
  const maxScroll = el.scrollHeight - el.clientHeight;
  if (maxScroll <= 0) return false;
  if (maxScroll - el.scrollTop <= 1.5) return false;
  el.scrollTop = el.scrollHeight;
  return true;
}
function scheduleScrollTimelineToBottom(): void {
  if (scrollBottomRaf) cancelAnimationFrame(scrollBottomRaf);
  scrollBottomRaf = requestAnimationFrame(() => {
    scrollBottomRaf = requestAnimationFrame(() => {
      scrollBottomRaf = 0;
      scrollTimelineToBottomIfNeeded();
    });
  });
  if (scrollBottomTimer !== null) window.clearTimeout(scrollBottomTimer);
  scrollBottomTimer = window.setTimeout(() => {
    scrollBottomTimer = null;
    scrollTimelineToBottomIfNeeded();
  }, 200);
}
watch(
  () => workspaceStore.activeSessionId,
  () => {
    void nextTick(() => scheduleScrollTimelineToBottom());
  }
);
watch(
  () => timelineItems.value.length,
  () => {
    void nextTick(() => scheduleScrollTimelineToBottom());
  }
);
watch(
  () => workspaceStore.loadingMessages,
  (loading) => {
    if (!loading) void nextTick(() => scheduleScrollTimelineToBottom());
  }
);
onMounted(() => {
  void nextTick(() => scheduleScrollTimelineToBottom());
});
onUnmounted(() => {
  if (scrollBottomRaf) cancelAnimationFrame(scrollBottomRaf);
  if (scrollBottomTimer !== null) window.clearTimeout(scrollBottomTimer);
  scrollBottomRaf = 0;
  scrollBottomTimer = null;
});
void truncateToolText;
</script>

<style scoped>
/* need04-02：排版已收敛到 main.css 全局 .markdown-body，此处不重复定义 p/pre，避免 scoped 与全局打架 */
</style>

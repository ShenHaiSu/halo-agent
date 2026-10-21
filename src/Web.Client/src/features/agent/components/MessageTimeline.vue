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

      <!-- 持久化时间线全量直渲：user/system 顶层 + 轮次归并容器 -->
      <template v-for="item in timelineItems" :key="item.key">
        <!-- User -->
        <div v-if="item.kind === 'user'" :ref="setAnchor(item.key)" class="flex justify-end">
          <div
            class="max-w-[85%] rounded-lg bg-elevated border border-subtle px-3.5 py-2.5 app-text-body text-primary whitespace-pre-wrap break-words select-text min-w-0"
            @contextmenu.prevent="openMenu($event, { kind: 'user', id: String(item.msg?.id || ''), text: String(item.msg?.content || '') })"
          >
            {{ item.msg?.content }}
            <div v-if="msgImages(item.msg).length" class="mt-1.5 flex flex-wrap gap-1.5" data-testid="msg-images">
              <template v-for="img in msgImages(item.msg)" :key="img.imageId">
                <n-image
                  v-if="thumbOf(img)"
                  :src="thumbOf(img)"
                  :alt="img.fileName"
                  width="96"
                  object-fit="cover"
                  class="rounded border"
                  :data-testid="`msg-image-${img.imageId}`"
                />
                <n-tag v-else size="small" :bordered="true" :title="img.fileName">{{ img.fileName }}</n-tag>
              </template>
            </div>
            <div v-if="hasLostImage(item.msg)" class="text-[11px] text-amber-500">部分图片已丢失（文件被删除）</div>
          </div>
        </div>
        <!-- System -->
        <div v-else-if="item.kind === 'system'" :ref="setAnchor(item.key)" class="text-[11px] text-secondary text-center font-mono px-4">
          {{ item.msg?.content }}
        </div>
        <!-- 轮次容器：正文常显 + 思考+工具归并舱（默认收缩） -->
        <div v-else :ref="setAnchor(item.key)" class="space-y-2 min-w-0">
          <div
            v-if="item.group?.content"
            class="app-text-body leading-relaxed text-primary break-words select-text markdown-body min-w-0"
            @click="onMarkdownLinkClick"
            @contextmenu.prevent="
              openMenu($event, {
                kind: 'assistant',
                id: String(item.group?.assistantId || item.group?.key || ''),
                text: String(item.group?.content || ''),
              })
            "
            v-html="renderMarkdown(item.group?.content || '')"
          />
          <div
            v-if="item.group?.reasoning || (item.group?.toolMessages?.length || 0) > 0"
            class="rounded-r-md bg-[var(--reasoning-bg)] border-l-2 border-indigo-500/70 overflow-hidden"
            data-testid="turn-thinking-tools"
          >
            <div
              class="flex items-center justify-between px-2.5 h-7 cursor-pointer select-none"
              @click="toggleTurn(item.group?.key || item.key)"
            >
              <span class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] min-w-0">
                <BrainCircuit class="w-3.5 h-3.5 text-indigo-400 shrink-0" />
                <span class="truncate"
                  >🧠 思考与工具调用 · {{ item.group?.toolMessages.length || 0 }} 个工具{{ turnDuration(item.group as TurnGroup) }}</span
                >
                <span
                  v-if="hasFailedTool(item.group as TurnGroup)"
                  class="shrink-0 font-mono text-[10px] px-1.5 py-px rounded-full bg-rose-500/20 text-rose-400 border border-rose-500/40"
                  >有失败</span
                >
              </span>
              <span class="flex items-center gap-1 text-[11px] text-[var(--reasoning-text)] opacity-80 shrink-0">
                <span>{{ isTurnExpanded(item.group?.key || item.key) ? '点击折叠' : '点击展开' }}</span>
                <ChevronDown
                  class="w-3 h-3 transition-transform"
                  :class="isTurnExpanded(item.group?.key || item.key) ? 'rotate-180' : ''"
                />
              </span>
            </div>
            <n-collapse-transition :show="isTurnExpanded(item.group?.key || item.key)">
              <div class="px-3 pb-2 space-y-2 max-h-72 overflow-y-auto min-w-0">
                <div
                  v-if="item.group?.reasoning"
                  class="whitespace-pre-wrap break-words select-text app-text-small text-[var(--reasoning-text)]"
                >
                  {{ item.group?.reasoning }}
                </div>
                <ToolCallCard
                  v-for="t in item.group?.toolMessages || []"
                  :key="toolKeyOf(t)"
                  :card="toCard(t, item.group as TurnGroup)"
                  :default-collapsed="true"
                />
              </div>
            </n-collapse-transition>
          </div>
          <div
            v-if="item.group && hasTelemetryGroup(item.group)"
            class="text-[11px] text-secondary font-mono flex flex-wrap items-center gap-x-3 gap-y-0.5"
            title="IN为末轮快照，多轮工具调用轮内多次请求只记末次占用，累计见顶栏Σ"
          >
            <span>协议: {{ protocolOfGroup(item.group as TurnGroup) }}</span>
            <span
              >IN {{ item.group?.telemetry?.inputTokens ?? '-' }} · Cache {{ item.group?.telemetry?.cachedTokens ?? 0 }} ·
              {{ cachePercentOf(item.group?.telemetry?.cachedTokens ?? 0, item.group?.telemetry?.inputTokens ?? 0) }}</span
            >
            <span>OUT {{ item.group?.telemetry?.outputTokens ?? '-' }}</span>
          </div>
        </div>
      </template>

      <!-- need04-03：一轮一容器（流式期间唯一容器，顺序恒为 思考→工具→正文） -->
      <div
        v-if="agentStore.hasActiveTurn"
        data-testid="active-turn-container"
        class="rounded-lg border border-subtle bg-surface/40 px-3 py-2 space-y-2 min-w-0"
      >
        <div class="flex items-center gap-1.5 text-[11px] text-[var(--reasoning-text)] select-none">
          <BrainCircuit class="w-3.5 h-3.5 text-indigo-400 shrink-0" />
          <span>思考中… · 已调用 {{ agentStore.toolCards.length }} 个工具 · 第 {{ agentStore.activeTurn.curIteration || 1 }} 轮</span>
          <Loader2 v-if="agentStore.isGenerating" class="w-3 h-3 text-indigo-400 animate-spin shrink-0" />
        </div>
        <template v-if="!agentStore.segKillSwitch && agentStore.visibleSegs.length">
          <template v-for="seg in agentStore.visibleSegs" :key="seg.key">
            <div
              v-if="seg.kind === 'reasoning' && segTextOf(seg)"
              class="whitespace-pre-wrap break-words app-text-small text-[var(--reasoning-text)] max-h-24 overflow-y-auto"
              :data-seg="seg.key"
            >
              {{ segTextOf(seg) }}
            </div>
            <div v-else-if="seg.kind === 'tool' && segCardOf(seg)" data-testid="active-turn-tools" :data-seg="seg.key">
              <ToolCallCard :card="segCardOf(seg)!" :default-collapsed="true" />
            </div>
            <div
              v-else-if="seg.kind === 'text' && segTextOf(seg)"
              class="app-text-body leading-relaxed text-primary break-words select-text min-w-0"
              style="white-space: pre-wrap"
              :data-seg="seg.key"
              @click="onMarkdownLinkClick"
              v-html="renderStreamingText(segTextOf(seg))"
            />
          </template>
        </template>
        <template v-else-if="agentStore.segKillSwitch">
          <div v-if="agentStore.toolCards.length" class="space-y-1.5" data-testid="active-turn-tools">
            <ToolCallCard v-for="card in agentStore.toolCards" :key="card.callId" :card="card" :default-collapsed="true" />
          </div>
          <div
            v-if="agentStore.streamingText"
            class="app-text-body leading-relaxed text-primary break-words select-text min-w-0"
            style="white-space: pre-wrap"
            @click="onMarkdownLinkClick"
            v-html="renderStreamingText(agentStore.streamingText)"
          />
        </template>
        <div
          v-if="!agentStore.visibleSegs.length && !agentStore.streamingText && agentStore.streamingActive && agentStore.isGenerating"
          class="flex items-center gap-2 text-xs text-secondary"
        >
          <Loader2 class="w-3.5 h-3.5 animate-spin" />
          <span>Agent 推理中...</span>
        </div>
      </div>

      <!-- 中断/截断/错误提示 -->
      <n-alert v-if="agentStore.turnNotice" type="info" :show-icon="false" class="text-xs">
        {{ agentStore.turnNotice }}
      </n-alert>
      <n-alert v-if="agentStore.lastError" type="error" class="text-xs" data-testid="turn-error-alert">
        {{ agentStore.lastError }}
      </n-alert>
      <div v-if="agentStore.activeTurn.lastErrorInfo?.retry" class="flex items-center gap-2">
        <n-button size="small" type="error" ghost data-testid="turn-retry-btn" @click="onRetryTurn"> 重试本轮 </n-button>
        <n-button size="small" quaternary data-testid="turn-error-dismiss" @click="onDismissError"> 忽略 </n-button>
      </div>

      <!-- Telemetry footer（本轮快照语义：IN=总量快照，OUT=本轮生成） -->
      <div
        v-if="showLiveTelemetry"
        class="text-[11px] text-secondary font-mono flex flex-wrap items-center gap-x-3 gap-y-0.5 border-t border-subtle pt-2"
        title="IN为末轮快照，多轮工具调用轮内多次请求只记末次占用，累计见顶栏Σ"
      >
        <span>协议: {{ agentStore.telemetry.protocol === 'resp' ? 'RESP (/v1/responses)' : 'CHAT (/v1/chat/completions)' }}</span>
        <span v-if="agentStore.telemetry.ttftMs !== null">首字: {{ agentStore.telemetry.ttftMs }}ms</span>
        <span>In: {{ agentStore.telemetry.inputTokens }} (Cache {{ agentStore.telemetry.cachedTokens }} · {{ cachePercent }})</span>
        <span>Out: {{ agentStore.telemetry.outputTokens }}</span>
      </div>
      <MessageContextMenu
        :visible="menuState.visible"
        :x="menuState.x"
        :y="menuState.y"
        :busy="menuBusy"
        :can-copy="menuCanCopy"
        @copy="onMenuCopy"
        @replay="onMenuReplay"
        @branch="onMenuBranch"
        @delete="onMenuDelete"
      />
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
import MessageContextMenu from '@/components/chat/MessageContextMenu.vue';
import { copyTextToClipboard, type HistoryTarget } from '@/utils/historyAnchor';
import { getHostBridge, type ChatImageRef, type UnifiedChatMessage } from '@/bridge/hostBridge';
import { blockHash, imagesOf, type TimelineEntry, type TurnGroup } from '@/utils/turnGroup';
import { useTimelineNav } from '@/composables/useTimelineNav';
import { useUiStore } from '@/stores/uiStore';
import type { ComponentPublicInstance } from 'vue';

const workspaceStore = useWorkspaceStore();
const agentStore = useAgentStore();
const appearanceStore = useAppearanceStore();
const uiStore = useUiStore();
const nav = useTimelineNav();
const { sessionMessages, budgetStatus } = storeToRefs(workspaceStore);
import { useDialog } from 'naive-ui';
const chatWidthMode = computed(() => appearanceStore.chatWidthMode);
const dialog = useDialog();

const persistedMessages = computed<UnifiedChatMessage[]>(() => sessionMessages.value || []);
const expandedTurns = reactive<Record<string, boolean>>({});
function isTurnExpanded(key: string): boolean {
  return expandedTurns[key] === true;
}
function toggleTurn(key: string): void {
  expandedTurns[key] = !isTurnExpanded(key);
}
type TimelineItem = TimelineEntry;
const timelineItems = computed<TimelineItem[]>(() => agentStore.activeTimelineEntries);
function setAnchor(key: string) {
  return (el: Element | ComponentPublicInstance | null) => {
    try {
      if (el instanceof HTMLElement) nav.register(key, el);
      else nav.unregister(key);
    } catch (e) {
      console.warn('[timeline] anchor failed:', e);
    }
  };
}
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
  const rawCalls = (
    rawAssistant as unknown as {
      toolCalls?: Array<{
        id?: string;
        callId?: string;
        argumentsJson?: string;
        function?: { arguments?: string; name?: string };
        functionName?: string;
      }>;
    }
  ).toolCalls;
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
    receivedBytes: 0,
    images: imagesOf(m),
  };
}

// ---- need09-05：时间轴缩略图（miss 异步回填；失败置 lost + 黄字；只认 images 数组） ----
const thumbnailCache = ref<Record<string, string>>({});
const lostImageIds = ref<Record<string, boolean>>({});
const thumbPending = new Set<string>();

function msgImages(msg: UnifiedChatMessage | undefined): ChatImageRef[] {
  return imagesOf(msg);
}

function hasLostImage(msg: UnifiedChatMessage | undefined): boolean {
  return msgImages(msg).some((i) => lostImageIds.value[i.imageId] || i.lost === true);
}

function thumbOf(img: ChatImageRef): string {
  const hit = thumbnailCache.value[img.imageId];
  if (hit) return hit;
  if (img.lost === true || lostImageIds.value[img.imageId]) return '';
  if (!thumbPending.has(img.imageId)) {
    thumbPending.add(img.imageId);
    void (async () => {
      try {
        const ws = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
        const sid = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
        const res = await getHostBridge().getImageThumbnail(ws, sid, img.imageId);
        if (res.ok && typeof res.data === 'string' && res.data) {
          thumbnailCache.value = { ...thumbnailCache.value, [img.imageId]: res.data };
        } else {
          lostImageIds.value = { ...lostImageIds.value, [img.imageId]: true };
        }
      } catch {
        lostImageIds.value = { ...lostImageIds.value, [img.imageId]: true };
      } finally {
        thumbPending.delete(img.imageId);
      }
    })();
  }
  return '';
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
  // need22-P3 G1：hljs 只跑终态且只 highlight(lang)（语言已知时）；未知语言直接转义，不做 highlightAuto 全量猜测。
  highlight: (code: string, lang: string): string => {
    try {
      if (lang && hljs.getLanguage(lang)) {
        return hljs.highlight(code, { language: lang }).value;
      }
      return escapeHtml(code);
    } catch (e) {
      console.warn('[markdown] highlight failed:', e);
      return escapeHtml(code);
    }
  },
});

function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
// need22-P3 G1：markdown 两档——流式期纯文本（pre-wrap，不调 md.render）；终态一次 md.render 并按 blockHash 缓存。
const mdRenderCache = new Map<string, string>();
const MD_CACHE_MAX = 200;
function renderMarkdown(content: string): string {
  const key = blockHash(content || '');
  const hit = mdRenderCache.get(key);
  if (hit !== undefined) return hit;
  let html: string;
  try {
    html = md.render(content || '');
  } catch (e) {
    console.warn('[markdown] render failed:', e);
    html = `<pre>${escapeHtml(content || '')}</pre>`;
  }
  mdRenderCache.set(key, html);
  if (mdRenderCache.size > MD_CACHE_MAX) {
    const oldest = mdRenderCache.keys().next();
    if (!oldest.done) mdRenderCache.delete(oldest.value);
  }
  return html;
}
function segTextOf(seg: { text?: string } | null | undefined): string {
  try {
    return agentStore.segDisplayText(seg as unknown as Parameters<typeof agentStore.segDisplayText>[0]);
  } catch (e) {
    console.warn('[timeline] seg text failed:', e);
    return String(seg?.text || '');
  }
}
function segCardOf(seg: { callId?: string } | null | undefined): AgentToolCard | null {
  try {
    const cid = String(seg?.callId || '');
    if (!cid) return null;
    const t = agentStore.activeTurn;
    const idx = (t.toolCardIndex as Record<string, number>)[cid];
    if (idx !== undefined) return t.toolCards[idx] || null;
    return t.toolCards.find((c) => c.callId === cid) || null;
  } catch (e) {
    console.warn('[timeline] seg card failed:', e);
    return null;
  }
}
function renderStreamingText(content: string): string {
  return escapeHtml(content || '');
}
// 链接永不进 CEF 主帧：渲染侧强制外链属性（漏网时走 popup→系统浏览器），点击侧委托后端系统打开。
const defaultLinkOpenRender = md.renderer.rules.link_open;
md.renderer.rules.link_open = (tokens, idx, options, env, self): string => {
  try {
    tokens[idx].attrSet('target', '_blank');
    tokens[idx].attrSet('rel', 'noopener noreferrer');
  } catch {
    /* 属性加不上也不阻断渲染 */
  }
  if (defaultLinkOpenRender) return defaultLinkOpenRender(tokens, idx, options, env, self);
  return self.renderToken(tokens, idx, options);
};

async function onMarkdownLinkClick(e: MouseEvent): Promise<void> {
  const el = e.target as HTMLElement | null;
  const anchor = el?.closest?.('a[href]') as HTMLAnchorElement | null;
  if (!anchor) return;
  const href = (anchor.getAttribute('href') || '').trim();
  if (!href || href.startsWith('#')) return;
  // 拦截 CEF 帧内导航，转后端系统级打开（浏览器 / explorer），杜绝白屏
  e.preventDefault();
  e.stopPropagation();
  try {
    const workspaceRoot = workspaceStore.activeWorkspace?.rootPath || '';
    const res = await getHostBridge().openExternalLink(href, workspaceRoot);
    if (!res.ok) console.warn('[markdown-link] open failed:', res.code, res.message);
  } catch (err) {
    console.warn('[markdown-link] open failed:', err);
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

// need07：右键菜单（锚点定位 + 摆位 + 编排下沉 store）
const menuState = reactive<{ visible: boolean; x: number; y: number; target: HistoryTarget | null }>({
  visible: false,
  x: 0,
  y: 0,
  target: null,
});
const menuBusy = computed(() => {
  const sid = workspaceStore.activeSessionId || '';
  return agentStore.isGenerating || agentStore.streamingActive || (sid ? agentStore.isCompacting(sid) : false);
});
const menuCanCopy = computed(() => String(menuState.target?.text || '').length > 0);
function openMenu(e: MouseEvent, target: HistoryTarget): void {
  if (!target.id) return;
  const w = 176 + 24;
  const h = 176 + 16;
  menuState.x = Math.max(8, Math.min(e.clientX, window.innerWidth - w));
  menuState.y = Math.max(8, Math.min(e.clientY, window.innerHeight - h));
  menuState.target = target;
  menuState.visible = true;
}
function closeMenu(): void {
  menuState.visible = false;
}
async function onMenuCopy(): Promise<void> {
  const target = menuState.target;
  closeMenu();
  if (!target || !target.text) return;
  const ok = await copyTextToClipboard(target.text);
  try {
    (window as unknown as { $message?: { success: (s: string) => void; error: (s: string) => void } }).$message?.[
      ok ? 'success' : 'error'
    ]?.(ok ? '已复制' : '复制失败');
  } catch {
    /* ignore */
  }
  if (ok === false) {
    /* 保持静默，仅日志 */ console.warn('[history] copy failed');
  }
}
async function onMenuReplay(): Promise<void> {
  const target = menuState.target;
  closeMenu();
  if (!target || menuBusy.value) return;
  if (target.kind === 'user') await agentStore.replayUserMessage(target.id);
  else await agentStore.replayAssistantTurn(target.id);
}
async function onMenuBranch(): Promise<void> {
  const target = menuState.target;
  closeMenu();
  if (!target || menuBusy.value) return;
  await workspaceStore.branchFromMessage(target.id, target.kind);
}
function onMenuDelete(): void {
  const target = menuState.target;
  closeMenu();
  if (!target || !target.id || menuBusy.value) return;
  const label = target.kind === 'user' ? '这条用户消息' : '本轮 assistant 回复';
  dialog.warning({
    title: '删除消息',
    content: `确定删除${label}吗？删除前会自动备份；若因此缺失工具回包，会自动补中断占位，保证历史仍可继续对话。`,
    positiveText: '删除',
    negativeText: '取消',
    onPositiveClick: async () => {
      const res = await workspaceStore.deleteMessage(target.id);
      try {
        (window as unknown as { $message?: { success: (s: string) => void; error: (s: string) => void } }).$message?.[
          res.ok ? 'success' : 'error'
        ]?.(res.ok ? '已删除' : res.message || '删除失败');
      } catch {
        /* ignore */
      }
    },
  });
}
// need22-P2 F5：失败重试（保留的流式卡 + 原文重发）/ 忽略（清错误态，卡片保留由下一轮覆盖）
async function onRetryTurn(): Promise<void> {
  try {
    await agentStore.retryLastTurn();
  } catch (err) {
    console.warn('[timeline] retry failed:', err);
  }
}
function onDismissError(): void {
  try {
    agentStore.clearLastError();
  } catch {
    /* ignore */
  }
}
function bindMenuGlobalClose(): void {
  window.addEventListener('click', closeMenu);
  window.addEventListener('resize', closeMenu);
  window.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') closeMenu();
  });
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
function cancelScheduledScrollToBottom(): void {
  if (scrollBottomRaf) cancelAnimationFrame(scrollBottomRaf);
  if (scrollBottomTimer !== null) window.clearTimeout(scrollBottomTimer);
  scrollBottomRaf = 0;
  scrollBottomTimer = null;
}
function isPinnedToBottom(): boolean {
  const el = scrollContainerRef.value;
  if (!el) return true;
  try {
    return el.scrollHeight - el.scrollTop - el.clientHeight <= 80;
  } catch (e) {
    console.warn('[timeline] pinned check failed:', e);
    return true;
  }
}
function scrollTimelineToBottomIfNeeded(force?: boolean): boolean {
  const el = scrollContainerRef.value;
  if (!el) return false;
  try {
    if (performance.now() < uiStore.jumpLockUntil) return false;
  } catch (e) {
    console.warn('[timeline] jump lock check failed:', e);
  }
  if (!force && !isPinnedToBottom()) return false;
  const maxScroll = el.scrollHeight - el.clientHeight;
  if (maxScroll <= 0) return false;
  if (!force && maxScroll - el.scrollTop <= 1.5) return false;
  el.scrollTop = el.scrollHeight;
  return true;
}
function forceScrollToBottom(): void {
  const el = scrollContainerRef.value;
  if (!el) return;
  void nextTick(() => {
    try {
      el.scrollTop = el.scrollHeight;
    } catch (e) {
      console.warn('[timeline] force scroll failed:', e);
    }
  });
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
function onUserScrollIntent(): void {
  try {
    uiStore.releaseJumpLock();
  } catch (e) {
    console.warn('[timeline] unlock failed:', e);
  }
}
function bindUserScrollUnlock(): void {
  const el = scrollContainerRef.value;
  if (!el) return;
  el.addEventListener('wheel', onUserScrollIntent, { passive: true });
  el.addEventListener('touchstart', onUserScrollIntent, { passive: true });
  el.addEventListener('keydown', onUserScrollIntent);
}
function unbindUserScrollUnlock(): void {
  const el = scrollContainerRef.value;
  if (!el) return;
  try {
    el.removeEventListener('wheel', onUserScrollIntent);
    el.removeEventListener('touchstart', onUserScrollIntent);
    el.removeEventListener('keydown', onUserScrollIntent);
  } catch (e) {
    console.warn('[timeline] unbind failed:', e);
  }
}
watch(
  () => workspaceStore.activeSessionId,
  () => {
    closeMenu();
    forceScrollToBottom();
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
    if (!loading) forceScrollToBottom();
  }
);
watch(
  () => timelineItems.value.map((i) => i.key),
  (keys) => {
    try {
      nav.setOrder(keys);
    } catch (e) {
      console.warn('[timeline] nav order failed:', e);
    }
  },
  { immediate: true }
);
onMounted(() => {
  void nextTick(() => scheduleScrollTimelineToBottom());
  bindMenuGlobalClose();
  try {
    nav.setContainer(scrollContainerRef.value);
    nav.setOrder(timelineItems.value.map((i) => i.key));
  } catch (e) {
    console.warn('[timeline] nav init failed:', e);
  }
  bindUserScrollUnlock();
});
onUnmounted(() => {
  cancelScheduledScrollToBottom();
  try {
    nav.clear();
    nav.setContainer(null);
  } catch (e) {
    console.warn('[timeline] nav clear failed:', e);
  }
  unbindUserScrollUnlock();
});
void truncateToolText;
</script>
<style scoped>
/* need04-02 */
</style>

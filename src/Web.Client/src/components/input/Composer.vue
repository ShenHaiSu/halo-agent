<!-- src/Web.Client/src/components/input/Composer.vue -->
<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import { useMessagesStore } from '@/stores/messages';
import { useStreamStore } from '@/stores/stream';
import { useSessionStore } from '@/stores/session';
import { useSessionsStore } from '@/stores/sessions';
import { usePermissionsStore } from '@/stores/permissions';
import { useHostStore } from '@/stores/host';
import { useUiStore } from '@/stores/ui';
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';
import { useComposition } from '@/composables/useComposition';
import { useAutoResize } from '@/composables/useAutoResize';
import { useFileDrop } from '@/composables/useFileDrop';
import { copyText } from '@/utils/clipboard';
import HBadge from '@/components/base/HBadge.vue';
import HTag from '@/components/base/HTag.vue';
import PermissionCard from '@/components/ask/PermissionCard.vue';
import RoundsCard from '@/components/ask/RoundsCard.vue';
import MentionPopover from './MentionPopover.vue';
import SlashPopover from './SlashPopover.vue';
import DropOverlay from './DropOverlay.vue';
import QueueList from './QueueList.vue';
import ComposerToolbar from './ComposerToolbar.vue';
import { useMention } from './useMention';
import { useSlash } from './useSlash';
import { createInputHandler } from './composerInput';
import { handleComposerKeydown } from './composerKeys';
import { pasteImages } from './pasteImage';

const messages = useMessagesStore();
const stream = useStreamStore();
const session = useSessionStore();
const sessions = useSessionsStore();
const perms = usePermissionsStore();
const host = useHostStore();
const ui = useUiStore();
const toast = useToast();

const ta = ref<HTMLTextAreaElement | null>(null);
const text = ref('');
const attachments = ref<string[]>([]);
const queue = ref<string[]>([]);
const history = ref<string[]>([]);
const historyIdx = ref(-1);

const MAX_QUEUE = 5;
const MAX_ATTACH = 20;

const composing = useComposition();
useAutoResize(ta, text, { minRows: 1, maxRows: 12 });

const mention = useMention(ta, text);
const slash = useSlash(ta, text, { onRun: runCommand });
const drop = useFileDrop((paths) => addAttachments(paths, true), {
  hostFiles: computed(() => host.droppedFiles),
  consumeHostFiles: () => {
    host.droppedFiles = [];
  },
});

/**
 * ============ 能否发送 ============
 *  询问未答 → 绝不能发（P2 保证单待答，发了会被拒绝，前端不让用户白按）
 *  生成中   → 可以发，但走排队
 * =================================
 */
const askPending = computed(() => perms.pending !== null);
const canSend = computed(() => {
  if (askPending.value) return false;
  const hasContent = text.value.trim().length > 0 || attachments.value.length > 0;
  if (!hasContent) return false;
  if (stream.busy) return queue.value.length < MAX_QUEUE;
  return true;
});

/** 模式徽章：danger / warning / default（default 即中性） */
const modeVariant = computed<'default' | 'warning' | 'danger'>(() => {
  if (session.mode === 'yolo' || session.mode === 'bypassPermissions') return 'danger';
  if (session.mode === 'acceptEdits') return 'warning';
  return 'default';
});

const modeLabel = computed(() => {
  switch (session.mode) {
    case 'ask':
      return '询问模式';
    case 'acceptEdits':
      return '自动接受编辑';
    case 'bypassPermissions':
      return '自动放行';
    case 'yolo':
      return '完全放行';
    default:
      return '询问模式';
  }
});

const totalUsage = computed(() => session.usage.input + session.usage.output);

const onInput = createInputHandler({
  ta,
  text,
  isComposingNow: composing.isComposingNow,
  onTrigger: (kind, start, q) => {
    if (kind === 'slash') {
      slash.trigger(start, q);
      mention.close();
    } else {
      mention.trigger(start, q);
      slash.close();
    }
  },
  onCloseOverlays: () => {
    mention.close();
    slash.close();
  },
});

function onKeydown(e: KeyboardEvent): void {
  handleComposerKeydown(e, {
    composing,
    text,
    mention,
    slash,
    history,
    historyIdx,
    askPending,
    isBusy: computed(() => stream.busy),
    submit: () => {
      void submit();
    },
    cancel: () => {
      void cancel();
    },
    prevSession: () => void stepSession(-1),
    nextSession: () => void stepSession(1),
  });
}

async function stepSession(delta: -1 | 1): Promise<void> {
  const list = sessions.list;
  if (list.length < 2) return;
  const cur = list.findIndex((s) => s.id === sessions.currentId);
  const base = cur < 0 ? (delta > 0 ? -1 : 0) : cur;
  const next = list[(base + delta + list.length) % list.length];
  if (!next || next.id === sessions.currentId) return;
  try {
    await sessions.select(next.id);
  } catch (e) {
    toast.error(`切换会话失败：${(e as Error).message}`);
  }
}

async function submit(): Promise<void> {
  const t = text.value.trim();
  if (!t && attachments.value.length === 0) return;
  if (askPending.value) return;

  const finalText = attachments.value.length
    ? `${t}\n\n[引用文件]\n${attachments.value.map((p) => `- ${p}`).join('\n')}`
    : t;

  // 生成中 → 排队
  if (stream.busy) {
    if (queue.value.length >= MAX_QUEUE) {
      toast.warning(`队列已满（最多 ${MAX_QUEUE} 条）`);
      return;
    }
    try {
      const r = (await transport.request('agent.queue', { text: finalText })) as {
        queued: boolean;
        position?: number;
      };
      if (r.queued) {
        if (t) pushHistory(t);
        queue.value.push(finalText);
        text.value = '';
        attachments.value = [];
        const pos = typeof r.position === 'number' ? r.position : queue.value.length;
        toast.info(`已排队（第 ${pos} 位）`);
      } else {
        toast.warning('排队失败');
      }
    } catch (e) {
      toast.error(`排队失败：${(e as Error).message}`);
    }
    return;
  }

  // 空闲 → 发送（乐观追加，失败恢复草稿，I-7）
  const draftBackup = { text: t, attachments: [...attachments.value] };
  messages.append({ role: 'user', content: finalText });
  text.value = '';
  attachments.value = [];
  if (t) pushHistory(t);

  try {
    await transport.request('agent.prompt', { text: finalText });
  } catch (e) {
    toast.error(`发送失败：${(e as Error).message}`);
    text.value = draftBackup.text;
    attachments.value = draftBackup.attachments;
  }
}

function pushHistory(t: string): void {
  history.value.push(t);
  if (history.value.length > 100) history.value.shift();
  historyIdx.value = -1;
}

async function cancel(): Promise<void> {
  try {
    await transport.request('agent.cancel', {});
  } catch {
    /* 已结束 */
  }
}

async function unqueue(i: number): Promise<void> {
  const t = queue.value[i];
  if (t === undefined) return;
  try {
    const r = (await transport.request('agent.unqueue', { text: t })) as { removed: boolean };
    if (r.removed) queue.value.splice(i, 1);
  } catch (e) {
    toast.error(`撤回失败：${(e as Error).message}`);
  }
}

function addAttachments(paths: string[], silentDup = false): void {
  const next = [...attachments.value];
  for (const p of paths) {
    if (next.length >= MAX_ATTACH) {
      toast.warning(`一次最多添加 ${MAX_ATTACH} 个文件`);
      break;
    }
    if (next.includes(p)) {
      if (!silentDup) toast.info(`已添加：${p}`);
      continue;
    }
    next.push(p);
  }
  attachments.value = next;
  if (paths.length) insertAtCursor(paths.map((p) => `@${p} `).join(''));
}

function insertAtCursor(s: string): void {
  const el = ta.value;
  if (!el) {
    text.value += s;
    return;
  }
  const pos = el.selectionStart;
  text.value = text.value.slice(0, pos) + s + text.value.slice(el.selectionEnd);
  void nextTick(() => {
    el.selectionStart = el.selectionEnd = pos + s.length;
    el.focus();
  });
}

async function onPaste(e: ClipboardEvent): Promise<void> {
  const items = e.clipboardData?.items;
  if (!items) return;
  const hasImage = [...items].some((i) => i.type.startsWith('image/'));
  if (!hasImage) return; // 普通文本粘贴不拦

  e.preventDefault();
  const r = await pasteImages(items);
  if (r.savedPaths.length) {
    addAttachments(r.savedPaths);
    toast.success(`已添加 ${r.savedPaths.length} 张图片`);
  }
  if (r.skipped.length) {
    toast.warning(`${r.skipped.length} 张图片未保存（${r.skipped[0] ?? ''}）`);
  }
}

async function runCommand(name: string, args: string): Promise<void> {
  try {
    switch (name) {
      case 'new':
        await transport.request('session.new', null);
        break;
      case 'clear':
        await transport.request('session.clear', null);
        break;
      case 'compact':
        await transport.request('session.compact', null);
        break;
      case 'undo':
        toast.info('撤销面板尚未接入，请稍候');
        break;
      case 'model':
      case 'mcp':
      case 'mode':
        ui.openSettings();
        break;
      case 'persona':
        if (args) await session.setPersona(args);
        else ui.openSettings();
        break;
      case 'cwd':
        if (args) await session.setCwd(args);
        break;
      case 'diagnostics': {
        const d = await transport.request<unknown>('system.diagnostics', null);
        const ok = await copyText(JSON.stringify(d, null, 2));
        toast[ok ? 'success' : 'error'](ok ? '诊断信息已复制' : '复制失败');
        break;
      }
      case 'help':
        window.dispatchEvent(new CustomEvent('halo:shortcut-help'));
        toast.info('快捷键：Enter 发送，Shift+Enter 换行，Esc 中断，Alt+↑↓ 切换会话');
        break;
      default:
        toast.warning(`未知命令：/${name}`);
    }
  } catch (e) {
    toast.error(`执行 /${name} 失败：${(e as Error).message}`);
  }
}

onMounted(() => ta.value?.focus());

// 询问消失后把焦点还给输入框（I-5）
watch(
  () => perms.pending,
  (now, before) => {
    if (before && !now) void nextTick(() => ta.value?.focus());
  }
);

/** 外部（示例提示、技能运行）填入文本 */
defineExpose({
  fill: (s: string) => {
    text.value = s;
    void nextTick(() => ta.value?.focus());
  },
});
</script>

<template>
  <div
    class="composer"
    @dragenter.prevent="drop.onDragEnter"
    @dragover.prevent
    @dragleave.prevent="drop.onDragLeave"
    @drop.prevent="drop.onDrop"
  >
    <DropOverlay v-if="drop.isDragging.value" />

    <!-- 权限询问卡（唯一可阻塞的 UI） -->
    <PermissionCard v-if="perms.pending && perms.pending.kind !== 'agent.rounds'" />
    <RoundsCard v-else-if="perms.pending" />

    <!-- 排队列表 -->
    <QueueList v-if="queue.length" :items="queue" @remove="unqueue" />

    <div class="composer-box">
      <div class="composer-top">
        <HBadge :content="modeLabel" :variant="modeVariant" />
        <HTag v-if="session.persona" size="sm">{{ session.persona }}</HTag>
        <span class="top-spacer" />
        <span v-if="totalUsage > 0" class="top-tokens">累计 {{ totalUsage }} tokens</span>
      </div>

      <textarea
        ref="ta"
        v-model="text"
        class="composer-input"
        rows="1"
        aria-label="消息输入框"
        :disabled="askPending"
        :placeholder="askPending ? '请先处理上方的确认' : '输入消息，Enter 发送，Shift+Enter 换行'"
        @input="onInput"
        @keydown="onKeydown"
        @paste="onPaste"
      />

      <div v-if="attachments.length" class="composer-attach">
        <HTag
          v-for="(a, i) in attachments"
          :key="a"
          closable
          size="sm"
          @close="attachments.splice(i, 1)"
        >
          {{ a }}
        </HTag>
      </div>

      <ComposerToolbar
        :busy="stream.busy"
        :can-send="canSend"
        :ask-pending="askPending"
        :cwd="session.cwd"
        @mention="mention.triggerManually()"
        @slash="slash.triggerManually()"
        @submit="submit"
        @cancel="cancel"
      />
    </div>

    <MentionPopover
      v-if="mention.open.value"
      :items="mention.items.value"
      :active="mention.active.value"
      :loading="mention.loading.value"
      :error="mention.error.value"
      :empty="mention.empty.value"
      @select="mention.select"
      @hover="mention.setActive"
    />
    <SlashPopover
      v-if="slash.open.value"
      :items="slash.items.value"
      :active="slash.active.value"
      @select="slash.select"
      @hover="slash.setActive"
    />
  </div>
</template>

<style scoped>
.composer {
  position: relative;
  padding: var(--space-2) var(--space-4) var(--space-3);
  border-top: 1px solid hsl(var(--border));
  background: hsl(var(--bg));
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  min-width: 0;
}
.composer-box {
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-md);
  background: hsl(var(--bg-elevated));
  padding: var(--space-2);
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  min-width: 0;
}
.composer-box:focus-within {
  box-shadow: var(--shadow-focus);
  border-color: hsl(var(--accent));
}
.composer-top {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
}
.top-spacer {
  flex: 1;
  min-width: 0;
}
.top-tokens {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-family: var(--font-mono);
}
.composer-input {
  width: 100%;
  border: 0;
  background: transparent;
  color: hsl(var(--fg));
  font-size: var(--text-sm);
  line-height: 1.5; /* 坑 8：IME 候选框定位 */
  padding: 6px 8px;
  min-height: calc(1.5em + 12px);
  resize: none;
  outline: none;
  margin-bottom: 8px; /* 坑 8：候选框不压住按钮栏 */
}
.composer-input::placeholder {
  color: hsl(var(--fg-subtle));
}
.composer-input::selection {
  background: hsl(var(--accent) / 0.35);
  color: hsl(var(--fg));
}
.composer-input:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}
.composer-attach {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-1);
}
</style>

<template>
  <div
    class="rounded-md border border-subtle bg-surface overflow-hidden text-xs"
    :data-testid="isViewImage ? 'tool-card-view_image' : 'tool-call-card'"
    :data-tool="card.functionName"
  >
    <!-- Header -->
    <div class="flex items-center gap-2 px-2.5 py-1.5 cursor-pointer select-none hover:bg-elevated/60 transition-colors" @click="toggle">
      <component :is="iconComponent" class="w-3.5 h-3.5 shrink-0 text-accent" />
      <span class="font-mono font-medium text-primary truncate">{{ card.functionName }}</span>

      <!-- Runner badge for exec_command -->
      <n-tag v-if="isExec" :type="runnerType" size="tiny" :bordered="false" class="font-mono shrink-0">
        {{ runnerLabel }}
      </n-tag>

      <span v-if="headText" class="font-mono text-secondary truncate flex-1 min-w-0" :title="headText">
        {{ headText }}
      </span>

      <!-- diff stats -->
      <span v-if="isFileEdit" class="font-mono text-[11px] shrink-0">
        <span class="text-[var(--stat-add-text)]">+{{ addedLines }}</span>
        <span> </span>
        <span class="text-[var(--stat-del-text)]">-{{ removedLines }}</span>
      </span>

      <!-- status -->
      <n-tag
        :type="effectiveStatus === 'done' ? 'success' : effectiveStatus === 'error' ? 'error' : 'info'"
        size="tiny"
        :bordered="false"
        class="shrink-0"
        :role="isExec ? 'status' : undefined"
      >
        {{ effectiveStatus === 'done' ? '已完成' : effectiveStatus === 'error' ? '失败' : '执行中' }}
      </n-tag>
      <span v-if="card.durationMs !== null && card.durationMs !== undefined" class="font-mono text-[11px] text-secondary shrink-0">
        {{ card.durationMs }}ms
      </span>
      <span
        v-if="isExec && card.exitCode !== null && card.exitCode !== undefined"
        class="font-mono text-[11px] shrink-0"
        :class="card.exitCode === 0 ? 'text-[var(--stat-add-text)]' : 'text-[var(--stat-del-text)]'"
      >
        Exit: {{ card.exitCode }}
      </span>
      <ChevronDown class="w-3.5 h-3.5 text-secondary shrink-0 transition-transform" :class="expanded ? 'rotate-180' : ''" />
    </div>

    <!-- need12 R1：流式写入字节计数行（write_file/edit_file 专属） -->
    <div
      v-show="bytesRowLatched"
      class="flex items-center gap-1.5 px-2.5 py-1 font-mono text-[11px] text-secondary border-t border-subtle bytes-counter"
      data-testid="tool-bytes-counter"
      :title="bytesTooltip"
    >
      <span class="bytes-dot" :class="bytesDotClass"></span>
      <span class="truncate" style="font-variant-numeric: tabular-nums">{{ bytesText }}</span>
      <span v-if="isReceiving" class="shimmer shrink-0">流式接收中…</span>
      <span v-else-if="card.status === 'running'" class="shrink-0">正在写入磁盘…</span>
      <span v-else-if="card.status === 'done'" class="shrink-0 text-[var(--stat-add-text)]">✓</span>
    </div>
    <!-- Body -->
    <n-collapse-transition :show="expanded">
      <div v-if="expanded" class="border-t border-subtle">
        <!-- (1) edit_file / write_file -> Unified Diff -->
        <div v-if="isFileEdit" class="font-mono app-text-small leading-relaxed">
          <div
            v-for="(line, idx) in visibleDiffLines"
            :key="idx"
            class="flex items-start gap-2 px-2.5 py-px whitespace-pre-wrap break-all border-l-2"
            :class="
              line.kind === 'del'
                ? 'bg-rose-500/15 text-[var(--diff-del-text)] border-rose-500'
                : line.kind === 'add'
                  ? 'bg-emerald-500/15 text-[var(--diff-add-text)] border-emerald-500'
                  : 'text-[var(--diff-ctx-text)] border-transparent'
            "
          >
            <span class="w-4 shrink-0 select-none opacity-70">{{ line.kind === 'del' ? '-' : line.kind === 'add' ? '+' : ' ' }}</span>
            <span class="flex-1 min-w-0">{{ line.text }}</span>
          </div>
          <button
            v-if="diffLines.length > 15 && !showAllDiff"
            type="button"
            class="w-full py-1 text-center text-accent hover:underline text-[11px]"
            @click="showAllDiff = true"
          >
            展开全部 {{ diffLines.length }} 行变更
          </button>
        </div>

        <!-- (2) exec_command -> Terminal -->
        <div v-else-if="isExec" class="p-2 space-y-1.5">
          <div class="font-mono app-text-small text-primary truncate" :title="execCommand">
            <span class="text-secondary select-none">$ </span>{{ execCommand }}
          </div>
          <div
            class="max-h-72 overflow-y-auto bg-[var(--tool-output-bg)] p-3 font-mono text-xs leading-relaxed text-[var(--tool-output-text)] select-text rounded whitespace-pre-wrap break-all border border-subtle"
            data-testid="terminal-output"
          >
            <span v-if="!displayOutput" class="text-secondary">等待输出...</span><span v-else>{{ displayOutput }}</span>
          </div>
          <div class="flex items-center justify-end gap-2">
            <n-button v-if="card.status === 'running'" size="tiny" type="error" ghost @click="handleKill">
              <template #icon><Square class="w-3 h-3" /></template>
              终止进程
            </n-button>
            <n-button v-else size="tiny" quaternary @click="handleCopy">
              <template #icon><Copy class="w-3 h-3" /></template>
              复制输出
            </n-button>
          </div>
        </div>
        <!-- (2.5) need09-05: view_image 三态 -->
        <div v-else-if="isViewImage" class="p-2.5 space-y-1.5">
          <div v-if="card.status === 'running'" class="font-mono app-text-small text-secondary break-all whitespace-pre-wrap">
            正在读取图片：{{ viewImagePathShort }}
          </div>
          <div v-else-if="card.status === 'error'" class="font-mono app-text-small text-rose-400 break-all whitespace-pre-wrap select-text">
            {{ displayOutput || '读取图片失败' }}
          </div>
          <div v-else class="space-y-1.5">
            <div class="font-mono app-text-small text-emerald-400 break-all">
              已读取 {{ viewImageName }} ({{ viewImageWH }}, {{ viewImageKB }})
            </div>
            <div v-if="viewImages.length" class="flex flex-wrap gap-1.5">
              <template v-for="img in viewImages" :key="img.imageId">
                <n-image
                  v-if="toolThumbOf(img)"
                  :src="toolThumbOf(img)"
                  :alt="img.fileName"
                  width="96"
                  object-fit="cover"
                  class="rounded border border-subtle"
                  :data-testid="`tool-image-${img.imageId}`"
                />
                <n-tag v-else size="small" :bordered="true" :title="img.fileName">
                  {{ img.fileName }}
                </n-tag>
              </template>
            </div>
            <div class="flex items-center gap-2">
              <n-button size="tiny" quaternary :data-testid="`tool-open-image-${card.callId}`" @click="handleOpenOrigin">
                打开原图
              </n-button>
              <n-button size="tiny" quaternary @click="handleCopy">
                <template #icon><Copy class="w-3 h-3" /></template>
                复制输出
              </n-button>
            </div>
            <div
              v-if="displayOutput"
              class="max-h-64 overflow-y-auto bg-[var(--tool-output-bg)] p-2.5 rounded font-mono app-text-small leading-relaxed text-[var(--tool-output-text)] whitespace-pre-wrap break-all select-text border border-subtle"
            >
              {{ viewImageTruncated }}
            </div>
          </div>
        </div>
        <div v-else class="p-2.5 space-y-1.5">
          <div class="font-mono app-text-small text-secondary break-all whitespace-pre-wrap">{{ compactSummary }}</div>
          <div
            v-if="displayOutput"
            class="max-h-64 overflow-y-auto bg-[var(--tool-output-bg)] p-2.5 rounded font-mono app-text-small leading-relaxed text-[var(--tool-output-text)] whitespace-pre-wrap break-all select-text border border-subtle"
          >
            {{ truncatedOutput }}
          </div>
          <div v-if="isSpawn" class="pt-1">
            <n-button size="small" type="primary" ghost data-testid="toolcard-open-sub" :disabled="!subId" @click="openSub">{{
              subId ? '查看子会话 →' : '执行中…'
            }}</n-button>
          </div>
        </div>
      </div>
    </n-collapse-transition>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useMessage } from 'naive-ui';
import { ChevronDown, Copy, FileDiff, FilePlus2, FileText, FolderSearch, Search, Square, Terminal, Wrench } from 'lucide-vue-next';
import type { AgentToolCard } from '@features/agent/stores/model';
import { getHostBridge, type ChatImageRef } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useSubSessionStore } from '@/stores/subSessionStore';
import { formatSmartBytes, formatSmartBytesShort } from '@shared/utils/formatSmartBytes';

const props = defineProps<{
  card: AgentToolCard;
  defaultCollapsed?: boolean;
}>();

const message = useMessage();
const workspaceStore = useWorkspaceStore();

const defaultExpanded = props.defaultCollapsed
  ? false
  : props.card.functionName === 'edit_file' || props.card.functionName === 'write_file' || props.card.functionName === 'exec_command';
const expanded = ref(defaultExpanded);
const showAllDiff = ref(false);

function toggle(): void {
  expanded.value = !expanded.value;
}

const isFileEdit = computed(() => props.card.functionName === 'edit_file' || props.card.functionName === 'write_file');
const isExec = computed(() => props.card.functionName === 'exec_command');
const isViewImage = computed(() => props.card.functionName === 'view_image');

// ---- need12 R1：字节计数行 ----
const receivedBytes = computed(() => Math.max(0, Math.floor(props.card.receivedBytes || 0)));
const showBytesRow = computed(() => isFileEdit.value);
/**
 * M1-5：单向锁存 —— 该行**一旦出现就不再消失**。
 * 当前 showBytesRow 只依赖 functionName（在同一张卡内恒定），锁存是为防止将来把
 * receivedBytes/status 纳入判定后，行反复显隐造成 ±20px 的高度抖动（治 R6.2）。
 * 配合 `v-show` + `.bytes-counter { min-height: 20px }`，占位在出现后保持稳定。
 */
const bytesRowLatched = ref(showBytesRow.value);
watch(
  showBytesRow,
  (v) => {
    if (v) bytesRowLatched.value = true;
  },
  { immediate: true }
);
const isReceiving = computed(
  () => props.card.status === 'running' && (props.card.fileBytes === null || props.card.fileBytes === undefined)
);
const bytesText = computed(() => {
  if (props.card.status === 'done' && typeof props.card.fileBytes === 'number' && Number.isFinite(props.card.fileBytes)) {
    return `参数已接收 ${formatSmartBytes(receivedBytes.value)} · 落盘 ${formatSmartBytes(props.card.fileBytes)}`;
  }
  const hot = receivedBytes.value >= 50 * 1024 ? ' 🔥' : '';
  return `参数已接收 ${formatSmartBytes(receivedBytes.value)}${hot}`;
});
const bytesTooltip = computed(() => `参数线字节（含 JSON 转义）≈ ${receivedBytes.value.toLocaleString('en-US')}；文件实际字节以完成态为准`);
const bytesDotClass = computed(() => {
  if (props.card.status === 'done') return 'bytes-dot-done';
  if (props.card.status === 'error') return 'bytes-dot-error';
  return 'bytes-dot-run';
});

const iconComponent = computed(() => {
  switch (props.card.functionName) {
    case 'edit_file':
      return FileDiff;
    case 'write_file':
      return FilePlus2;
    case 'read_file':
      return FileText;
    case 'list_dir':
      return FolderSearch;
    case 'grep_search':
      return Search;
    case 'exec_command':
      return Terminal;
    default:
      return Wrench;
  }
});

// need22-P3 G2：argsJson 按 callId+hash memo 缓存（工具卡多时避免重复 JSON.parse）。
const argsParseCache = new Map<string, Record<string, unknown>>();
const ARGS_CACHE_MAX = 100;
function hashStr(s: string): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return (h >>> 0).toString(16);
}
function parseArgs(): Record<string, unknown> {
  const raw = props.card.argsJson || '{}';
  const key = `${props.card.callId}:${hashStr(raw)}:${raw.length}`;
  const hit = argsParseCache.get(key);
  if (hit) return hit;
  let out: Record<string, unknown> = {};
  try {
    const parsed = JSON.parse(raw);
    if (parsed && typeof parsed === 'object') out = parsed as Record<string, unknown>;
  } catch (e) {
    console.warn('[tool-card] args parse failed:', props.card.callId, e);
  }
  argsParseCache.set(key, out);
  if (argsParseCache.size > ARGS_CACHE_MAX) {
    const oldest = argsParseCache.keys().next();
    if (!oldest.done) argsParseCache.delete(oldest.value);
  }
  return out;
}
const argsObj = computed(() => parseArgs());

const filePath = computed(() => {
  const v = argsObj.value['file_path'] ?? argsObj.value['path'] ?? '';
  return typeof v === 'string' ? v : '';
});

// bigChange P2-6: head 运行/完成两套文案；Bash 以 shell exit 覆盖调用状态。
const effectiveStatus = computed<'running' | 'done' | 'error'>(() => {
  if (isExec.value && props.card.exitCode !== null && props.card.exitCode !== undefined) {
    return props.card.exitCode === 0 ? 'done' : 'error';
  }
  if (props.card.status === 'error') return 'error';
  if (props.card.status === 'done') return 'done';
  return 'running';
});
const headRunning = computed(() => {
  const a = argsObj.value;
  const s = (v: unknown): string => (typeof v === 'string' ? v : '');
  switch (props.card.functionName) {
    case 'read_file':
      return filePath.value ? `正在读取 ${filePath.value}` : '正在读取文件';
    case 'write_file':
      return filePath.value ? `正在写入 ${filePath.value}` : '正在写入文件';
    case 'edit_file':
      return filePath.value ? `正在修改 ${filePath.value}` : '正在修改文件';
    case 'list_dir':
      return s(a['path'] || a['dir']) ? `正在浏览 ${s(a['path'] || a['dir'])}` : '正在浏览目录';
    case 'grep_search':
      return s(a['pattern'] || a['query']) ? `正在检索 ${s(a['pattern'] || a['query'])}` : '正在检索';
    case 'exec_command':
      return '正在运行';
    case 'view_image':
      return filePath.value ? `正在查看 ${filePath.value}` : '正在查看图片';
    default:
      return '正在执行';
  }
});
const headCompleted = computed(() => {
  const a = argsObj.value;
  const s = (v: unknown): string => (typeof v === 'string' ? v : '');
  switch (props.card.functionName) {
    case 'read_file':
      return filePath.value ? `已读取 ${filePath.value}` : '读取文件';
    case 'write_file':
      return filePath.value ? `写入 ${filePath.value}` : '写入文件';
    case 'edit_file':
      return filePath.value ? `修改 ${filePath.value}` : '修改文件';
    case 'list_dir':
      return s(a['path'] || a['dir']) ? `浏览 ${s(a['path'] || a['dir'])}` : '浏览目录';
    case 'grep_search':
      return s(a['pattern'] || a['query']) ? `检索 ${s(a['pattern'] || a['query'])}` : '检索';
    case 'exec_command':
      return '';
    case 'view_image':
      return filePath.value ? `查看 ${filePath.value}` : '查看图片';
    default:
      return filePath.value;
  }
});
const headText = computed(() => (effectiveStatus.value === 'running' ? headRunning.value : headCompleted.value));

interface DiffLine {
  kind: 'add' | 'del' | 'ctx';
  text: string;
}

const diffLines = computed<DiffLine[]>(() => {
  const a = argsObj.value;
  const toLines = (v: unknown): string[] => (typeof v === 'string' ? v.split('\n') : []);
  if (props.card.functionName === 'edit_file') {
    const oldLines = toLines(a['old_string']).map((t) => ({ kind: 'del' as const, text: t }));
    const newLines = toLines(a['new_string']).map((t) => ({ kind: 'add' as const, text: t }));
    return [...oldLines, ...newLines];
  }
  return toLines(a['content'] ?? a['new_string'] ?? '').map((t) => ({ kind: 'add' as const, text: t }));
});

const visibleDiffLines = computed(() => (showAllDiff.value ? diffLines.value : diffLines.value.slice(0, 15)));
const addedLines = computed(() => diffLines.value.filter((l) => l.kind === 'add').length);
const removedLines = computed(() => diffLines.value.filter((l) => l.kind === 'del').length);

const execCommand = computed(() => {
  const v = argsObj.value['command'];
  return typeof v === 'string' && v ? v : '(空命令)';
});

const runnerLabel = computed(() => {
  const v = argsObj.value['runner'];
  return typeof v === 'string' && v.toLowerCase() === 'python' ? 'PYTHON' : 'CMD.EXE';
});

const runnerType = computed<'info' | 'success'>(() => (runnerLabel.value === 'PYTHON' ? 'success' : 'info'));

const displayOutput = computed(() => props.card.resultText || '');

const truncatedOutput = computed(() => {
  const out = displayOutput.value;
  if (out.length > 8000) return out.slice(0, 8000) + '\n...（输出过长已截断）';
  return out;
});

/** need09-05：view_image 结果文本与存量一致截断（base64 永不进卡，6000 上限） */
const viewImageTruncated = computed(() => {
  const out = displayOutput.value;
  if (out.length > 6000) return out.slice(0, 6000) + '\n...（输出过长已截断）';
  return out;
});

const compactSummary = computed(() => {
  const a = argsObj.value;
  const s = (v: unknown): string => (typeof v === 'string' ? v : '');
  switch (props.card.functionName) {
    case 'read_file':
      return `已读取文件 ${filePath.value || '(未知)'}（点击展开查看参数与输出）`;
    case 'list_dir':
      return `已浏览目录 ${s(a['path'] || a['dir']) || '.'}（点击展开查看输出）`;
    case 'grep_search':
      return `检索 "${s(a['pattern'] || a['query'])}"（点击展开查看匹配行）`;
    default:
      return `参数：${props.card.argsJson || '{}'}（点击展开）`;
  }
});

// ---- need09-05 view_image 图片行 ----
const viewImages = computed<ChatImageRef[]>(() => (Array.isArray(props.card.images) ? props.card.images : []));

const viewImageRawPath = computed(() => {
  const a = argsObj.value;
  const v = a['path'] ?? a['file_path'] ?? '';
  return typeof v === 'string' ? v : '';
});

const viewImagePathShort = computed(() => {
  const p = viewImageRawPath.value || '(未知路径)';
  return p.length > 60 ? '…' + p.slice(p.length - 60) : p;
});

const viewImageName = computed(() => {
  const imgs = viewImages.value;
  if (imgs.length > 0 && imgs[0].fileName) return imgs[0].fileName;
  const p = viewImageRawPath.value;
  if (!p) return '(未知图片)';
  const parts = p.split(/[\\/]/).filter(Boolean);
  return parts.length > 0 ? parts[parts.length - 1] : p;
});

const viewImageWH = computed(() => {
  const first = viewImages.value.length > 0 ? viewImages.value[0] : null;
  if (first && first.width > 0 && first.height > 0) return `${first.width}x${first.height}`;
  return '?x?';
});

const viewImageKB = computed(() => {
  const first = viewImages.value.length > 0 ? viewImages.value[0] : null;
  // need22-P5 J7：收敛到 utils/formatSmartBytes（无尺寸时保持 '?KB' 占位）。
  if (first && first.sizeBytes > 0) return formatSmartBytesShort(first.sizeBytes);
  return '?KB';
});

const toolThumbCache = ref<Record<string, string>>({});
const toolThumbPending = new Set<string>();

function toolThumbOf(img: ChatImageRef): string {
  const hit = toolThumbCache.value[img.imageId];
  if (hit) return hit;
  if (!toolThumbPending.has(img.imageId)) {
    toolThumbPending.add(img.imageId);
    void (async () => {
      try {
        const ws = workspaceStore.activeWorkspaceId || workspaceStore.activeWorkspace?.id || '';
        const sid = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
        const res = await getHostBridge().getImageThumbnail(ws, sid, img.imageId);
        if (res.ok && typeof res.data === 'string' && res.data) {
          toolThumbCache.value = { ...toolThumbCache.value, [img.imageId]: res.data };
        }
      } catch {
        /* 失败显示文件名 chip，不崩时间轴 */
      } finally {
        toolThumbPending.delete(img.imageId);
      }
    })();
  }
  return '';
}

async function handleOpenOrigin(): Promise<void> {
  const first = viewImages.value.length > 0 ? viewImages.value[0] : null;
  const target = (first && first.relPath) || viewImageRawPath.value;
  if (!target) {
    message.warning('无可用原图路径');
    return;
  }
  try {
    await getHostBridge().openPathInExplorer(target);
  } catch {
    message.error('打开原图失败');
  }
}

async function handleKill(): Promise<void> {
  const sessionId = workspaceStore.activeSessionId || workspaceStore.activeSession?.id || '';
  if (!sessionId) return;
  try {
    const bridge = getHostBridge();
    await bridge.cancelAgentTurn(sessionId);
    message.info('已发送终止指令');
  } catch {
    message.error('终止指令发送失败');
  }
}

async function handleCopy(): Promise<void> {
  try {
    await navigator.clipboard.writeText(displayOutput.value);
    message.success('已复制终端输出');
  } catch {
    message.info('复制失败，请手动选择文本');
  }
}
const isSpawn = computed(() => props.card.functionName === 'spawn_subagent');
const subId = computed(() => {
  try {
    const out = displayOutput.value || '';
    const m =
      out.match(/"subSessionId"\s*:\s*"([^"]+)"/) ||
      out.match(/subSessionId[:=]\s*([A-Za-z0-9_\-]+)/) ||
      out.match(/(sess_[A-Za-z0-9_\-]+)/);
    if (m && m[1]) return m[1];
  } catch {
    /* ignore */
  }
  return '';
});
async function openSub() {
  if (!subId.value) return;
  try {
    const s = useSubSessionStore();
    const wsId = workspaceStore.activeWorkspaceId || '';
    const parent = workspaceStore.activeSessionId || '';
    if (wsId && parent) await s.enterSub(wsId, parent, subId.value);
  } catch {
    /* ignore */
  }
}
</script>

<style scoped>
.bytes-dot {
  width: 7px;
  height: 7px;
  border-radius: 9999px;
  flex-shrink: 0;
}
.bytes-dot-run {
  background: #3b82f6;
  animation: bytes-pulse 1.2s ease-in-out infinite;
}
.bytes-dot-done {
  background: #10b981;
}
.bytes-dot-error {
  background: #ef4444;
}
@keyframes bytes-pulse {
  0%,
  100% {
    opacity: 1;
    transform: scale(1);
  }
  50% {
    opacity: 0.45;
    transform: scale(0.8);
  }
}
.shimmer {
  animation: bytes-pulse 1.2s ease-in-out infinite;
}
.bytes-counter {
  font-variant-numeric: tabular-nums;
  /* M1-5：字节行占位高度固定，出现后不再改变卡片总高 */
  min-height: 20px;
}
</style>

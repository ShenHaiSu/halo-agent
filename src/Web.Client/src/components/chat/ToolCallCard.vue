<template>
  <div
    class="rounded-md border border-subtle bg-surface overflow-hidden text-xs"
    data-testid="tool-call-card"
    :data-tool="card.functionName"
  >
    <!-- Header -->
    <div
      class="flex items-center gap-2 px-2.5 py-1.5 cursor-pointer select-none hover:bg-elevated/60 transition-colors"
      @click="toggle"
    >
      <component :is="iconComponent" class="w-3.5 h-3.5 shrink-0 text-accent" />
      <span class="font-mono font-medium text-primary truncate">{{ card.functionName }}</span>

      <!-- Runner badge for exec_command -->
      <n-tag v-if="isExec" :type="runnerType" size="tiny" :bordered="false" class="font-mono shrink-0">
        {{ runnerLabel }}
      </n-tag>

      <span v-if="summaryText" class="font-mono text-secondary truncate flex-1 min-w-0" :title="summaryText">
        {{ summaryText }}
      </span>

      <!-- diff stats -->
      <span v-if="isFileEdit" class="font-mono text-[11px] shrink-0">
        <span class="text-emerald-400">+{{ addedLines }}</span>
        <span class="text-slate-500"> </span>
        <span class="text-rose-400">-{{ removedLines }}</span>
      </span>

      <!-- status -->
      <n-tag
        :type="card.status === 'done' ? 'success' : card.status === 'error' ? 'error' : 'info'"
        size="tiny"
        :bordered="false"
        class="shrink-0"
      >
        {{ card.status === 'done' ? '已完成' : card.status === 'error' ? '失败' : '执行中' }}
      </n-tag>
      <span v-if="card.durationMs !== null && card.durationMs !== undefined" class="font-mono text-[11px] text-secondary shrink-0">
        {{ card.durationMs }}ms
      </span>
      <span v-if="isExec && card.exitCode !== null && card.exitCode !== undefined" class="font-mono text-[11px] shrink-0" :class="card.exitCode === 0 ? 'text-emerald-400' : 'text-rose-400'">
        Exit: {{ card.exitCode }}
      </span>

      <ChevronDown
        class="w-3.5 h-3.5 text-secondary shrink-0 transition-transform"
        :class="expanded ? 'rotate-180' : ''"
      />
    </div>

    <!-- Body -->
    <n-collapse-transition :show="expanded">
      <div class="border-t border-subtle">
        <!-- (1) edit_file / write_file -> Unified Diff -->
        <div v-if="isFileEdit" class="font-mono text-[11px] leading-relaxed">
          <div
            v-for="(line, idx) in visibleDiffLines"
            :key="idx"
            class="flex items-start gap-2 px-2.5 py-px whitespace-pre-wrap break-all border-l-2"
            :class="
              line.kind === 'del'
                ? 'bg-rose-500/15 text-rose-200 border-rose-500'
                : line.kind === 'add'
                  ? 'bg-emerald-500/15 text-emerald-200 border-emerald-500'
                  : 'text-slate-400 border-transparent'
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
          <div class="font-mono text-[11px] text-slate-300 truncate" :title="execCommand">
            <span class="text-secondary select-none">$ </span>{{ execCommand }}
          </div>
          <div
            class="max-h-72 overflow-y-auto bg-[#090d12] p-3 font-mono text-xs leading-relaxed text-slate-200 select-text rounded whitespace-pre-wrap break-all"
            data-testid="terminal-output"
          ><span v-if="!displayOutput" class="text-slate-500">等待输出...</span><span v-else>{{ displayOutput }}</span></div>
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

        <!-- (3) read/list/grep -> compact expandable -->
        <div v-else class="p-2.5 space-y-1.5">
          <div class="font-mono text-[11px] text-secondary break-all whitespace-pre-wrap">{{ compactSummary }}</div>
          <div
            v-if="displayOutput"
            class="max-h-64 overflow-y-auto bg-[#090d12] p-2.5 rounded font-mono text-[11px] leading-relaxed text-slate-300 whitespace-pre-wrap break-all select-text"
          >{{ truncatedOutput }}</div>
        </div>
      </div>
    </n-collapse-transition>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { useMessage } from 'naive-ui';
import {
  ChevronDown,
  Copy,
  FileDiff,
  FilePlus2,
  FileText,
  FolderSearch,
  Search,
  Square,
  Terminal,
  Wrench,
} from 'lucide-vue-next';
import type { AgentToolCard } from '@/stores/agentStore';
import { getHostBridge } from '@/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';

const props = defineProps<{
  card: AgentToolCard;
}>();

const message = useMessage();
const workspaceStore = useWorkspaceStore();

const defaultExpanded = props.card.functionName === 'edit_file' || props.card.functionName === 'write_file' || props.card.functionName === 'exec_command';
const expanded = ref(defaultExpanded);
const showAllDiff = ref(false);

function toggle(): void {
  expanded.value = !expanded.value;
}

const isFileEdit = computed(() => props.card.functionName === 'edit_file' || props.card.functionName === 'write_file');
const isExec = computed(() => props.card.functionName === 'exec_command');

const iconComponent = computed(() => {
  switch (props.card.functionName) {
    case 'edit_file': return FileDiff;
    case 'write_file': return FilePlus2;
    case 'read_file': return FileText;
    case 'list_dir': return FolderSearch;
    case 'grep_search': return Search;
    case 'exec_command': return Terminal;
    default: return Wrench;
  }
});

function parseArgs(): Record<string, unknown> {
  try {
    const parsed = JSON.parse(props.card.argsJson || '{}');
    if (parsed && typeof parsed === 'object') return parsed as Record<string, unknown>;
  } catch {
    /* fall through */
  }
  return {};
}

const argsObj = computed(() => parseArgs());

const filePath = computed(() => {
  const v = argsObj.value['file_path'] ?? argsObj.value['path'] ?? '';
  return typeof v === 'string' ? v : '';
});

const summaryText = computed(() => {
  const a = argsObj.value;
  const s = (v: unknown): string => (typeof v === 'string' ? v : '');
  switch (props.card.functionName) {
    case 'read_file': return filePath.value ? `已读取 ${filePath.value}` : '读取文件';
    case 'write_file': return filePath.value ? `写入 ${filePath.value}` : '写入文件';
    case 'edit_file': return filePath.value ? `修改 ${filePath.value}` : '修改文件';
    case 'list_dir': return s(a['path'] || a['dir']) ? `浏览 ${s(a['path'] || a['dir'])}` : '浏览目录';
    case 'grep_search': return s(a['pattern'] || a['query']) ? `检索 ${s(a['pattern'] || a['query'])}` : '检索';
    case 'exec_command': return '';
    default: return filePath.value;
  }
});

interface DiffLine { kind: 'add' | 'del' | 'ctx'; text: string; }

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

const compactSummary = computed(() => {
  const a = argsObj.value;
  const s = (v: unknown): string => (typeof v === 'string' ? v : '');
  switch (props.card.functionName) {
    case 'read_file': return `已读取文件 ${filePath.value || '(未知)'}（点击展开查看参数与输出）`;
    case 'list_dir': return `已浏览目录 ${s(a['path'] || a['dir']) || '.'}（点击展开查看输出）`;
    case 'grep_search': return `检索 "${s(a['pattern'] || a['query'])}"（点击展开查看匹配行）`;
    default: return `参数：${props.card.argsJson || '{}'}（点击展开）`;
  }
});

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
</script>

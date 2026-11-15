<template>
  <div
    class="rounded-md border border-subtle bg-surface overflow-hidden text-xs"
    :data-testid="isViewImage ? 'tool-card-view_image' : 'tool-call-card'"
    :data-tool="card.functionName"
  >
    <ToolCardHeader :card="card" :head-text="headText" :effective-status="effectiveStatus" :expanded="expanded" @toggle="toggle" />
    <ToolBytesRow :card="card" />
    <n-collapse-transition :show="expanded">
      <div v-if="expanded" class="border-t border-subtle">
        <ToolDiffView v-if="isFileEdit" :args="argsObj" :function-name="card.functionName" />
        <ToolExecView v-else-if="isExec" :card="card" />
        <ToolViewImageView v-else-if="isViewImage" :card="card" />
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
import { computed, ref } from 'vue';
import type { AgentToolCard } from '@features/agent/stores/model';
import { toolHeadText, type ToolCardStatus } from '@features/agent/utils/toolHeadSummary';
import { execCommandOf, execShortOf, parseToolArgs, stringArg } from '@features/agent/utils/toolArgs';
import { useToolCardActions } from '@features/agent/composables/useToolCardActions';
import ToolCardHeader from './toolcard/ToolCardHeader.vue';
import ToolBytesRow from './toolcard/ToolBytesRow.vue';
import ToolDiffView from './toolcard/ToolDiffView.vue';
import ToolExecView from './toolcard/ToolExecView.vue';
import ToolViewImageView from './toolcard/ToolViewImageView.vue';

/** 工具卡装配器：分支路由 + 头文案 + 通用分支，分支细节下沉 toolcard 子组件。 */
const props = defineProps<{
  card: AgentToolCard;
  defaultCollapsed?: boolean;
}>();

const defaultExpanded = props.defaultCollapsed
  ? false
  : props.card.functionName === 'edit_file' || props.card.functionName === 'write_file' || props.card.functionName === 'exec_command';
const expanded = ref(defaultExpanded);

function toggle(): void {
  expanded.value = !expanded.value;
}

const argsObj = computed(() => parseToolArgs(props.card));

const isFileEdit = computed(() => props.card.functionName === 'edit_file' || props.card.functionName === 'write_file');
const isExec = computed(() => props.card.functionName === 'exec_command');
const isViewImage = computed(() => props.card.functionName === 'view_image');
const isSpawn = computed(() => props.card.functionName === 'spawn_subagent');

// 执行类以退出码覆盖调用状态，其余直用卡状态。
const effectiveStatus = computed<ToolCardStatus>(() => {
  if (isExec.value && props.card.exitCode !== null && props.card.exitCode !== undefined) {
    return props.card.exitCode === 0 ? 'done' : 'error';
  }
  if (props.card.status === 'error') return 'error';
  if (props.card.status === 'done') return 'done';
  return 'running';
});

const execShort = computed(() => execShortOf(execCommandOf(argsObj.value)));
const headText = computed(() => toolHeadText(props.card.functionName, argsObj.value, execShort.value, effectiveStatus.value));

const actions = useToolCardActions(() => props.card);
const displayOutput = computed(() => actions.displayOutput.value);
const subId = computed(() => actions.subId.value);
function openSub(): void {
  void actions.openSub();
}

const truncatedOutput = computed(() => {
  const out = displayOutput.value;
  if (out.length > 8000) return out.slice(0, 8000) + '\n...（输出过长已截断）';
  return out;
});

const filePath = computed(() => stringArg(argsObj.value, 'file_path', 'path'));

const compactSummary = computed(() => {
  const a = argsObj.value;
  switch (props.card.functionName) {
    case 'read_file':
      return `已读取文件 ${filePath.value || '(未知)'}（点击展开查看参数与输出）`;
    case 'list_dir':
      return `已浏览目录 ${stringArg(a, 'path', 'dir') || '.'}（点击展开查看输出）`;
    case 'grep_search':
      return `检索 "${stringArg(a, 'pattern', 'query')}"（点击展开查看匹配行）`;
    default:
      return `参数：${props.card.argsJson || '{}'}（点击展开）`;
  }
});
</script>

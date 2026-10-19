<template>
  <div class="goal-panel-content" :class="[`goal-panel--${mode}`]" data-testid="goal-panel-content">
    <div class="goal-panel-head">
      <span class="goal-panel-title">🎯 目标<span v-if="readonly">（父目标·只读）</span></span>
      <span class="goal-panel-head-actions">
        <n-button v-if="mode === 'drawer'" size="tiny" quaternary :loading="loading" title="刷新" data-testid="goal-refresh-btn" @click="onRefresh">刷新</n-button>
      </span>
    </div>
    <div v-if="loading" class="goal-skeleton" aria-busy="true">
      <div v-for="i in 3" :key="i" class="goal-skeleton-bar" />
    </div>
    <div v-else-if="loadError" class="goal-error" data-testid="goal-load-error">
      <p>目标加载失败：{{ loadError }}</p>
      <n-button size="small" @click="onRefresh">重试</n-button>
    </div>
    <template v-else>
      <div v-if="corrupt" class="goal-corrupt" data-testid="goal-corrupt-warn">
        目标文件损坏，已视为空，可重置
        <n-button v-if="!readonly" size="tiny" data-testid="goal-reset-btn" @click="onReset">重置</n-button>
      </div>
      <div v-if="editing && staleTip" class="goal-stale" data-testid="goal-stale-tip">
        目标已被 Agent 更新，你的编辑以保存为准。
        <n-button size="tiny" quaternary @click="reloadDraft">载入最新</n-button>
      </div>
      <div v-if="!hasContent && !editing" class="goal-empty" data-testid="goal-empty">
        <p>尚未设定目标</p>
        <n-button v-if="!readonly" size="small" type="primary" data-testid="goal-save-btn" @click="openEdit">设定目标</n-button>
      </div>
      <div v-else-if="!editing" class="goal-info">
        <h4 class="goal-h" :title="title">{{ title || '（无标题）' }}</h4>
        <p v-if="showDesc" class="goal-desc">{{ descShown }}<button v-if="descClamped" class="goal-link" @click="descOpen = !descOpen">{{ descOpen ? '收起' : '展开' }}</button></p>
        <div class="goal-progress-row">
          <n-progress type="line" :percentage="pct" :show-indicator="false" data-testid="goal-progress-bar" style="flex: 1" />
          <span class="goal-meta">{{ done }}/{{ total }} · {{ note }}</span>
        </div>
        <ul class="goal-ms">
          <li v-for="m in milestones" :key="m.id" data-testid="goal-milestone-item" class="goal-ms-item">
            <n-checkbox :checked="m.done" :disabled="readonly" @update:checked="(v: boolean) => onToggleMilestone(m.id, v)">{{ m.title }}</n-checkbox>
          </li>
        </ul>
      </div>
      <div v-if="!readonly" class="goal-foot">
        <n-button v-if="!editing" size="small" data-testid="goal-save-btn" @click="openEdit">编辑</n-button>
        <n-button v-if="!editing" size="small" data-testid="goal-reset-btn" @click="onReset">重置</n-button>
        <span v-if="dirty && !editing" class="goal-dirty">● 未保存</span>
      </div>
      <div v-if="editing" class="goal-edit" data-testid="goal-edit-form">
        <n-input v-model:value="draft.title" placeholder="标题" />
        <n-input v-model:value="draft.description" type="textarea" placeholder="描述" :autosize="{ minRows: 2, maxRows: 6 }" />
        <div class="goal-slider-row">
          <n-slider v-model:value="draft.percent" :min="0" :max="100" style="flex: 1" />
          <span class="goal-meta">{{ draft.percent }}%</span>
          <n-button size="tiny" quaternary title="按里程碑自动计算进度" @click="autoPercent">自动</n-button>
        </div>
        <n-input v-model:value="draft.note" placeholder="备注" />
        <n-dynamic-input v-model:value="msTitles" placeholder="里程碑一行一标题" />
        <div class="goal-edit-btns">
          <n-button size="small" type="primary" data-testid="goal-save-btn" :loading="saving" @click="onSave">保存</n-button>
          <n-button size="small" @click="editing = false">取消</n-button>
        </div>
      </div>
    </template>
  </div>
</template>
<script setup lang="ts">
/**
 * need20：Goal 唯一实现（Drawer 内使用；mode=float/drawer 保留兼容）。
 * 数据唯一来源：goalStore.goal。组件内部不 watch 会话切换，由宿主负责 load。
 */
import { computed, ref, watch } from 'vue';
import { useDialog, useMessage } from 'naive-ui';
import { useGoalStore } from '@/stores/goalStore';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { copyTextToClipboard } from '@/utils/historyAnchor';

const props = defineProps<{
  mode: 'float' | 'drawer';
  readonly?: boolean;
}>();
defineEmits<{
  (e: 'collapse'): void;
  (e: 'minimize'): void;
}>();

const goalStore = useGoalStore();
const ws = useWorkspaceStore();
const message = useMessage();
const dialog = useDialog();

const loading = computed(() => goalStore.loading);
const corrupt = computed(() => goalStore.corrupted);
const title = computed(() => goalStore.goal?.title || '');
const description = computed(() => goalStore.goal?.description || '');
const pct = computed(() => goalStore.pct);
const done = computed(() => goalStore.done);
const total = computed(() => goalStore.total);
const note = computed(() => goalStore.goal?.note || '');
const milestones = computed(() => goalStore.goal?.milestones || []);
const hasContent = computed(() => !!title.value || !!description.value || total.value > 0);
const loadError = ref('');
const staleTip = ref(false);

watch(() => goalStore.goal, () => { if (editing.value) staleTip.value = true; });

const descOpen = ref(false);
const descShown = computed(() => {
  const d = description.value;
  if (props.mode !== 'drawer' || descOpen.value) return d;
  const lines = d.split('\n');
  if (lines.length <= 6 && d.length <= 600) return d;
  return lines.slice(0, 6).join('\n').slice(0, 600);
});
const descClamped = computed(() => props.mode === 'drawer' && !descOpen.value && (description.value.split('\n').length > 6 || description.value.length > 600));
const showDesc = computed(() => !!description.value);

const editing = ref(false);
const saving = ref(false);
const dirty = ref(false);
const draft = ref({ title: '', description: '', percent: 0, note: '', milestones: [] as Array<{ id: string; title: string; done: boolean }> });
const msTitles = ref<string[]>([]);

function openEdit(): void {
  draft.value = {
    title: title.value, description: description.value, percent: pct.value, note: note.value,
    milestones: milestones.value.map((m) => ({ ...m })),
  };
  msTitles.value = milestones.value.map((m) => m.title);
  staleTip.value = false;
  editing.value = true;
}
function reloadDraft(): void {
  openEdit();
}
function randId(): string {
  const base = Date.now().toString(36);
  let rand = '';
  try {
    const arr = new Uint32Array(2);
    crypto.getRandomValues(arr);
    rand = Array.from(arr).map((n) => n.toString(36)).join('').slice(0, 6);
  } catch {
    rand = Math.floor(Math.random() * 36 * 36 * 36 * 36 * 36 * 36).toString(36);
  }
  return `ms_${base}_${rand || '0'}`;
}
function autoPercent(): void {
  const totalN = msTitles.value.filter((t) => t.trim()).length;
  if (totalN === 0) return;
  const doneN = draft.value.milestones.filter((m) => m.done && m.title.trim()).length;
  draft.value.percent = Math.round((doneN / totalN) * 100);
}
function onToggleMilestone(id: string, v: boolean): void {
  if (props.readonly) return;
  const hit = draft.value.milestones.find((m) => m.id === id);
  const live = milestones.value.find((m) => m.id === id);
  if (editing.value && hit) {
    hit.done = v;
  } else {
    // 非编辑态勾选：本地 draft + 脏标记，由保存落盘
    if (draft.value.milestones.length === 0) {
      draft.value = {
        title: title.value, description: description.value, percent: pct.value, note: note.value,
        milestones: milestones.value.map((m) => ({ ...m })),
      };
      msTitles.value = milestones.value.map((m) => m.title);
    }
    const d = draft.value.milestones.find((m) => m.id === id);
    if (d) d.done = v;
    else if (live) draft.value.milestones.push({ ...live, done: v });
    dirty.value = true;
    void onSaveDraftOnly();
  }
}

let autoSaveTimer: number | null = null;
async function onSaveDraftOnly(): Promise<void> {
  if (editing.value) return;
  if (autoSaveTimer !== null) window.clearTimeout(autoSaveTimer);
  autoSaveTimer = window.setTimeout(() => {
    autoSaveTimer = null;
    void (async () => {
      const wsId = ws.activeWorkspaceId || '';
      const sessId = ws.activeSessionId || '';
      if (!wsId || !sessId) return;
      const payload = buildPayload();
      const r = await goalStore.save(wsId, sessId, payload);
      if (r.ok) { dirty.value = false; message.success('目标已保存，下一轮生效'); }
      else message.error(r.message);
    })();
  }, 2000);
}

function buildPayload() {
  return {
    title: draft.value.title.trim(),
    description: draft.value.description,
    percent: Math.max(0, Math.min(100, draft.value.percent)),
    note: draft.value.note,
    updatedAtUtc: new Date().toISOString(),
    milestones: msTitles.value
      .map((t) => t.trim())
      .map((t, i) => {
        const src = draft.value.milestones[i];
        return { id: src?.id || randId(), title: t, done: !!src?.done };
      })
      .filter((m) => m.title),
  };
}

async function onSave(): Promise<void> {
  const wsId = ws.activeWorkspaceId || '';
  const sessId = ws.activeSessionId || '';
  if (!wsId || !sessId) { message.warning('请先选择工作区/会话'); return; }
  saving.value = true;
  try {
    const r = await goalStore.save(wsId, sessId, buildPayload());
    if (r.ok) { message.success('目标已保存，下一轮生效'); editing.value = false; dirty.value = false; staleTip.value = false; }
    else message.error(r.message);
  } finally {
    saving.value = false;
  }
}
async function onReset(): Promise<void> {
  const wsId = ws.activeWorkspaceId || '';
  const sessId = ws.activeSessionId || '';
  const doReset = async () => {
    const r = await goalStore.clear(wsId, sessId);
    if (r.ok) { message.success('目标已重置'); editing.value = false; dirty.value = false; }
    else message.error(r.message);
  };
  if (props.mode === 'drawer') {
    dialog.warning({
      title: '重置目标',
      content: '将清空标题/描述/里程碑/进度，不可撤销。',
      positiveText: '重置',
      negativeText: '取消',
      onPositiveClick: () => { void doReset(); },
    });
  } else {
    await doReset();
  }
}
async function onRefresh(): Promise<void> {
  const wsId = ws.activeWorkspaceId || '';
  const sessId = ws.activeSessionId || '';
  loadError.value = '';
  try {
    await goalStore.load(wsId, sessId);
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e);
  }
}
async function onCopyMarkdown(): Promise<void> {
  const g = goalStore.goal;
  if (!g) return;
  const lines = [`# ${g.title || '（无标题）'}`, '', g.description || '', '', `进度：${pct.value}%（${done.value}/${total.value}）`];
  for (const m of milestones.value) lines.push(`- [${m.done ? 'x' : ' '}] ${m.title}`);
  const ok = await copyTextToClipboard(lines.join('\n'));
  if (ok) message.success('已复制为 Markdown');
  else message.error('复制失败');
}
void onCopyMarkdown;
</script>
<style scoped>
.goal-panel-content { display: flex; flex-direction: column; gap: 8px; min-height: 0; color: var(--text-primary); }
.goal-panel-head { display: flex; align-items: center; justify-content: space-between; height: 34px; flex: 0 0 auto; }
.goal-panel-title { font-size: 13px; font-weight: 600; }
.goal-panel-head-actions { display: flex; gap: 4px; }
.goal-skeleton-bar { height: 12px; border-radius: 6px; background: var(--bg-elevated); border: 1px solid var(--border-subtle); margin: 6px 0; }
.goal-error { padding: 16px; text-align: center; color: var(--text-secondary); font-size: 12px; }
.goal-corrupt { background: #fef3c7; border: 1px solid #f59e0b; border-radius: 8px; padding: 6px 8px; font-size: 12px; color: #92400e; display: flex; align-items: center; gap: 8px; }
.goal-stale { background: rgba(245,158,11,.12); border: 1px solid rgba(245,158,11,.4); border-radius: 8px; padding: 6px 8px; font-size: 12px; display: flex; align-items: center; gap: 8px; }
.goal-empty { padding: 20px 12px; text-align: center; color: var(--text-secondary); font-size: 12px; }
.goal-info { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.goal-h { font-size: 14px; font-weight: 600; margin: 0; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
.goal-desc { font-size: 12px; color: var(--text-secondary); white-space: pre-wrap; word-break: break-word; margin: 0; }
.goal-link { font-size: 12px; color: var(--accent-primary); background: none; border: none; cursor: pointer; }
.goal-progress-row { display: flex; align-items: center; gap: 8px; }
.goal-meta { font-size: 11px; color: var(--text-secondary); font-family: var(--font-mono); white-space: nowrap; }
.goal-ms { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
.goal-ms-item { font-size: 12px; }
.goal-foot { display: flex; align-items: center; gap: 8px; position: sticky; bottom: 0; padding: 6px 0; background: inherit; }
.goal-dirty { font-size: 11px; color: #f59e0b; }
.goal-edit { display: flex; flex-direction: column; gap: 8px; }
.goal-edit-btns { display: flex; gap: 8px; }
.goal-slider-row { display: flex; align-items: center; gap: 8px; }
.goal-panel--float { padding: 8px 10px; }
.goal-panel--drawer { padding: 2px; }
</style>

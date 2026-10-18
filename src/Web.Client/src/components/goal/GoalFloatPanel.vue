<template>
  <div class="goal-float" :class="[panel, { flash: updatedFlash }]" :style="posStyle" data-testid="goal-float-panel">
    <button v-if="panel === 'mini'" data-testid="goal-mini-btn" class="goal-mini" @click="expand">◎ {{ pct }}% / 设目标</button>
    <div v-else-if="panel === 'collapsed'" class="goal-collapsed" @click="expand">
      <span class="goal-title">{{ title || '尚未设定目标' }}</span>
      <n-progress type="circle" :percentage="pct" :show-indicator="false" style="width:16px;height:16px" data-testid="goal-progress-bar" />
      <button data-testid="goal-expand-btn" class="goal-link" @click.stop="expand">展开</button>
      <button data-testid="goal-minimize-btn" class="goal-link" @click.stop="minimize">–</button>
    </div>
    <div v-else class="goal-expanded">
      <header class="goal-head">
        <span>🎯 目标<span v-if="readonly">（父目标·只读）</span></span>
        <span class="goal-drag" title="拖拽移动" @pointerdown="onDragStart">⠿</span>
        <button data-testid="goal-collapse-btn" @click="collapse">收起</button>
        <button data-testid="goal-minimize-btn" @click="minimize">–</button>
      </header>
      <div v-if="corrupt" class="goal-corrupt" data-testid="goal-corrupt-warn">目标文件损坏，已视为空，可重置 <button data-testid="goal-reset-btn" @click="onReset">重置</button></div>
      <div class="goal-body">
        <div v-if="!hasContent">空态：尚未设定目标 <button data-testid="goal-expand-btn" @click="openEdit">设定目标</button></div>
        <div v-else>
          <h4>{{ title }}</h4>
          <p>{{ description }}</p>
          <n-progress type="line" :percentage="pct" :show-indicator="false" data-testid="goal-progress-bar" />
          <span>{{ done }}/{{ total }} · {{ note }}</span>
          <ul>
            <li v-for="m in milestones" :key="m.id" data-testid="goal-milestone-item">{{ m.done ? '[x]' : '[ ]' }} {{ m.title }}</li>
          </ul>
        </div>
      </div>
      <footer v-if="!readonly" class="goal-foot">
        <button data-testid="goal-save-btn" @click="openEdit">编辑</button>
        <button data-testid="goal-reset-btn" @click="onReset">重置</button>
      </footer>
      <div v-if="editing" class="goal-edit">
        <n-input v-model:value="draft.title" placeholder="标题" />
        <n-input v-model:value="draft.description" type="textarea" placeholder="描述" />
        <n-slider v-model:value="draft.percent" :min="0" :max="100" />
        <n-input v-model:value="draft.note" placeholder="备注" />
        <n-dynamic-input v-model:value="msTitles" placeholder="里程碑一行一标题" />
        <div class="goal-edit-btns">
          <n-button size="small" type="primary" data-testid="goal-save-btn" @click="onSave">保存</n-button>
          <n-button size="small" @click="editing = false">取消</n-button>
        </div>
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue';
import { useGoalStore } from '@/stores/goalStore';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useSubSessionStore } from '@/stores/subSessionStore';
import { useMessage } from 'naive-ui';

const goalStore = useGoalStore();
const ws = useWorkspaceStore();
const sub = useSubSessionStore();
const message = useMessage();
const panel = computed(() => goalStore.panel);
const pct = computed(() => goalStore.pct);
const done = computed(() => goalStore.done);
const total = computed(() => goalStore.total);
const title = computed(() => goalStore.goal?.title || '');
const description = computed(() => goalStore.goal?.description || '');
const note = computed(() => goalStore.goal?.note || '');
const milestones = computed(() => goalStore.goal?.milestones || []);
const hasContent = computed(() => !!title.value || !!description.value || total.value > 0);
const corrupt = computed(() => goalStore.corrupted);
const updatedFlash = computed(() => goalStore.updatedFlash);
const readonly = computed(() => sub.mode === 'sub');
const posStyle = computed(() => ({ position: 'absolute' as const, right: goalStore.pos.right + 'px', bottom: goalStore.pos.bottom + 'px' }));
const editing = ref(false);
const draft = ref({ title: '', description: '', percent: 0, note: '', milestones: [] as Array<{ id: string; title: string; done: boolean }> });
const msTitles = ref<string[]>([]);

function expand() { goalStore.setPanel('expanded'); }
function collapse() { goalStore.setPanel('collapsed'); }
function minimize() { goalStore.setPanel('mini'); }
function openEdit() {
  draft.value = { title: title.value, description: description.value, percent: pct.value, note: note.value, milestones: [...milestones.value] };
  msTitles.value = milestones.value.map((m) => m.title);
  editing.value = true;
  goalStore.setPanel('expanded');
}
async function onSave() {
  const wsId = ws.activeWorkspaceId || '';
  const sessId = ws.activeSessionId || '';
  const payload = { title: draft.value.title, description: draft.value.description, percent: draft.value.percent, note: draft.value.note, updatedAtUtc: new Date().toISOString(), milestones: msTitles.value.filter((t) => t.trim()).map((t, i) => ({ id: draft.value.milestones[i]?.id || `ms_${Date.now()}_${i}`, title: t.trim(), done: draft.value.milestones[i]?.done || false })) };
  const r = await goalStore.save(wsId, sessId, payload);
  if (r.ok) { message.success('目标已保存，下一轮生效'); editing.value = false; }
  else message.error(r.message);
}
async function onReset() {
  const wsId = ws.activeWorkspaceId || '';
  const sessId = ws.activeSessionId || '';
  const r = await goalStore.clear(wsId, sessId);
  if (r.ok) { message.success('目标已重置'); editing.value = false; }
  else message.error(r.message);
}
let dragOff: { x: number; y: number } | null = null;
function onDragStart(e: PointerEvent) {
  dragOff = { x: e.clientX, y: e.clientY };
  const startR = goalStore.pos.right; const startB = goalStore.pos.bottom;
  const mv = (ev: PointerEvent) => {
    if (!dragOff) return;
    const dx = dragOff.x - ev.clientX; const dy = ev.clientY - dragOff.y;
    goalStore.movePos(Math.max(0, startR + dx), Math.max(0, startB + dy));
  };
  const up = () => { dragOff = null; window.removeEventListener('pointermove', mv); window.removeEventListener('pointerup', up); };
  window.addEventListener('pointermove', mv); window.addEventListener('pointerup', up);
}
function onKey(e: KeyboardEvent) { if (e.key === 'Escape' && dragOff) dragOff = null; }
onMounted(() => {
  goalStore.watchSession();
  if (window.innerWidth < 900 && goalStore.panel === 'expanded') goalStore.setPanel('collapsed');
  window.addEventListener('keydown', onKey);
});
onUnmounted(() => window.removeEventListener('keydown', onKey));
</script>
<style scoped>
.goal-float { position: absolute; z-index: 40; width: 320px; max-width: 94vw; max-height: 46vh; overflow: auto; background: var(--bg-elevated, #fff); border: 1px solid var(--border-subtle, #e5e7eb); border-radius: 12px; box-shadow: 0 8px 24px rgba(0,0,0,.12); transition: all 180ms; color: var(--text-primary, #111); }
.goal-float.mini { width: auto; }
.goal-mini { border: 1px dashed var(--border-subtle); border-radius: 999px; padding: 6px 10px; background: transparent; cursor: pointer; }
.goal-collapsed { display: flex; align-items: center; gap: 8px; padding: 8px 10px; cursor: pointer; }
.goal-expanded { padding: 8px 10px; }
.goal-head { display: flex; align-items: center; gap: 8px; font-weight: 600; }
.goal-drag { cursor: grab; margin-left: auto; }
.goal-link { font-size: 12px; }
.goal-corrupt { background: #fef3c7; border: 1px solid #f59e0b; border-radius: 8px; padding: 6px 8px; margin: 6px 0; font-size: 12px; }
.flash { outline: 2px solid var(--text-accent, #3b82f6); }
@media (prefers-reduced-motion: reduce) { .goal-float { transition: none; } }
</style>

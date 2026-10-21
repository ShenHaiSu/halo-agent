<template>
  <div class="space-y-4 min-w-0 max-w-full overflow-x-hidden" data-testid="skill-memory-tab">
    <!-- A. 说明与筛选区 -->
    <n-card title="技能与记忆（只读管理）" size="small">
      <n-alert type="info" :show-icon="true" class="mb-3">
        仅查看、开关与删除，不支持新建与编辑。正文由模型经 skill_tool/memory_tool 维护；开关与删除下一轮对话起生效。Project 域随当前工作区 .haloagent/ 走。
      </n-alert>
      <div class="sm-filters">
        <div class="sm-field">
          <div class="sm-field-label text-secondary">种类</div>
          <div class="sm-field-control">
            <n-segmented
              v-model:value="store.kind"
              :options="kindOptions"
              data-testid="sm-kind-seg"
              @update:value="onKind"
            />
          </div>
        </div>
        <div class="sm-field">
          <div class="sm-field-label text-secondary">域</div>
          <div class="sm-field-control">
            <n-select
              v-model:value="store.scopeFilter"
              :options="scopeOptions"
              style="width: 140px;"
              data-testid="sm-scope-select"
              @update:value="onScope"
            />
          </div>
        </div>
        <div class="sm-field">
          <div class="sm-field-label text-secondary">工作区</div>
          <div class="sm-field-control">
            <n-select
              v-model:value="wsSelect"
              :options="wsOptions"
              style="width: 200px;"
              data-testid="sm-workspace-select"
              placeholder="选择工作区"
              @update:value="onWorkspace"
            />
          </div>
        </div>
        <div class="sm-field">
          <div class="sm-field-label text-secondary">搜索</div>
          <div class="sm-field-control">
            <n-input
              v-model:value="searchInput"
              clearable
              placeholder="按名称 / 标题 / 摘要搜索"
              style="width: 220px;"
              data-testid="sm-search"
              @update:value="onSearch"
            />
          </div>
        </div>
        <div class="sm-field">
          <div class="sm-field-label">&nbsp;</div>
          <div class="sm-field-control">
            <n-button size="small" data-testid="sm-refresh" :loading="store.loading" @click="onReload">
              刷新
            </n-button>
          </div>
        </div>
      </div>
      <p v-if="showNoWsHint" class="text-xs text-amber-500 mt-2">
        当前无工作区，Project 为空，只显示 Global。
      </p>
    </n-card>

    <!-- B. 列表区（可伸缩卡片） -->
    <n-card title="条目列表" size="small">
      <div v-if="store.loading" class="flex justify-center py-6">
        <n-spin />
      </div>
      <n-alert v-else-if="store.lastError" type="error" :show-icon="true">
        {{ store.lastError }}
        <div class="mt-2">
          <n-button size="small" @click="onReload">重试</n-button>
        </div>
      </n-alert>
      <n-empty
        v-else-if="store.filtered.length === 0"
        data-testid="sm-empty"
        description="暂无条目（scope/关键字过滤后为空）。用模型对话让它先创建，或换个筛选试试。"
      />
      <!-- data-testid 保持 sm-native-table，沿用 04 联调钩子（现为卡片列表） -->
      <div v-else class="sm-card-list" data-testid="sm-native-table">
        <div v-for="item in store.filtered" :key="`${item.scope}:${item.name}`" class="sm-card">
          <div class="sm-card-head">
            <div class="sm-head-left">
              <n-tag :type="item.scope === 'global' ? 'info' : 'success'" size="small" :bordered="false" class="shrink-0">
                {{ item.scope }}
              </n-tag>
              <n-button text type="primary" class="sm-name" @click="toggleExpand(item)">
                <template #icon>
                  <ChevronDown class="sm-chevron w-3.5 h-3.5" :class="{ open: isExpanded(item) }" />
                </template>
                {{ item.name }}
              </n-button>
            </div>
            <div class="sm-head-center font-mono text-xs">{{ shortTime(item.updatedAtUtc) }}</div>
            <div class="sm-head-right">
              <n-switch
                size="small"
                :value="item.enabled"
                :data-testid="`sm-switch-${item.scope}-${item.name}`"
                @update:value="() => onToggle(item)"
              />
              <n-button circle tertiary size="small" title="预览" :data-testid="`sm-preview-${item.scope}-${item.name}`" @click="onPreview(item)">
                <template #icon><Eye class="w-3.5 h-3.5" /></template>
              </n-button>
              <n-button circle tertiary size="small" title="打开文件夹" :data-testid="`sm-folder-${item.scope}-${item.name}`" @click="onFolder(item)">
                <template #icon><FolderOpen class="w-3.5 h-3.5" /></template>
              </n-button>
              <n-button circle ghost type="error" size="small" title="删除" :data-testid="`sm-delete-${item.scope}-${item.name}`" @click="onDelete(item)">
                <template #icon><Trash2 class="w-3.5 h-3.5" /></template>
              </n-button>
            </div>
          </div>
          <div v-if="isExpanded(item)" class="sm-card-body">
            <p class="text-sm font-medium">{{ item.title }}</p>
            <p class="text-xs text-secondary mt-1 sm-sub">{{ item.summary }}</p>
            <p class="text-xs font-mono mt-2">
              pin {{ item.pin }} · v{{ item.version }} · ~{{ item.estTokens }} tokens · {{ item.chars }} 字{{ item.hasAssets ? ' · 含附件' : '' }}
            </p>
          </div>
        </div>
      </div>
    </n-card>

    <!-- C. 预览弹窗（只读） -->
    <n-modal
      v-model:show="showModal"
      preset="card"
      :title="modalTitle"
      style="width: 640px; max-width: 90vw;"
      data-testid="sm-preview-modal"
    >
      <n-card size="small" :bordered="false">
        <p class="text-sm font-medium">{{ store.preview?.title }}</p>
        <p class="text-xs text-secondary mt-1">{{ store.preview?.summary }}</p>
        <p class="text-xs font-mono mt-2">
          pin {{ store.preview?.pin }} · v{{ store.preview?.version }} · ~{{ store.preview?.estTokens }} tokens · {{ store.preview?.chars }} 字{{ store.preview?.hasAssets ? ' · 含附件' : '' }}
        </p>
        <pre class="sm-preview-body mt-3">{{ store.preview?.bodyPreview }}</pre>
        <p v-if="store.preview?.truncated" class="text-xs text-secondary mt-2">
          正文过长，仅显示前 800 字，完整内容点「打开文件夹」查看 readme.md。
        </p>
        <p v-if="store.preview?.hasAssets" class="text-xs text-secondary mt-1">
          含 assets/ 附件，正文引用 assets/...
        </p>
      </n-card>
      <template #footer>
        <div class="flex justify-end gap-2">
          <n-button size="small" type="primary" ghost data-testid="sm-preview-folder" @click="onPreviewFolder">打开文件夹</n-button>
          <n-button size="small" type="error" ghost @click="onPreviewDelete">删除</n-button>
          <n-button size="small" @click="store.closePreview()">关闭</n-button>
        </div>
      </template>
    </n-modal>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue';
import { useMessage, useDialog } from 'naive-ui';
import { Eye, FolderOpen, Trash2, ChevronDown } from 'lucide-vue-next';
import { useSkillMemoryStore } from '@/stores/skillMemoryStore';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import type { SkillMemoryKind, SkillMemoryScopeFilter, SkillMemoryListItem } from '@/bridge/hostBridge';

const store = useSkillMemoryStore();
const workspaceStore = useWorkspaceStore();
const message = useMessage();
const dialog = useDialog();

const kindOptions = [
  { label: '技能', value: 'skill' },
  { label: '记忆', value: 'memory' },
];
const scopeOptions = [
  { label: '全部', value: 'all' },
  { label: '全局', value: 'global' },
  { label: '项目', value: 'project' },
];

const searchInput = ref('');
const wsSelect = ref('');
const expandedMap = ref<Record<string, boolean>>({});

const wsOptions = computed(() => {
  const list = workspaceStore.workspaces.map((w) => ({ label: w.name, value: w.id }));
  if (list.length === 0) return [{ label: '全局模式', value: '' }];
  return [{ label: '全局模式', value: '' }, ...list];
});

const showNoWsHint = computed(() => {
  const wid = wsSelect.value || store.currentWorkspaceId();
  return !wid && (store.scopeFilter === 'project' || store.scopeFilter === 'all');
});

const showModal = computed({
  get: () => store.preview != null,
  set: (v: boolean) => { if (!v) store.closePreview(); },
});
const modalTitle = computed(() => {
  const p = store.preview;
  return p ? `[${p.scope}] ${p.name}` : '预览';
});

function expandKey(item: SkillMemoryListItem): string {
  return `${store.kind}:${item.scope}:${item.name}`;
}

function isExpanded(item: SkillMemoryListItem): boolean {
  return !!expandedMap.value[expandKey(item)];
}

function toggleExpand(item: SkillMemoryListItem): void {
  const k = expandKey(item);
  expandedMap.value[k] = !expandedMap.value[k];
}

function kindCn(): string {
  return store.kind === 'skill' ? '技能' : '记忆';
}

function shortTime(iso: string): string {
  if (!iso) return '-';
  const t = iso.replace('T', ' ').slice(0, 16);
  return t || iso.slice(0, 16);
}

function onKind(v: SkillMemoryKind): void {
  expandedMap.value = {};
  store.setKind(v);
}
function onScope(v: SkillMemoryScopeFilter): void {
  store.setScope(v);
}
function onSearch(v: string): void {
  store.setKeyword(v);
}
function onWorkspace(v: string): void {
  wsSelect.value = v;
  store.setWorkspace(v);
}
function onReload(): void {
  expandedMap.value = {};
  void store.reload();
}

async function onPreview(item: SkillMemoryListItem): Promise<void> {
  await store.openPreview(item.scope, item.name);
}

async function onToggle(item: SkillMemoryListItem): Promise<void> {
  const r = await store.toggleEnabled(item);
  if (r.ok) message.success(r.message || `已${item.enabled ? '启用' : '停用'} ${kindCn()} '${item.name}'（${item.scope}）。下一轮对话起生效。`);
  else message.error(r.message || '开关失败');
}

function confirmDelete(item: SkillMemoryListItem, after?: () => void): void {
  dialog.error({
    title: '确认删除？',
    content: `将删除 ${item.scope} ${kindCn()} '${item.name}'，同级保留一份 .deleted-时间.bak 后不可撤销。下一轮对话起生效。`,
    positiveText: '确认删除',
    negativeText: '取消',
    onPositiveClick: async () => {
      const r = await store.removeItem(item);
      if (r.ok) {
        message.success(r.message || '已删除');
        if (store.preview && store.preview.name === item.name && store.preview.scope === item.scope) {
          store.closePreview();
        }
        after?.();
      } else {
        message.error(r.message || '删除失败');
      }
    },
  });
}

function onDelete(item: SkillMemoryListItem): void {
  confirmDelete(item);
}

async function onFolder(item: SkillMemoryListItem): Promise<void> {
  const r = await store.openFolder(item);
  if (!r.ok) message.error(r.message || '目录已不存在，请刷新');
}

async function onPreviewFolder(): Promise<void> {
  const p = store.preview;
  if (!p) return;
  await onFolder(p);
}

function onPreviewDelete(): void {
  const p = store.preview;
  if (!p) return;
  confirmDelete(p);
}

watch(
  () => workspaceStore.activeWorkspaceId,
  (v) => {
    if (!wsSelect.value && v) {
      wsSelect.value = v;
      store.setWorkspace(v);
    }
  }
);

onMounted(() => {
  wsSelect.value = store.workspaceId || workspaceStore.activeWorkspaceId || '';
  if (!store.workspaceId && wsSelect.value) store.setWorkspace(wsSelect.value);
  else void store.reload();
  store.attachBus();
});

onUnmounted(() => {
  store.detachBus();
});
</script>

<style scoped>
/* A 区筛选：label 与控件各包一层 div，自然形成组件边距 */
.sm-filters { display: flex; flex-wrap: wrap; gap: 4px 16px; align-items: flex-end; min-width: 0; max-width: 100%; }
.sm-field { min-width: 0; }
.sm-field-label { font-size: 12px; line-height: 1.5; margin: 0 0 6px 0; }
.sm-field-control { min-width: 0; }
/* B 区卡片列表：纵向伸缩卡片 */
.sm-card-list { display: flex; flex-direction: column; gap: 8px; min-width: 0; max-width: 100%; }
.sm-card { border: 1px solid var(--border-subtle); border-radius: 8px; padding: 8px 12px; min-width: 0; max-width: 100%; box-sizing: border-box; }
.sm-card-head { display: grid; grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr); align-items: center; gap: 8px; min-width: 0; }
.sm-head-left { display: flex; align-items: center; gap: 8px; min-width: 0; justify-self: start; }
.sm-head-center { justify-self: center; white-space: nowrap; }
.sm-head-right { display: flex; align-items: center; gap: 6px; justify-self: end; }
.sm-name { min-width: 0; max-width: 100%; }
.sm-chevron { transition: transform 0.15s ease; }
.sm-chevron.open { transform: rotate(180deg); }
.sm-card-body { margin-top: 8px; padding-top: 8px; border-top: 1px dashed var(--border-subtle); min-width: 0; }
.sm-sub { white-space: normal; overflow-wrap: break-word; }
.sm-preview-body { white-space: pre-wrap; overflow-wrap: break-word; font-size: 12px; line-height: 1.7; max-height: 40vh; overflow-y: auto; margin: 0; }
</style>

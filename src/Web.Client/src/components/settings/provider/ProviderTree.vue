<!-- src/Web.Client/src/components/settings/provider/ProviderTree.vue -->
<!-- P27 左树：220px 一级 Provider 列表（F-2 定稿：不展示 Model）+ 搜索 + 新建。
     悬停/选中用 135° accent-subtle→白 渐变；● 表示该 Provider 为使用中。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import HButton from '@/components/base/HButton.vue';
import HInput from '@/components/base/HInput.vue';
import { mainName, type ProviderEdit } from '../providerEditing';
import { filterProviders } from '../providerFormat';

const props = defineProps<{
  providers: ProviderEdit[];
  /** 当前选中（右侧展示的）Provider id */
  currentId: string;
  activeProvider: string;
}>();

const emit = defineEmits<{
  select: [id: string];
  /** 新建 Provider（已校验注册名格式，不含重名判定） */
  create: [id: string];
}>();

const query = ref('');
const creating = ref(false);
const newId = ref('');
const newError = ref<string | null>(null);

const visible = computed(() => filterProviders(props.providers, query.value));

const CREATE_RE = /^[a-z0-9][a-z0-9-_]{0,63}$/;

function startCreate(): void {
  creating.value = true;
  newId.value = '';
  newError.value = null;
}

function cancelCreate(): void {
  creating.value = false;
  newId.value = '';
  newError.value = null;
}

function submitCreate(): void {
  const id = newId.value.trim();
  if (!CREATE_RE.test(id)) {
    newError.value = '注册名须匹配 ^[a-z0-9][a-z0-9-_]{0,63}$';
    return;
  }
  if (props.providers.some((p) => p.id === id)) {
    newError.value = '该注册名已存在';
    return;
  }
  cancelCreate();
  emit('create', id);
}

function nodeTitle(p: ProviderEdit): string {
  return `${p.id} · ${p.models.length}个模型（点击查看详情）`;
}
</script>

<template>
  <div class="ptree">
    <HInput
      v-model="query"
      size="sm"
      placeholder="搜索 Provider…"
      aria-label="搜索 Provider"
      class="ptree-search"
    />

    <div class="ptree-list" role="listbox" aria-label="Provider 列表">
      <button
        v-for="p in visible"
        :key="p.id"
        type="button"
        role="option"
        class="tnode"
        :class="{ current: p.id === currentId }"
        :aria-selected="p.id === currentId"
        :title="nodeTitle(p)"
        @click="emit('select', p.id)"
      >
        <span v-if="p.id === activeProvider" class="dot" aria-label="使用中" />
        <span v-else class="dot off" aria-hidden="true" />
        <span class="tnode-name">{{ mainName(p.id, p.displayName) }}</span>
        <span class="tnode-count">{{ p.models.length }}</span>
      </button>
      <div v-if="visible.length === 0" class="ptree-empty">没有匹配的 Provider</div>
    </div>

    <form v-if="creating" class="ptree-new" @submit.prevent="submitCreate">
      <HInput
        v-model="newId"
        size="sm"
        placeholder="如 deepseek"
        aria-label="新 Provider 注册名称"
      />
      <div class="ptree-new-actions">
        <HButton size="sm" variant="primary" type="submit">创建</HButton>
        <HButton size="sm" variant="ghost" @click="cancelCreate">取消</HButton>
      </div>
      <p v-if="newError" class="ptree-new-error" role="alert">{{ newError }}</p>
    </form>
    <HButton v-else size="sm" variant="secondary" block icon="plus" @click="startCreate">
      新建 Provider
    </HButton>
  </div>
</template>

<style scoped>
.ptree {
  display: flex;
  flex-direction: column;
  gap: var(--space-1-5);
  min-width: 0;
  min-height: 0;
}
.ptree-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  overflow-y: auto;
  min-height: 0;
}
.tnode {
  display: flex;
  align-items: center;
  gap: var(--space-1-5);
  width: 100%;
  min-width: 0;
  padding: 7px var(--space-2);
  border: 1px solid transparent;
  border-radius: var(--radius-sm);
  background: transparent;
  cursor: pointer;
  text-align: left;
  color: hsl(var(--fg));
  /* 遮罩层做 opacity 过渡（渐变不可插值），时长 ≤150ms */
  position: relative;
  isolation: isolate;
  transition: border-color 130ms ease-out;
}
.tnode::after {
  content: '';
  position: absolute;
  inset: 0;
  z-index: -1;
  pointer-events: none;
  border-radius: inherit;
  opacity: 0;
  background: linear-gradient(135deg, hsl(var(--accent-subtle)), hsl(var(--bg)) 70%);
  transition: opacity 130ms ease-out;
}
.tnode:hover {
  border-color: hsl(var(--border-subtle));
}
.tnode:hover::after {
  opacity: 1;
}
.tnode.current {
  border-color: hsl(var(--accent) / 0.45);
}
.tnode.current::after {
  opacity: 1;
  background: linear-gradient(135deg, hsl(var(--accent-subtle)), hsl(var(--bg-muted)));
}
.tnode:focus-visible {
  outline: 2px solid hsl(var(--accent) / 0.4);
  outline-offset: 1px;
}
.dot {
  flex: none;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: hsl(var(--accent));
}
.dot.off {
  visibility: hidden;
}
.tnode-name {
  font-size: var(--text-sm);
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}
.tnode-count {
  margin-left: auto;
  flex: none;
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-variant-numeric: tabular-nums;
}
.ptree-empty {
  font-size: var(--text-xs);
  color: hsl(var(--fg-subtle));
  padding: var(--space-2) var(--space-1);
}
.ptree-new {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
}
.ptree-new-actions {
  display: flex;
  gap: var(--space-1);
}
.ptree-new-error {
  margin: 0;
  font-size: var(--text-2xs);
  color: hsl(var(--danger));
  line-height: 1.4;
}
@media (prefers-reduced-motion: reduce) {
  .tnode {
    transition: none;
  }
  .tnode::after {
    display: none;
  }
  .tnode:hover,
  .tnode.current {
    background: hsl(var(--accent-subtle));
  }
}
</style>

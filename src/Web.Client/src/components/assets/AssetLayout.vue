<!-- src/Web.Client/src/components/assets/AssetLayout.vue -->
<!-- 三类资源的统一两栏布局（05 §3.2）：列表 240px + 编辑器自适应；列表头含搜索，编辑底为 flex 尾栏。 -->
<script setup lang="ts">
import HButton from '@/components/base/HButton.vue';
import HInput from '@/components/base/HInput.vue';
import HTag from '@/components/base/HTag.vue';

defineProps<{
  title: string;
  /** 列表 aria-label，具名如“技能列表” */
  listLabel: string;
  query: string;
  queryPlaceholder?: string;
  canCreate: boolean;
  dirty: boolean;
  saving: boolean;
}>();

defineEmits<{
  create: [];
  save: [];
  reload: [];
  'update:query': [string];
}>();
</script>

<template>
  <div class="asset">
    <aside class="asset-list-col" :aria-label="listLabel">
      <div class="asset-list-head">
        <span class="asset-title">{{ title }}</span>
        <span class="spacer" />
        <HButton v-if="canCreate" size="sm" variant="secondary" @click="$emit('create')">
          新建
        </HButton>
      </div>
      <div class="asset-list-search">
        <HInput
          :model-value="query"
          type="text"
          size="sm"
          clearable
          :placeholder="queryPlaceholder ?? '搜索…'"
          :aria-label="`${title}搜索`"
          @update:model-value="$emit('update:query', $event)"
        />
      </div>
      <div class="asset-list-body">
        <slot name="list" />
      </div>
    </aside>

    <section class="asset-edit-col">
      <div class="asset-edit-head">
        <div class="asset-edit-title">
          <slot name="editTitle" />
        </div>
        <span class="spacer" />
        <HTag v-if="dirty" size="sm" variant="warning" role="status">未保存</HTag>
        <HButton size="sm" variant="secondary" :disabled="saving" @click="$emit('reload')">
          重新加载
        </HButton>
        <HButton
          size="sm"
          variant="primary"
          :loading="saving"
          :disabled="!dirty || saving"
          @click="$emit('save')"
        >
          保存
        </HButton>
      </div>
      <div class="asset-edit-body">
        <slot />
      </div>
      <div class="asset-edit-foot">
        <span class="kbd-hint">Ctrl+S 保存（编辑器获焦时）</span>
      </div>
    </section>
  </div>
</template>

<style scoped>
.asset {
  display: flex;
  height: 100%;
  min-height: 0;
}
.asset-list-col {
  width: 240px;
  min-width: 180px;
  flex: none;
  border-right: 1px solid hsl(var(--border));
  display: flex;
  flex-direction: column;
  min-height: 0;
}
.asset-list-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2);
  border-bottom: 1px solid hsl(var(--border));
  flex: none;
}
.asset-title {
  font-size: var(--text-sm);
  font-weight: 600;
}
.spacer {
  flex: 1;
  min-width: 0;
}
.asset-list-search {
  padding: var(--space-2);
  border-bottom: 1px solid hsl(var(--border));
  flex: none;
}
.asset-list-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}
.asset-edit-col {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  min-height: 0;
}
.asset-edit-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-2);
  border-bottom: 1px solid hsl(var(--border));
  flex: none;
  min-width: 0;
}
.asset-edit-title {
  min-width: 0;
  overflow: hidden;
}
.asset-edit-body {
  flex: 1;
  min-height: 0;
  padding: var(--space-2);
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
  overflow-y: auto;
}
.asset-edit-foot {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--space-2);
  padding: var(--space-1-5) var(--space-2);
  border-top: 1px solid hsl(var(--border));
  flex: none;
}
.kbd-hint {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  font-family: var(--font-mono);
}
@media (max-width: 900px) { /* 与 var(--bp-compact) 同值；var() 不可用于 @media 条件，保持字面量 */
  .asset {
    flex-direction: column;
  }
  .asset-list-col {
    width: 100%;
    min-width: 0;
    max-height: 240px;
    border-right: 0;
    border-bottom: 1px solid hsl(var(--border));
  }
}
@media (max-width: 700px) {
  .asset-list-col {
    max-height: 200px;
  }
}
</style>

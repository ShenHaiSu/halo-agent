<!-- src/Web.Client/src/components/assets/AssetLayout.vue -->
<!-- 三类资源的统一两栏布局（05 §3.2）：列表 240px + 编辑器自适应。 -->
<script setup lang="ts">
defineProps<{
  title: string;
  canCreate: boolean;
  dirty: boolean;
  saving: boolean;
}>();

defineEmits<{
  create: [];
  save: [];
  reload: [];
}>();
</script>

<template>
  <div class="asset">
    <aside class="asset-list-col" aria-label="列表">
      <div class="asset-list-head">
        <span class="asset-title">{{ title }}</span>
        <span class="spacer" />
        <button v-if="canCreate" class="asset-new" type="button" @click="$emit('create')">
          新建
        </button>
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
        <span v-if="dirty" class="dirty" role="status">未保存</span>
        <button class="asset-btn" type="button" :disabled="saving" @click="$emit('reload')">
          重新加载
        </button>
        <button
          class="asset-btn primary"
          type="button"
          :disabled="!dirty || saving"
          @click="$emit('save')"
        >
          {{ saving ? '保存中…' : '保存' }}
        </button>
      </div>
      <div class="asset-edit-body">
        <slot />
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
  gap: var(--space-1);
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
.asset-new {
  background: transparent;
  border: 0;
  color: hsl(var(--accent));
  font-size: var(--text-xs);
  cursor: pointer;
  padding: 2px 4px;
}
.asset-new:hover {
  text-decoration: underline;
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
.dirty {
  font-size: var(--text-2xs);
  color: hsl(var(--warning));
  flex: none;
}
.asset-btn {
  flex: none;
  background: hsl(var(--bg-elevated));
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius-sm);
  padding: 2px var(--space-2);
  font-size: var(--text-xs);
  cursor: pointer;
  color: hsl(var(--fg));
}
.asset-btn.primary {
  background: hsl(var(--accent));
  border-color: hsl(var(--accent));
  color: hsl(var(--accent-fg));
}
.asset-btn:disabled {
  opacity: 0.5;
  cursor: default;
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
@media (max-width: 700px) {
  .asset-list-col {
    width: 200px;
    min-width: 150px;
  }
}
</style>

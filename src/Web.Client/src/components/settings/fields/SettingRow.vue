<!-- src/Web.Client/src/components/settings/fields/SettingRow.vue -->
<!-- 设置行：标签 + 说明 + 控件三栏，字段错误红字（04 §6.3 §6.4）。 -->
<script setup lang="ts">
defineProps<{
  label: string;
  /** 中文说明（I-10：技术名词配中文括号说明） */
  desc?: string;
  required?: boolean;
  error?: string | null;
}>();
</script>

<template>
  <div class="srow" :class="{ invalid: !!error }">
    <div class="srow-label">
      <span class="srow-name">
        {{ label }}
        <span v-if="required" class="req" aria-hidden="true">*</span>
      </span>
    </div>
    <div class="srow-desc">
      <span v-if="desc">{{ desc }}</span>
    </div>
    <div class="srow-control">
      <slot />
      <p v-if="error" class="srow-error" role="alert">• {{ error }}</p>
    </div>
  </div>
</template>

<style scoped>
.srow {
  display: grid;
  grid-template-columns: 168px minmax(0, 1fr) minmax(0, 1.4fr);
  gap: var(--space-3);
  padding: var(--space-3) 0;
  border-bottom: 1px solid hsl(var(--border-subtle));
  align-items: start;
}
.srow:last-child {
  border-bottom: 0;
}
.srow.invalid .srow-name {
  color: hsl(var(--danger));
}
.srow-label {
  min-width: 0;
}
.srow-name {
  font-size: var(--text-sm);
  color: hsl(var(--fg));
  font-weight: 500;
}
.req {
  color: hsl(var(--danger));
}
.srow-desc {
  font-size: var(--text-xs);
  color: hsl(var(--fg-muted));
  line-height: 1.6;
  min-width: 0;
}
.srow-control {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
}
.srow-error {
  margin: 0;
  font-size: var(--text-xs);
  color: hsl(var(--danger));
  line-height: 1.4;
}
@media (max-width: 700px) {
  .srow {
    grid-template-columns: 1fr;
    gap: var(--space-1);
  }
}
</style>

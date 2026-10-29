<!-- src/Web.Client/src/components/settings/fields/SettingRow.vue -->
<!-- 设置行：标签 + 说明 + 控件三栏，字段错误红字（04 §6.3 §6.4）。compact 模式见 P11-02。 -->
<script setup lang="ts">
import HTooltip from '@/components/base/HTooltip.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

withDefaults(
  defineProps<{
    label: string;
    /** 中文说明（I-10：技术名词配中文括号说明） */
    desc?: string;
    required?: boolean;
    error?: string | null;
    /** 紧凑模式：desc 收进 label 旁 info 图标 + HTooltip，不再占列。默认 false，其他页零变化。 */
    compact?: boolean;
    tooltipMaxWidth?: number;
  }>(),
  { compact: false, tooltipMaxWidth: 260 },
);
</script>

<template>
  <div class="srow" :class="{ invalid: !!error, compact }">
    <div class="srow-label">
      <span class="srow-name">
        {{ label }}
        <span v-if="required" class="req" aria-hidden="true">*</span>
      </span>
      <HTooltip v-if="compact && desc" :content="desc" placement="top" :delay="150" :maxWidth="tooltipMaxWidth">
        <span class="help" tabindex="0" :aria-label="`${label}说明：${desc}`"><HaloIcon name="info" :size="13" /></span>
      </HTooltip>
      <span v-else-if="compact" class="help placeholder" aria-hidden="true" />
    </div>
    <div v-if="!compact && desc" class="srow-desc">
      <span>{{ desc }}</span>
    </div>
    <div v-else-if="!compact && !desc" class="srow-desc" />
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
.srow.compact {
  grid-template-columns: minmax(0, 150px) minmax(0, 1fr);
  gap: var(--space-2);
  padding: var(--space-1-5) 0;
  border-bottom: 0;
}
.srow.compact .srow-name {
  font-size: var(--text-sm);
}
.srow.compact .srow-label {
  display: flex;
  align-items: center;
  min-width: 0;
}
.srow.compact .help {
  display: inline-flex;
  margin-left: var(--space-1);
  color: hsl(var(--fg-subtle));
  cursor: help;
  border-radius: var(--radius-xs);
}
.srow.compact .help:hover {
  color: hsl(var(--fg));
}
.srow.compact .help:focus-visible {
  outline: 2px solid hsl(var(--accent) / 0.4);
  outline-offset: 1px;
}
.srow.compact .help.placeholder {
  visibility: hidden;
  width: 13px;
}
@media (max-width: 700px) {
  .srow {
    grid-template-columns: 1fr;
    gap: var(--space-1);
  }
  .srow.compact {
    grid-template-columns: 1fr;
  }
}
</style>

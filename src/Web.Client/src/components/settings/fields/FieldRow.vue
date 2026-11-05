<!-- src/Web.Client/src/components/settings/fields/FieldRow.vue -->
<!-- P27 密排字段行：86px 标签（label + ⓘ）+ 控件，行高锁 --ih（紧凑 28 / 舒适 32）。
     说明文字只以 ⓘ tooltip 存在，不占行（见定稿 §3）。 -->
<script setup lang="ts">
import HTooltip from '@/components/base/HTooltip.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';

withDefaults(
  defineProps<{
    label: string;
    /** ⓘ 说明；空则不渲染 ⓘ（也不占位） */
    desc?: string;
    required?: boolean;
    /** 字段级错误文案（红字，占控件下方一行） */
    error?: string | null;
    tooltipMaxWidth?: number;
  }>(),
  { desc: '', required: false, error: null, tooltipMaxWidth: 280 }
);
</script>

<template>
  <div class="frow" :class="{ invalid: !!error }">
    <div class="frow-label">
      <span class="frow-name">
        {{ label }}<span v-if="required" class="req" aria-hidden="true">*</span>
      </span>
      <HTooltip v-if="desc" :content="desc" placement="top" :delay="150" :max-width="tooltipMaxWidth">
        <span class="frow-info" tabindex="0" :aria-label="`${label}说明：${desc}`">
          <HaloIcon name="info" :size="13" />
        </span>
      </HTooltip>
    </div>
    <div class="frow-control">
      <slot />
      <p v-if="error" class="frow-error" role="alert">• {{ error }}</p>
    </div>
  </div>
</template>

<style scoped>
.frow {
  display: grid;
  grid-template-columns: 86px minmax(0, 1fr);
  gap: var(--space-2);
  align-items: center;
  /* 行高锁：唯一高度变量 --ih（28/32），加 8px 留白 */
  min-height: calc(var(--settings-ih, 28px) + 8px);
  min-width: 0;
  padding: 2px 0;
}
.frow-label {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  font-size: var(--text-xs);
  font-weight: 600;
  color: hsl(var(--fg));
  white-space: nowrap;
}
.frow.invalid .frow-name {
  color: hsl(var(--danger));
}
.frow-name {
  overflow: hidden;
  text-overflow: ellipsis;
}
.req {
  color: hsl(var(--danger));
  margin-left: 2px;
}
.frow-info {
  display: inline-flex;
  align-items: center;
  color: hsl(var(--fg-subtle));
  cursor: help;
  border-radius: var(--radius-xs);
  flex: none;
}
.frow-info:hover {
  color: hsl(var(--fg));
}
.frow-info:focus-visible {
  outline: 2px solid hsl(var(--accent) / 0.4);
  outline-offset: 1px;
}
.frow-control {
  min-width: 0;
  display: flex;
  align-items: center;
  gap: var(--space-1-5);
  flex-wrap: wrap;
}
.frow-error {
  margin: 0;
  flex-basis: 100%;
  font-size: var(--text-xs);
  color: hsl(var(--danger));
  line-height: 1.4;
}
/* 窄窗：标签与控件上下排（与设置页断点一致） */
@media (max-width: 700px) {
  .frow {
    grid-template-columns: 1fr;
    gap: var(--space-1);
    align-items: start;
  }
}
</style>

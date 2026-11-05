// src/Web.Client/src/composables/useContextWaterline.ts
// 上下文水位线纯计算（无副作用，便于单测）。
import { computed, type Ref } from 'vue';
import { formatNumber } from '@/utils/format';

export type WaterlineState = 'hidden' | 'estimated' | 'measured' | 'overflow';

export interface WaterlineInput {
  lastPromptTokens: Ref<number | null>;
  estimatedInitialTokens: Ref<number | null>;
  contextWindow: Ref<number | null>;
}

export interface WaterlineModel {
  /** 水位区是否展示（false → v-if 隐藏整个水位区，不占位）。 */
  visible: Ref<boolean>;
  /** 当前状态。 */
  state: Ref<WaterlineState>;
  /** 分子（展示用，已钳制 >=0；null 表示未知）。 */
  used: Ref<number | null>;
  /** 分母（null 表示未知窗口）。 */
  total: Ref<number | null>;
  /** 百分比 0~100（null 表示未知）。 */
  pct: Ref<number | null>;
  /** 是否为估算态（显示“预估”角标）。 */
  isEstimated: Ref<boolean>;
  /** 默认文本：百分比；估算态带“约”与“·预估”。 */
  label: Ref<string>;
  /** hover/focus 文本：`used / total` 千分位。 */
  hoverText: Ref<string>;
  /** 条形宽度百分比（0~100；超窗钳制 100）。 */
  barPct: Ref<number>;
  /** 颜色档：normal | warn | danger。 */
  tone: Ref<'normal' | 'warn' | 'danger'>;
  /** 超窗时 tooltip 追加的提示。 */
  overflowHint: Ref<string>;
}

export function useContextWaterline(input: WaterlineInput): WaterlineModel {
  const used = computed<number | null>(() => {
    const v = input.lastPromptTokens.value ?? input.estimatedInitialTokens.value ?? null;
    if (v === null || typeof v !== 'number' || Number.isNaN(v)) return null;
    return Math.max(0, Math.floor(v));
  });
  const total = computed<number | null>(() => {
    const t = input.contextWindow.value;
    return typeof t === 'number' && t > 0 ? t : null;
  });
  const isEstimated = computed(
    () => input.lastPromptTokens.value === null && input.estimatedInitialTokens.value !== null,
  );
  const pct = computed<number | null>(() => {
    const u = used.value;
    const t = total.value;
    if (u === null || t === null || t <= 0) return null;
    return Math.min(100, Math.round((u / t) * 100));
  });
  const visible = computed(() => used.value !== null && total.value !== null && pct.value !== null);
  const state = computed<WaterlineState>(() => {
    if (!visible.value) return 'hidden';
    const u = used.value as number;
    const t = total.value as number;
    if (u > t) return 'overflow';
    return isEstimated.value ? 'estimated' : 'measured';
  });
  const barPct = computed(() => pct.value ?? 0);
  const tone = computed<'normal' | 'warn' | 'danger'>(() => {
    const u = used.value;
    const t = total.value;
    if (u !== null && t !== null && u > t) return 'danger';
    const p = pct.value ?? 0;
    if (p >= 90) return 'danger';
    if (p >= 70) return 'warn';
    return 'normal';
  });
  const label = computed(() => {
    if (!visible.value || pct.value === null) return '';
    return isEstimated.value ? `约${pct.value}%·预估` : `${pct.value}%`;
  });
  const hoverText = computed(() => {
    if (!visible.value || used.value === null || total.value === null) return '';
    return `${formatNumber(used.value)} / ${formatNumber(total.value)}`;
  });
  const overflowHint = computed(() =>
    state.value === 'overflow' ? '已超窗，建议 /compact' : '',
  );
  return { visible, state, used, total, pct, isEstimated, label, hoverText, barPct, tone, overflowHint };
}

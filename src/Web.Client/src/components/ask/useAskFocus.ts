// src/Web.Client/src/components/ask/useAskFocus.ts
import { nextTick, onMounted, watch, type Ref } from 'vue';

export type AskFocusTarget = 'allow' | 'deny' | 'stop';

/** 可聚焦目标：HTMLElement 本体，或 Vue 组件实例（取 $el） */
type FocusableRef = Ref<{ $el?: unknown } | null>;

function toElement(r: FocusableRef): HTMLElement | null {
  const v = r.value;
  if (!v) return null;
  if (v instanceof HTMLElement) return v;
  const el = v.$el;
  return el instanceof HTMLElement ? el : null;
}

/**
 * 询问焦点策略（见 04 §4.2，焦点表见 04 §4.1）。
 *
 * 场景 → 初始焦点：
 *  无 warning（普通授权）→ 允许一次
 *  有 warning（危险操作）→ 拒绝（阻止"条件反射式按 Enter"）
 *  ask.rounds           → 停止
 *
 * ============ 为什么必须 await nextTick() ============
 *  `v-if` 渲染的按钮在 setup 里还不存在。
 *  不 nextTick 直接 `focus()` 会静默失败，
 *  用户看不到焦点，键盘操作会落在背后的输入框上。
 * =====================================================
 */
export function useAskFocus(
  askId: Ref<string | null>,
  target: Ref<AskFocusTarget>,
  els: {
    allow: FocusableRef;
    deny: FocusableRef;
    stop: FocusableRef;
  }
): void {
  async function apply(): Promise<void> {
    await nextTick();
    const el =
      target.value === 'deny'
        ? toElement(els.deny)
        : target.value === 'stop'
          ? toElement(els.stop)
          : toElement(els.allow);
    el?.focus();
  }

  onMounted(() => {
    if (askId.value) void apply();
  });

  // 询问变化（新的 askId）→ 重新聚焦
  watch(askId, (now) => {
    if (now) void apply();
  });
}

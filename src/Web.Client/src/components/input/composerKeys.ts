// src/Web.Client/src/components/input/composerKeys.ts
import type { Ref } from 'vue';

/** 浮层控制器（useMention / useSlash 的结构子集，便于复用与测试） */
export interface OverlayController {
  isOpen: () => boolean;
  move: (delta: number) => void;
  select: () => void;
  close: () => void;
}

export interface ComposerKeyContext {
  composing: Ref<boolean>;
  text: Ref<string>;
  mention: OverlayController;
  slash: OverlayController;
  history: Ref<string[]>;
  historyIdx: Ref<number>;
  askPending: Ref<boolean>;
  isBusy: Ref<boolean>;
  submit: () => void;
  cancel: () => void;
  prevSession: () => void;
  nextSession: () => void;
}

/**
 * 输入框键盘处理（见 04 §3.4 onKeydown，抽出供 Composer 复用）。
 *
 * ============ 键盘处理的五条优先级 ============
 *  0. IME 组合中 → 只认 Esc（交给输入法取消组合，坑 7）
 *  1. 浮层打开  → 上下选、Enter 确认、Esc 关闭
 *  2. Esc       → 关闭浮层 / 中断生成
 *  3. ↑（空输入）→ 历史；Alt+↑/↓ → 切会话
 *  4. Enter     → 发送/排队（Ctrl+Enter 也发送；Shift/Alt+Enter 换行）
 *  * 任何一条都不做 preventDefault 之前就先 return（避免吞掉输入法的回车）
 * ==================================================
 */
export function handleComposerKeydown(e: KeyboardEvent, d: ComposerKeyContext): void {
  // 0. IME（坑 7：组合中 Esc 交给 IME；坑 1 + 坑 2：其他键一律不处理）
  if (e.key === 'Escape' && (e.isComposing || d.composing.value)) return;
  if (e.isComposing || d.composing.value) return;

  // 1. 浮层优先
  if (d.mention.isOpen()) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      d.mention.move(1);
      return;
    }
    if (e.key === 'ArrowUp') {
      e.preventDefault();
      d.mention.move(-1);
      return;
    }
    if (e.key === 'Enter' || e.key === 'Tab') {
      e.preventDefault();
      d.mention.select();
      return;
    }
    if (e.key === 'Escape') {
      e.preventDefault();
      d.mention.close();
      return;
    }
    return;
  }
  if (d.slash.isOpen()) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      d.slash.move(1);
      return;
    }
    if (e.key === 'ArrowUp') {
      e.preventDefault();
      d.slash.move(-1);
      return;
    }
    if (e.key === 'Enter' || e.key === 'Tab') {
      e.preventDefault();
      d.slash.select();
      return;
    }
    if (e.key === 'Escape') {
      e.preventDefault();
      d.slash.close();
      return;
    }
    return;
  }

  // Alt+↑/↓ 切换会话（I-9）
  if (e.altKey && e.key === 'ArrowUp') {
    e.preventDefault();
    d.prevSession();
    return;
  }
  if (e.altKey && e.key === 'ArrowDown') {
    e.preventDefault();
    d.nextSession();
    return;
  }

  // 2. Esc
  if (e.key === 'Escape') {
    e.preventDefault();
    if (d.isBusy.value) d.cancel();
    return;
  }

  // 3. 历史（仅空输入且无 Shift/Alt）
  if (
    e.key === 'ArrowUp' &&
    d.text.value === '' &&
    !e.shiftKey &&
    !e.altKey &&
    !e.ctrlKey &&
    !e.metaKey
  ) {
    if (d.history.value.length === 0) return;
    e.preventDefault();
    if (d.historyIdx.value < 0) d.historyIdx.value = d.history.value.length - 1;
    else if (d.historyIdx.value > 0) d.historyIdx.value--;
    d.text.value = d.history.value[d.historyIdx.value] ?? '';
    return;
  }
  if (e.key === 'ArrowDown' && d.historyIdx.value >= 0 && !e.altKey) {
    e.preventDefault();
    d.historyIdx.value++;
    if (d.historyIdx.value >= d.history.value.length) {
      d.historyIdx.value = -1;
      d.text.value = '';
    } else {
      d.text.value = d.history.value[d.historyIdx.value] ?? '';
    }
    return;
  }

  // 4. Enter（Shift/Alt+Enter 交给 textarea 换行；Ctrl+Enter 也发送）
  if (e.key === 'Enter') {
    if (e.shiftKey || e.altKey) return;
    e.preventDefault();
    if (d.askPending.value) return;
    d.submit();
  }
}

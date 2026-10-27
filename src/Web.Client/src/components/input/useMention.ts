// src/Web.Client/src/components/input/useMention.ts
import { computed, ref, watch, type Ref } from 'vue';
import { transport } from '@/transport';

export interface MentionItem {
  path: string;
  isDir: boolean;
}

const DEBOUNCE_MS = 120;
const MAX_ITEMS = 20;
const BLUR_CLOSE_DELAY = 120;

/**
 * `@` 文件引用（见 04 §2.2）。
 *
 * ============ 三种关闭时机 ============
 *  ① Esc / 选中 / 光标移出 @ 段落
 *  ② 失焦（延迟 120ms，等浮层的 click 完成）
 *  ③ 用户输入了空格（引用已结束）
 * ============ 竞态处理 ============
 *  用户快速输入 "a" → "ab" → "abc"，三个请求可能乱序返回。
 *  用 requestSeq 记录序号，只有最新序号的响应才写入 items。
 * ==========================================
 */
export function useMention(ta: Ref<HTMLTextAreaElement | null>, text: Ref<string>) {
  const open = ref(false);
  const items = ref<MentionItem[]>([]);
  const active = ref(0);
  const loading = ref(false);
  const error = ref<string | null>(null);
  /** 无结果时的提示（区别于"还没加载"） */
  const empty = ref(false);

  let anchorStart = -1;
  let query = '';
  let debounceTimer: number | null = null;
  /** 竞态令牌：只有最新一次的响应才写入 */
  let reqSeq = 0;

  function reset(): void {
    open.value = false;
    items.value = [];
    active.value = 0;
    error.value = null;
    empty.value = false;
    loading.value = false;
    if (debounceTimer !== null) {
      window.clearTimeout(debounceTimer);
      debounceTimer = null;
    }
  }

  async function fetchItems(q: string): Promise<void> {
    const seq = ++reqSeq;
    loading.value = true;
    error.value = null;
    empty.value = false;

    try {
      const r = (await transport.request('fs.completePaths', { prefix: q || '' })) as {
        items: MentionItem[];
      };
      if (seq !== reqSeq) return; // * 丢弃过期响应
      items.value = (r.items ?? []).slice(0, MAX_ITEMS);
      active.value = 0;
      empty.value = items.value.length === 0;
    } catch (e) {
      if (seq !== reqSeq) return;
      error.value = (e as Error).message;
      items.value = [];
    } finally {
      if (seq === reqSeq) loading.value = false;
    }
  }

  /** 由 composerInput 触发（已判过 IME 与位置） */
  function trigger(start: number, q: string): void {
    anchorStart = start;
    query = q;
    open.value = true;
    if (debounceTimer !== null) window.clearTimeout(debounceTimer);
    debounceTimer = window.setTimeout(() => void fetchItems(q), DEBOUNCE_MS);
  }

  function close(): void {
    reset();
    anchorStart = -1;
  }

  function move(delta: number): void {
    if (!items.value.length) return;
    const n = items.value.length;
    active.value = (active.value + delta + n) % n;
  }

  function setActive(i: number): void {
    if (!items.value.length) return;
    active.value = Math.max(0, Math.min(items.value.length - 1, i));
  }

  function select(): void {
    const item = items.value[active.value];
    const el = ta.value;
    if (!item || !el) return;

    const pos = el.selectionStart;
    const inserted = `@${item.path}${item.isDir ? '/' : ''} `;
    text.value = text.value.slice(0, anchorStart) + inserted + text.value.slice(pos);

    const caret = anchorStart + inserted.length;
    // 坑 5：用 rAF 等一帧再设光标
    requestAnimationFrame(() => {
      el.selectionStart = el.selectionEnd = caret;
      el.focus();
    });

    close();
  }

  /** 工具栏按钮点击时手动插入一个 @ */
  function triggerManually(): void {
    const el = ta.value;
    if (!el) return;
    const pos = el.selectionStart;
    text.value = text.value.slice(0, pos) + '@' + text.value.slice(el.selectionEnd);
    el.focus();
    requestAnimationFrame(() => {
      el.selectionStart = el.selectionEnd = pos + 1;
      trigger(pos, '');
    });
  }

  // 失焦延迟关闭（等浮层的 click 完成；若焦点进了浮层则不关）
  watch(
    () => ta.value,
    (el) => {
      if (!el) return;
      el.addEventListener('blur', () => {
        window.setTimeout(() => {
          if (document.activeElement?.closest('[data-mention-popover]')) return;
          close();
        }, BLUR_CLOSE_DELAY);
      });
    },
    { immediate: true }
  );

  return {
    open: computed(() => open.value),
    items: computed(() => items.value),
    active: computed(() => active.value),
    loading: computed(() => loading.value),
    error: computed(() => error.value),
    empty: computed(() => empty.value),
    queryText: computed(() => query),
    isOpen: () => open.value,
    trigger,
    move,
    setActive,
    select,
    close,
    triggerManually,
  };
}

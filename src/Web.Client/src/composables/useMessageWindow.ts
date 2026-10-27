// src/Web.Client/src/composables/useMessageWindow.ts
// P5 D5-3 消息列表窗口化（见 03 §5.2）：不做完整虚拟滚动（消息高度不定，
// 测量会强制同步布局导致滚动掉帧），只渲染"关注点"附近 31 条，其余折叠为占位。
//
// 分层注意：composables 禁止 import stores（check-layering），所以这里用泛型，
// 调用方（components/chat/MessageList.vue）传入具体消息类型。

import { computed, type ComputedRef } from 'vue';

export interface WindowItem {
  id: string;
}

export interface WindowSlice<T extends WindowItem = WindowItem> {
  /** 需要渲染的消息 */
  items: T[];
  /** 顶部折叠了多少条 */
  hiddenTop: number;
  /** 底部折叠了多少条 */
  hiddenBottom: number;
}

// 三条规则：
//  ① 消息数 <= 120：全部渲染（消息少时窗口化反而增加复杂度）
//  ② 有 streamingId：以它为中心（用户正在看的地方）
//  ③ 否则：以最后一条为中心（最新消息）
const FULL_RENDER_LIMIT = 120;
const HALF_WINDOW = 15;

export function useMessageWindow<T extends WindowItem>(
  items: ComputedRef<T[]>,
  streamingId: ComputedRef<string | null>
): ComputedRef<WindowSlice<T>> {
  return computed(() => {
    const all = items.value;
    const n = all.length;

    if (n <= FULL_RENDER_LIMIT) {
      return { items: all, hiddenTop: 0, hiddenBottom: 0 };
    }

    let center = n - 1;
    if (streamingId.value) {
      const idx = all.findIndex((m) => m.id === streamingId.value);
      if (idx >= 0) center = idx;
    }

    let from = Math.max(0, center - HALF_WINDOW);
    let to = Math.min(n, center + HALF_WINDOW + 1);

    // 保证至少 31 条
    const MIN = HALF_WINDOW * 2 + 1;
    if (to - from < MIN) {
      if (from === 0) to = Math.min(n, MIN);
      else from = Math.max(0, to - MIN);
    }

    return {
      items: all.slice(from, to),
      hiddenTop: from,
      hiddenBottom: n - to,
    };
  });
}

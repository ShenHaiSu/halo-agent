/** bigChange P2-3: follow/free-read reducer. pure. */
export const REPIN_THRESHOLD_PX = 48;
export const ROUNDING = 1;
export const GESTURE_WINDOW_MS = 200;

export interface ScrollState {
  pinned: boolean;
  showJump: boolean;
}

export type ScrollGesture = 'wheel' | 'touchstart' | 'touchmove' | 'pointerdown' | 'keydown' | 'programmatic' | 'unknown';

/** distanceFromBottom in px (already rounding-adjusted by caller for non-gesture paths). */
export function reduceTranscriptScroll(distanceFromBottom: number, gesture: ScrollGesture, prev?: ScrollState): ScrollState {
  const isGesture = gesture !== 'programmatic' && gesture !== 'unknown';
  if (isGesture) {
    // 1px 上滚即解 pinned（手势路径 0 舍入）
    if (distanceFromBottom > 0) return { pinned: false, showJump: true };
    return { pinned: true, showJump: false };
  }
  void prev;
  if (distanceFromBottom <= REPIN_THRESHOLD_PX) return { pinned: true, showJump: false };
  return { pinned: false, showJump: true };
}

export function shouldRepin(distanceFromBottom: number): boolean {
  return distanceFromBottom <= REPIN_THRESHOLD_PX;
}

/** 文案键（无 i18n 时硬编码中文同义，键名保留注释） */
export const SCROLL_TEXT_KEYS = {
  scrollToBottom: 'chat.scrollToBottom', // 回到底部
  loadingEarlier: 'chat.loadingEarlier', // 正在加载更早消息…
  showEarlier: 'chat.showEarlier', // 查看更早消息
  loadLater: 'chat.loadLater', // 加载更新消息
  loadingSession: 'chat.loadingSession', // 会话加载中…
} as const;

export const SCROLL_TEXT_FALLBACK: Record<string, string> = {
  'chat.scrollToBottom': '回到底部',
  'chat.loadingEarlier': '正在加载更早消息…',
  'chat.showEarlier': '查看更早消息',
  'chat.loadLater': '加载更新消息',
  'chat.loadingSession': '会话加载中…',
};

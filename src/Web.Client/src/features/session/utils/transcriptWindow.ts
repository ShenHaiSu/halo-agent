/** bigChange P2-1/P2-3: transcript window constants + grow/shrink helpers. */
export const INITIAL_MOUNT = 15;
export const WINDOW_MIN = 60;
export const STEP = 40;
export const TRANSCRIPT_PAGE_SIZE = 100;

export interface TranscriptWindow {
  start: number;
  end: number;
  total: number;
}

export function initialWindow(total: number): TranscriptWindow {
  const end = total;
  const start = Math.max(0, end - INITIAL_MOUNT);
  return { start, end, total };
}

export function fullWindow(total: number): TranscriptWindow {
  return { start: 0, end: total, total };
}

/** Grow visible window by STEP (top expansion); clamps at 0. Returns delta grown. */
export function growWindow(w: TranscriptWindow, step = STEP): { window: TranscriptWindow; grown: number } {
  const nextStart = Math.max(0, w.start - step);
  const grown = w.start - nextStart;
  return { window: { ...w, start: nextStart }, grown };
}

/** Expand to WINDOW_MIN after mount (second frame); only when pinned. */
export function expandToMin(w: TranscriptWindow): TranscriptWindow {
  if (w.end - w.start >= WINDOW_MIN) return w;
  return { ...w, start: Math.max(0, w.end - WINDOW_MIN) };
}

export function coversAllLoaded(w: TranscriptWindow, loadedCount: number): boolean {
  return w.start <= Math.max(0, w.total - loadedCount);
}

export function sliceWindow<T>(list: T[], w: TranscriptWindow): T[] {
  return list.slice(w.start, w.end);
}

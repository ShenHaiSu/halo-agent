/** bigChange P2-2/P2-7: render content limits — staged overflow gates. */
export const SYNC_MD_LIMIT = 128 * 1024;
export const STREAM_TAIL_LIMIT = 32 * 1024;
export const SMOOTH_TEXT_LIMIT = 32 * 1024;
export const HIGHLIGHT_CHAR_LIMIT = 100 * 1024;
export const HIGHLIGHT_MAX_LINES = 800;
export const HIGHLIGHT_MAX_COLS = 2000;
export const MERMAID_CHAR_LIMIT = 20 * 1024;
export const MERMAID_EDGE_LIMIT = 500;

export type OverflowKind = 'ok' | 'block-text' | 'source-text';

export function classifySource(source: string): OverflowKind {
  if ((source || '').length > SYNC_MD_LIMIT) return 'source-text';
  return 'ok';
}

export function classifyBlock(raw: string, streaming: boolean): OverflowKind {
  const len = (raw || '').length;
  if (streaming && len > STREAM_TAIL_LIMIT) return 'block-text';
  const lines = raw.split('\n');
  if (len > HIGHLIGHT_CHAR_LIMIT || lines.length > HIGHLIGHT_MAX_LINES) return 'block-text';
  for (const ln of lines) {
    if (ln.length > HIGHLIGHT_MAX_COLS) return 'block-text';
  }
  return 'ok';
}

export function overflowHint(kind: OverflowKind): string {
  if (kind === 'source-text') return '内容超长（>128KB），已降级为纯文本';
  if (kind === 'block-text') return '该块超长，已降级为纯文本';
  return '';
}

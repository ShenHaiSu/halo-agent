/** bigChange P2-8: composer gates — send/IME/Alt+steering/queue/slash/files. Minimal usable landing. */
export const LARGE_PASTE_DEFAULT = 600;
export const LARGE_PASTE_MIN = 1;
export const LARGE_PASTE_MAX = 1000000;
export const SLASH_FILE_LIMIT = 50;
export const SLASH_CACHE_TTL_MS = 10 * 1000;
export const SLASH_DEBOUNCE_MS = 150;
export const SINGLE_FLIGHT_ATTACH = 1;

export interface SendGate {
  hasDraftContent: boolean;
  modelReady: boolean;
  approvalPending: boolean;
  pasting: boolean;
  nativeInputBlocked: boolean;
  draftText: string;
}

/** hasDraftContent(trim || refs) && !sendBlocked && (modelReady || 首字 /) */
export function canSend(g: SendGate): boolean {
  const sendBlocked = g.approvalPending || g.pasting || g.nativeInputBlocked;
  if (sendBlocked || !g.hasDraftContent) return false;
  if (g.modelReady) return true;
  const t = (g.draftText || '').trimStart();
  return t.startsWith('/');
}

/** Stop replaces send when runActive && !hasDraftContent; gated by canStop. */
export function canStop(runActive: boolean, hasDraftContent: boolean): boolean {
  return runActive && !hasDraftContent;
}

/** Enter: !Shift && (enterToSend || Cmd/Ctrl) sends; Alt+Enter steers even with AC. */
export function enterAction(
  e: { key: string; shiftKey: boolean; ctrlKey: boolean; metaKey: boolean; altKey: boolean },
  opts: { enterToSend: boolean; isComposing: boolean; runActive: boolean }
): 'send' | 'steer' | 'newline' | 'none' {
  if (opts.isComposing) return 'none';
  if (e.key !== 'Enter') return 'none';
  if (e.altKey) return 'steer';
  if (!e.shiftKey && (opts.enterToSend || e.ctrlKey || e.metaKey)) return 'send';
  return 'newline';
}

/** Big pasted text over threshold converts to .txt attachment. */
export function shouldConvertToFile(text: string, threshold = LARGE_PASTE_DEFAULT): boolean {
  return (text || '').length >= threshold;
}

/** Slash dispatch: /name body — template/unknown -> prompt; builtin/extension/mode by body. */
export function parseSlashCommand(text: string): { name: string; body: string } | null {
  const t = (text || '').trimStart().replace(/^[，]/, '/');
  const m = t.match(/^\/([A-Za-z0-9_-]+)([\s\S]*)$/);
  if (!m) return null;
  return { name: m[1], body: (m[2] || '').trim() };
}

/** Queue row actions. */
export type QueueAction = 'moveUp' | 'moveDown' | 'sendNow' | 'edit' | 'delete';

export function queueActionAllowed(status: 'pending' | 'promoted' | 'sending', action: QueueAction): boolean {
  if (status !== 'pending') return false;
  void action;
  return true;
}

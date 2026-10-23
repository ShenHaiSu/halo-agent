/** bigChange P2-5: per-session disclosure map (replaces scattered local expanded booleans). */
export interface DisclosureChoice {
  open: boolean;
  revealRequest: boolean;
}

const perSession = new Map<string, Map<string, DisclosureChoice>>();

function sessionMap(sessionId: string): Map<string, DisclosureChoice> {
  let m = perSession.get(sessionId);
  if (!m) {
    m = new Map();
    perSession.set(sessionId, m);
  }
  return m;
}

/** Only explicit user choice is stored; pane eviction retains (no disk persist). */
export function setDisclosure(sessionId: string, key: string, choice: DisclosureChoice): void {
  if (!sessionId || !key) return;
  sessionMap(sessionId).set(key, choice);
}

export function getDisclosure(sessionId: string, key: string): DisclosureChoice | null {
  return perSession.get(sessionId)?.get(key) || null;
}

export function clearSessionDisclosure(sessionId: string): void {
  perSession.delete(sessionId);
}

/**
 * requestedOpen = choice?.open ?? (revealRequest mismatch ? true : automaticOpen).
 * Search navigation forces open on first paint via revealRequest=true.
 */
export function requestedOpen(sessionId: string, key: string, opts: { automaticOpen: boolean; revealRequest?: boolean }): boolean {
  const choice = getDisclosure(sessionId, key);
  if (choice) {
    if (opts.revealRequest !== undefined && choice.revealRequest !== opts.revealRequest) return true;
    return choice.open;
  }
  if (opts.revealRequest) return true;
  return opts.automaticOpen;
}

/** Nested: open = requestedOpen && scopeVisible; child open claims ancestors open. */
export function nestedOpen(requested: boolean, scopeVisible: boolean): boolean {
  return requested && scopeVisible;
}

/** Focus guard: completed auto-open true->false while focus inside or selection present => pin explicit open. */
export function focusGuardPin(hasFocusInside: boolean, hasSelection: boolean): boolean {
  return hasFocusInside || hasSelection;
}

/** Whole-turn default: automaticOpen = isActive; failed-but-active stays open; empty steps not rendered. */
export function turnAutomaticOpen(isActive: boolean): boolean {
  return isActive;
}

/** Tool row: never auto-opens. Thinking row: prop-controlled default closed. */
export const TOOL_AUTOMATIC_OPEN = false;
export const THINKING_AUTOMATIC_OPEN = false;

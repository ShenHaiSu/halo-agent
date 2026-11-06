// 与 Engine.Storage/Sessions/SessionTitle.cs 同语义（乐观占位用，最终以事件为准）。
export const SESSION_TITLE_MAX = 60;
export const EMPTY_TITLE = '(空会话)';
export const NEW_SESSION_TITLE = '新会话';

const PLACEHOLDERS = new Set(['新会话', '新对话', '', EMPTY_TITLE]);

export function buildTitle(raw: unknown): string {
  const t = String(raw ?? '').trim().replace(/\n/g, ' ');
  if (!t) return EMPTY_TITLE;
  return t.length > SESSION_TITLE_MAX ? t.slice(0, SESSION_TITLE_MAX) + '…' : t;
}

/** 是否为“尚未首答”的占位标题（允许乐观覆盖）。备注名 note 不在此列。 */
export function isPlaceholderTitle(title: unknown): boolean {
  return PLACEHOLDERS.has(String(title ?? ''));
}

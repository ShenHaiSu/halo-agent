// P10-fix3 01：斜杠命令白名单 + 解析 + 去引号（与 useSlash 命令表同源）。
export const SLASH_COMMANDS = [
  'new',
  'clear',
  'compact',
  'undo',
  'model',
  'mode',
  'mcp',
  'persona',
  'cwd',
  'diagnostics',
  'help',
] as const;

export type SlashCommandName = (typeof SLASH_COMMANDS)[number];

const SLASH_SET: ReadonlySet<string> = new Set<string>(SLASH_COMMANDS);

/** 解析 `^\/([a-z-]+)\s*([\s\S]*)$`；非斜杠或不在白名单返回 null（回落普通聊天）。 */
export function parseSlashCommand(raw: string): { name: string; args: string } | null {
  const t = (raw ?? '').trim();
  if (!t.startsWith('/')) return null;
  const m = /^\/([a-z-]+)\s*([\s\S]*)$/.exec(t);
  if (!m) return null;
  const name = (m[1] ?? '').toLowerCase();
  if (!SLASH_SET.has(name)) return null;
  return { name, args: (m[2] ?? '').trim() };
}

/** 去首尾配对单/双引号 + Trim（`"C:\My Work"` → `C:\My Work`）。 */
export function unquoteArgs(raw: string): string {
  let s = (raw ?? '').trim();
  if (s.length >= 2) {
    const f = s[0];
    const l = s[s.length - 1];
    if ((f === '"' && l === '"') || (f === "'" && l === "'")) {
      s = s.slice(1, -1).trim();
    }
  }
  return s;
}

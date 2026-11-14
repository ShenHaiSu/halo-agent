/**
 * 差异行装配：edit 按旧串拼删除行、新串拼新增行，write 按正文拼新增行。
 * 纯函数，头徽标计数与差异视图共用同一口径。
 */
export interface DiffLine {
  kind: 'add' | 'del' | 'ctx';
  text: string;
}

const DIFF_DEL = 'del' as const;
const DIFF_ADD = 'add' as const;

function toLines(v: unknown): string[] {
  return typeof v === 'string' ? v.split('\n') : [];
}

export function buildDiffLines(args: Record<string, unknown>, functionName: string): DiffLine[] {
  if (functionName === 'edit_file') {
    const removed = toLines(args['old_string']).map((t) => ({ kind: DIFF_DEL, text: t }));
    const added = toLines(args['new_string']).map((t) => ({ kind: DIFF_ADD, text: t }));
    return [...removed, ...added];
  }
  return toLines(args['content'] ?? args['new_string'] ?? '').map((t) => ({ kind: DIFF_ADD, text: t }));
}

/** 头徽标用增删计数（与差异视图同一行集，避免两处各算一次）。 */
export function countDiffLines(lines: DiffLine[]): { added: number; removed: number } {
  let added = 0;
  let removed = 0;
  for (const l of lines) {
    if (l.kind === 'add') added += 1;
    else if (l.kind === 'del') removed += 1;
  }
  return { added, removed };
}

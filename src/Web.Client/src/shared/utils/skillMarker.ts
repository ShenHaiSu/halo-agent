/**
 * need22-F01：技能激活 marker 语法单一事实源（纯字符串处理，零依赖）。
 *
 * 冻结语法：[[skill:<scope>/<name>]]
 * - scope ∈ global | project（小写）
 * - name 遵循后端 SkillMemoryService.NormalizeAndValidateName：^[a-z0-9][a-z0-9-_]{1,47}$
 * 非法 scope / 非法 name 一律当普通文本（不报错、不吞、不静默替换）。
 */
import type { SlashScope } from './slashRegistry';

export const SKILL_MARKER_PREFIX = '[[skill:';

/** 冻结语法：[[skill:<scope>/<name>]]；name 规则与后端 NormalizeAndValidateName 对齐。 */
export const SKILL_MARKER_RE = /\[\[skill:(global|project)\/([a-z0-9][a-z0-9-_]{1,47})\]\]/g;

export interface SkillMarkerRef {
  scope: SlashScope;
  name: string;
  /** 完整 marker 文本 */
  marker: string;
  /** 在原串中的起始下标 */
  index: number;
}

export type SkillSplitPart = { type: 'text'; text: string } | { type: 'skill'; scope: SlashScope; name: string; marker: string };

/** 生成 marker：'[[skill:global/foo]]'。 */
export function skillMarker(scope: SlashScope, name: string): string {
  return `${SKILL_MARKER_PREFIX}${scope}/${name}]]`;
}

/** 解析出全部 marker（按出现顺序）。非法 scope/name 不匹配、不返回。 */
export function parseSkillMarkers(text: string): SkillMarkerRef[] {
  const src = String(text ?? '');
  const out: SkillMarkerRef[] = [];
  const re = new RegExp(SKILL_MARKER_RE.source, 'g');
  let m: RegExpExecArray | null;
  while ((m = re.exec(src)) !== null) {
    out.push({ scope: m[1] as SlashScope, name: m[2], marker: m[0], index: m.index });
  }
  return out;
}

/** 去掉全部 marker（用于标题/摘要/TOC/复制可读文本）；只删 marker 本身，不删相邻空白。 */
export function stripSkillMarkers(text: string): string {
  const src = String(text ?? '');
  return src.replace(new RegExp(SKILL_MARKER_RE.source, 'g'), '');
}

/**
 * 按 marker 切分：返回 [{ type:'text', text } | { type:'skill', scope, name, marker }]。
 * 前后空白保留在 text 段里（切分不做 trim），保证拼回原串可 100% 还原。
 * - 相邻两个 marker 之间没有文本 ⇒ 中间不产生空 text 段；
 * - 首尾空 text 段保留（保证还原）。
 */
export function splitBySkillMarkers(text: string): SkillSplitPart[] {
  const src = String(text ?? '');
  const parts: SkillSplitPart[] = [];
  const re = new RegExp(SKILL_MARKER_RE.source, 'g');
  let m: RegExpExecArray | null;
  let cursor = 0;
  let first = true;
  while ((m = re.exec(src)) !== null) {
    const seg = src.slice(cursor, m.index);
    if (seg !== '' || first) parts.push({ type: 'text', text: seg });
    parts.push({ type: 'skill', scope: m[1] as SlashScope, name: m[2], marker: m[0] });
    cursor = m.index + m[0].length;
    first = false;
  }
  if (first) {
    // 全文无 marker ⇒ 单个 text 段（空串也保留）
    parts.push({ type: 'text', text: src });
    return parts;
  }
  // 尾部 text 段（可能为空）保留
  parts.push({ type: 'text', text: src.slice(cursor) });
  return parts;
}

/** 判是否含 marker（快路径，避免无谓切分）。 */
export function hasSkillMarker(text: string): boolean {
  const src = String(text ?? '');
  if (!src.includes(SKILL_MARKER_PREFIX)) return false;
  return new RegExp(SKILL_MARKER_RE.source, 'g').test(src);
}

/** 取出 (scope,name) 去重后的集合（发消息去重用，D4）。 */
export function uniqueSkillRefs(text: string): Array<{ scope: SlashScope; name: string }> {
  const seen = new Set<string>();
  const out: Array<{ scope: SlashScope; name: string }> = [];
  for (const ref of parseSkillMarkers(text)) {
    const key = `${ref.scope}/${ref.name}`;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push({ scope: ref.scope, name: ref.name });
  }
  return out;
}

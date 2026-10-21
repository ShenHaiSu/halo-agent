/** need19：历史目录纯函数（无 Vue / 无 DOM 依赖，可裸 node 断言） */
import type { TimelineEntry } from '@features/session/utils/turnGroup';
// need22-F04：目录摘要 strip skill marker（显示"帮我改代码…"而不是 [[skill:...]]）
import { stripSkillMarkers } from '../../../shared/utils/skillMarker.ts';

export type TocRole = 'user' | 'assistant' | 'system';
export interface TocItem {
  /** 与 TimelineEntry.key 完全一致，也是锚点 key */
  anchorKey: string;
  role: TocRole;
  /** 用于气泡显示的标签（正文前 15 字，已清洗） */
  label: string;
  /** 原始全文（仅供 tooltip / aria-label，不渲染） */
  fullText: string;
  /** 字符数（清洗后，用于 tooltip“共 N 字”） */
  charCount: number;
  /** 序号（在过滤前全局递增，1-based，显示为 #n） */
  index: number;
  /** 所属轮次序号（1-based；user 与其后 assistant 同轮；system=0） */
  round: number;
  /** assistant 轮的附加信息 */
  toolCount: number;
  hasReasoning: boolean;
  /** 消息创建时间（可能为空串） */
  createdAtUtc: string;
  /** 是否为该轮第一条（保留兼容；过滤已改为“只看每轮末条”） */
  isRoundHead: boolean;
  /** 是否为该轮最后一条 assistant（用于“只看每轮末条”） */
  isRoundTail: boolean;
}
export interface TocFilter {
  role: 'all' | 'user' | 'assistant';
  roundHeadsOnly: boolean;
  skipSystem?: boolean;
}
const MAX_SCAN = 4000; // 巨型消息只扫前 4k（07 E-07）
const LABEL_MAX = 15;

/**
 * 清洗成“正文”。顺序严格按 9 步（错了会出怪文本）。
 * 输入超长时先截断到 MAX_SCAN（气泡只需要前 15 字，tooltip 60 字，4k 足够）。
 */
export function toDisplayText(raw: string | null | undefined): string {
  let s = String(raw ?? '');
  if (s.length > MAX_SCAN) s = s.slice(0, MAX_SCAN);
  // 0) need22-F04：去掉技能 marker（目录里不显示 [[skill:...]] 内部语法）
  s = stripSkillMarkers(s);
  // 1) 去掉图片占位：[image:xxx] / ![alt](path) / <img ...>
  s = s
    .replace(/\[image:[^\]]*\]/gi, ' ')
    .replace(/!\[[^\]]*\]\([^)]*\)/g, ' ')
    .replace(/<img\b[^>]*>/gi, ' ');
  // 2) 去掉代码围栏标记（保留代码内容文字）
  s = s.replace(/```[a-zA-Z0-9_+-]*\s*/g, ' ').replace(/```/g, ' ');
  // 3) 行内代码反引号
  s = s.replace(/`{1,3}/g, '');
  // 4) 去掉 HTML 标签
  s = s.replace(/<[^>]{1,200}>/g, ' ');
  // 5) 链接 [text](url) → text
  s = s.replace(/\[([^\]]*)\]\([^)]*\)/g, '$1');
  // 6) 强调/标题/引用/列表符
  s = s
    .replace(/^\s{0,3}#{1,6}\s+/gm, '')
    .replace(/^\s{0,3}>\s?/gm, '')
    .replace(/^\s{0,3}([-*+]|\d{1,3}[.)])\s+/gm, '');
  s = s.replace(/[*_~]{1,3}/g, '');
  // 7) 换行/连续空白 → 单空格
  s = s.replace(/\s+/g, ' ').trim();
  // 8) 控制字符与零宽字符
  s = s.replace(/[\u0000-\u001F\u007F\u200B-\u200F\uFEFF]/g, '');
  // 9) 若清洗后为空但原文有图 → 给“[图片]”
  if (!s && /\[image:|!\[|<img\b/i.test(String(raw ?? ''))) return '[图片]';
  return s;
}

/** 正文前 15 字（码点切分，中文/emoji 安全；超长加 …）。 */
export function bubbleLabel(text: string, max = LABEL_MAX): string {
  const s = (text || '').trim();
  if (!s) return '';
  const chars = Array.from(s);
  if (chars.length <= max) return s;
  return chars.slice(0, max).join('') + '…';
}

function textOfEntry(e: TimelineEntry): { text: string; createdAtUtc: string } {
  if (e.kind === 'user' || e.kind === 'system') {
    return {
      text: String(e.msg?.content ?? ''),
      createdAtUtc: String((e.msg as unknown as { createdAtUtc?: string } | undefined)?.createdAtUtc || ''),
    };
  }
  const g = e.group;
  if (!g) return { text: '', createdAtUtc: '' };
  return {
    text: String(g.content ?? ''),
    createdAtUtc: String(g.createdAtUtc || ''),
  };
}

export function labelOfEntry(e: TimelineEntry): string {
  if (e.kind === 'user') {
    const t = toDisplayText(String(e.msg?.content ?? ''));
    if (t) return bubbleLabel(t);
    if (
      e.msg &&
      Array.isArray((e.msg as unknown as { images?: unknown[] }).images) &&
      ((e.msg as unknown as { images?: unknown[] }).images as unknown[]).length > 0
    )
      return '[图片]';
    return '[空消息]';
  }
  if (e.kind === 'system') {
    const t = toDisplayText(String(e.msg?.content ?? ''));
    if (t) return bubbleLabel(t);
    return '[系统消息]';
  }
  const g = e.group;
  if (!g) return '[空回复]';
  const t = toDisplayText(String(g.content ?? ''));
  if (t) {
    if (/^\s*(?:\[上下文压缩|压缩摘要)/.test(String(g.content ?? '')) || /^\s*(?:\[上下文压缩|压缩摘要)/.test(t))
      return 'Σ ' + bubbleLabel(t);
    return bubbleLabel(t);
  }
  const toolCount = (g.toolMessages || []).length;
  if (toolCount > 0) {
    if (!g.assistantId) return '🔧 工具结果';
    return `🔧 调用了 ${toolCount} 个工具`;
  }
  if (g.reasoning) return '🧠 仅思考未输出';
  return '[空回复]';
}

export function buildHistoryToc(entries: TimelineEntry[], filter: TocFilter): TocItem[] {
  // 预扫描：确定每轮最后一个 assistant 下标（只看每轮末条用）
  const rounds: number[] = [];
  const roles: TocRole[] = [];
  let r = 0;
  for (const e of entries) {
    let role: TocRole;
    if (e.kind === 'user') {
      role = 'user';
      r += 1;
    } else if (e.kind === 'turn') role = 'assistant';
    else role = 'system';
    roles.push(role);
    rounds.push(role === 'system' ? 0 : r);
  }
  const lastAssistantByRound = new Map<number, number>();
  for (let i = 0; i < entries.length; i++) {
    if (roles[i] === 'assistant') lastAssistantByRound.set(rounds[i], i);
  }
  const out: TocItem[] = [];
  let sawHeadInRound = false;
  let curRound = 0;
  for (let i = 0; i < entries.length; i++) {
    const e = entries[i];
    const role = roles[i];
    const round = rounds[i];
    if (role === 'user') {
      curRound = round;
      sawHeadInRound = false;
    }
    void curRound;
    const isRoundHead = role === 'assistant' && !sawHeadInRound;
    if (role === 'assistant') sawHeadInRound = true;
    const isRoundTail = role === 'assistant' && lastAssistantByRound.get(round) === i;
    // 过滤：角色
    if (filter.role === 'user' && role !== 'user') continue;
    if (filter.role === 'assistant' && role !== 'assistant') continue;
    // roundHeadsOnly 复用为“只看每轮末条”
    if (filter.roundHeadsOnly && role === 'assistant' && !isRoundTail) continue;
    if (filter.roundHeadsOnly && role === 'system') continue;
    if (filter.skipSystem !== false && role === 'system') continue;
    const { text, createdAtUtc } = textOfEntry(e);
    const display = toDisplayText(text);
    const label = labelOfEntry(e);
    const toolCount = e.kind === 'turn' ? (e.group?.toolMessages || []).length : 0;
    const hasReasoning = e.kind === 'turn' ? !!e.group?.reasoning : false;
    out.push({
      anchorKey: e.key,
      role,
      label,
      fullText: display.slice(0, MAX_SCAN),
      charCount: Array.from(display).length,
      index: i + 1,
      round,
      toolCount,
      hasReasoning,
      createdAtUtc,
      isRoundHead,
      isRoundTail,
    });
  }
  return out;
}

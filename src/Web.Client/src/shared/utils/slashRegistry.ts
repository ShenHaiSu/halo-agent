/**
 * need22-F01：可注册候选池（零 Vue / 零 bridge / 零 DOM 依赖的纯数据 + 纯函数）。
 *
 * 契约见 docs/need22/frontend/01-候选池注册表与标记语法.md。
 * 后续新增 /command、@agent 等候选池只需再注册一个 provider，不改面板与输入框。
 */

/** 候选作用域：与后端 SkillMemoryScope 对齐。 */
export type SlashScope = 'global' | 'project';

export interface SlashCandidate {
  /** 全局唯一，例：'skill:global:foo' */
  id: string;
  /** 产出该候选的 provider id，例：'skill' */
  providerId: string;
  /** 作用域（可选：非作用域类候选可省） */
  scope?: SlashScope;
  /** 展示前缀，例：'global/' */
  prefix?: string;
  /** 主展示文本，例：'global/foo' */
  label: string;
  /** 副标题（skill 的 title） */
  title?: string;
  /** 描述（skill 的 summary） */
  description?: string;
  /** 额外匹配关键词（命中即算匹配） */
  keywords?: string[];
  /** 选中后带回的数据（skill 候选放 { scope, name }） */
  payload?: unknown;
}

export interface SlashProviderCtx {
  /** 当前工作区 id（可能为空） */
  workspaceId: string;
  /** 触发时的查询串（供 provider 自定义过滤，默认 provider 不用） */
  query: string;
  /** 可选：provider 软失败上报（面板顶部红字），不抛异常、不影响其它 provider */
  reportError?: (message: string) => void;
}

export interface SlashProvider {
  id: string;
  /** 小的排前面；默认 100 */
  order?: number;
  /** 懒加载：面板首次打开时调用；失败由调用方兜底 */
  load(ctx: SlashProviderCtx): Promise<SlashCandidate[]> | SlashCandidate[];
}

const providers = new Map<string, SlashProvider>();

/** 注册（同名覆盖，幂等）。 */
export function registerSlashProvider(p: SlashProvider): void {
  if (!p || !p.id) return;
  providers.set(p.id, p);
}

/** 注销；不存在则忽略。 */
export function unregisterSlashProvider(id: string): void {
  providers.delete(id);
}

/** 按 order 升序、id 升序返回快照（稳定顺序）。 */
export function getSlashProviders(): SlashProvider[] {
  return Array.from(providers.values()).sort((a, b) => {
    const ao = a.order ?? 100;
    const bo = b.order ?? 100;
    if (ao !== bo) return ao - bo;
    return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
  });
}

/** 已注册的 provider id（便于面板提示"来源"）。 */
export function listSlashProviderIds(): string[] {
  return getSlashProviders().map((p) => p.id);
}

function norm(s: string | undefined | null): string {
  return String(s ?? '')
    .toLowerCase()
    .trim();
}

/** 命中判定：前缀命中(0) > 其它包含命中(1)；不命中返回 -1。 */
function matchTier(c: SlashCandidate, namePart: string): number {
  if (!namePart) return 0;
  const label = norm(c.label);
  if (label.startsWith(namePart)) return 0;
  if (label.includes(namePart)) return 1;
  if (norm(c.title).includes(namePart)) return 1;
  if (norm(c.description).includes(namePart)) return 1;
  for (const k of c.keywords || []) {
    if (norm(k).includes(namePart)) return 1;
  }
  return -1;
}

/**
 * 过滤与排序（规则顺序敏感，见 frontend/01 §2.3）：
 * 1) query trim；为空 ⇒ 原样返回；
 * 2) query 含 '/' ⇒ 拆 scopePart / namePart（仅 global|project 视为作用域过滤）；
 * 3) 名字匹配：label 前缀 / label 包含 / title / description / keyword；
 * 4) 排序：前缀命中 > 包含命中，同档按 label 升序，稳定兜底原始下标。
 */
export function filterSlashCandidates(list: SlashCandidate[], query: string): SlashCandidate[] {
  const q = String(query ?? '').trim();
  if (!q) return list.slice();
  let scopeFilter: SlashScope | null = null;
  let namePart: string;
  if (q.includes('/')) {
    const idx = q.indexOf('/');
    const sp = norm(q.slice(0, idx));
    const np = norm(q.slice(idx + 1));
    if (sp === 'global' || sp === 'project') {
      scopeFilter = sp;
      namePart = np;
    } else {
      // 非法作用域前缀 ⇒ 不按作用域过滤，整体当普通文本匹配
      namePart = norm(q);
    }
  } else {
    namePart = norm(q);
  }
  const scored: Array<{ c: SlashCandidate; i: number; tier: number }> = [];
  for (let i = 0; i < list.length; i++) {
    const c = list[i];
    if (scopeFilter && c.scope !== scopeFilter) continue;
    const tier = matchTier(c, namePart);
    if (tier < 0) continue;
    scored.push({ c, i, tier });
  }
  scored.sort((a, b) => {
    if (a.tier !== b.tier) return a.tier - b.tier;
    const l = a.c.label.localeCompare(b.c.label);
    if (l !== 0) return l;
    return a.i - b.i;
  });
  return scored.map((s) => s.c);
}

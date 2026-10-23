/**
 * need22-F03：斜杠候选面板的状态编排（触发检测 / 候选加载 / 键盘导航 / 选中落 pill）。
 *
 * 契约见 docs/need22/frontend/03-斜杠候选面板与输入框接线.md。
 * 纯函数 detectSlashTrigger 便于单测；组合式函数持有面板状态。
 */
import { computed, ref, type ComputedRef, type Ref } from 'vue';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { filterSlashCandidates, getSlashProviders, type SlashCandidate, type SlashProviderCtx } from '@shared/utils/slashRegistry';
import { hasSkillSegment, type ComposerDraft } from '@shared/utils/composerModel';
import {
  caretOffsetInEditor,
  createSkillPillEl,
  editorPlainText,
  insertSkillPill,
  rangeFromTextOffsets,
  replaceRangeWithPill,
} from './composerEditorDom';

export interface SlashTriggerInfo {
  /** '/' 之后的查询串 */
  query: string;
  /** '/' 的起始偏移（含） */
  start: number;
  /** 光标偏移（不含） */
  end: number;
}

export interface SlashMenuApi {
  active: Ref<boolean>;
  query: Ref<string>;
  candidates: ComputedRef<SlashCandidate[]>;
  index: Ref<number>;
  loading: Ref<boolean>;
  error: Ref<string>;
  /** 输入框每次 input 后调用：重算触发区、必要时加载候选 */
  refresh(editor: HTMLElement | null): void;
  /** 键盘处理：返回 true 表示已消费该按键 */
  handleKeydown(e: KeyboardEvent): boolean;
  /** 选中候选：把 '/query' 文本替换为 pill */
  select(candidate: SlashCandidate): void;
  close(): void;
}

export interface SlashMenuOptions {
  editorRef: Ref<HTMLElement | null>;
  /** 读回编辑器内容到草稿（选中后调用） */
  readEditorToDraft: () => void;
  /** 取当前草稿（去重判断用） */
  getDraft: () => ComposerDraft;
  /** 可选：轻提示（如"该技能已激活"） */
  notify?: (message: string) => void;
}

/** 词首 '/' 触发正则：前为行首/空白/( [ { , ; : 引号；query 不含空白与 '/'。 */
/** 词首 '/' 触发正则（need22 冻结）：前为行首/空白/( [ { , ; : 引号；query 不含空白与 '/'。
    注：字符类内的 '[' 无需转义，去掉冗余 '\' 以消除 eslint no-useless-escape（语义完全等价）。 */
const SLASH_TRIGGER_RE = /(?:^|[\s([{,;:'"“”（(])(\/)([^\s/]*)$/;
/** 词首校验用的前置字符集（与 SLASH_TRIGGER_RE 的字符类一致） */
const TRIGGER_PREFIX_CHARS = '([{,;:\'"“”（';
/** URL / 协议前缀排除：http:// https:// file:// */
const URL_PREFIX_RE = /(?:^|[^a-z0-9])(?:https?|file)$/i;
/** 候选缓存 TTL（毫秒） */
const CACHE_TTL_MS = 30_000;

interface CacheEntry {
  at: number;
  list: SlashCandidate[];
}
/** 同 workspaceId 下缓存已加载候选 30 秒（输入变化只做本地过滤，不重复请求）。 */
const candidateCache = new Map<string, CacheEntry>();

/**
 * 纯函数：检测词首 '/' 触发区（7 步算法，见 frontend/03 §3）。
 * 偏移口径与 editorPlainText / rangeFromTextOffsets 一致（只累计文本节点）。
 */
export function detectSlashTrigger(editor: HTMLElement): SlashTriggerInfo | null {
  if (!editor) return null;
  // 1) 选区必须折叠
  const sel = window.getSelection();
  if (!sel || !sel.isCollapsed || sel.rangeCount <= 0) return null;
  // 2) 光标必须落在编辑器内
  const anchor = sel.anchorNode;
  if (!anchor) return null;
  if (anchor !== editor && !editor.contains(anchor)) return null;
  // 3) 光标偏移
  const caret = caretOffsetInEditor(editor);
  if (caret === null) return null;
  // 4) 取光标前的文本
  const text = editorPlainText(editor);
  const before = text.slice(0, caret);
  // 5) 正则匹配（文档 §3 的正则首组为非捕获组，query 取最后一个捕获组）
  const m = SLASH_TRIGGER_RE.exec(before);
  if (!m) return null;
  const query = String(m[m.length - 1] ?? '');
  const slashOffset = caret - query.length - 1;
  // 6) 词首校验（防止 a/b 这类路径被误判；字符集与触发正则保持一致）
  if (slashOffset > 0) {
    const prev = text[slashOffset - 1];
    const ok = /\s/.test(prev) || TRIGGER_PREFIX_CHARS.indexOf(prev) >= 0;
    if (!ok) return null;
  }
  // 7) 排除 URL / 协议前缀
  if (slashOffset > 0 && text[slashOffset - 1] === ':') {
    const head = text.slice(0, slashOffset - 1);
    if (URL_PREFIX_RE.test(head)) return null;
  }
  return { query, start: slashOffset, end: caret };
}

export function useSlashMenu(opts: SlashMenuOptions): SlashMenuApi {
  const active = ref(false);
  const query = ref('');
  const index = ref(0);
  const loading = ref(false);
  const error = ref('');
  const all = ref<SlashCandidate[]>([]);
  const trigger = ref<SlashTriggerInfo | null>(null);
  let lastQuery = '';

  const candidates = computed<SlashCandidate[]>(() => filterSlashCandidates(all.value, query.value));

  function currentWorkspaceId(): string {
    try {
      const ws = useWorkspaceStore();
      return ws.activeWorkspaceId || ws.activeWorkspace?.id || '';
    } catch {
      return '';
    }
  }

  function close(): void {
    active.value = false;
    index.value = 0;
    trigger.value = null;
    lastQuery = '';
  }

  async function loadCandidates(workspaceId: string, q: string): Promise<void> {
    loading.value = true;
    error.value = '';
    const out: SlashCandidate[] = [];
    for (const p of getSlashProviders()) {
      const ctx: SlashProviderCtx = {
        workspaceId,
        query: q,
        reportError: (m: string) => {
          error.value = m;
        },
      };
      try {
        const res = await p.load(ctx);
        if (Array.isArray(res)) out.push(...res);
      } catch (e) {
        // 单个 provider 抛错 ⇒ 记 error，不影响其它 provider
        error.value = (e as Error)?.message || '候选加载失败';
      }
    }
    all.value = out;
    loading.value = false;
  }

  function refresh(editor: HTMLElement | null): void {
    if (!editor) {
      close();
      return;
    }
    const info = detectSlashTrigger(editor);
    if (!info) {
      close();
      return;
    }
    trigger.value = info;
    query.value = info.query;
    if (info.query !== lastQuery) {
      lastQuery = info.query;
      index.value = 0;
    }
    active.value = true;
    if (index.value >= candidates.value.length) index.value = 0;
    const wid = currentWorkspaceId();
    const cached = candidateCache.get(wid);
    if (cached && Date.now() - cached.at < CACHE_TTL_MS) {
      all.value = cached.list;
      return;
    }
    void loadCandidates(wid, info.query).then(() => {
      candidateCache.set(wid, { at: Date.now(), list: all.value });
    });
  }

  function handleKeydown(e: KeyboardEvent): boolean {
    // IME 组合态一律不消费
    if (e.isComposing) return false;
    if (!active.value) return false;
    if (e.key === 'Escape') {
      close();
      return true;
    }
    if (candidates.value.length === 0) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        close();
        return false; // 交还发送逻辑
      }
      return false; // Enter 不拦截 → 照常发送
    }
    if (e.key === 'ArrowDown') {
      index.value = (index.value + 1) % candidates.value.length;
      return true;
    }
    if (e.key === 'ArrowUp') {
      index.value = (index.value - 1 + candidates.value.length) % candidates.value.length;
      return true;
    }
    if (e.key === 'Enter') {
      e.preventDefault();
      const c = candidates.value[index.value];
      if (c) select(c);
      return true;
    }
    return false;
  }

  function select(candidate: SlashCandidate): void {
    const editor = opts.editorRef.value;
    if (!editor) return;
    const scope = candidate.scope;
    const name = String(candidate.label || '').replace(/^(global|project)\//, '');
    if (!scope || !name) return;
    // D4：草稿里已有同 (scope,name) ⇒ 不再插入第二个 pill
    if (hasSkillSegment(opts.getDraft(), scope, name)) {
      opts.notify?.('该技能已激活');
      close();
      return;
    }
    const pill = createSkillPillEl(scope, name, candidate.title);
    const t = trigger.value;
    const range = t ? rangeFromTextOffsets(editor, t.start, t.end) : null;
    if (range) replaceRangeWithPill(editor, range, pill);
    else insertSkillPill(editor, scope, name, candidate.title);
    opts.readEditorToDraft();
    close();
  }

  return { active, query, candidates, index, loading, error, refresh, handleKeydown, select, close };
}

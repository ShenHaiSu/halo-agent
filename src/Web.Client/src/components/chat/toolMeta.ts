// src/Web.Client/src/components/chat/toolMeta.ts
// P5 D5-4 工具名中文映射 + 可视状态派生 + 折叠记忆。
// 参数摘要见 ./toolArgs（summarizeArgs）。
// P11-E：折叠记忆单源化。以 ui.collapsedTools 为准，本模块的 collapsedToolIds
// 改为其薄代理（引用点零变更）；三处清空点统一调 clearCollapsedTools()。

import { reactive } from 'vue';
import { useUiStore } from '@/stores/ui';
import type { IconName } from '@/components/icons/paths';
import type { ToolCallInfo } from '@/stores/messages';

export interface ToolMeta {
  label: string;
  icon: IconName;
  badgeVariant: 'default' | 'accent' | 'success' | 'warning' | 'danger';
  /** 是否会产生文件改动（决定是否优先展示 diff） */
  mutates: boolean;
}

/**
 * 未知工具不翻译：看到 "mcp__github__create_issue" 显示原名，
 * 用户才知道危险度。显示"未知工具"会让用户无法判断，一律允许更危险。
 */
export function toolMeta(name: string, kind?: string): ToolMeta {
  // P13：分流键 kind 优先于 name；unknown 短路不走任何名称映射（原名展示）。
  if (kind === 'unknown') {
    return { label: name, icon: 'info', badgeVariant: 'default', mutates: false };
  }
  if (name.startsWith('mcp__')) {
    const parts = name.split('__');
    return {
      label: 'MCP：' + (parts[1] ?? '?') + ' / ' + (parts[2] ?? '?'),
      icon: 'layers',
      badgeVariant: 'accent',
      mutates: /write|create|update|delete|remove/i.test(name),
    };
  }
  if (name.startsWith('skill__')) {
    return {
      label: '技能：' + name.slice(7),
      icon: 'zap',
      badgeVariant: 'accent',
      mutates: false,
    };
  }

  const TABLE: Record<string, ToolMeta> = {
    read_file: { label: '读取文件', icon: 'file-text', badgeVariant: 'default', mutates: false },
    write_file: { label: '写入文件', icon: 'file-pen', badgeVariant: 'warning', mutates: true },
    edit_file: { label: '修改文件', icon: 'edit', badgeVariant: 'warning', mutates: true },
    delete_file: { label: '删除文件', icon: 'trash', badgeVariant: 'danger', mutates: true },
    list_directory: { label: '列出目录', icon: 'folder', badgeVariant: 'default', mutates: false },
    glob: { label: '查找文件', icon: 'search', badgeVariant: 'default', mutates: false },
    grep: { label: '搜索内容', icon: 'search', badgeVariant: 'default', mutates: false },
    run_shell: { label: '执行命令', icon: 'terminal', badgeVariant: 'warning', mutates: true },
    web_search: { label: '联网搜索', icon: 'link', badgeVariant: 'default', mutates: false },
    web_fetch: { label: '抓取网页', icon: 'link', badgeVariant: 'default', mutates: false },
    todo_write: { label: '更新计划', icon: 'check', badgeVariant: 'default', mutates: false },
  };

  return TABLE[name] ?? { label: name, icon: 'info', badgeVariant: 'default', mutates: false };
}

// ---------------------------------------------------------------------------
// 可视状态派生：卡片不自存 state，全部从 ToolCallInfo + 询问槽派生
// 优先级：cancelled > error > awaiting-user > running > pending > done
// ---------------------------------------------------------------------------

export type ToolVisualState =
  | 'pending'
  | 'running'
  | 'awaiting-user'
  | 'done'
  | 'error'
  | 'cancelled';

export function deriveState(tool: ToolCallInfo, awaitingUser: boolean): ToolVisualState {
  if (tool.status === 'cancelled') return 'cancelled';
  if (tool.status === 'error') return 'error';
  if (tool.status === 'done') return tool.ok === false ? 'error' : 'done';
  if (awaitingUser) return 'awaiting-user';
  if (tool.status === 'running') return 'running';
  return 'pending';
}

/** P11-D：状态文案唯一实现（ToolCard 头部与未知工具链共用）。 */
export function toolStatusText(state: ToolVisualState): string {
  switch (state) {
    case 'pending':
      return '排队中';
    case 'running':
      return '执行中';
    case 'awaiting-user':
      return '等待授权';
    case 'done':
      return '成功';
    case 'error':
      return '失败';
    case 'cancelled':
      return '已中断';
    default:
      return '未知';
  }
}

// ---------------------------------------------------------------------------
// P11-E：折叠记忆单源（ui.collapsedTools 为准）。
// collapsedToolIds 为其薄代理：ToolCard 引用点零变更；pinia 未挂载时回退本地 Set。
// 会话内有效、刷新清空语义不变（不持久化）。
// ---------------------------------------------------------------------------

/** pinia 未就绪时的回退（导入期/单测直引 toolMeta 不崩）。 */
const fallbackCollapsed = reactive(new Set<string>());

function collapsedSet(): Set<string> {
  try {
    return useUiStore().collapsedTools as unknown as Set<string>;
  } catch {
    return fallbackCollapsed;
  }
}

/** 会话内折叠记忆（Set<toolCallId>）。单源代理至 ui.collapsedTools。 */
export const collapsedToolIds = {
  has: (id: string): boolean => collapsedSet().has(id),
  add: (id: string): Set<string> => {
    collapsedSet().add(id);
    return collapsedSet();
  },
  delete: (id: string): boolean => collapsedSet().delete(id),
  clear: (): void => collapsedSet().clear(),
  get size(): number {
    return collapsedSet().size;
  },
  values: (): IterableIterator<string> => collapsedSet().values(),
  [Symbol.iterator]: function* (): IterableIterator<string> {
    yield* collapsedSet();
  },
  forEach: (
    cb: (value: string, value2: string, set: Set<string>) => void,
    thisArg?: unknown
  ): void => collapsedSet().forEach(cb, thisArg),
};

/** 三处清空点（resync / contextCleared / 切换会话）统一调此函数。 */
export function clearCollapsedTools(): void {
  collapsedSet().clear();
}

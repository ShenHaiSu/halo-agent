// src/Web.Client/src/components/chat/toolMeta.ts
// P5 D5-4 工具名中文映射 + 可视状态派生 + 折叠记忆。
// 参数摘要见 ./toolArgs（summarizeArgs）。
// 说明：ui store 暂无 collapsedTools 字段（不改 stores），折叠记忆放在本模块的
// 响应式 Set 中，会话内有效，刷新即清空（与 03 §7.2 的语义一致）。

import { reactive } from 'vue';
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
export function toolMeta(name: string): ToolMeta {
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

/** 会话内折叠记忆（Set<toolCallId>）。刷新即清空，语义同 ui.collapsedTools。 */
export const collapsedToolIds = reactive(new Set<string>());

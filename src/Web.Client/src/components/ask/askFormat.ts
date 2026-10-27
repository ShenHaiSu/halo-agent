// src/Web.Client/src/components/ask/askFormat.ts
import type { IconName } from '@/components/icons/paths';

/**
 * kind → 中文文案（见 01 §7.4）。
 * 图标映射到 ICONS 现有图标（无 globe/plug/repeat 等，用近义替代）。
 */
export function askTitle(kind: string, fallback: string): string {
  switch (kind) {
    case 'tool.shell':
      return '是否允许执行命令？';
    case 'tool.write':
      return '是否允许修改文件？';
    case 'tool.delete':
      return '是否允许删除文件？';
    case 'tool.network':
      return '是否允许联网访问？';
    case 'tool.mcp':
      return '是否允许调用 MCP 工具？';
    case 'fs.outside-cwd':
      return '是否允许访问工作目录之外？';
    case 'agent.rounds':
      return '是否继续执行？';
    default:
      // * 未知 kind 用后端 title，绝不自己编
      return fallback || '需要你的确认';
  }
}

export function askIcon(kind: string): IconName {
  switch (kind) {
    case 'tool.shell':
      return 'terminal';
    case 'tool.write':
      return 'file-pen';
    case 'tool.delete':
      return 'trash';
    case 'tool.network':
      return 'link';
    case 'tool.mcp':
      return 'layers';
    case 'fs.outside-cwd':
      return 'folder';
    case 'agent.rounds':
      return 'refresh';
    default:
      return 'info';
  }
}

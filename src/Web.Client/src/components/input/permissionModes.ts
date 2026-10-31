// src/Web.Client/src/components/input/permissionModes.ts
// 输入框上方权限级别快切：4 档定义（label/desc/危险色），与 Composer 现有文案口径一致。
import type { PermissionMode } from '@/stores/session';

export interface PermissionModeOption {
  value: PermissionMode;
  label: string;
  desc: string;
  /** 危险档（自动放行/完全放行）：触发器圆点红色 */
  danger: boolean;
  /** 警告档（自动接受编辑）：触发器圆点黄色 */
  warning: boolean;
}

export const PERMISSION_MODES: readonly PermissionModeOption[] = [
  { value: 'ask', label: '询问模式', desc: '每个工具调用都询问你', danger: false, warning: false },
  {
    value: 'acceptEdits',
    label: '自动接受编辑',
    desc: '文件修改自动放行，命令仍需询问',
    danger: false,
    warning: true,
  },
  {
    value: 'bypassPermissions',
    label: '自动放行',
    desc: '不再逐个询问，高危命令除外',
    danger: true,
    warning: false,
  },
  {
    value: 'yolo',
    label: '完全放行',
    desc: '一切放行，含危险命令（极度危险）',
    danger: true,
    warning: false,
  },
] as const;

export function permissionModeLabel(mode: string | null | undefined): string {
  return PERMISSION_MODES.find((m) => m.value === mode)?.label ?? '询问模式';
}

export function permissionModeDesc(mode: string | null | undefined): string {
  return PERMISSION_MODES.find((m) => m.value === mode)?.desc ?? '';
}

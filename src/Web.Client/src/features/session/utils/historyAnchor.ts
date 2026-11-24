/** 摘要源菜单目标：复制/重放/分支/删除按轮锚点。 */
export interface HistoryTarget {
  kind: 'user' | 'assistant';
  /** 历史操作消息 ID（真实 message.id），不是 turnId 或 finalBlockId。 */
  id: string;
  text: string;
  /** 破坏性历史操作是否可用；undefined 仅用于兼容旧 live 回退路径。 */
  canReplay?: boolean;
  canBranch?: boolean;
  /** 禁用原因：忙碌、无可用锚点、压缩行或能力缺失。 */
  actionDisabledReason?: string;
  /** 用户分支草稿原文（来自摘要 userContent）。 */
  draftText?: string;
}

/** 剪贴板复制：优先异步接口，失败降级同步方案。 */
export async function copyTextToClipboard(text: string): Promise<boolean> {
  const value = String(text || '');
  if (!value) return false;
  try {
    if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(value);
      return true;
    }
  } catch {
    /* 降级同步方案 */
  }
  try {
    const ta = document.createElement('textarea');
    ta.value = value;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(ta);
    return ok;
  } catch {
    return false;
  }
}

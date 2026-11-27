/** 取消态判断：跨 feature 共享的纯谓词。
 * 入参用结构化最小形态，避免 shared 反向依赖任一 feature 的 PendingUser 类型。 */
export function isPendingCancelled(pending: { status?: string | null } | null | undefined): boolean {
  return !!pending && pending.status === 'cancelled';
}

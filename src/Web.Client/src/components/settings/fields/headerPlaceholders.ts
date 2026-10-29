// src/Web.Client/src/components/settings/fields/headerPlaceholders.ts
// 预设请求头占位常量：以后端 HeaderTemplate 为准（ConfigStore.cs:400-401）。
// 前端不得自创第三个占位。

/** 完整会话 ID 原文展开。 */
export const SESSION_ID_TOKEN = '{{session_id}}';
/** SHA256(sessionId)[:16] 小写hex，同会话稳定。 */
export const SESSION_HASH_TOKEN = '{{session_hash}}';

/** 自定义请求头 label hover tooltip 文案（纯文本，不放示例实值防泄漏）。 */
export function headerPlaceholderTooltip(): string {
  return (
    '可用占位（大小写不敏感，按会话展开，不预览实值）：\n' +
    `· ${SESSION_ID_TOKEN} — 完整会话 ID 原文\n` +
    `· ${SESSION_HASH_TOKEN} — SHA256(sessionId)[:16] 小写hex，同会话稳定\n` +
    '无会话 / 探测（/models）时，含占位的头整条跳过。'
  );
}

/** need21-F03：token 估算纯函数（自 stores/agentStore.ts 迁入；唯一源，store 只 import 不再定义）。 */

/** need05-01：前端估算函数（与 znaide app.rs:2119 同口径）：ASCII/4 向上取整，非 ASCII 按 1 */
export function estTokens(text: string): number {
  if (!text) return 0;
  let ascii = 0;
  let other = 0;
  for (const ch of text) {
    const cp = ch.codePointAt(0) || 0;
    if (cp <= 127) ascii++;
    else other++;
  }
  return other + Math.ceil(ascii / 4);
}

/** need12 R1：旧后端兼容——前端按 UTF-8 估算 delta 字节数（与后端 GetByteCount 同单位）。 */
export function estimateUtf8Bytes(text: string): number {
  if (!text) return 0;
  try {
    if (typeof TextEncoder !== 'undefined') return new TextEncoder().encode(text).length;
  } catch {
    /* fall through */
  }
  let n = 0;
  for (const ch of text) {
    const cp = ch.codePointAt(0) || 0;
    if (cp <= 0x7f) n += 1;
    else if (cp <= 0x7ff) n += 2;
    else if (cp <= 0xffff) n += 3;
    else n += 4;
  }
  return n;
}

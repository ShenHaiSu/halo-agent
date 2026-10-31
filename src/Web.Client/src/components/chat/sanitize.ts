// src/Web.Client/src/components/chat/sanitize.ts
// P5 D5-1 链接/图片 URL 安全白名单（XSS 防线 L2/L3）。

const SAFE_PROTOCOLS_LINK = ['http:', 'https:', 'mailto:'];
const SAFE_PROTOCOLS_IMG = ['http:', 'https:', 'data:'];

// eslint-disable-next-line no-control-regex -- 必须匹配控制字符才能拦控制字符绕过
const CONTROL_CHARS = new RegExp('[\\u0000-\\u001F\\u007F]', 'g');
/** 去掉控制字符与首尾空白（拦 `java\tscript:` 类绕过） */
export function normalizeUrl(raw: string): string {
  return raw.replace(CONTROL_CHARS, '').trim();
}

export function isSafeUrl(raw: string, kind: 'a' | 'img'): boolean {
  const url = normalizeUrl(raw);
  if (!url) return false;

  // 相对路径（/x、./x、x.md）允许
  if (!/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(url)) return true;

  let parsed: URL;
  try {
    parsed = new URL(url, 'http://localhost/');
  } catch {
    return false;
  }

  const proto = parsed.protocol.toLowerCase();

  if (kind === 'a') {
    return SAFE_PROTOCOLS_LINK.includes(proto);
  }

  if (!SAFE_PROTOCOLS_IMG.includes(proto)) return false;
  // data: 只允许图片
  if (proto === 'data:') {
    return /^data:image\/(png|jpe?g|gif|webp|bmp);base64,/i.test(url);
  }
  return true;
}

 /** 是否外部链接（走宿主打开默认浏览器，避免 CefSharp 内部导航） */
 export function isExternal(raw: string): boolean {
   return /^https?:/i.test(normalizeUrl(raw));
 }

 /** 是否邮件链接（允许默认行为，交 OS 邮件客户端） */
 export function isMailto(raw: string): boolean {
   return /^mailto:/i.test(normalizeUrl(raw));
 }

 export type LinkKind = 'external' | 'mailto' | 'relative' | 'unsafe';

 /** 点击策略三档收敛到一处：外部 / mailto / 相对路径 / 非安全协议 */
 export function classifyLink(raw: string): LinkKind {
   if (!isSafeUrl(raw, 'a')) return 'unsafe';
   if (isMailto(raw)) return 'mailto';
   if (isExternal(raw)) return 'external';
   return 'relative';
 }

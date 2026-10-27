// src/Web.Client/src/utils/clipboard.ts
/**
 * ============ 三级降级 ============
 *  ① navigator.clipboard.writeText（需要安全上下文；app:// 算安全上下文吗？
 *     CefSharp 的 app:// 是自定义 scheme，Chrome 视为“secure context”
 *     （因为注册了 standard + secure 标志）—— P3 已配置）
 *  ② document.execCommand('copy')（已废弃但 Win7 上最可靠）
 *  ③ 让用户手动选择（返回 false，调用方提示“请按 Ctrl+C”）
 * ===================================
 */
export async function copyText(text: string): Promise<boolean> {
  // ①
  try {
    if (typeof navigator !== 'undefined' && navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    /* 落到 ② */
  }

  // ②
  try {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.setAttribute('readonly', '');
    ta.style.cssText = 'position:fixed;top:-1000px;left:-1000px;opacity:0';
    document.body.appendChild(ta);
    ta.select();
    ta.setSelectionRange(0, text.length);
    const ok = document.execCommand('copy');
    ta.remove();
    if (ok) return true;
  } catch {
    /* 落到 ③ */
  }

  return false;
}

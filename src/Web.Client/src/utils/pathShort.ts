// src/Web.Client/src/utils/pathShort.ts

/**
 * 超长路径截短显示：超 46 字符时保留盘符头 + 省略号 + 尾部。
 * 例：C:\very\long\...\tail
 */
const MAX_PATH_LEN = 46;

export function shortPath(p: string): string {
  if (p.length <= MAX_PATH_LEN) return p;
  const drive = /^[A-Za-z]:\\/.test(p) ? p.slice(0, 3) : '';
  const tailLen = MAX_PATH_LEN - drive.length - 2; // 2 = '…' + '\'
  const tail = p.slice(-tailLen);
  return `${drive}…\\${tail}`;
}

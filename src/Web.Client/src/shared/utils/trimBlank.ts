/**
 * 首尾空白裁剪：头部整行空白全删；尾部换行折叠为 1 个（无尾换行则不添加）。
 *
 * 为什么不用 String.prototype.trim()：模型输出的思考常以缩进列表开头，
 * 全文 trim 虽只多删几个空格，但语义上我们希望保留中间缩进与全角空格（中文有意缩进属内容）。
 *
 * 为什么尾部做“折叠为 1 个”而非“全删”：模型常在末尾带 1~3 个空行，全删会让收尾
 * 与下一块视觉粘连；折叠为 1 个既压掉多余空行，又保留“这行结束了”的信息。
 *
 * 为什么不用 CSS：CSS 没有 text-trim 能力（text-box-trim 需 Chrome 133+，本仓基线 Chrome 109）。
 */
const LEAD_BLANK = /^(?:[ \t]*\r?\n)+/; // 头部整行空白
const TRAIL_HSPACE = /[ \t]+$/; // 末行行内空白（必须先删，否则尾换行后面跟着空格时匹配不到）
const TRAIL_NEWLINES = /(?:\r?\n[ \t]*)+$/; // 尾部“换行 + 其后的行内空白”整体
const EDGE_WS = new Set<number>([9, 10, 11, 12, 13, 32]);
// BOM（U+FEFF）：用编码点构造，避免源码里出现不可见字符
const BOM = String.fromCharCode(0xfeff);
const BOM_ALL = new RegExp(BOM, 'g');

export function trimBlankEdges(raw: string | null | undefined): string {
  const s = String(raw ?? '');
  if (!s) return '';
  // 快路径：流式阶段每帧都会重算，绝大多数帧两端都不是空白 → 零分配直接返回。
  // BOM 必须全串删除，所以快路径也要确认串里没有 BOM，否则带 BOM 的短串会被原样漏过。
  if (!EDGE_WS.has(s.charCodeAt(0)) && !EDGE_WS.has(s.charCodeAt(s.length - 1)) && !s.includes(BOM)) return s;
  let t = s.replace(BOM_ALL, '');
  t = t.replace(LEAD_BLANK, '');
  t = t.replace(TRAIL_HSPACE, '');
  const tail = TRAIL_NEWLINES.exec(t);
  // 1 个或多个尾换行 → 恰好 1 个；没命中就一个字都不加
  if (tail) t = t.slice(0, tail.index) + '\n';
  // 折叠后末行行尾可能残留行内空白（"abc  \n\n" → "abc  \n"）：裁剪前它们被换行挡住，
  // 折叠后才暴露在尾部，一并删掉。只删末行行尾，中间行缩进不受影响。
  t = t.replace(/[ \t]+\n$/, '\n');
  t = t.replace(/^[ \t]+/, '');
  return t;
}

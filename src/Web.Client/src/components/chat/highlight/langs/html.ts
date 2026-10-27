// src/Web.Client/src/components/chat/highlight/langs/html.ts
import type { LangDef } from '../tokenizer';

export const html: LangDef = {
  name: 'html',
  // 注释必须最先（<!-- 里可能有引号）
  comments: [/<!--[\s\S]*?-->/y],
  strings: [
    // 属性值（双/单引号）
    /"[^"]*"/y,
    /'[^']*'/y,
    // 文本节点（从 > 到 <）—— 交给 punct 处理更简单，这里不匹配
  ],
  keywords:
    /^(?:html|head|body|div|span|p|a|img|input|button|form|label|select|option|textarea|table|thead|tbody|tfoot|tr|td|th|ul|ol|li|h1|h2|h3|h4|h5|h6|section|article|header|footer|nav|main|aside|script|style|link|meta|title|canvas|svg|path|g|rect|circle|line|polygon|polyline|defs|use|symbol|template|slot|component|transition|router-view|router-link)$/i,
  numbers: /^\d+(?:\.\d+)?$/,
};

// src/Web.Client/src/components/chat/highlight/langs/markdown.ts
import type { LangDef } from '../tokenizer';

export const markdown: LangDef = {
  name: 'markdown',
  // 代码围栏内容用 string 类高亮（视觉上“这是代码”）
  comments: [/<!--[\s\S]*?-->/y],
  strings: [/```[\s\S]*?```/y, /`[^`\n]*`/y],
  keywords: /^(?:[#*>-]|\[|\]\(|\*\*|__|~~)$/,
  numbers: /^\d+[.)]$/,
};

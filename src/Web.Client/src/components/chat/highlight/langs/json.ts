// src/Web.Client/src/components/chat/highlight/langs/json.ts
import type { LangDef } from '../tokenizer';

export const json: LangDef = {
  name: 'json',
  // JSON 不允许注释，但 jsonc（带注释）很常见，所以保留
  comments: [/\/\*[\s\S]*?\*\//y, /\/\/[^\n]*/y],
  strings: [/"(?:\\[\s\S]|[^"\\\n])*"/y],
  keywords: /^(?:true|false|null)$/,
  numbers: /^-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?$/,
};

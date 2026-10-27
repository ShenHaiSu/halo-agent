// src/Web.Client/src/components/chat/highlight/langs/css.ts
import type { LangDef } from '../tokenizer';

export const css: LangDef = {
  name: 'css',
  comments: [/\/\*[\s\S]*?\*\//y],
  strings: [/"[^"\n]*"/y, /'[^'\n]*'/y],
  keywords:
    /^(?:important|none|auto|inherit|initial|unset|block|inline|flex|grid|absolute|relative|fixed|sticky|static|hidden|visible|solid|dashed|dotted|bold|italic|center|left|right|top|bottom|middle|pointer|default|transparent|currentColor|var|calc|rgb|rgba|hsl|hsla|url|linear-gradient|radial-gradient)$/i,
  numbers: /^-?\d*\.?\d+(?:px|em|rem|%|vh|vw|vmin|vmax|s|ms|deg|fr|ch|ex|pt|cm|mm|in)?$/,
};

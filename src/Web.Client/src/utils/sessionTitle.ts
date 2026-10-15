/**
 * need03-03 会话两阶段自动标题纯函数（可单测，无 PJ 依赖）。
 * T1: 首条 user 发出即取第一句话前 5 字；T2: 首轮结束后精炼回写一次，此后冻结。
 */

export function instantTitleFromUserText(text: string): string {
  const firstLine = (text || '')
    .trim()
    .split(/[\r\n]+/)
    .map((s) => s.trim())
    .filter(Boolean)[0] || '';
  const firstSentence = firstLine.split(/[。！？!?;；]+/).map((s) => s.trim()).filter(Boolean)[0] || firstLine;
  const compact = firstSentence.replace(/\s+/g, '');
  return [...compact].slice(0, 5).join('') || '新建会话';
}

export function truncateRoundText(text: string, max = 2000): string {
  if (!text) return '';
  return text.length > max ? text.slice(0, max) : text;
}

export function buildTitlePrompt(firstUser: string, firstAssistant: string): string {
  const round = truncateRoundText(
    `<user>: ${firstUser || ''}\n<assistant>: ${firstAssistant || ''}`,
    2000
  );
  return (
    '你是会话标题生成器。只根据用户首轮对话生成 4-10 个汉字的简短标题，不加标点、不加引号、不解释，只输出标题本身。若无法概括则输出首条消息前5字。\n' +
    '对话全文如下：\n---\n' + round + '\n---'
  );
}

function stripTitleNoise(s: string): string {
  let t = (s || '').trim().split(/[\r\n]+/)[0]?.trim() || '';
  t = t.replace(/^(标题[:：]|题目[:：]|名称[:：]|title[:：])\s*/i, '');
  t = t.replace(/^["'「『“‘(\[【]+/, '').replace(/["'」』”’)\]】]+$/, '');
  t = t.replace(/[。！？!?;；，,、.\-—…·\s]+$/g, '');
  t = t.replace(/^[-*•\d.\s]+/, '');
  return t.trim();
}

/**
 * 解析 LLM 标题结果：取首行、去噪、钳 4-12 字；非法返回 ''（调用方保留 T1 的 5 字）。
 * 允许中文/字母/数字混合；长度按码点计。
 */
export function parseTitleResult(text: string, fallback5 = ''): string {
  const cleaned = stripTitleNoise(text);
  if (!cleaned) return '';
  const chars = [...cleaned].filter((c) => !/[\r\n]/.test(c));
  if (chars.length === 0) return '';
  if (chars.length > 12) return chars.slice(0, 12).join('');
  if (chars.length < 2 && !fallback5) return '';
  // 过短（单字）且有 fallback 时视为非法，调用方保留 T1
  if (chars.length < 2) return '';
  return chars.join('');
}

/** 本地精炼兜底（无网/无后端标题接口时）：取 assistant 首句去噪后截 4-12 字，非法返回 '' */
export function refineTitleLocal(firstAssistant: string, firstUser: string): string {
  const src = (firstAssistant || '').trim() || (firstUser || '').trim();
  if (!src) return '';
  const firstLine = src.split(/[\r\n]+/).map((s) => s.trim()).filter(Boolean)[0] || '';
  const firstSentence = firstLine.split(/[。！？!?;；]+/).map((s) => s.trim()).filter(Boolean)[0] || firstLine;
  const compact = firstSentence.replace(/\s+/g, '');
  const chars = [...stripTitleNoise(compact)];
  if (chars.length < 2) return '';
  return chars.slice(0, Math.min(12, Math.max(4, Math.min(chars.length, 10)))).join('');
}

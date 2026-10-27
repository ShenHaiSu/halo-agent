// src/Web.Client/src/theme/validate.ts
import { THEME_TOKEN_KEYS, type ThemeTokenKey } from './types';

/**
 * HSL 三段无逗号格式：`H S% L%` 或 `H S% L% / A`
 *   H: 0-360（可带小数）
 *   S: 0-100%
 *   L: 0-100%
 *   A: 0-1（可选）
 */
const HSL_RE =
  /^\s*-?\d+(?:\.\d+)?\s+\d+(?:\.\d+)?%\s+\d+(?:\.\d+)?%(?:\s*\/\s*(?:\d+(?:\.\d+)?|\.\d+))?\s*$/;

/** shadow / 复合值：只要不含禁用函数即可 */
const FORBIDDEN_IN_VALUE = [
  /\boklch\s*\(/i,
  /\blch\s*\(/i,
  /\blab\s*\(/i,
  /\bcolor-mix\s*\(/i,
  /\bcolor\s*\(\s*display-p3/i,
];

const COLOR_LIKE: ThemeTokenKey[] = [
  'bg',
  'bg-subtle',
  'bg-muted',
  'bg-elevated',
  'bg-overlay',
  'fg',
  'fg-muted',
  'fg-subtle',
  'fg-inverse',
  'border',
  'border-strong',
  'border-subtle',
  'accent',
  'accent-hover',
  'accent-fg',
  'accent-subtle',
  'success',
  'success-subtle',
  'warning',
  'warning-subtle',
  'danger',
  'danger-subtle',
  'info',
  'info-subtle',
];

export interface ThemeValidationResult {
  ok: boolean;
  /** 通过校验的 token（只含白名单 key） */
  tokens: Partial<Record<ThemeTokenKey, string>>;
  errors: string[];
}

/**
 * 校验一份主题文件的 tokens。
 *
 * ============ 为什么要严格校验 ============
 *  用户可能手写一个主题 JSON，写错一个字符（比如 `#1a1a22`），
 *  结果 `hsl(#1a1a22 / 1)` → 整条声明失效 →
 *  该 token 变成未定义 → `var(--bg)` 回退到继承值 →
 *  可能出现"白底白字"完全看不见。
 *  所以：非法 token 一律丢弃（不应用），并用上一层的值兜底。
 * ============ ============
 */
export function validateThemeTokens(raw: unknown): ThemeValidationResult {
  const errors: string[] = [];
  const tokens: Partial<Record<ThemeTokenKey, string>> = {};

  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) {
    return { ok: false, tokens, errors: ['tokens 必须是一个对象'] };
  }

  const obj = raw as Record<string, unknown>;
  const allowed = new Set<string>(THEME_TOKEN_KEYS);

  for (const [key, value] of Object.entries(obj)) {
    if (!allowed.has(key)) {
      errors.push(`未知 token「${key}」（已忽略）`);
      continue;
    }
    if (typeof value !== 'string') {
      errors.push(`token「${key}」的值必须是字符串`);
      continue;
    }
    const v = value.trim();
    if (v.length === 0) {
      errors.push(`token「${key}」为空`);
      continue;
    }
    let forbidden = false;
    for (const re of FORBIDDEN_IN_VALUE) {
      if (re.test(v)) {
        errors.push(`token「${key}」含 Chrome 109 不支持的函数：${v}`);
        forbidden = true;
        break;
      }
    }
    if (forbidden) continue;
    if (COLOR_LIKE.includes(key as ThemeTokenKey) && !HSL_RE.test(v)) {
      errors.push(
        `token「${key}」格式必须是 "H S% L%"(可带 /A)，实际是「${v}」。` + `不要用 #hex 或 rgb()。` // @allow-hardcoded-color
      );
      continue;
    }
    tokens[key as ThemeTokenKey] = v;
  }

  return { ok: errors.length === 0, tokens, errors };
}

// src/Web.Client/src/components/settings/validators.ts

/**
 * P10 设置页字段级校验器。全部纯函数，无依赖，可单测。
 */

export interface FieldError {
  field: string;
  message: string;
}

/** URL 校验：必须能 new URL()，且协议是 http/https */
export function isValidHttpUrl(s: string): boolean {
  if (!s.trim()) return false;
  try {
    const u = new URL(s.trim());
    return u.protocol === 'http:' || u.protocol === 'https:';
  } catch {
    return false;
  }
}

export function isIntInRange(v: unknown, min: number, max: number): boolean {
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isInteger(n) && n >= min && n <= max;
}

/** 技能名：小写字母开头，只含小写字母/数字/连字符 */
export const SKILL_NAME_RE = /^[a-z][a-z0-9-]*$/;

/** 请求头名：RFC 7230 token */
const HEADER_NAME_RE = /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/;

export function validateHeaderName(k: string): boolean {
  return HEADER_NAME_RE.test(k);
}

export interface JsonValidation {
  ok: boolean;
  line?: number;
  message?: string;
}

/** JSON 校验，返回行号（用于定位错误）。顶层必须是非数组对象。 */
export function validateJson(text: string): JsonValidation {
  try {
    const v: unknown = JSON.parse(text || '{}');
    if (v === null || typeof v !== 'object' || Array.isArray(v)) {
      return { ok: false, line: 1, message: '顶层必须是一个 JSON 对象' };
    }
    return { ok: true };
  } catch (e) {
    const msg = (e as Error).message;
    const pos = Number(/position (\d+)/.exec(msg)?.[1] ?? -1);
    const line = pos >= 0 ? text.slice(0, pos).split('\n').length : 1;
    return { ok: false, line, message: msg };
  }
}

export const PROVIDER_ID_RE = /^[a-z0-9][a-z0-9-_]{0,63}$/;

/** 网络分页校验（P10 瘦身）：代理模式 + 地址 + 重试 0~8 */
export function validateNetworkForm(f: { mode: string; url: string; retry: number }): FieldError[] {
  const errs: FieldError[] = [];
  if (!['direct', 'auto', 'manual'].includes(f.mode))
    errs.push({ field: 'mode', message: '代理模式不合法' });
  if (f.mode === 'manual') {
    if (!f.url.trim()) errs.push({ field: 'url', message: '自定义代理请填写代理地址' });
    else if (!isValidHttpUrl(f.url))
      errs.push({ field: 'url', message: '代理地址必须是 http(s) 开头的合法 URL' });
  }
  if (!isIntInRange(f.retry, 0, 8))
    errs.push({ field: 'retry', message: '重试次数需在 0 ~ 8 之间' });
  return errs;
}

 /** P13 responses：模型协议白名单（chat/responses 放行，其余禁用）。 */
 export const SUPPORTED_MODEL_PROTOCOLS = ['chat', 'responses'] as const;
 export type SupportedModelProtocol = (typeof SUPPORTED_MODEL_PROTOCOLS)[number];

 /** 模型协议是否可选（缺省按 chat 理解）。 */
 export function isSupportedModelProtocol(p: string): boolean {
   return (SUPPORTED_MODEL_PROTOCOLS as readonly string[]).includes(p);
 }

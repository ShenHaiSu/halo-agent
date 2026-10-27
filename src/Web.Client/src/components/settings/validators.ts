// src/Web.Client/src/components/settings/validators.ts

/**
 * 设置页字段级校验器（见 04 §6.2）。
 * 全部纯函数，无依赖，可单测。
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
export interface ModelForm {
  provider: string;
  model: string;
  baseUrl: string;
  apiKey?: string;
  contextWindow: number;
  maxTurns: number;
}
export interface NetworkForm {
  mode: string;
  url: string;
  retry: number;
  headers: Array<{ key: string; value: string }>;
}
/** 模型分页 17 条校验中的模型部分：必填 + 范围 + URL 协议白名单 */
export function validateModelForm(f: ModelForm): FieldError[] {
  const errs: FieldError[] = [];
  if (!f.provider.trim()) errs.push({ field: 'provider', message: '请选择模型服务商' });
  if (!f.model.trim()) errs.push({ field: 'model', message: '请填写模型名（可用探测获取）' });
  if (!f.baseUrl.trim()) errs.push({ field: 'baseUrl', message: '请填写接口地址' });
  else if (!isValidHttpUrl(f.baseUrl))
    errs.push({ field: 'baseUrl', message: '接口地址必须是 http(s) 开头的合法 URL' });
  if (!isIntInRange(f.contextWindow, 8000, 1000000))
    errs.push({ field: 'contextWindow', message: '上下文长度需在 8000 ~ 1000000 之间' });
  if (!isIntInRange(f.maxTurns, 1, 1000))
    errs.push({ field: 'maxTurns', message: '最大轮数需在 1 ~ 1000 之间' });
  return errs;
}
/** 网络分页校验：代理模式 + 地址 + 重试 + 请求头名 */
export function validateNetworkForm(f: NetworkForm): FieldError[] {
  const errs: FieldError[] = [];
  if (!['direct', 'system', 'manual', 'auto'].includes(f.mode))
    errs.push({ field: 'mode', message: '代理模式不合法' });
  if (f.mode === 'manual') {
    if (!f.url.trim()) errs.push({ field: 'url', message: '自定义代理请填写代理地址' });
    else if (!isValidHttpUrl(f.url))
      errs.push({ field: 'url', message: '代理地址必须是 http(s) 开头的合法 URL' });
  }
  if (!isIntInRange(f.retry, 0, 10))
    errs.push({ field: 'retry', message: '重试次数需在 0 ~ 10 之间' });
  f.headers.forEach((h, i) => {
    if (h.key.trim() && !validateHeaderName(h.key.trim()))
      errs.push({ field: `headers[${i}]`, message: `第 ${i + 1} 行请求头名称不合法` });
  });
  return errs;
}

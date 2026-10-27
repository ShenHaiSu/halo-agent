// src/Web.Client/src/components/settings/validators.test.ts
// 设置页字段级校验测试（P5 A-6.2：17 条规则正反例）。
import { describe, expect, it } from 'vitest';
import {
  isValidHttpUrl,
  isIntInRange,
  SKILL_NAME_RE,
  validateHeaderName,
  validateJson,
  validateModelForm,
  validateNetworkForm,
} from './validators';

describe('isValidHttpUrl', () => {
  it('http 通过', () => expect(isValidHttpUrl('http://a.com/v1')).toBe(true));
  it('https 通过', () => expect(isValidHttpUrl('https://a.com')).toBe(true));
  it('无协议拒绝', () => expect(isValidHttpUrl('a.com')).toBe(false));
  it('ftp 拒绝', () => expect(isValidHttpUrl('ftp://a.com')).toBe(false));
  it('空拒绝', () => expect(isValidHttpUrl('')).toBe(false));
});

describe('isIntInRange', () => {
  it('下界通过', () => expect(isIntInRange(8000, 8000, 1000000)).toBe(true));
  it('上界通过', () => expect(isIntInRange(1000000, 8000, 1000000)).toBe(true));
  it('低于下界拒绝', () => expect(isIntInRange(7999, 8000, 1000000)).toBe(false));
  it('小数拒绝', () => expect(isIntInRange(1.5, 0, 10)).toBe(false));
});

describe('SKILL_NAME_RE', () => {
  it('合法名通过', () => expect(SKILL_NAME_RE.test('code-review')).toBe(true));
  it('大写拒绝', () => expect(SKILL_NAME_RE.test('CodeReview')).toBe(false));
  it('下划线拒绝', () => expect(SKILL_NAME_RE.test('code_review')).toBe(false));
  it('数字开头拒绝', () => expect(SKILL_NAME_RE.test('1review')).toBe(false));
});

describe('validateHeaderName', () => {
  it('合法通过', () => expect(validateHeaderName('X-Api-Key')).toBe(true));
  it('空格拒绝', () => expect(validateHeaderName('X Api Key')).toBe(false));
  it('冒号拒绝', () => expect(validateHeaderName('X:Key')).toBe(false));
});

describe('validateJson', () => {
  it('对象通过', () => expect(validateJson('{"a":1}').ok).toBe(true));
  it('数组拒绝', () => expect(validateJson('[1,2]').ok).toBe(false));
  it('语法错误带行号', () => {
    const bad = validateJson('{\n"a":1,\n"b":}');
    expect(bad.ok).toBe(false);
    expect(bad.line).toBeGreaterThanOrEqual(1);
  });
});

describe('validateModelForm', () => {
  const good = {
    provider: 'deepseek',
    model: 'deepseek-chat',
    baseUrl: 'https://api.deepseek.com/v1',
    contextWindow: 128000,
    maxTurns: 200,
  };
  it('合法无错', () => expect(validateModelForm(good)).toEqual([]));
  it('缺服务商', () =>
    expect(validateModelForm({ ...good, provider: '' }).some((e) => e.field === 'provider')).toBe(
      true
    ));
  it('baseUrl 无协议', () =>
    expect(
      validateModelForm({ ...good, baseUrl: 'api.deepseek.com' }).some((e) => e.field === 'baseUrl')
    ).toBe(true));
  it('上下文越界', () =>
    expect(
      validateModelForm({ ...good, contextWindow: 100 }).some((e) => e.field === 'contextWindow')
    ).toBe(true));
  it('轮数越界', () =>
    expect(validateModelForm({ ...good, maxTurns: 0 }).some((e) => e.field === 'maxTurns')).toBe(
      true
    ));
});

describe('validateNetworkForm', () => {
  it('直连合法', () =>
    expect(validateNetworkForm({ mode: 'direct', url: '', retry: 3, headers: [] })).toEqual([]));
  it('manual 无地址拒绝', () =>
    expect(
      validateNetworkForm({ mode: 'manual', url: '', retry: 3, headers: [] }).some(
        (e) => e.field === 'url'
      )
    ).toBe(true));
  it('重试越界拒绝', () =>
    expect(
      validateNetworkForm({ mode: 'direct', url: '', retry: 99, headers: [] }).some(
        (e) => e.field === 'retry'
      )
    ).toBe(true));
  it('请求头非法拒绝', () =>
    expect(
      validateNetworkForm({
        mode: 'direct',
        url: '',
        retry: 3,
        headers: [{ key: '坏 键', value: 'v' }],
      }).some((e) => e.field.startsWith('headers'))
    ).toBe(true));
});

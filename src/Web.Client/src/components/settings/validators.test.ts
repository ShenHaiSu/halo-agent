 // src/Web.Client/src/components/settings/validators.test.ts
 // 字段级校验（网络瘦身：mode/url；行为页：maxTurns/retry/compact/persona）。
 import { describe, expect, it } from 'vitest';
 import {
   isValidHttpUrl,
   isIntInRange,
   SKILL_NAME_RE,
   validateHeaderName,
   validateJson,
   validateNetworkForm,
   validateBehaviorForm,
   isValidPersonaName,
   PROVIDER_ID_RE,
 } from './validators';

describe('isValidHttpUrl', () => {
  it('http 通过', () => expect(isValidHttpUrl('http://a.com/v1')).toBe(true));
  it('https 通过', () => expect(isValidHttpUrl('https://a.com')).toBe(true));
  it('无协议拒绝', () => expect(isValidHttpUrl('a.com')).toBe(false));
  it('ftp 拒绝', () => expect(isValidHttpUrl('ftp://a.com')).toBe(false));
  it('空拒绝', () => expect(isValidHttpUrl('')).toBe(false));
});

describe('isIntInRange', () => {
  it('下界通过', () => expect(isIntInRange(1000, 1000, 2000000)).toBe(true));
  it('上界通过', () => expect(isIntInRange(2000000, 1000, 2000000)).toBe(true));
  it('低于下界拒绝', () => expect(isIntInRange(999, 1000, 2000000)).toBe(false));
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

describe('PROVIDER_ID_RE', () => {
  it('合法通过', () => expect(PROVIDER_ID_RE.test('deepseek')).toBe(true));
  it('大写拒绝', () => expect(PROVIDER_ID_RE.test('DeepSeek')).toBe(false));
   it('空拒绝', () => expect(PROVIDER_ID_RE.test('')).toBe(false));
 });
 
 describe('validateNetworkForm', () => {
   it('直连合法', () =>
     expect(validateNetworkForm({ mode: 'direct', url: '' })).toEqual([]));
   it('跟随系统合法', () =>
     expect(validateNetworkForm({ mode: 'system', url: '' })).toEqual([]));
   it('旧 auto 拒绝（已改为 system/direct/manual）', () =>
     expect(
       validateNetworkForm({ mode: 'auto', url: '' }).some(
         (e) => e.field === 'mode'
       )
     ).toBe(true));
   it('manual 无地址拒绝', () =>
     expect(
       validateNetworkForm({ mode: 'manual', url: '' }).some(
         (e) => e.field === 'url'
       )
     ).toBe(true));
 });
 
 describe('validateBehaviorForm', () => {
   const ok = { maxTurns: 200, retry: 0, compactKeepTail: 2, persona: '' };
   it('默认合法', () => expect(validateBehaviorForm(ok)).toEqual([]));
   it('0=不限通过', () =>
     expect(validateBehaviorForm({ ...ok, maxTurns: 0 })).toEqual([]));
   it('500 通过/501 拒绝', () => {
     expect(validateBehaviorForm({ ...ok, maxTurns: 500 })).toEqual([]);
     expect(
       validateBehaviorForm({ ...ok, maxTurns: 501 }).some((e) => e.field === 'maxTurns')
     ).toBe(true);
   });
   it('compact 1~10 通过/0、11 拒绝', () => {
     expect(validateBehaviorForm({ ...ok, compactKeepTail: 1 })).toEqual([]);
     expect(validateBehaviorForm({ ...ok, compactKeepTail: 10 })).toEqual([]);
     expect(
       validateBehaviorForm({ ...ok, compactKeepTail: 0 }).some((e) => e.field === 'compactKeepTail')
     ).toBe(true);
     expect(
       validateBehaviorForm({ ...ok, compactKeepTail: 11 }).some((e) => e.field === 'compactKeepTail')
     ).toBe(true);
   });
   it('retry 8 通过/9 拒绝', () => {
     expect(validateBehaviorForm({ ...ok, retry: 8 })).toEqual([]);
     expect(
       validateBehaviorForm({ ...ok, retry: 9 }).some((e) => e.field === 'retry')
     ).toBe(true);
   });
   it('persona 非法字符拒绝', () => {
     expect(isValidPersonaName('a/b')).toBe(false);
     expect(
       validateBehaviorForm({ ...ok, persona: 'a/b' }).some((e) => e.field === 'persona')
     ).toBe(true);
   });
 });

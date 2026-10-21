/** need22-F01：marker 语法单测（frontend/01 §3.4 M1~M12）。 */
import { describe, expect, it } from 'vitest';
import {
  hasSkillMarker,
  parseSkillMarkers,
  skillMarker,
  splitBySkillMarkers,
  stripSkillMarkers,
  uniqueSkillRefs,
} from '@shared/utils/skillMarker';

describe('skillMarker 生成与解析（M1~M7）', () => {
  it('M1 skillMarker("global","foo") === "[[skill:global/foo]]"', () => {
    expect(skillMarker('global', 'foo')).toBe('[[skill:global/foo]]');
    expect(skillMarker('project', 'bar')).toBe('[[skill:project/bar]]');
  });

  it('M2 解析合法 marker ⇒ 1 项且 scope/name 正确', () => {
    const refs = parseSkillMarkers('帮我改代码 [[skill:global/foo]] 谢谢');
    expect(refs.length).toBe(1);
    expect(refs[0].scope).toBe('global');
    expect(refs[0].name).toBe('foo');
    expect(refs[0].marker).toBe('[[skill:global/foo]]');
  });

  it('M3 解析非法 scope [[skill:xxx/foo]] ⇒ 空数组', () => {
    expect(parseSkillMarkers('[[skill:xxx/foo]]')).toEqual([]);
  });

  it('M4 解析非法 name [[skill:global/Foo Bar]] ⇒ 空数组', () => {
    expect(parseSkillMarkers('[[skill:global/Foo Bar]]')).toEqual([]);
  });

  it('M5 name 长度 1（a）不匹配', () => {
    expect(parseSkillMarkers('[[skill:global/a]]')).toEqual([]);
  });

  it('M6 name 长度边界：47 匹配；49 不匹配（总长上限 48 = 冻结正则 [a-z0-9][a-z0-9-_]{1,47}）', () => {
    // 冻结正则与后端 NormalizeAndValidateName 同源：^[a-z0-9][a-z0-9-_]{1,47}$ ⇒ 总长 2..48
    const n47 = 'a'.repeat(47);
    const n48 = 'a'.repeat(48);
    const n49 = 'a'.repeat(49);
    expect(parseSkillMarkers(`[[skill:global/${n47}]]`).length).toBe(1);
    expect(parseSkillMarkers(`[[skill:global/${n48}]]`).length).toBe(1);
    expect(parseSkillMarkers(`[[skill:global/${n49}]]`)).toEqual([]);
  });

  it('M7 混排文本解析出 2 项且 index 正确', () => {
    const text = 'a[[skill:global/foo]]b[[skill:project/bar]]c';
    const refs = parseSkillMarkers(text);
    expect(refs.length).toBe(2);
    expect(refs[0].index).toBe(1);
    expect(refs[1].index).toBe(22);
    expect(refs[1].scope).toBe('project');
    expect(refs[1].name).toBe('bar');
  });
});

describe('splitBySkillMarkers / strip（M8~M11）', () => {
  it('M8 切分后拼回 === 原串（含首尾空白）', () => {
    const text = '  帮我改代码 [[skill:global/foo]] 谢谢  ';
    const parts = splitBySkillMarkers(text);
    const rebuilt = parts.map((p) => (p.type === 'text' ? p.text : p.marker)).join('');
    expect(rebuilt).toBe(text);
  });

  it('M9 相邻 marker ⇒ 中间无空 text 段', () => {
    const parts = splitBySkillMarkers('a[[skill:global/foo]][[skill:project/bar]]b');
    const mids = parts.filter((p) => p.type === 'text' && p.text === '');
    expect(mids.length).toBe(0);
    expect(parts.map((p) => p.type)).toEqual(['text', 'skill', 'skill', 'text']);
  });

  it('M10 stripSkillMarkers 只删 marker，空格保留', () => {
    expect(stripSkillMarkers('a [[skill:global/foo]] b')).toBe('a  b');
  });

  it('M11 hasSkillMarker 有/无分别 true/false', () => {
    expect(hasSkillMarker('a [[skill:global/foo]] b')).toBe(true);
    expect(hasSkillMarker('普通文本')).toBe(false);
    expect(hasSkillMarker('[[skill:xxx/foo]]')).toBe(false);
  });
});

describe('uniqueSkillRefs（M12）', () => {
  it('M12 同 (scope,name) 多次出现只留 1 项', () => {
    const text = '[[skill:global/foo]] 和 [[skill:global/foo]] 与 [[skill:project/bar]]';
    const refs = uniqueSkillRefs(text);
    expect(refs.length).toBe(2);
    expect(refs[0]).toEqual({ scope: 'global', name: 'foo' });
    expect(refs[1]).toEqual({ scope: 'project', name: 'bar' });
  });
});

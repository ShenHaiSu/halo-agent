import { afterEach, describe, expect, it } from 'vitest';
import { clearToolArgsCache, execCommandOf, execShortOf, parseToolArgs, stringArg } from './toolArgs';

const cardOf = (argsJson: string, callId = 'c1') => ({
  argsJson,
  callId,
});

afterEach(() => {
  clearToolArgsCache();
});

describe('parseToolArgs', () => {
  it('解析合法 JSON', () => {
    expect(parseToolArgs(cardOf('{"file_path":"a.txt"}'))).toEqual({ file_path: 'a.txt' });
  });

  it('非法 JSON 回空对象不抛错', () => {
    expect(parseToolArgs(cardOf('{"file_path":'))).toEqual({});
  });

  it('同键命中缓存返回同一引用', () => {
    const a = parseToolArgs(cardOf('{"x":1}'));
    const b = parseToolArgs(cardOf('{"x":1}'));
    expect(b).toBe(a);
  });

  it('不同调用标识隔离缓存', () => {
    const a = parseToolArgs(cardOf('{"x":1}', 'c1'));
    const b = parseToolArgs(cardOf('{"x":1}', 'c2'));
    expect(b).not.toBe(a);
    expect(b).toEqual(a);
  });

  it('清空后重新解析', () => {
    const a = parseToolArgs(cardOf('{"x":1}'));
    clearToolArgsCache();
    const b = parseToolArgs(cardOf('{"x":1}'));
    expect(b).not.toBe(a);
    expect(b).toEqual(a);
  });
});

describe('stringArg', () => {
  it('按优先级取首个非空串', () => {
    expect(stringArg({ a: '', b: 'x', c: 'y' }, 'a', 'b', 'c')).toBe('x');
  });

  it('缺省回空串', () => {
    expect(stringArg({}, 'a')).toBe('');
  });
});

describe('execCommandOf/execShortOf', () => {
  it('缺省命令回占位', () => {
    expect(execCommandOf({})).toBe('(空命令)');
  });

  it('长命令单行截断 80ch', () => {
    const cmd = `echo ${'x'.repeat(200)}`;
    const s = execShortOf(cmd);
    expect(s.length).toBeLessThanOrEqual(81);
    expect(s.endsWith('…')).toBe(true);
  });

  it('空白折叠为单空格', () => {
    expect(execShortOf('echo   a\n  b')).toBe('echo a b');
  });
});

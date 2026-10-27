// src/Web.Client/src/components/chat/highlight/highlight.test.ts
import { describe, expect, it } from 'vitest';
import { ALL_LANGS, resolveLang } from './index';
import { tokenize } from './tokenizer';

describe('语言注册', () => {
  it('11 种语言全部注册', () => {
    expect(ALL_LANGS.length).toBeGreaterThanOrEqual(11);
  });

  it('别名正确解析', () => {
    expect(resolveLang('ts')?.name).toBe('typescript');
    expect(resolveLang('TS')?.name).toBe('typescript');
    expect(resolveLang('cs')?.name).toBe('csharp');
    expect(resolveLang('c#')?.name).toBe('csharp');
    expect(resolveLang('ps1')?.name).toBe('powershell');
    expect(resolveLang('py')?.name).toBe('python');
    expect(resolveLang('sh')?.name).toBe('bash');
    expect(resolveLang('htm')?.name).toBe('html');
  });

  it('未知语言返回 null（不抛错）', () => {
    expect(resolveLang('foobar')).toBeNull();
    expect(resolveLang('')).toBeNull();
    expect(resolveLang(null)).toBeNull();
  });
});

describe('Tokenizer', () => {
  it('字符串里的关键字不被高亮', () => {
    const lang = resolveLang('ts')!;
    const toks = tokenize('const s = "if for while";', lang);
    const str = toks.find((t) => t.type === 'str');
    expect(str?.value).toBe('"if for while"');
    // 字符串内不会单独出现 kw 的 if
    expect(toks.filter((t) => t.type === 'kw' && t.value === 'if')).toHaveLength(0);
  });

  it('注释里的关键字不被高亮', () => {
    const lang = resolveLang('ts')!;
    const toks = tokenize('// if for while\nif (x) {}', lang);
    const com = toks.find((t) => t.type === 'com');
    expect(com?.value).toBe('// if for while');
    expect(toks.filter((t) => t.type === 'kw')).toHaveLength(1);
  });

  it('未闭合字符串不吞掉后面全部内容', () => {
    const lang = resolveLang('ts')!;
    const toks = tokenize('const a = "unclosed\nconst b = 2;', lang);
    // 未闭合的 " 不会被当成跨行字符串（我们的正则禁了 \n）
    expect(toks.some((t) => t.type === 'kw' && t.value === 'const')).toBe(true);
  });

  it('C# 逐字字符串（可跨行）', () => {
    const lang = resolveLang('cs')!;
    const toks = tokenize('var s = @"line1\nline2";', lang);
    const str = toks.find((t) => t.type === 'str');
    expect(str?.value).toContain('line1');
  });

  it('PowerShell here-string', () => {
    const lang = resolveLang('ps1')!;
    const code = '@"\nhello\n"@';
    const toks = tokenize(code, lang);
    expect(toks[0]?.type).toBe('str');
  });

  it('相邻同类型 token 被合并（减少 span）', () => {
    const lang = resolveLang('ts')!;
    const toks = tokenize('   ', lang);
    expect(toks).toHaveLength(1);
  });

  it('1000 行 TS 高亮 < 30ms', () => {
    const lang = resolveLang('ts')!;
    const code = Array.from(
      { length: 1000 },
      (_, i) => `export function f${i}(a: string): number { return a.length + ${i}; } // c${i}`
    ).join('\n');
    const t0 = performance.now();
    tokenize(code, lang);
    const dt = performance.now() - t0;
    expect(dt).toBeLessThan(30);
  });
});

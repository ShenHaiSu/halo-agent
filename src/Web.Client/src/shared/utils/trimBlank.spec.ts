/** 首尾空白裁剪单测：头部清空 / 尾部折叠为 1 个换行 / 中间与特殊空格保留。 */
import { describe, expect, it } from 'vitest';
import { trimBlankEdges } from '@shared/utils/trimBlank';

const BOM = String.fromCharCode(0xfeff);
const FULL_WIDTH_SPACE = String.fromCharCode(0x3000);
const NBSP = String.fromCharCode(0x00a0);
const f = trimBlankEdges;

describe('trimBlankEdges 首尾空白裁剪', () => {
  it('头部清空 + 尾部折叠为 1 个换行', () => {
    expect(f('\n\nabc\n\n')).toBe('abc\n');
  });

  it('无尾换行不添加', () => {
    const out = f('abc');
    expect(out).toBe('abc');
    expect(out.length).toBe(3);
  });

  it('单个尾换行原样保留', () => {
    expect(f('abc\n')).toBe('abc\n');
  });

  it('多个尾换行折叠为 1 个', () => {
    expect(f('abc\n\n\n')).toBe('abc\n');
  });

  it('首尾行内空白删除', () => {
    expect(f('  abc  ')).toBe('abc');
  });

  it('末行空白先删再折叠（步序敏感）', () => {
    expect(f('abc\n   ')).toBe('abc\n');
  });

  it('中间空行保留', () => {
    expect(f('a\n\nb')).toBe('a\n\nb');
  });

  it('首行前导空格吃掉、次行缩进保留', () => {
    expect(f('  - item\n  - item2')).toBe('- item\n  - item2');
  });

  it('全空白输入返回空串（不得输出换行）', () => {
    expect(f('\n\n \t \n')).toBe('');
    expect(f('\n')).toBe('');
    expect(f('\n\n')).toBe('');
  });

  it('末尾全角空格属内容，原样返回', () => {
    expect(f('前文\n' + FULL_WIDTH_SPACE + FULL_WIDTH_SPACE)).toBe('前文\n' + FULL_WIDTH_SPACE + FULL_WIDTH_SPACE);
  });

  it('NBSP 属内容，原样返回', () => {
    expect(f('abc' + NBSP)).toBe('abc' + NBSP);
  });

  it('BOM 删除 + 尾换行保留 1 个', () => {
    expect(f(BOM + '你好\n')).toBe('你好\n');
  });

  it('CRLF 尾部折叠，生成统一为 LF', () => {
    expect(f('a\r\n\r\n')).toBe('a\n');
  });

  it('幂等 + 空安全', () => {
    const inputs = [
      '\n\nabc\n\n',
      'abc',
      'abc\n',
      'abc\n\n\n',
      '  abc  ',
      'abc\n   ',
      'a\n\nb',
      '  - item\n  - item2',
      '\n\n \t \n',
      '前文\n' + FULL_WIDTH_SPACE,
      'abc' + NBSP,
      BOM + '你好\n',
      'a\r\n\r\n',
      '```\ncode\n```\n\n',
    ];
    for (const x of inputs) {
      expect(f(f(x))).toBe(f(x));
    }
    expect(f('')).toBe('');
    expect(f(null)).toBe('');
    expect(f(undefined)).toBe('');
  });
});

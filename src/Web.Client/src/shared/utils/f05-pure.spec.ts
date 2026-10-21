/** need21-F05: 纯函数单测（estTokens / formatSmartBytes / sessionTitle）。 */
import { describe, expect, it } from 'vitest';
import { estTokens, estimateUtf8Bytes } from '@shared/utils/tokenEstimate';
import { formatSmartBytes, formatSmartBytesShort, formatTokenShort } from '@shared/utils/formatSmartBytes';
import { instantTitleFromUserText, parseTitleResult, refineTitleLocal } from '@shared/utils/sessionTitle';

describe('estTokens（与 znaide app.rs 同口径：ASCII/4 上取整，非 ASCII 按 1）', () => {
  it('空串=0', () => {
    expect(estTokens('')).toBe(0);
  });
  it('纯 ASCII 按 4 上取整', () => {
    expect(estTokens('abcd')).toBe(1);
    expect(estTokens('abcde')).toBe(2);
  });
  it('中文按字计', () => {
    expect(estTokens('你好世界')).toBe(4);
  });
  it('中英混合', () => {
    // 4 ASCII -> 1 + 2 中文 = 3
    expect(estTokens('abcd你好')).toBe(3);
  });
  it('estimateUtf8Bytes 与 TextEncoder 同单位', () => {
    expect(estimateUtf8Bytes('')).toBe(0);
    expect(estimateUtf8Bytes('abc')).toBe(3);
    expect(estimateUtf8Bytes('你')).toBe(3);
  });
});

describe('formatSmartBytes', () => {
  it('小字节精确值', () => {
    expect(formatSmartBytes(512)).toBe('512 Bytes');
  });
  it('KB 段人类可读在前精确值在后', () => {
    expect(formatSmartBytes(2048)).toContain('KB = 2,048 Bytes');
  });
  it('short/token 短格式', () => {
    expect(formatSmartBytesShort(512)).toBe('512 B');
    expect(formatTokenShort(1500)).toBe('1.5K');
    expect(formatTokenShort(2_500_000)).toBe('2.50M');
  });
});

describe('sessionTitle 两阶段标题', () => {
  it('T1 取首句前 5 字', () => {
    expect(instantTitleFromUserText('你好世界这是一个测试。后面忽略')).toBe('你好世界这');
  });
  it('空文本回退新建会话', () => {
    expect(instantTitleFromUserText('')).toBe('新建会话');
  });
  it('parseTitleResult 去噪钳 12 字', () => {
    expect(parseTitleResult('标题：我的测试标题')).toBe('我的测试标题');
    expect(parseTitleResult('')).toBe('');
  });
  it('refineTitleLocal 非法返回空', () => {
    expect(refineTitleLocal('', '')).toBe('');
    expect(refineTitleLocal('这是一个很长的助手回复内容用于标题', '')).not.toBe('');
  });
});

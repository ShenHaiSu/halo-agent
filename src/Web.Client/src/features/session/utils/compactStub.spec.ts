import { describe, expect, it } from 'vitest';
import { dividerMarkdown, isCompactStub, LEGACY_STUB_PREFIX, metaOf, stubLabel, summaryBodyOf } from './compactStub';
import type { UnifiedChatMessage } from '@shared/bridge/protocol/domain';

function msg(partial: Partial<UnifiedChatMessage> & { id: string }): UnifiedChatMessage {
  return { role: 'user', content: '', createdAtUtc: '', ...partial } as UnifiedChatMessage;
}

describe('P007 compactStub 判定与文案', () => {
  it('F1：kind 判定只认 kind', () => {
    expect(isCompactStub(msg({ id: 's1', kind: 'compact_stub', content: '摘要' }))).toBe(true);
    expect(isCompactStub(msg({ id: 'u1', kind: 'chat', content: 'hi' }))).toBe(false);
    expect(isCompactStub(msg({ id: 'u2', content: 'hi' }))).toBe(false);
    expect(isCompactStub(null)).toBe(false);
    expect(isCompactStub(undefined)).toBe(false);
  });

  it('F2：老桩无 kind 时按前缀兼容', () => {
    expect(isCompactStub(msg({ id: 's2', content: `${LEGACY_STUB_PREFIX}】旧摘要` }))).toBe(true);
    expect(isCompactStub(msg({ id: 'u3', content: '普通提问' }))).toBe(false);
    expect(isCompactStub(msg({ id: 'a1', role: 'assistant', content: `${LEGACY_STUB_PREFIX}】x` }))).toBe(false);
  });

  it('F3：摘要剥离只去掉旧前缀首行', () => {
    expect(summaryBodyOf(msg({ id: 's3', content: `${LEGACY_STUB_PREFIX}】标题\n正文第一行\n正文第二行` }))).toBe('正文第一行\n正文第二行');
    expect(summaryBodyOf(msg({ id: 's4', content: '新式摘要正文' }))).toBe('新式摘要正文');
    expect(summaryBodyOf(null)).toBe('');
  });

  it('F4：meta 解析合法与脏 JSON', () => {
    expect(metaOf(msg({ id: 's5', metaJson: '{"removedCount":42,"backupFile":"bak.jsonl"}' }))).toEqual({
      removedCount: 42,
      backupFile: 'bak.jsonl',
    });
    expect(metaOf(msg({ id: 's6', metaJson: '{broken' }))).toEqual({});
    expect(metaOf(msg({ id: 's7' }))).toEqual({});
  });

  it('F5：pill 文案条数口径', () => {
    expect(stubLabel(42)).toBe('上下文压缩 · 42 条 → 摘要');
    expect(stubLabel(0)).toBe('上下文压缩');
    expect(stubLabel(null)).toBe('上下文压缩');
    expect(stubLabel(undefined)).toBe('上下文压缩');
  });

  it('F6：导出分隔片段含分隔线与引用行', () => {
    const md = dividerMarkdown('上下文压缩 · 42 条 → 摘要', '摘要正文', 'messages.compact-bak-x.jsonl');
    expect(md).toContain('---');
    expect(md).toContain('> **上下文压缩 · 42 条 → 摘要**');
    expect(md).toContain('摘要正文');
    expect(md).toContain('messages.compact-bak-x.jsonl');
    const plain = dividerMarkdown('上下文压缩', '正文', null);
    expect(plain).toContain('> **上下文压缩**');
    expect(plain).not.toContain('备份');
  });
});

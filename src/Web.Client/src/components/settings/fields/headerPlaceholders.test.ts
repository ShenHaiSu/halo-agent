// src/Web.Client/src/components/settings/fields/headerPlaceholders.test.ts
import { describe, expect, it } from 'vitest';
import {
  SESSION_HASH_TOKEN,
  SESSION_ID_TOKEN,
  headerPlaceholderTooltip,
} from './headerPlaceholders';

describe('headerPlaceholders', () => {
  it('两 token 字符串等于后端 HeaderTemplate', () => {
    expect(SESSION_ID_TOKEN).toBe('{{session_id}}');
    expect(SESSION_HASH_TOKEN).toBe('{{session_hash}}');
  });

  it('tooltip 文案含两占位 + 跳过语义', () => {
    const t = headerPlaceholderTooltip();
    expect(t).toContain('{{session_id}}');
    expect(t).toContain('{{session_hash}}');
    expect(t).toContain('整条跳过');
    expect(t).toContain('大小写不敏感');
  });
});

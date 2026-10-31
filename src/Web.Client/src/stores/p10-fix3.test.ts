// P10-fix3 F1-F7 前端回归（06-测试矩阵）。
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useSessionStore } from './session';
import { transport } from '@/transport';
import { parseSlashCommand, unquoteArgs } from '@/utils/slash';

beforeEach(() => {
  setActivePinia(createPinia());
  vi.restoreAllMocks();
});

describe('slash parse (F1-F4)', () => {
  it('F1: /cwd 带参命中白名单', () => {
    expect(parseSlashCommand('/cwd C:\\work')).toEqual({ name: 'cwd', args: 'C:\\work' });
  });
  it('F2: 未知斜杠回落普通聊天（/usr/bin/ls …）', () => {
    expect(parseSlashCommand('/usr/bin/ls 是什么意思')).toBeNull();
  });
  it('F3: /cwd 空参解析为空', () => {
    expect(parseSlashCommand('/cwd')).toEqual({ name: 'cwd', args: '' });
    expect(parseSlashCommand('/cwd   ')).toEqual({ name: 'cwd', args: '' });
  });
  it('F4: 去引号', () => {
    expect(unquoteArgs('"C:\\My Work"')).toBe('C:\\My Work');
    expect(unquoteArgs("'C:\\My Work'")).toBe('C:\\My Work');
    expect(unquoteArgs('  C:\\a  ')).toBe('C:\\a');
  });
  it('非斜杠返回 null', () => {
    expect(parseSlashCommand('hello')).toBeNull();
    expect(parseSlashCommand('/cwd C:\\work')?.name).toBe('cwd');
  });
});

describe('session.setCwd (F7)', () => {
  it('成功用引擎返回的规范路径回填', async () => {
    const s = useSessionStore();
    s.cwd = 'old';
    vi.spyOn(transport, 'request').mockResolvedValue({ cwd: 'C:\\norm\\a' } as never);
    await s.setCwd('C:\\a/../a');
    expect(s.cwd).toBe('C:\\norm\\a');
    expect(transport.request).toHaveBeenCalledWith('session.setCwd', { path: 'C:\\a/../a' });
  });
  it('失败回滚 prev', async () => {
    const s = useSessionStore();
    s.cwd = 'old';
    vi.spyOn(transport, 'request').mockRejectedValue(new Error('nope'));
    await expect(s.setCwd('C:\\new')).rejects.toThrow('nope');
    expect(s.cwd).toBe('old');
  });
});

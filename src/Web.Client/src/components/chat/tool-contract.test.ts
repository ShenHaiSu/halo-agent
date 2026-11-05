import { describe, expect, it } from 'vitest';
import { deriveState, toolMeta, toolStatusText } from './toolMeta';
import { summarizeArgs } from './toolArgs';

const REAL_TOOLS = [
  'list_directory',
  'read_file',
  'write_file',
  'edit',
  'glob',
  'grep_search',
  'run_shell_command',
  'memory_write',
  'memory_read',
  'web_fetch',
] as const;

describe('toolMeta maps all real tools', () => {
  for (const name of REAL_TOOLS) {
    it(`${name} has label and icon`, () => {
      const meta = toolMeta(name);
      expect(meta.label).not.toBe(name);
      expect(meta.icon).not.toBe('info');
    });
  }

  it('ghost entries are gone', () => {
    // 后端不存在的工具名应落到原名兜底
    expect(toolMeta('delete_file').label).toBe('delete_file');
    expect(toolMeta('todo_write').label).toBe('todo_write');
    expect(toolMeta('web_search').label).toBe('web_search');
  });
});

describe('toolArgs summarizes real tools', () => {
  it('run_shell_command summarizes command', () => {
    expect(summarizeArgs('run_shell_command', JSON.stringify({ command: 'ls -la' }))).toBe('ls -la');
  });
  it('edit summarizes path', () => {
    expect(summarizeArgs('edit', JSON.stringify({ path: 'a.txt' }))).toBe('a.txt');
  });
  it('grep_search prefers pattern', () => {
    expect(summarizeArgs('grep_search', JSON.stringify({ pattern: 'TODO', path: '.' }))).toBe('TODO');
  });
});

describe('skipped state renders', () => {
  it("deriveState({skipped:true,status:'done'}) === 'skipped'", () => {
    expect(
      deriveState({ id: 'c1', name: 'read_file', arguments: '{}', status: 'done', skipped: true }, false)
    ).toBe('skipped');
  });
  it("toolStatusText('skipped') === '未执行'", () => {
    expect(toolStatusText('skipped')).toBe('未执行');
  });
  it('finished event without skipped is normal', () => {
    expect(
      deriveState({ id: 'c1', name: 'read_file', arguments: '{}', status: 'done' }, false)
    ).toBe('done');
  });
});

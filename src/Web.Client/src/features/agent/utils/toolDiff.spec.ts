import { describe, expect, it } from 'vitest';
import { buildDiffLines, countDiffLines } from './toolDiff';

describe('buildDiffLines', () => {
  it('edit 按旧串拼删、按新串拼增', () => {
    const lines = buildDiffLines({ old_string: 'a\nb', new_string: 'c' }, 'edit_file');
    expect(lines).toEqual([
      { kind: 'del', text: 'a' },
      { kind: 'del', text: 'b' },
      { kind: 'add', text: 'c' },
    ]);
  });

  it('write 按正文拼增', () => {
    const lines = buildDiffLines({ content: 'x\ny' }, 'write_file');
    expect(lines).toEqual([
      { kind: 'add', text: 'x' },
      { kind: 'add', text: 'y' },
    ]);
  });

  it('缺省正文回单空行（与旧逻辑一致）', () => {
    expect(buildDiffLines({}, 'write_file')).toEqual([{ kind: 'add', text: '' }]);
  });
});

describe('countDiffLines', () => {
  it('增删计数与行集一致', () => {
    const lines = buildDiffLines({ old_string: 'a\nb', new_string: 'c' }, 'edit_file');
    expect(countDiffLines(lines)).toEqual({ added: 1, removed: 2 });
  });
});

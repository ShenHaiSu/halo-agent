/** need22-F01：候选池注册表单测（frontend/01 §2.4 R1~R10）。 */
import { describe, expect, it } from 'vitest';
import {
  filterSlashCandidates,
  getSlashProviders,
  listSlashProviderIds,
  registerSlashProvider,
  unregisterSlashProvider,
  type SlashCandidate,
} from '@shared/utils/slashRegistry';

function cand(partial: Partial<SlashCandidate> & { id: string; label: string }): SlashCandidate {
  return { providerId: 'skill', ...partial };
}

describe('slashRegistry 注册表（R1~R3）', () => {
  it('R1 注册两个 provider（order 2 / 1）⇒ order 升序', () => {
    registerSlashProvider({ id: 'zzz-order2', order: 2, load: () => [] });
    registerSlashProvider({ id: 'aaa-order1', order: 1, load: () => [] });
    const ids = getSlashProviders().map((p) => p.id);
    expect(ids.indexOf('aaa-order1')).toBeLessThan(ids.indexOf('zzz-order2'));
    unregisterSlashProvider('zzz-order2');
    unregisterSlashProvider('aaa-order1');
  });

  it('R2 同名 provider 重复注册 ⇒ 只有 1 条且后者覆盖前者', () => {
    registerSlashProvider({ id: 'dup', order: 5, load: () => [{ id: 'a', providerId: 'dup', label: 'a' }] });
    registerSlashProvider({ id: 'dup', order: 5, load: () => [{ id: 'b', providerId: 'dup', label: 'b' }] });
    const list = getSlashProviders().filter((p) => p.id === 'dup');
    expect(list.length).toBe(1);
    const loaded = list[0].load({ workspaceId: '', query: '' });
    expect((loaded as SlashCandidate[])[0].id).toBe('b');
    unregisterSlashProvider('dup');
  });

  it('R3 注销不存在的 id 不抛', () => {
    expect(() => unregisterSlashProvider('__not_exist__')).not.toThrow();
  });

  it('listSlashProviderIds 返回已注册 id', () => {
    registerSlashProvider({ id: 'list-probe', load: () => [] });
    expect(listSlashProviderIds()).toContain('list-probe');
    unregisterSlashProvider('list-probe');
  });
});

describe('filterSlashCandidates（R4~R10）', () => {
  const list: SlashCandidate[] = [
    cand({ id: 'skill:global:foo', scope: 'global', prefix: 'global/', label: 'global/foo', title: 'Foo 工具' }),
    cand({ id: 'skill:project:foobar', scope: 'project', prefix: 'project/', label: 'project/foobar' }),
    cand({ id: 'skill:global:bar', scope: 'global', prefix: 'global/', label: 'global/bar', description: '描述词在这里' }),
    cand({ id: 'skill:project:xxx-yyy', scope: 'project', prefix: 'project/', label: 'project/xxx-yyy' }),
  ];

  it('R4 空 query ⇒ 原样返回、顺序不变', () => {
    const out = filterSlashCandidates(list, '');
    expect(out.length).toBe(list.length);
    expect(out.map((c) => c.id)).toEqual(list.map((c) => c.id));
    expect(filterSlashCandidates(list, '   ').map((c) => c.id)).toEqual(list.map((c) => c.id));
  });

  it('R5 query="project/" ⇒ 只剩 scope === project 的项', () => {
    const out = filterSlashCandidates(list, 'project/');
    expect(out.length).toBe(2);
    expect(out.every((c) => c.scope === 'project')).toBe(true);
  });

  it('R6 query="foo" ⇒ 前缀命中排在包含命中之前', () => {
    const probe = [
      cand({ id: 'skill:global:foobar', scope: 'global', label: 'global/foobar' }), // 包含命中
      cand({ id: 'skill:global:foo-tools', scope: 'global', label: 'foo-tools' }), // 前缀命中
    ];
    const out = filterSlashCandidates(probe, 'foo');
    expect(out.map((c) => c.id)).toEqual(['skill:global:foo-tools', 'skill:global:foobar']);
  });

  it('R7 query="zzz" ⇒ 空数组', () => {
    expect(filterSlashCandidates(list, 'zzz')).toEqual([]);
  });

  it('R8 query="global/" ⇒ scope 过滤生效，global 项保留', () => {
    const out = filterSlashCandidates(list, 'global/');
    expect(out.length).toBe(2);
    expect(out.every((c) => c.scope === 'global')).toBe(true);
    expect(out.map((c) => c.id)).toContain('skill:global:foo');
  });

  it('R9 query="描述词" ⇒ 命中 description 的候选', () => {
    const out = filterSlashCandidates(list, '描述词');
    expect(out.map((c) => c.id)).toEqual(['skill:global:bar']);
  });

  it('R10 非法 scope 前缀 "xxx/" ⇒ 不按作用域过滤，退化为普通文本匹配', () => {
    const probe = [...list, cand({ id: 'skill:project:xxx-yyy2', scope: 'project', label: 'xxx/yyy2' })];
    const out = filterSlashCandidates(probe, 'xxx/');
    // 未按 scope 过滤（project 项仍在），但 "xxx/" 只作为普通文本匹配 label
    expect(out.map((c) => c.id)).toEqual(['skill:project:xxx-yyy2']);
  });
});

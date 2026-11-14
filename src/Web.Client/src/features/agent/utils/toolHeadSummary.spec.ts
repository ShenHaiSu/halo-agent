import { describe, expect, it } from 'vitest';
import { summarizeToolHead } from './toolHeadSummary';

describe('summarizeToolHead', () => {
  it('skill_tool list 展示 action/scope', () => {
    expect(summarizeToolHead({ action: 'list', scope: 'project' }, '{"action":"list","scope":"project"}')).toBe(
      'action=list · scope=project'
    );
  });

  it('goal_tool progress 展示 action/title', () => {
    expect(summarizeToolHead({ action: 'progress', percent: 50, title: 'M1' }, '{"action":"progress","percent":50}')).toContain(
      'action=progress'
    );
  });

  it('未知键回落 {k:v}', () => {
    expect(summarizeToolHead({ foo: 'bar' }, '{"foo":"bar"}')).toBe('{foo:bar}');
  });

  it('大字段 content 不进头', () => {
    const s = summarizeToolHead({ action: 'create', name: 'x', content: 'a'.repeat(5000) }, '{}');
    expect(s).toContain('action=create');
    expect(s).not.toContain('a'.repeat(50));
  });

  it('空参返回空（调用方兜底无参数）', () => {
    expect(summarizeToolHead({}, '{}')).toBe('');
  });

  it('半包 JSON 回落 raw 切片', () => {
    expect(summarizeToolHead({}, '{"action":"li')).toBe('{"action":"li');
  });

  it('超长截断 120ch', () => {
    const s = summarizeToolHead({ task: 't'.repeat(200) }, '{}');
    expect(s.length).toBeLessThanOrEqual(121);
    expect(s.endsWith('…')).toBe(true);
  });
});

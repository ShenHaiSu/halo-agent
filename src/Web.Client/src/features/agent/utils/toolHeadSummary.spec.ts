import { describe, expect, it } from 'vitest';
import { summarizeToolHead, toolHeadText } from './toolHeadSummary';

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

describe('toolHeadText', () => {
  it('运行态文件读写带路径', () => {
    expect(toolHeadText('read_file', { file_path: 'a.txt' }, '', 'running')).toBe('正在读取 a.txt');
    expect(toolHeadText('write_file', { file_path: 'b.txt' }, '', 'running')).toBe('正在写入 b.txt');
    expect(toolHeadText('edit_file', { file_path: 'c.txt' }, '', 'running')).toBe('正在修改 c.txt');
  });

  it('完成态文件读写带路径', () => {
    expect(toolHeadText('read_file', { file_path: 'a.txt' }, '', 'done')).toBe('已读取 a.txt');
    expect(toolHeadText('write_file', { path: 'b.txt' }, '', 'done')).toBe('写入 b.txt');
  });

  it('执行类运行态拼短命令、完成态直出', () => {
    expect(toolHeadText('exec_command', {}, 'pnpm build', 'running')).toBe('正在运行 pnpm build');
    expect(toolHeadText('exec_command', {}, 'pnpm build', 'done')).toBe('pnpm build');
  });

  it('读图分支三态文案', () => {
    expect(toolHeadText('view_image', { path: 'x.png' }, '', 'running')).toBe('正在查看 x.png');
    expect(toolHeadText('view_image', { path: 'x.png' }, '', 'done')).toBe('查看 x.png');
  });

  it('通用工具回落摘要、无参兜底', () => {
    expect(toolHeadText('skill_tool', { action: 'list', scope: 'project' }, '', 'done')).toContain('action=list');
    expect(toolHeadText('skill_tool', {}, '', 'done')).toBe('(无参数)');
  });
});

import { describe, expect, it } from 'vitest';
import { isReactive } from 'vue';
import { AGENT_BEHAVIOR_CARD_NAMES } from '../agentBehaviorTypes';
import { DEFAULT_OPEN, useAgentBehaviorCards } from './useAgentBehaviorCards';

describe('useAgentBehaviorCards', () => {
  it('初始展开前三张卡片并保持后两张折叠', () => {
    const { expanded, allOpen } = useAgentBehaviorCards();

    expect(isReactive(expanded)).toBe(true);
    expect(expanded).toEqual(DEFAULT_OPEN);
    expect(expanded).not.toBe(DEFAULT_OPEN);
    expect(allOpen.value).toBe(false);
  });

  it('单卡设置只改变目标卡片', () => {
    const { expanded, setOpen } = useAgentBehaviorCards();

    setOpen('monitor', true);

    expect(expanded).toEqual({ ...DEFAULT_OPEN, monitor: true });
  });

  it('单卡切换只反转目标卡片', () => {
    const { expanded, toggle } = useAgentBehaviorCards();

    toggle('timeout');

    expect(expanded).toEqual({ ...DEFAULT_OPEN, timeout: false });
    toggle('rejection');
    expect(expanded).toEqual({ ...DEFAULT_OPEN, timeout: false, rejection: true });
  });

  it('任意卡片展开时全部收起', () => {
    const { expanded, allOpen, toggleAll } = useAgentBehaviorCards();

    toggleAll();

    expect(Object.values(expanded).every((open) => !open)).toBe(true);
    expect(allOpen.value).toBe(false);
  });

  it('全部关闭时全部展开', () => {
    const { expanded, allOpen, toggleAll } = useAgentBehaviorCards();
    toggleAll();

    toggleAll();

    expect(expanded).toEqual({
      timeout: true,
      iterations: true,
      subagents: true,
      monitor: true,
      rejection: true,
    });
    expect(AGENT_BEHAVIOR_CARD_NAMES.every((name) => expanded[name])).toBe(true);
    expect(allOpen.value).toBe(true);
  });

  it('再次全部切换会收起所有卡片', () => {
    const { expanded, allOpen, toggleAll } = useAgentBehaviorCards();
    toggleAll();
    toggleAll();

    toggleAll();

    expect(Object.values(expanded).every((open) => !open)).toBe(true);
    expect(allOpen.value).toBe(false);
  });

  it('超时字段只标记超时卡片', () => {
    const { isCardDirty } = useAgentBehaviorCards();

    expect(isCardDirty('timeout', ['execDefaultTimeoutMs'])).toBe(true);
    expect(isCardDirty('timeout', ['execMaxTimeoutMs'])).toBe(true);
    expect(isCardDirty('iterations', ['execDefaultTimeoutMs'])).toBe(false);
    expect(isCardDirty('subagents', ['execDefaultTimeoutMs'])).toBe(false);
    expect(isCardDirty('timeout', [])).toBe(false);
  });

  it('轮次与子代理字段映射到各自卡片', () => {
    const { isCardDirty } = useAgentBehaviorCards();

    expect(isCardDirty('iterations', ['maxIterations', 'turnTimeoutMs'])).toBe(true);
    expect(isCardDirty('timeout', ['turnTimeoutMs'])).toBe(false);
    expect(isCardDirty('subagents', ['subAgentEnabled', 'subMaxParallel', 'subAllowMemoryWrite'])).toBe(true);
    expect(isCardDirty('iterations', ['subMaxParallel'])).toBe(false);
    expect(isCardDirty('subagents', ['unknownField'])).toBe(false);
  });

  it('监控与拒绝卡片永不显示配置脏点', () => {
    const { isCardDirty } = useAgentBehaviorCards();
    const dirtyFields = ['execDefaultTimeoutMs', 'maxIterations', 'subAgentEnabled'];

    expect(isCardDirty('monitor', dirtyFields)).toBe(false);
    expect(isCardDirty('rejection', dirtyFields)).toBe(false);
  });

  it('不同调用实例不共享展开状态', () => {
    const first = useAgentBehaviorCards();
    const second = useAgentBehaviorCards();

    first.toggleAll();
    first.toggleAll();

    expect(first.allOpen.value).toBe(true);
    expect(second.expanded).toEqual(DEFAULT_OPEN);
    expect(second.allOpen.value).toBe(false);
  });
});

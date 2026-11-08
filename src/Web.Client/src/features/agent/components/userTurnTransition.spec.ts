// need02-P3：交接容器单测（completing 期两分区共存；p3 off 硬切；06 §6.2 testid）。
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import UserTurnTransition from '@features/agent/components/UserTurnTransition.vue';

function mountTransition(props: { running?: boolean; completing?: boolean; animOn?: boolean } = {}) {
  return mount(UserTurnTransition, {
    props: { running: false, completing: false, animOn: true, ...props },
    slots: {
      live: '<div data-testid="slot-live">LIVE</div>',
      default: '<div data-testid="slot-steady">STEADY</div>',
    },
  });
}

describe('need02-P3 UserTurnTransition 交接时序', () => {
  it('运行中：live 可见 + 稳态共存（流态全量展开）', () => {
    const w = mountTransition({ running: true });
    expect(w.find('[data-testid="user-turn-handoff-live"]').exists()).toBe(true);
    expect(w.find('[data-testid="user-turn-handoff-steady"]').exists()).toBe(true);
    expect(w.find('[data-testid="slot-live"]').exists()).toBe(true);
  });

  it('completing 期两分区共存（300ms 交接窗，无消失帧）', () => {
    const w = mountTransition({ running: false, completing: true });
    // 流容器仍在（淡出中），终态已就位（淡入中）。
    expect(w.find('[data-testid="user-turn-handoff-live"]').exists()).toBe(true);
    expect(w.find('[data-testid="user-turn-handoff-steady"]').exists()).toBe(true);
    expect(w.find('.uturn-live-fading').exists()).toBe(true);
    expect(w.find('.uturn-steady-arriving').exists()).toBe(true);
    expect(w.attributes('data-completing')).toBe('true');
  });

  it('稳态：live 移除，只留终态', () => {
    const w = mountTransition({ running: false, completing: false });
    expect(w.find('[data-testid="user-turn-handoff-live"]').exists()).toBe(false);
    expect(w.find('[data-testid="user-turn-handoff-steady"]').exists()).toBe(true);
    expect(w.attributes('data-completing')).toBeUndefined();
  });

  it('p3=off（animOn=false）：data-handoff=off，交接窗内直接切终态（硬切：live 不共存）', () => {
    const w = mountTransition({ running: false, completing: true, animOn: false });
    expect(w.attributes('data-handoff')).toBe('off');
    // 硬切：流容器立即让位，终态直接可见（内容不断，只无淡入淡出）。
    expect(w.find('[data-testid="user-turn-handoff-live"]').exists()).toBe(false);
    expect(w.find('[data-testid="user-turn-handoff-steady"]').exists()).toBe(true);
    expect(w.find('[data-testid="slot-steady"]').exists()).toBe(true);
  });

  it('p3=on：data-handoff=on', () => {
    const w = mountTransition({ running: true, animOn: true });
    expect(w.attributes('data-handoff')).toBe('on');
  });
});

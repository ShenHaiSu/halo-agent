import { beforeEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import CompactTurn from './CompactTurn.vue';
import { clearExpansion, isTurnExpanded } from '../utils/timelineExpansion';
import type { FlatTimelineTurn } from '../utils/timelineSummary';

const turn: FlatTimelineTurn = {
  key: 'turn-compact-a',
  turnId: 'turn-compact-a',
  turnSeq: 2,
  userPreview: '上下文压缩',
  userContent: '**摘要重点**\n\n- 保留任务目标',
  finalPreview: '',
  finalBlockId: null,
  blocks: 0,
  tools: 0,
  failed: 0,
  durationMs: 0,
  chars: 20,
  status: 'completed',
  lastRequestId: '',
  anchorKey: 'turn-compact-a',
  kind: 'compact',
  removedMessages: 12,
};

describe('CompactTurn', () => {
  beforeEach(() => clearExpansion());

  it('默认折叠并提供稳定导航根节点', () => {
    const wrapper = mount(CompactTurn, { props: { turn } });
    expect(wrapper.attributes('data-turn-key')).toBe('turn-compact-a');
    expect(wrapper.find('[data-testid="compact-divider"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="compact-summary"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid^="timeline-turn-"]').exists()).toBe(false);
  });

  it('点击后渲染 markdown 并写入共享展开仓库', async () => {
    const wrapper = mount(CompactTurn, { props: { turn } });
    await wrapper.find('[data-testid="compact-pill"]').trigger('click');
    expect(wrapper.find('[data-testid="compact-summary"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="compact-summary"] strong').text()).toBe('摘要重点');
    expect(isTurnExpanded(turn.turnId)).toBe(true);

    const remounted = mount(CompactTurn, { props: { turn } });
    expect(remounted.find('[data-testid="compact-summary"]').exists()).toBe(true);
  });

  it('不渲染 user/process/final 普通轮槽位', () => {
    const wrapper = mount(CompactTurn, { props: { turn } });
    expect(wrapper.find('.turn-user-slot').exists()).toBe(false);
    expect(wrapper.find('.turn-process-slot').exists()).toBe(false);
    expect(wrapper.find('.turn-final-slot').exists()).toBe(false);
  });
});

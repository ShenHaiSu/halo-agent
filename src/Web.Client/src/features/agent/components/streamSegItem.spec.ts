// P1：流式空工具卡不再渲染可展开的空折叠（参数/结果未到前占位明示）。
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import StreamSegItem from '@features/agent/components/StreamSegItem.vue';
import type { DisplaySeg } from '@features/agent/composables/useTimeline';

function toolSeg(card: Record<string, unknown>): DisplaySeg {
  return { kind: 'tool', key: 'seg-tool-1', text: '', card: card as unknown as DisplaySeg['card'] } as DisplaySeg;
}

function mountSeg(seg: DisplaySeg) {
  return mount(StreamSegItem, {
    props: {
      seg,
      streamMdKillSwitch: false,
      renderStream: (s: string) => s,
      isTail: true,
      isGenerating: true,
    },
    global: {
      stubs: { ToolCallCard: { template: '<div class="tool-card-stub" />' } },
    },
  });
}

describe('StreamSegItem 空工具卡占位（P1）', () => {
  it('running 且参数/结果都为空 → 占位“参数接收中”，不出空折叠', () => {
    const wrapper = mountSeg(toolSeg({ callId: 'c1', functionName: 'exec_command', argsJson: '', status: 'running', resultText: '' }));
    expect(wrapper.find('[data-testid="tool-card-pending"]').exists()).toBe(true);
    expect(wrapper.text()).toContain('参数接收中');
    expect(wrapper.find('.tool-card-stub').exists()).toBe(false);
  });

  it('running 且参数已到 → 渲染工具卡折叠', () => {
    const wrapper = mountSeg(
      toolSeg({ callId: 'c1', functionName: 'exec_command', argsJson: '{"command":"dir"}', status: 'running', resultText: '' })
    );
    expect(wrapper.find('.tool-card-stub').exists()).toBe(true);
    expect(wrapper.find('[data-testid="tool-card-pending"]').exists()).toBe(false);
  });

  it('终态（done）即使结果为空也展示卡片（状态本身即信息）', () => {
    const wrapper = mountSeg(toolSeg({ callId: 'c1', functionName: 'read_file', argsJson: '', status: 'done', resultText: '' }));
    expect(wrapper.find('.tool-card-stub').exists()).toBe(true);
    expect(wrapper.find('[data-testid="tool-card-pending"]').exists()).toBe(false);
  });
});

/** need02-P1：乐观 user 渲染（06 §6.2 组件断言）。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import TimelineRow from '@features/agent/components/TimelineRow.vue';

function mountUserRow(msgId: string, pendingStatus: string) {
  const item = {
    key: `u-${msgId}`,
    kind: 'user',
    msg: { id: msgId, role: 'user', content: 'hi', createdAtUtc: new Date().toISOString(), images: [] },
  } as never;
  return mount(TimelineRow, {
    props: {
      item,
      isExpanded: () => false,
      anchorRef: () => () => undefined,
      msgImages: () => [],
      thumbOf: () => '',
      hasLost: () => false,
      renderHtml: (t: string) => t,
      cardFor: () => ({}) as never,
      toolKey: () => '',
      hasFailed: () => false,
      showTelemetry: () => false,
      protocolOf: () => '',
      cacheLineOf: () => '',
      isPending: (m: unknown) => String((m as { id?: string })?.id || '').startsWith('pending-'),
      pendingStatus: () => pendingStatus,
    },
    global: { plugins: [naive] },
  });
}

describe('pending user 渲染', () => {
  it('sending 显示 spinner + 发送中', () => {
    const w = mountUserRow('pending-req1', 'sending');
    expect(w.find('[data-testid="pending-sending"]').exists()).toBe(true);
    expect(w.find('[data-testid="pending-failed"]').exists()).toBe(false);
  });

  it('failed 显示标红 + 重发按钮，点击触发 retry-pending', async () => {
    const w = mountUserRow('pending-req2', 'failed');
    expect(w.find('[data-testid="pending-failed"]').exists()).toBe(true);
    const btn = w.find('[data-testid="pending-retry-btn"]');
    expect(btn.exists()).toBe(true);
    await btn.trigger('click');
    expect(w.emitted('retry-pending')).toBeTruthy();
  });

  it('普通 user 无 pending 态', () => {
    const w = mountUserRow('m-real-1', '');
    expect(w.find('[data-testid="pending-sending"]').exists()).toBe(false);
    expect(w.find('[data-testid="pending-failed"]').exists()).toBe(false);
  });
});

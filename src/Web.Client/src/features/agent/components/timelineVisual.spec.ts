/** M1 静态抖动：图片槽位恒定 + 思考段类名（§1.1 (b)(c)）。 */
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import naive from 'naive-ui';
import TimelineRow from '@features/agent/components/TimelineRow.vue';
import TimelineView from '@features/agent/components/TimelineView.vue';
import type { ChatImageRef } from '@shared/bridge/hostBridge';

const img = { imageId: 'i1', fileName: 'a.png' } as unknown as ChatImageRef;
const item = {
  key: 'u-1',
  kind: 'user',
  msg: { id: 'm1', role: 'user', content: '看图', createdAtUtc: '', images: [img] },
} as never;

function mountRow(thumb: string, opts?: { lost?: boolean }) {
  return mount(TimelineRow, {
    props: {
      item,
      isExpanded: () => false,
      anchorRef: () => () => undefined,
      msgImages: () => [img],
      thumbOf: () => thumb,
      hasLost: () => !!opts?.lost,
      imgLost: () => !!opts?.lost,
      renderHtml: (t: string) => t,
      cardFor: () => ({}) as never,
      toolKey: () => '',
      hasFailed: () => false,
      showTelemetry: () => false,
      protocolOf: () => '',
      cacheLineOf: () => '',
    },
    global: { plugins: [naive] },
  });
}

function mountTimeline(segs: unknown[]) {
  return mount(TimelineView, {
    props: {
      isEmpty: false,
      items: [],
      historyItems: [],
      tailItems: [],
      chatWidthMode: 'centered',
      isExpanded: () => false,
      anchorRef: () => () => undefined,
      msgImages: () => [],
      thumbOf: () => '',
      hasLost: () => false,
      renderHtml: (t: string) => t,
      renderStream: (t: string) => t,
      cardFor: () => ({}) as never,
      toolKey: () => '',
      hasFailed: () => false,
      showTelemetry: () => false,
      protocolOf: () => '',
      cacheLineOf: () => '',
      hasActiveTurn: true,
      thinkingLine: '',
      isGenerating: false,
      segs: segs as never,
      segKillSwitch: false,
      fallbackCards: [],
      streamingText: '',
      showThinking: false,
      turnNotice: '',
      lastError: '',
      showRetry: false,
      liveTelemetry: null,
      menuVisible: false,
      menuX: 0,
      menuY: 0,
      menuBusy: false,
      menuCanCopy: false,
    },
    global: { plugins: [naive] },
  });
}

describe('M1 静态抖动（图片槽位 / 思考段高度）', () => {
  it('图片未加载时占据与加载后相同的槽位（.msg-thumb-slot + 骨架）', () => {
    const loading = mountRow('');
    expect(loading.find('.msg-thumb-skeleton').exists()).toBe(true);
    expect(loading.find('.msg-thumb-slot').exists()).toBe(true);
    expect(loading.find('[data-testid="msg-image-i1"]').exists()).toBe(false);
  });

  it('缩略图到达后在同一槽位渲染图片', () => {
    const loaded = mountRow('data:image/png;base64,AAA');
    expect(loaded.find('[data-testid="msg-image-i1"]').exists()).toBe(true);
    expect(loaded.find('.msg-thumb-skeleton').exists()).toBe(false);
    expect(loaded.find('.msg-thumb-slot').exists()).toBe(true);
  });

  it('图片丢失时渲染固定尺寸的标签槽位（不再是裸 n-tag）', () => {
    const lost = mountRow('', { lost: true });
    expect(lost.find('.msg-thumb-skeleton').exists()).toBe(false);
    expect(lost.find('.msg-thumb-slot').exists()).toBe(true);
    expect(lost.text()).toContain('a.png');
  });

  it('reasoning 段使用 reasoning-seg 类（稳定滚动条槽位），不再用 max-h-24', () => {
    const wrapper = mountTimeline([{ kind: 'reasoning', key: 'r1', text: '思考'.repeat(200), card: null }]);
    const html = wrapper.html();
    expect(html).toContain('reasoning-seg');
    expect(html).not.toContain('max-h-24');
    expect(html).toContain('max-h-[40vh]');
  });
});

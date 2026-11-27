/** 交接分组：tag 是正文/过程的唯一真相源，多轮迭代不误判；投影层必须把它透传到视图模型。 */
import { beforeEach, describe, expect, it } from 'vitest';
import { computed, defineComponent, h } from 'vue';
import { mount } from '@vue/test-utils';
import { isHandoffEnabled, splitHandoffSegs } from '@features/agent/utils/handoffGrouping';
import { provideTimelineScene } from './useTimelineScene';
import { useTimelineView, type TimelineLiveSeg } from './timelineViewKey';
import type { TurnSummary } from '@shared/bridge/hostBridge';

const seg = (kind: string, key: string, text: string, tag: string | null): TimelineLiveSeg => ({
  kind,
  key,
  text,
  tag,
  card: null,
});

beforeEach(() => {
  window.localStorage.removeItem('halo-turn-handoff');
});

describe('交接期正文分组', () => {
  it('透传的 final 标签决定唯一正文，过程段与早期正文都不当正文', () => {
    const segs = [
      seg('reasoning', 'r1', '思考', 'process'),
      seg('text', 't1', '早期文字', 'process'),
      seg('text', 't2', '# 最终结论', 'final'),
    ];
    const { process, final } = splitHandoffSegs(segs);
    expect(final.map((s) => s.key)).toEqual(['t2']);
    expect(process.map((s) => s.key)).toEqual(['r1', 't1']);
  });

  it('多 final 异常只保留到达序最后一条，其余原样降级进过程槽', () => {
    const segs = [
      seg('text', 'txt-early', '早期迭代正文', 'final'),
      seg('tool', 'tool-a', '', 'process'),
      seg('text', 'txt-middle', '第二轮过程文字', 'process'),
      seg('tool', 'tool-b', '', 'process'),
      seg('text', 'txt-last', '末轮正文', 'final'),
    ];
    const { process, final } = splitHandoffSegs(segs);
    expect(final.map((s) => s.key)).toEqual(['txt-last']);
    expect(process.map((s) => s.key)).toEqual(['txt-early', 'tool-a', 'txt-middle', 'tool-b']);
    // 保留原对象与文本：降级只改分组，不丢过程内容、不改原 tag。
    expect(process[0].text).toBe('早期迭代正文');
    expect(process[0].tag).toBe('final');
    expect(segs.filter((s) => s.tag === 'final').length).toBe(2);
  });

  it('final 后仍有过程工具段时不按数组末位 text 猜测正文', () => {
    const segs = [seg('text', 'txt-answer', '末轮正文', 'final'), seg('tool', 'tool-after', '', 'process')];
    const { process, final } = splitHandoffSegs(segs);
    expect(final.map((s) => s.key)).toEqual(['txt-answer']);
    expect(process.map((s) => s.key)).toEqual(['tool-after']);
  });

  it('全空白 segs 时 final 为空，不用外部整轮文本', () => {
    const { process, final } = splitHandoffSegs([seg('text', 'txt-blank', '   ', null), seg('tool', 'tool-only', '', 'process')]);
    expect(final).toEqual([]);
    expect(process.map((s) => s.key)).toEqual(['txt-blank', 'tool-only']);
  });

  it('tag 全缺失时才按最后一个非空文本兜底', () => {
    const segs = [seg('reasoning', 'r1', '思考', null), seg('text', 't1', '正文A', null), seg('text', 't2', '正文B', null)];
    const { process, final } = splitHandoffSegs(segs);
    expect(final.map((s) => s.key)).toEqual(['t2']);
    expect(process.map((s) => s.key)).toEqual(['r1', 't1']);
  });

  it('热降级开关：off 时跳过交接容器', () => {
    expect(isHandoffEnabled()).toBe(true);
    window.localStorage.setItem('halo-turn-handoff', 'off');
    expect(isHandoffEnabled()).toBe(false);
  });
});

describe('交接投影到视图模型', () => {
  function vmFrom(source: Record<string, unknown>, summaries: TurnSummary[] = []) {
    let captured: ReturnType<typeof useTimelineView> | null = null;
    const Probe = defineComponent({
      setup() {
        captured = useTimelineView();
        return () => h('div');
      },
    });
    const Host = defineComponent({
      setup() {
        provideTimelineScene(source as never, {
          renderHtml: (s) => s,
          renderStream: (s) => s,
          summaryList: computed(() => summaries),
          singleProcess: true,
        });
        return () => h(Probe);
      },
    });
    mount(Host);
    return captured as unknown as ReturnType<typeof useTimelineView>;
  }

  function source(over: Record<string, unknown> = {}): Record<string, unknown> {
    return {
      liveCompleting: true,
      steadyReady: true,
      handoffAnim: true,
      stream: { streamingText: '整轮文本' },
      liveInclusiveSegs: [seg('reasoning', 'r1', '思考', 'process'), seg('text', 't2', '# 末轮正文', 'final')],
      ...over,
    };
  }

  it('交接窗内 final 文本来自透传 tag 的 final 段，而非整轮 streamingText', () => {
    const vm = vmFrom(source());
    expect(vm.handoffActive).toBe(true);
    expect(vm.handoffFinalText).toBe('# 末轮正文');
    expect(vm.handoffReduceHeight).toBe(true);
  });

  it('稳态未就绪不算交接窗，正文种子仍可取到', () => {
    const vm = vmFrom(source({ steadyReady: false }));
    expect(vm.handoffActive).toBe(false);
    expect(vm.handoffFinalText).toBe('# 末轮正文');
  });

  it('热降级 off 时交接窗口与收缩都关闭', () => {
    window.localStorage.setItem('halo-turn-handoff', 'off');
    const vm = vmFrom(source());
    expect(vm.handoffActive).toBe(false);
    expect(vm.handoffReduceHeight).toBe(false);
  });

  it('多 final 异常时种子只取末位 final，不拼接早期迭代正文', () => {
    const vm = vmFrom(
      source({
        stream: { streamingText: '早期迭代正文\n# 末轮正文' },
        liveInclusiveSegs: [seg('text', 'txt-early', '早期迭代正文\n', 'final'), seg('text', 'txt-last', '# 末轮正文', 'final')],
      })
    );
    expect(vm.handoffFinalText).toBe('# 末轮正文');
  });

  it('没有 final 段时不再用整轮 streamingText 兜底', () => {
    const vm = vmFrom(
      source({
        stream: { streamingText: '早期迭代正文\n过程文字' },
        liveInclusiveSegs: [seg('text', 'txt-process', '早期迭代正文\n过程文字', 'process')],
      })
    );
    expect(vm.handoffFinalText).toBe('');
  });

  it('稳态已对账且 requestId 精确匹配时优先取摘要 finalContent', () => {
    const vm = vmFrom(
      source({
        turn: { activeTurn: { handoffRequestId: 'req-active', requestId: '' } },
        liveInclusiveSegs: [seg('text', 'txt-live', '内存过渡种子', 'final')],
      }),
      [
        {
          turnId: 'turn-a',
          lastRequestId: 'req-active',
          finalContent: '# 落盘末轮正文',
          finalPreview: '',
        } as TurnSummary,
      ]
    );
    expect(vm.handoffFinalText).toBe('# 落盘末轮正文');
  });

  it('requestId 不匹配时不取最后一条摘要，回退唯一 live final', () => {
    const vm = vmFrom(
      source({
        turn: { activeTurn: { handoffRequestId: 'req-active', requestId: '' } },
        liveInclusiveSegs: [seg('text', 'txt-live', '内存过渡种子', 'final')],
      }),
      [
        {
          turnId: 'turn-other',
          lastRequestId: 'req-old',
          finalContent: '# 别的轮正文',
          finalPreview: '',
        } as TurnSummary,
      ]
    );
    expect(vm.handoffFinalText).toBe('内存过渡种子');
  });
});

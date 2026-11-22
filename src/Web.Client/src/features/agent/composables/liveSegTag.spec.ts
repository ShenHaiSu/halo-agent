/** 交接分组：tag 是正文/过程的唯一真相源，多轮迭代不误判；投影层必须把它透传到视图模型。 */
import { beforeEach, describe, expect, it } from 'vitest';
import { computed, defineComponent, h } from 'vue';
import { mount } from '@vue/test-utils';
import { isHandoffEnabled, splitHandoffSegs } from '@features/agent/utils/handoffGrouping';
import { provideTimelineScene } from './useTimelineScene';
import { useTimelineView, type TimelineLiveSeg } from './timelineViewKey';

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

  it('多轮工具迭代下末轮正文仍唯一，不按数组末位猜测', () => {
    const segs = [
      seg('text', 'first', '第一轮正文', 'final'),
      seg('tool', 'tool-1', '', 'process'),
      seg('text', 'middle', '第二轮过程文字', 'process'),
      seg('tool', 'tool-2', '', 'process'),
      seg('text', 'last', '末轮正文', 'final'),
    ];
    // 真相源异常出现两条 final 时全部收进 final 槽，但正文不会被当过程收缩。
    const { process, final } = splitHandoffSegs(segs);
    expect(final.map((s) => s.key)).toEqual(['first', 'last']);
    expect(process.every((s) => s.tag === 'process')).toBe(true);
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
  function vmFrom(source: Record<string, unknown>) {
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
          summaryList: computed(() => []),
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
});

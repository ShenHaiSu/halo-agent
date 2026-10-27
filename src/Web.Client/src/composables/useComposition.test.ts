// src/Web.Client/src/composables/useComposition.test.ts
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { defineComponent, h, nextTick } from 'vue';
import { mount } from '@vue/test-utils';
import { useComposition } from './useComposition';

function harness() {
  let api: ReturnType<typeof useComposition> | null = null;
  const C = defineComponent({
    setup() {
      api = useComposition();
      return () => h('div');
    },
  });
  const w = mount(C);
  return {
    w,
    get api() {
      return api!;
    },
  };
}

describe('useComposition（IME 8 坑）', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    document.body.innerHTML = ''; // @allow-innerhtml 测试清理 DOM
  });

  it('坑1/坑2：compositionend 后 30ms 才解除', async () => {
    const { w, api } = harness();
    document.dispatchEvent(new CompositionEvent('compositionstart'));
    expect(api.value).toBe(true);

    document.dispatchEvent(new CompositionEvent('compositionend'));
    // 立刻仍是 true（防同帧 Enter 误判）
    expect(api.value).toBe(true);

    await vi.advanceTimersByTimeAsync(29);
    expect(api.value).toBe(true);
    await vi.advanceTimersByTimeAsync(2);
    expect(api.value).toBe(false);
    w.unmount();
  });

  it('坑2：compositionend 与 keydown 同帧时不误判', async () => {
    const { w, api } = harness();
    document.dispatchEvent(new CompositionEvent('compositionstart'));
    document.dispatchEvent(new CompositionEvent('compositionend'));

    // 模拟同帧到达的 Enter keydown
    const composingAtKeydown = api.value;
    expect(composingAtKeydown).toBe(true); // 说明 keydown 里会 return

    w.unmount();
  });

  it('坑3：isComposingNow 对 isComposing=true 的 InputEvent 返回 true', () => {
    const { w, api } = harness();
    const ev = new InputEvent('input', { isComposing: true });
    expect(api.isComposingNow(ev)).toBe(true);
    w.unmount();
  });

  it('坑7：卸载后移除 document 监听', async () => {
    const { w, api } = harness();
    w.unmount();
    // 卸载后再触发不应改状态（因为监听已移除）
    document.dispatchEvent(new CompositionEvent('compositionstart'));
    await nextTick();
    expect(api.value).toBe(false);
  });
});

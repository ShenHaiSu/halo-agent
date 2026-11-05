import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { shallowMount } from '@vue/test-utils';
import { useStreamStore } from '@/stores/stream';
import { useToast } from '@/composables/useToast';
import { transport } from '@/transport';
import Composer from './Composer.vue';
import ComposerToolbar from './ComposerToolbar.vue';

function mountComposer() {
  const pinia = createPinia();
  setActivePinia(pinia);
  const wrapper = shallowMount(Composer, {
    global: { plugins: [pinia] },
  });
  return wrapper;
}

function emitCancel(wrapper: ReturnType<typeof shallowMount>) {
  wrapper.findComponent(ComposerToolbar).vm.$emit('cancel');
}

describe('Composer.cancel 反馈', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    useToast().clear();
    vi.restoreAllMocks();
  });

  it('R3a: RPC 失败时必须 toast.error（而不是静默）', async () => {
    const wrapper = mountComposer();
    const stream = useStreamStore();
    stream.beginTurn('t1');
    vi.spyOn(transport, 'request').mockRejectedValueOnce(new Error('bridge down'));

    emitCancel(wrapper);
    await new Promise((r) => setTimeout(r, 20));

    const items = useToast().items.value;
    expect(items.some((t) => t.level === 'error' && t.text.includes('取消失败'))).toBe(true);
    wrapper.unmount();
  });

  it('R3b: 点击后立即进入 cancelling 态（不等 RPC 返回）', () => {
    const wrapper = mountComposer();
    const stream = useStreamStore();
    stream.beginTurn('t1');
    vi.spyOn(transport, 'request').mockImplementation(() => new Promise(() => {}) as Promise<unknown>);

    emitCancel(wrapper);
    // 同步断言：beginCancel 必须在 RPC 返回前生效
    expect(stream.cancelling).toBe(true);
    wrapper.unmount();
  });

  it('R3c: cancelled===false 时必须退出 cancelling 且 busy=false', async () => {
    const wrapper = mountComposer();
    const stream = useStreamStore();
    stream.beginTurn('t1');
    vi.spyOn(transport, 'request').mockResolvedValueOnce({ cancelled: false } as unknown as never);

    emitCancel(wrapper);
    await new Promise((r) => setTimeout(r, 20));

    expect(stream.cancelling).toBe(false);
    expect(stream.busy).toBe(false);
    wrapper.unmount();
  });
});

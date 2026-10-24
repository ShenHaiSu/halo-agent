/** ask_user 三改验收：输入框永远显示 + 自定义互斥选项 + 单选可取消。 */
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { createPinia, setActivePinia, type Pinia } from 'pinia';
import naive from 'naive-ui';
import { nextTick } from 'vue';

vi.mock('@shared/bridge/hostBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@shared/bridge/hostBridge')>();
  return {
    ...actual,
    getHostBridge: () => ({
      resolveAskUser: vi.fn().mockResolvedValue({ ok: true, data: true }),
      cancelAgentTurn: vi.fn().mockResolvedValue({ ok: true, data: true }),
    }),
  };
});

// naive-ui 的 useMessage 需要 Provider 祖先；单测里替换为无副作用 stub
vi.mock('naive-ui', async (importOriginal) => {
  const actual = await importOriginal<typeof import('naive-ui')>();
  return {
    ...actual,
    useMessage: () => ({
      success: vi.fn(),
      error: vi.fn(),
      info: vi.fn(),
      warning: vi.fn(),
    }),
  };
});

import AskUserModal from './AskUserModal.vue';
import { useAskUserStore } from '@/stores/askUserStore';
import { useWorkspaceStore } from '@/stores/workspaceStore';

const SID = 'sess_ask_spec_01';

// n-modal 默认 teleport 到 body，wrapper.find 找不到，必须查 document.body
const q = (sel: string): Element | null => document.body.querySelector(sel);
const qa = (sel: string): NodeListOf<Element> => document.body.querySelectorAll(sel);

function seedSingle(allowCustom: boolean | undefined) {
  const ask = useAskUserStore();
  const ws = useWorkspaceStore();
  ws.activeSessionId = SID;
  const saved = ask.putPending({
    sessionId: SID,
    ticketId: 'tick_spec_01',
    callId: 'call_spec_01',
    questions: [
      {
        id: 'q1',
        question: '选哪个框架？',
        options: [{ label: 'Vue' }, { label: 'React' }],
        multiSelect: false,
        allowCustom,
      },
    ],
  });
  expect(saved).not.toBeNull();
  expect(ask.openModal(SID)).toBe(true);
  return ask;
}

function mountModal(pinia: Pinia) {
  return mount(AskUserModal, {
    attachTo: document.body,
    global: { plugins: [naive, pinia] },
  });
}

async function settle(times = 3) {
  await flushPromises();
  for (let i = 0; i < times; i++) await nextTick();
}

describe('AskUserModal（三改验收）', () => {
  let pinia: Pinia;

  beforeEach(() => {
    document.body.innerHTML = '';
    pinia = createPinia();
    setActivePinia(pinia);
  });

  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('store 冻结 allowCustom：历史 false 也归一为 true', () => {
    const ask = useAskUserStore();
    const saved = ask.putPending({
      sessionId: SID,
      ticketId: 't1',
      callId: 'c1',
      questions: [{ id: 'q1', question: 'Q?', options: [{ label: 'A' }], allowCustom: false }],
    });
    expect(saved).not.toBeNull();
    expect(saved?.questions[0].allowCustom).toBe(true);
  });

  it('改1：allowCustom:false 时输入框依然渲染', async () => {
    seedSingle(false);
    const w = mountModal(pinia);
    await settle();
    // n-modal teleport 到 body 上
    expect(q('[data-testid="ask-user-modal"]')).not.toBeNull();
    // 输入框不再依赖 allowCustom，永远存在
    expect(q('[data-testid="ask-user-custom-q1"]')).not.toBeNull();
    w.unmount();
  });

  it('改2：自定义输入非空互斥清空选项并禁用选项组', async () => {
    const ask = seedSingle(true);
    const w = mountModal(pinia);
    await settle();

    // 先选中一个选项：点 n-radio 根节点（带 data-testid）
    const radio0 = q('[data-testid="ask-user-option-q1-0"]') as HTMLElement | null;
    expect(radio0).not.toBeNull();
    radio0!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await settle();
    expect(ask.getDraft(SID)['q1']?.selected).toEqual(['Vue']);

    // 输入自定义答案 -> 选项被清空
    const textarea = q('[data-testid="ask-user-custom-q1"] textarea') as HTMLTextAreaElement | null;
    expect(textarea).not.toBeNull();
    textarea!.focus();
    textarea!.value = '我要用 Svelte';
    textarea!.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    expect(ask.getDraft(SID)['q1']?.customInput).toContain('Svelte');
    expect(ask.getDraft(SID)['q1']?.selected).toEqual([]);
    // 互斥提示出现
    expect(document.body.textContent || '').toContain('已输入自定义答案');
    w.unmount();
  });

  it('改3：单选可取消（同值再点 + 清除选择按钮）', async () => {
    const ask = seedSingle(true);
    const w = mountModal(pinia);
    await settle();

    const radio1 = q('[data-testid="ask-user-option-q1-1"]') as HTMLElement | null;
    expect(radio1).not.toBeNull();
    radio1!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await settle();
    expect(ask.getDraft(SID)['q1']?.selected).toEqual(['React']);

    // 同值再点取消（toggleSingle）
    radio1!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await settle();
    expect(ask.getDraft(SID)['q1']?.selected).toEqual([]);

    // 重新选中后用“清除选择”按钮取消
    const radio0 = q('[data-testid="ask-user-option-q1-0"]') as HTMLElement | null;
    radio0!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await settle();
    expect(ask.getDraft(SID)['q1']?.selected).toEqual(['Vue']);
    const clearBtn = q('[data-testid="ask-user-clear-q1"]') as HTMLElement | null;
    expect(clearBtn).not.toBeNull();
    clearBtn!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await settle();
    expect(ask.getDraft(SID)['q1']?.selected).toEqual([]);
    // 覆盖 qa helper（防 noUnusedLocals）：选项应仍是 2 个
    expect(qa('[data-testid^="ask-user-option-q1-"]').length).toBe(2);
    w.unmount();
  });
});

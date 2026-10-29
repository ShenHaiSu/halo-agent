// src/Web.Client/src/components/settings/fields/ProbeResultDialog.test.ts
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import ProbeResultDialog from './ProbeResultDialog.vue';

function openDialog(models: string[], existing: string[] = []) {
  return mount(ProbeResultDialog, {
    props: { open: true, models, existing },
    attachTo: document.body,
  });
}

describe('ProbeResultDialog', () => {
  it('默认全选新增、已存在禁用', async () => {
    const w = openDialog(['a', 'b', 'c'], ['b']);
    await nextTick();
    const html = document.body.innerHTML;
    expect(html).toContain('找到 3 个');
    expect(html).toContain('1 个已在列表');
    expect(html).toContain('已存在');
    // 确认按钮 K=2
    expect(html).toContain('加入所选 (2)');
    w.unmount();
  });

  it('K=0 时确认按钮 disabled', async () => {
    const w = openDialog(['a'], ['a']);
    await nextTick();
    const btns = document.body.querySelectorAll('button');
    const confirm = [...btns].find((b) => b.textContent?.includes('加入所选'));
    expect(confirm?.textContent).toContain('(0)');
    expect((confirm as HTMLButtonElement).disabled).toBe(true);
    w.unmount();
  });

  it('搜索过滤大小写不敏感', async () => {
    const w = openDialog(['DeepSeek-Chat', 'gpt-4o']);
    await nextTick();
    const input = document.body.querySelector('input') as HTMLInputElement;
    expect(input).not.toBeNull();
    input.value = 'DEEPSEEK';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await nextTick();
    const body = document.body.innerHTML;
    expect(body).toContain('DeepSeek-Chat');
    expect(body).not.toContain('gpt-4o');
    w.unmount();
  });

  it('confirm 只带新增（去重）', async () => {
    const w = openDialog(['a', 'b'], ['b']);
    await nextTick();
    const btns = [...document.body.querySelectorAll('button')];
    const confirm = btns.find((b) => b.textContent?.includes('加入所选')) as HTMLButtonElement;
    confirm.click();
    await nextTick();
    const emitted = w.emitted('confirm');
    expect(emitted).toBeTruthy();
    expect(emitted![0]![0]).toEqual(['a']);
    w.unmount();
  });

  it('0 结果空态', async () => {
    const w = openDialog([]);
    await nextTick();
    expect(document.body.innerHTML).toContain('远端返回 0 个');
    w.unmount();
  });
});

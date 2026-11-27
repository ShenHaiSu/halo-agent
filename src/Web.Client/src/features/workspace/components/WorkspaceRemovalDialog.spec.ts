import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import type { WorkspaceInfo } from '@shared/bridge/hostBridge';
import WorkspaceRemovalDialog from './WorkspaceRemovalDialog.vue';

const stubs = {
  'n-modal': {
    props: ['title'],
    template: '<div><div class="modal-title">{{ title }}</div><slot /><slot name="footer" /></div>',
  },
  'n-alert': {
    template: '<div class="alert"><slot /></div>',
  },
  'n-radio-group': {
    props: ['value'],
    emits: ['update:value'],
    template:
      '<div><button data-testid="choose-purge" @click="$emit(\'update:value\', \'purge\')">purge</button><button data-testid="choose-unbind" @click="$emit(\'update:value\', \'unbind\')">unbind</button><slot /></div>',
  },
  'n-radio': {
    props: ['value'],
    template: '<label><slot /></label>',
  },
  'n-input': {
    props: ['value'],
    emits: ['update:value'],
    template: '<input :value="value" @input="$emit(\'update:value\', $event.target.value)" />',
  },
  'n-button': {
    props: ['disabled', 'loading'],
    emits: ['click'],
    template: '<button :disabled="disabled" @click="$emit(\'click\')"><slot /></button>',
  },
};

const workspace = {
  id: 'ws-dialog',
  name: '文件夹名',
  alias: '别名优先',
  rootPath: 'C:\\projects\\dialog',
} as WorkspaceInfo;

interface DialogProps {
  open?: boolean;
  workspace?: WorkspaceInfo | null;
  canRemove?: boolean;
  busy?: boolean;
}

function mountDialog(props: DialogProps = {}) {
  return mount(WorkspaceRemovalDialog, {
    props: { open: true, workspace, canRemove: true, busy: false, ...props },
    global: { stubs },
  });
}

describe('工作区移除弹窗', () => {
  it('两个模式的文案与物理行为一致，不再出现“不会删除磁盘文件”', () => {
    const wrapper = mountDialog();
    const text = wrapper.text();

    expect(text).toContain('移除工作区「别名优先」');
    expect(text).toContain('保留会话数据（history.db、备份与所有会话）');
    expect(text).toContain('删除该工作区的全部会话数据、history.db 与备份');
    expect(text).not.toContain('不会删除磁盘文件');
  });

  it('Purge 强确认：名称不匹配时确认禁用，匹配后才可提交', async () => {
    const wrapper = mountDialog();
    const confirm = wrapper.get('[data-testid="workspace-removal-confirm-btn"]');
    expect(confirm.attributes('disabled')).toBeUndefined();

    await wrapper.get('[data-testid="choose-purge"]').trigger('click');
    expect(confirm.attributes('disabled')).toBeDefined();

    const input = wrapper.get('[data-testid="workspace-removal-confirm-input"]');
    await input.setValue('文件夹名');
    expect(confirm.attributes('disabled')).toBeDefined();

    await input.setValue('别名优先');
    expect(confirm.attributes('disabled')).toBeUndefined();
    await confirm.trigger('click');

    expect(wrapper.emitted('confirm')).toEqual([['purge', '别名优先']]);
  });

  it('宿主不支持或 busy 时禁用确认入口', () => {
    const unsupported = mountDialog({ canRemove: false });
    expect(unsupported.get('[data-testid="workspace-removal-confirm-btn"]').attributes('disabled')).toBeDefined();
    expect(unsupported.text()).toContain('请升级宿主后再试');

    const busyWrapper = mountDialog({ busy: true });
    expect(busyWrapper.get('[data-testid="workspace-removal-confirm-btn"]').attributes('disabled')).toBeDefined();
  });

  it('切换模式时清空强确认输入，避免沿用上一次的名称', async () => {
    const wrapper = mountDialog();
    await wrapper.get('[data-testid="choose-purge"]').trigger('click');
    const input = wrapper.get('[data-testid="workspace-removal-confirm-input"]') as ReturnType<typeof wrapper.get>;
    await input.setValue('别名优先');

    await wrapper.get('[data-testid="choose-unbind"]').trigger('click');
    expect(wrapper.find('[data-testid="workspace-removal-confirm-input"]').exists()).toBe(false);
    expect(wrapper.get('[data-testid="workspace-removal-confirm-btn"]').attributes('disabled')).toBeUndefined();
  });
});

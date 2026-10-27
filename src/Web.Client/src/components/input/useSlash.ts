// src/Web.Client/src/components/input/useSlash.ts
import { computed, ref, type Ref } from 'vue';

export interface SlashCommand {
  name: string;
  desc: string;
  /** 需要参数的命令选中后只填名字 */
  needsArgs: boolean;
}

/**
 * `/` 斜杠命令（见 04 §2.5）。
 *
 * ============ 命令表 ============
 *  顺序即显示顺序（常用在前）。
 * ============================
 */
const COMMANDS: SlashCommand[] = [
  { name: 'new', desc: '新建对话', needsArgs: false },
  { name: 'clear', desc: '清除上下文（历史文件保留）', needsArgs: false },
  { name: 'compact', desc: '压缩上下文', needsArgs: false },
  { name: 'undo', desc: '查看撤销历史', needsArgs: false },
  { name: 'model', desc: '打开模型设置', needsArgs: false },
  { name: 'mode', desc: '切换权限模式', needsArgs: false },
  { name: 'mcp', desc: '打开 MCP 扩展设置', needsArgs: false },
  { name: 'persona', desc: '切换人格（需名称）', needsArgs: true },
  { name: 'cwd', desc: '切换工作目录（需路径）', needsArgs: true },
  { name: 'diagnostics', desc: '复制诊断信息', needsArgs: false },
  { name: 'help', desc: '查看快捷键', needsArgs: false },
];

export function useSlash(
  ta: Ref<HTMLTextAreaElement | null>,
  text: Ref<string>,
  opts: { onRun: (name: string, args: string) => Promise<void> }
) {
  const open = ref(false);
  const query = ref('');
  const active = ref(0);

  const items = computed(() => {
    const q = query.value.toLowerCase();
    return COMMANDS.filter((c) => c.name.startsWith(q)).slice(0, 10);
  });

  function close(): void {
    open.value = false;
    query.value = '';
    active.value = 0;
  }

  /** 由 composerInput 触发（已确认光标在最开头） */
  function trigger(_start: number, q: string): void {
    query.value = q;
    open.value = true;
    active.value = 0;
  }

  function move(delta: number): void {
    if (!items.value.length) return;
    active.value = (active.value + delta + items.value.length) % items.value.length;
  }

  function setActive(i: number): void {
    if (!items.value.length) return;
    active.value = Math.max(0, Math.min(items.value.length - 1, i));
  }

  function select(): void {
    const cmd = items.value[active.value];
    const el = ta.value;
    if (!cmd || !el) return;

    if (cmd.needsArgs) {
      // 填名字 + 空格，让用户输入参数
      text.value = `/${cmd.name} `;
      const caret = text.value.length;
      requestAnimationFrame(() => {
        el.selectionStart = el.selectionEnd = caret;
        el.focus();
      });
      close();
      return;
    }

    // 无参数命令：清空输入并立即执行
    text.value = '';
    close();
    void opts.onRun(cmd.name, '');
  }

  function triggerManually(): void {
    const el = ta.value;
    if (!el) return;
    if (!text.value.startsWith('/')) {
      text.value = '/' + text.value;
    }
    el.focus();
    requestAnimationFrame(() => {
      el.selectionStart = el.selectionEnd = text.value.length;
      trigger(0, text.value.slice(1));
    });
  }

  return {
    open: computed(() => open.value),
    items,
    active: computed(() => active.value),
    isOpen: () => open.value,
    trigger,
    move,
    setActive,
    select,
    close,
    triggerManually,
  };
}

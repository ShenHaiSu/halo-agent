/**
 * 代码块复制的容器级事件委托。
 *
 * 渲染器注入的复制按钮位于 v-html 动态节点里，模板无法直接绑定 Vue 事件，
 * 因此把 click 委托挂在时间轴滚动容器上：一次覆盖历史区与 tail 区，
 * 且对未来新增的代码块同样生效。绑定与解绑成对，避免监听器泄漏。
 */
import { copyTextToClipboard } from './historyAnchor';

const COPY_SELECTOR = '[data-code-copy]';
const SHELL_SELECTOR = '.md-code-shell';
const CODE_SELECTOR = 'pre code';
const LABEL_SELECTOR = '.md-code-copy-label';
const FEEDBACK_MS = 1200;

/** 每个按钮的恢复定时器，避免连续点击时前一个 timer 提前还原文案。 */
const resetTimers = new WeakMap<HTMLElement, ReturnType<typeof setTimeout>>();

/** 复制成功后的可见反馈：切「已复制」文案 + 数据标记，短暂后恢复。 */
export function flashCopied(button: HTMLElement): void {
  const label = button.querySelector<HTMLElement>(LABEL_SELECTOR);
  button.setAttribute('data-copied', 'true');
  if (label) label.textContent = '已复制';

  const previous = resetTimers.get(button);
  if (previous !== undefined) clearTimeout(previous);
  const timer = setTimeout(() => {
    button.removeAttribute('data-copied');
    if (label) label.textContent = '复制';
    resetTimers.delete(button);
  }, FEEDBACK_MS);
  resetTimers.set(button, timer);
}

/**
 * 在容器上挂载复制点击委托，返回解绑函数。
 * @param container 时间轴滚动容器（来自 TimelineView 的 ready 事件）
 */
export function bindCodeCopyDelegation(container: HTMLElement): () => void {
  const onClick = (event: MouseEvent): void => {
    const target = event.target;
    if (!(target instanceof Element)) return;
    const button = target.closest<HTMLElement>(COPY_SELECTOR);
    if (!button || !container.contains(button)) return;

    const shell = button.closest<HTMLElement>(SHELL_SELECTOR);
    const code = shell?.querySelector<HTMLElement>(CODE_SELECTOR);
    const text = code?.textContent ?? '';
    if (!text) return;

    // 隔离事件：阻止冒泡到 window 的全局 click 监听（右键菜单的关闭监听），
    // 同时避免触发所在过程头 / 卡片的 click 行为。
    event.preventDefault();
    event.stopPropagation();
    void copyTextToClipboard(text).then((ok) => {
      if (ok) flashCopied(button);
    });
  };

  container.addEventListener('click', onClick);
  return () => container.removeEventListener('click', onClick);
}

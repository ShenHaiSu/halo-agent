// src/Web.Client/src/composables/useCompactAction.ts
// 压缩动作复用（Composer / CommandPalette 同逻辑，避免两处分叉）。
import { transport } from '@/transport';
import { useToast } from '@/composables/useToast';

function busyMessageOf(e: unknown): boolean {
  const code = (e as { code?: unknown })?.code;
  if (typeof code === 'string' && code.includes('busy')) return true;
  const msg = String((e as Error)?.message ?? '');
  return msg.includes('state.busy') || msg.includes('回合执行中') || msg.includes('正在压缩');
}

export function useCompactAction(): { runCompact: () => Promise<void> } {
  const toast = useToast();
  async function runCompact(): Promise<void> {
    try {
      const r = await transport.request<{
        removed?: number;
        summary?: string;
        totalBefore?: number;
        totalAfter?: number;
      }>('session.compact', null);
      if ((r?.removed ?? 0) === 0) toast.info('消息太少，无需压缩');
      else toast.success(`已压缩上下文，移除 ${r?.removed} 条`);
    } catch (e) {
      if (busyMessageOf(e)) toast.info('回合执行中，稍后再压缩');
      else toast.error(`执行 /compact 失败：${(e as Error).message}`);
    }
  }
  return { runCompact };
}

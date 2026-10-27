// src/Web.Client/src/composables/useFileDrop.ts
import { onBeforeUnmount, ref, watch, type Ref } from 'vue';

const MAX_FILES = 20;
const BLOCKED_EXT = /\.(exe|dll|msi|bat|cmd|com|scr|ps1|vbs|js|jar|zip|7z|rar|iso|lnk)$/i;

export interface DropResult {
  accepted: string[];
  rejected: Array<{ path: string; reason: string }>;
}

/**
 * 拖放文件（见 04 §3.1）。
 *
 * ============ 两个来源必须去重 ============
 *  在宿主里拖放时：
 *    ① P3 的 IDropTarget 会通过 haloHost 推 filesDropped（带完整路径）
 *    ② 浏览器也会触发原生 drop（带 File 对象，只有文件名）
 *  两者会同时到达。若都处理 → 同一个文件被加两次。
 *  解法：宿主路径优先；若 500ms 内收到过宿主推送，忽略浏览器原生 drop。
 * ==========================================
 *
 * ============ 为什么不直接 import useHostStore ============
 *  composables 目录被 check-layering 禁止 import stores/（见脚本 RULES）。
 *  所以宿主文件流由调用方（Composer，允许 import stores）以 Ref 注入，
 *  本 composable 只做"去重 + 过滤 + 500ms 窗口"，行为与 04 §3.1 一致。
 * =========================================================
 */
export function useFileDrop(
  onFiles: (paths: string[]) => void,
  opts?: {
    /** 宿主推送的文件（调用方从 host store 映射），消费后由调用方清空 */
    hostFiles?: Ref<readonly string[]>;
    consumeHostFiles?: () => void;
  }
) {
  const isDragging = ref(false);
  let lastHostDropAt = 0;

  function filter(paths: string[]): DropResult {
    const accepted: string[] = [];
    const rejected: Array<{ path: string; reason: string }> = [];
    const seen = new Set<string>();

    for (const p of paths) {
      if (seen.has(p)) continue;
      seen.add(p);

      if (accepted.length >= MAX_FILES) {
        rejected.push({ path: p, reason: `一次最多添加 ${MAX_FILES} 个文件` });
        continue;
      }
      if (BLOCKED_EXT.test(p)) {
        rejected.push({ path: p, reason: '不支持的文件类型' });
        continue;
      }
      accepted.push(p);
    }
    return { accepted, rejected };
  }

  // ① 宿主来源（调用方注入）
  if (opts?.hostFiles) {
    watch(
      () => opts.hostFiles!.value,
      (paths) => {
        if (!paths.length) return;
        lastHostDropAt = performance.now();
        const r = filter([...paths]);
        onFiles(r.accepted);
        opts.consumeHostFiles?.();
      }
    );
  }

  // ② 浏览器来源（降级）
  function onDragEnter(e: DragEvent): void {
    if (!e.dataTransfer?.types.includes('Files')) return;
    isDragging.value = true;
  }

  function onDragLeave(): void {
    isDragging.value = false;
  }

  function onDrop(e: DragEvent): void {
    isDragging.value = false;
    // 500ms 内已收到宿主推送 → 忽略
    if (performance.now() - lastHostDropAt < 500) return;

    const files = [...(e.dataTransfer?.files ?? [])];
    if (!files.length) return;
    const r = filter(files.map((f) => f.name));
    onFiles(r.accepted);
  }

  onBeforeUnmount(() => {
    isDragging.value = false;
  });

  return { isDragging, onDragEnter, onDragLeave, onDrop, filter };
}

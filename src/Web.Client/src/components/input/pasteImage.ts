// src/Web.Client/src/components/input/pasteImage.ts
import { transport } from '@/transport';

export interface PasteResult {
  /** 已保存到磁盘的绝对路径 */
  savedPaths: string[];
  /** 用户拒绝写入的文件名 */
  skipped: string[];
}

/** 宿主可选能力的宽松视图（P3 若未提供则降级，见下） */
interface OptionalHostFiles {
  saveFileDialog?: (opts: {
    title: string;
    defaultFileName: string;
    filters: Array<{ name: string; extensions: string[] }>;
  }) => Promise<string | null | undefined>;
  writeFileBase64?: (path: string, base64: string) => Promise<boolean | void>;
}

/**
 * 粘贴图片（见 04 §3.2）。
 *
 * ============ 为什么前端不能直接写文件 ============
 *  CefSharp 渲染进程在沙箱里，没有文件系统写权限。
 *  即使有，让网页直接写盘也是安全问题（任何 XSS 都能写盘）。
 *
 *  正确流程：
 *    ① 从剪贴板拿到 File 对象（内存里）
 *    ② 用 haloHost.saveFileDialog 让用户选保存位置
 *    ③ 把 base64 通过宿主写到文件
 *  若宿主没有 saveFileDialog / writeFileBase64，则退化为
 *  "只识别、不写入"（skipped），UI 明示 Toast。
 * ==================================================
 */
export async function pasteImages(items: DataTransferItemList): Promise<PasteResult> {
  const images = [...items].filter((i) => i.type.startsWith('image/'));

  const savedPaths: string[] = [];
  const skipped: string[] = [];

  const host = transport.host as unknown as OptionalHostFiles;

  for (const item of images) {
    const file = item.getAsFile();
    if (!file) continue;

    // ① 超过 8MB 直接拒绝（避免 base64 撑爆管道）
    if (file.size > 8 * 1024 * 1024) {
      skipped.push(`${file.name || '剪贴板图片'}（超过 8MB）`);
      continue;
    }

    // ② 走宿主保存（能力缺失则降级）
    try {
      if (typeof host.saveFileDialog !== 'function' || typeof host.writeFileBase64 !== 'function') {
        skipped.push(file.name || '剪贴板图片');
        continue;
      }
      const ext = (file.type.split('/')[1] ?? 'png').replace('jpeg', 'jpg');
      const target = await host.saveFileDialog({
        title: '保存粘贴的图片',
        defaultFileName: `paste-${Date.now()}.${ext}`,
        filters: [{ name: '图片', extensions: [ext] }],
      });
      if (!target) {
        skipped.push(file.name || '剪贴板图片');
        continue;
      }

      // ③ 读取为 base64 交给宿主写盘
      const buf = await file.arrayBuffer();
      const b64 = toBase64(new Uint8Array(buf));
      const written = await host.writeFileBase64(target, b64);
      if (written) savedPaths.push(target);
      else skipped.push(file.name || '剪贴板图片');
    } catch {
      skipped.push(file.name || '剪贴板图片');
    }
  }

  return { savedPaths, skipped };
}

function toBase64(bytes: Uint8Array): string {
  // 分块避免 "Maximum call stack size exceeded"
  const CHUNK = 0x8000;
  let bin = '';
  for (let i = 0; i < bytes.length; i += CHUNK) {
    bin += String.fromCharCode(...bytes.subarray(i, i + CHUNK));
  }
  return btoa(bin);
}

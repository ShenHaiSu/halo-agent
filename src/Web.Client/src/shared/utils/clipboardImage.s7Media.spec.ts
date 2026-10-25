/**
 * S7-E4 §5 验收 E4-1 / E4-2（前端侧决策锁定）：图片链路类型白名单。
 *
 * 决策（2026-10-25，见 docs/imageContentDev/07-stage7-entry-cas-media.md §7）：
 *   · GIF 不做内联（动图首帧语义 + 宽高解析需额外分支，收益不抵风险）；
 *   · SVG 永不进视觉（可携带脚本/外链），转 SVG 需求走 read_file 按文本读。
 * 故前端 accept 逻辑与拒绝文案必须与后端 ViewImageTool.UnsupportedTypeReason 同口径。
 */
import { describe, expect, it } from 'vitest';
import { acceptImageFile, collectImageFiles, UNSUPPORTED_IMAGE_REASON, ACCEPT_IMAGE_MIMES } from '@shared/utils/clipboardImage';

function file(name: string, type: string, size = 1024): File {
  return new File([new Uint8Array(size)], name, { type });
}

describe('S7-E4 前端图片类型白名单（GIF/SVG 不进视觉链路）', () => {
  it('E4-F1：ACCEPT_IMAGE_MIMES 只含 jpg/png/webp（不含 image/gif、image/svg+xml）', () => {
    expect(ACCEPT_IMAGE_MIMES).toEqual(['image/jpeg', 'image/png', 'image/webp']);
    expect(ACCEPT_IMAGE_MIMES).not.toContain('image/gif' as never);
    expect(ACCEPT_IMAGE_MIMES).not.toContain('image/svg+xml' as never);
  });

  it('E4-F2：gif 文件被拒，文案是统一拒绝文案（不是笼统"请转换"）', () => {
    const r = acceptImageFile(file('anim.gif', 'image/gif'));
    expect(r.ok).toBe(false);
    expect(r.reason).toBe(UNSUPPORTED_IMAGE_REASON);
    // 文案要素：白名单 + GIF/SVG 明确 + read_file 指引
    expect(r.reason).toContain('仅支持 jpg/png/webp');
    expect(r.reason).toContain('GIF');
    expect(r.reason).toContain('SVG');
    expect(r.reason).toContain('read_file');
  });

  it('E4-F3：svg 文件被拒且指路 read_file（前端不接 SVG，防粘贴进图片链）', () => {
    const r = acceptImageFile(file('icon.svg', 'image/svg+xml'));
    expect(r.ok).toBe(false);
    expect(r.reason).toBe(UNSUPPORTED_IMAGE_REASON);
    expect(r.reason).toContain('read_file');
  });

  it('E4-F4：伪装扩展名（mime 为空时按文件名推断）gif/svg 仍被拒', () => {
    expect(acceptImageFile(file('a.gif', '')).ok).toBe(false);
    expect(acceptImageFile(file('a.svg', '')).ok).toBe(false);
    // 反向：jpg/png/webp 不受影响
    expect(acceptImageFile(file('a.png', '')).ok).toBe(true);
    expect(acceptImageFile(file('a.jpeg', '')).ok).toBe(true);
    expect(acceptImageFile(file('a.webp', '')).ok).toBe(true);
  });

  it('E4-F5：collectImageFiles 只做 image/* 粗收集，gif/svg 由 acceptImageFile 兜底拒绝', () => {
    const files = [
      file('ok.png', 'image/png'),
      file('no.gif', 'image/gif'),
      file('no.svg', 'image/svg+xml'),
      file('ok.webp', 'image/webp'),
      file('note.txt', 'text/plain'),
    ];
    const dt = { files } as unknown as DataTransfer;
    const picked = collectImageFiles(dt);
    // 收集阶段按 image/* 前缀收（含 gif/svg），非图片一律丢掉
    expect(picked.map((f) => f.name)).toEqual(['ok.png', 'no.gif', 'no.svg', 'ok.webp']);
    // 真正的白名单判定在 acceptImageFile（drop/paste 都会走它），gif/svg 到此为止
    const accepted = picked.filter((f) => acceptImageFile(f).ok).map((f) => f.name);
    expect(accepted).toEqual(['ok.png', 'ok.webp']);
  });
});

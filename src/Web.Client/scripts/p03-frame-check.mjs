/**
 * P03 抽帧/方差脚本初版（docs/P03-render-enhance/09 §9.3）。
 * 用法（P0 落盘初版，验收前先跑基线再冻结阈值/抽样步长）：
 *   node scripts/p03-frame-check.mjs --frames ./frames.json [--dark] [--stride 37]
 * frames.json: [{ w, h, data: [r,g,b,a,...] }]（抽帧后像素数组）
 * 另含 scroll 跳变判定（G2-2）与 skeleton 灰度方差（G3-3）纯函数，供录屏后处理复用。
 */

const DARK_REF = [13, 17, 23]; // #0d1117
const LIGHT_REF = [248, 250, 252]; // #f8fafc

export function isFlashFrame(px, dark, stride = 37) {
  const t = dark ? DARK_REF : LIGHT_REF;
  let bad = 0;
  let n = 0;
  for (let i = 0; i + 2 < px.length; i += 4 * stride) {
    n++;
    const d = Math.abs(px[i] - t[0]) + Math.abs(px[i + 1] - t[1]) + Math.abs(px[i + 2] - t[2]);
    // 相对参考底色偏离过大（容差 ±2/通道 → 总和 24 按抽样口径放大）
    if (d > 24) bad++;
  }
  if (n === 0) return false;
  return bad / n > 0.98;
}

/** G2-2：60Hz 采 scrollHeight，去抖 2 帧；连续 2 帧 >8px 即早摘。 */
export function hasEarlyPop(heights, threshold = 8) {
  let hits = 0;
  for (let i = 1; i < heights.length; i++) {
    if (Math.abs(heights[i] - heights[i - 1]) > threshold) {
      hits++;
      if (hits >= 2) return true;
    } else {
      hits = 0;
    }
  }
  return false;
}

/** G3-3（装饰性）：skeleton 区灰度方差，阈值由 P0 基线定，不套 WCAG。 */
export function grayVariance(px) {
  const grays = [];
  for (let i = 0; i + 2 < px.length; i += 4) {
    grays.push((px[i] + px[i + 1] + px[i + 2]) / 3);
  }
  if (!grays.length) return 0;
  const mean = grays.reduce((a, b) => a + b, 0) / grays.length;
  return grays.reduce((a, g) => a + (g - mean) ** 2, 0) / grays.length;
}

async function main() {
  const args = process.argv.slice(2);
  const fileIdx = args.indexOf('--frames');
  const dark = args.includes('--dark');
  const strideIdx = args.indexOf('--stride');
  const stride = strideIdx >= 0 ? Number(args[strideIdx + 1]) || 37 : 37;
  if (fileIdx < 0) {
    console.log('用法: node scripts/p03-frame-check.mjs --frames <frames.json> [--dark] [--stride 37]');
    process.exit(2);
  }
  const { readFileSync } = await import('node:fs');
  const frames = JSON.parse(readFileSync(args[fileIdx + 1], 'utf8'));
  let flashes = 0;
  frames.forEach((f, i) => {
    if (isFlashFrame(f.data, dark, stride)) {
      flashes++;
      console.log(`flash frame #${i}`);
    }
  });
  console.log(`checked=${frames.length} flashes=${flashes} result=${flashes === 0 ? 'PASS' : 'FAIL'}`);
  process.exit(flashes === 0 ? 0 : 1);
}

const isMain = process.argv[1] && process.argv[1].endsWith('p03-frame-check.mjs');
if (isMain) {
  main().catch((e) => {
    console.error(e);
    process.exit(1);
  });
}

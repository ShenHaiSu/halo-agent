// need22-P5 J5：构建仅清理 wwwroot/assets（不再 emptyOutDir 整个 wwwroot，宿主另放文件不受影响）。
// 由 package.json prebuild 钩子调用：node scripts/clean-assets.mjs
import { rmSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const assetsDir = resolve(here, '../../Host.Desktop/wwwroot/assets');

try {
  if (existsSync(assetsDir)) {
    rmSync(assetsDir, { recursive: true, force: true });
  }
  mkdirSync(assetsDir, { recursive: true });
  console.log(`[clean-assets] cleared ${assetsDir}`);
} catch (e) {
  console.warn('[clean-assets] failed (non-blocking):', e instanceof Error ? e.message : String(e));
}

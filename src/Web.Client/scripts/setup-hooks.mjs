// need22-P4 H4 + need22-P5 J2：hook 安装脚本（由 package.json prepare 调用）。
// 统一指向仓库根 .husky（前后端分流 pre-commit），替代旧的本包 .husky。
// 仓库根是 develop/（.git 在上两级），husky v9 要求 .git 必须在包目录下，
// 在此嵌套布局中无法直接用 `husky` 安装，故用 git 原生 core.hooksPath 指向根 .husky。
import { execSync } from 'node:child_process';
import { dirname, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const huskyDir = resolve(dirname(fileURLToPath(import.meta.url)), '../../../.husky');
try {
  const top = execSync('git rev-parse --show-toplevel', { encoding: 'utf8' }).trim().replace(/\\/g, '/');
  const rel = relative(top.replace(/\//g, sep), huskyDir).split(sep).join('/');
  execSync(`git config core.hooksPath ${rel}`, { stdio: 'inherit' });
  console.log(`[setup-hooks] core.hooksPath=${rel} (repo: ${top})`);
} catch (e) {
  console.warn('[setup-hooks] skipped (not a git checkout?):', e instanceof Error ? e.message : String(e));
}

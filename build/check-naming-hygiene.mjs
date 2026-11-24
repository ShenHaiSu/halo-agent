/**
 * 命名卫生门禁：只检查新增/修改行，不扫描历史豁免文件。
 * 过程编号只允许留在 docs/、CHANGELOG 与提交信息中；代码、注释、测试名一律业务语义。
 * 用法：node build/check-naming-hygiene.mjs [--cached]
 */
import { execFileSync } from 'node:child_process';

const cached = process.argv.includes('--cached');
const exempt = (path) =>
  /(^|\/)(docs|CHANGELOG)(\/|$)/i.test(path) ||
  /generated/i.test(path) ||
  /(^|\/)check-naming-hygiene\.mjs$/.test(path);

function gitDiff() {
  const tryGit = (args) => {
    try {
      return execFileSync('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
    } catch {
      return '';
    }
  };
  if (cached) return tryGit(['diff', '--cached', '-U0', '--diff-filter=ACMRT']);
  const working = tryGit(['diff', '-U0', '--diff-filter=ACMRT', 'HEAD']);
  if (working.trim()) return working;
  return tryGit(['diff', '-U0', '--diff-filter=ACMRT', 'HEAD~1', 'HEAD']);
}

const processNumber = /(?:need\s*[-_]?\s*\d+|P00\d|\bStage\s*\d+\b|\b[SHFTRBE][-_\s]?\d+(?:[-.]\d+)*\b|\bP[0-5][-#][A-Z]?\d+)/i;

const diff = gitDiff();
if (!diff.trim()) {
  console.log('[naming] no changed lines to check');
  process.exit(0);
}

let currentFile = '';
const offenders = [];
for (const line of diff.split(/\r?\n/)) {
  if (line.startsWith('+++ b/')) {
    currentFile = line.slice(6).trim();
    continue;
  }
  if (!line.startsWith('+') || line.startsWith('+++')) continue;
  if (!currentFile || exempt(currentFile)) continue;
  const added = line.slice(1);
  if (processNumber.test(added)) offenders.push(`${currentFile}: ${added.trim()}`);
}

if (offenders.length > 0) {
  console.error('[naming] 新增/修改行包含过程编号，请改用业务语义命名：');
  for (const item of offenders) console.error(`  ${item}`);
  process.exit(1);
}
console.log('[naming] changed lines pass hygiene check');

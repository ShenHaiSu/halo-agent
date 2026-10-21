// need21-F02/F05: 生产构建后置断言（package.json postbuild 钩子：node scripts/assert-no-mock.mjs）。
// F02: Host.Desktop/wwwroot 文本产物内无 mockWorkspace（Mock 仅 DEV&&?mock 经 @vite-ignore 动态 import 加载；
//      若命中说明 mock 被静态打进生产包，必须阻断）。
// F05+: BRIDGE_VERSION 与后端一致（前端 src/shared/bridge/protocol/version.ts 对
//      后端 src/HaloAgent.Bridge.Contracts/BridgeVersion.cs 的 ProtocolVersion；不一致即红）。
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const wwwroot = resolve(here, '../../Host.Desktop/wwwroot');
const frontendVersionTs = resolve(here, '../src/shared/bridge/protocol/version.ts');
const backendVersionCs = resolve(here, '../../HaloAgent.Bridge.Contracts/BridgeVersion.cs');
const NEEDLE = 'mockWorkspace';
const TEXT_EXTS = new Set(['.js', '.mjs', '.cjs', '.html', '.css', '.json', '.txt', '.svg']);

let failed = false;

function walk(dir, out) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) walk(p, out);
    else out.push(p);
  }
  return out;
}

// ---- 断言 1：产物无 mock ----
if (!existsSync(wwwroot)) {
  console.error(`[assert-no-mock] FAIL: wwwroot 不存在：${wwwroot}`);
  process.exit(1);
}

const hits = [];
for (const file of walk(wwwroot, [])) {
  const lower = file.toLowerCase();
  const dot = lower.lastIndexOf('.');
  if (dot < 0 || !TEXT_EXTS.has(lower.slice(dot))) continue;
  try {
    const text = readFileSync(file, 'utf8');
    if (text.includes(NEEDLE)) hits.push(file);
  } catch {
    /* 二进制/不可读跳过 */
  }
}

if (hits.length > 0) {
  console.error(`[assert-no-mock] FAIL: 生产产物含 ${NEEDLE}（${hits.length} 个文件）：`);
  for (const h of hits.slice(0, 20)) console.error(`  - ${h}`);
  failed = true;
} else {
  console.log('[assert-no-mock] OK: wwwroot 无 mockWorkspace');
}

// ---- 断言 2：BRIDGE_VERSION 前后端一致 ----
function failVersion(msg) {
  console.error(`[assert-no-mock] FAIL: ${msg}`);
  failed = true;
}

try {
  if (!existsSync(frontendVersionTs)) failVersion(`前端版本单源缺失：${frontendVersionTs}`);
  else if (!existsSync(backendVersionCs)) failVersion(`后端版本单源缺失：${backendVersionCs}`);
  else {
    const front = readFileSync(frontendVersionTs, 'utf8');
    const back = readFileSync(backendVersionCs, 'utf8');
    const mFront = front.match(/export\s+const\s+BRIDGE_VERSION\s*=\s*(\d+)/);
    const mBack = back.match(/ProtocolVersion\s*=\s*(\d+)/);
    if (!mFront) failVersion('前端 version.ts 未找到 `export const BRIDGE_VERSION = <n>`');
    else if (!mBack) failVersion('后端 BridgeVersion.cs 未找到 `ProtocolVersion = <n>`');
    else if (mFront[1] !== mBack[1]) {
      failVersion(`BRIDGE_VERSION 不一致：前端=${mFront[1]}，后端=${mBack[1]}`);
    } else {
      console.log(`[assert-no-mock] OK: BRIDGE_VERSION 前后端一致（=${mFront[1]}）`);
    }
  }
} catch (e) {
  failVersion(`版本比对异常：${e instanceof Error ? e.message : String(e)}`);
}

process.exit(failed ? 1 : 0);

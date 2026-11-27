/**
 * 发布产物断言：自包含多文件、CEF 原生件齐全、可执行文件架构与 RID 匹配。
 * 用法：node build/check-publish-artifacts.mjs win-x64|win-x86
 */
import { readFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import { join } from 'node:path';

const rid = process.argv[2] || 'win-x64';
const expectedMachine = rid === 'win-x64' ? 0x8664 : rid === 'win-x86' ? 0x014c : null;
if (expectedMachine === null) {
  console.error(`[publish] 不支持的 RID：${rid}`);
  process.exit(1);
}

const root = join(process.cwd(), 'artifacts', 'publish', rid);
if (!existsSync(root)) {
  console.error(`[publish] 产物目录不存在：${root}`);
  process.exit(1);
}

const files = readdirSync(root).filter((name) => statSync(join(root, name)).isFile());
const failures = [];
const expectFile = (name) => {
  if (!files.includes(name)) failures.push(`缺少 ${name}`);
};
expectFile('Host.Desktop.exe');
expectFile('libcef.dll');
expectFile('CefSharp.BrowserSubprocess.exe');
if (files.length < 10) failures.push(`文件数 ${files.length} 过少，疑似单文件发布`);
if (!files.some((name) => name.toLowerCase().endsWith('.pak'))) failures.push('缺少 CEF *.pak 资源');
if (!files.some((name) => name.toLowerCase().includes('icu'))) failures.push('缺少 ICU 数据');

const exePath = join(root, 'Host.Desktop.exe');
if (existsSync(exePath)) {
  const bytes = readFileSync(exePath);
  const peOffset = bytes.readUInt32LE(0x3c);
  const machine = bytes.readUInt16LE(peOffset + 4);
  if (machine !== expectedMachine) {
    failures.push(`Host.Desktop.exe 架构 0x${machine.toString(16)} 与 ${rid} 不匹配`);
  }
}

if (failures.length > 0) {
  console.error(`[publish] ${rid} 产物断言失败：`);
  for (const failure of failures) console.error(`  - ${failure}`);
  process.exit(1);
}
console.log(`[publish] ${rid} ok (${files.length} files, machine=0x${expectedMachine.toString(16)})`);

/**
 * 架构静态断言：CefSharp/WinForms/.NET 6/Chrome 109/显式平台/协议版本 2。
 * 只读检查，不修改文件；发布还须由 CI 产物断言覆盖多文件与架构。
 */
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';

const root = process.cwd();
const read = (path) => (existsSync(join(root, path)) ? readFileSync(join(root, path), 'utf8') : '');
const failures = [];
const expect = (condition, message) => {
  if (!condition) failures.push(message);
};

const packages = read('Directory.Packages.props');
expect(
  /CefSharp\.Common\.NETCore[\s\S]*?109\.1\.110/.test(packages) &&
    /CefSharp\.WinForms\.NETCore[\s\S]*?109\.1\.110/.test(packages),
  'CefSharp 版本必须为 109.1.110（WinForms + Common.NETCore）'
);

const buildProps = read('Directory.Build.props');
expect(buildProps.includes('net6.0-windows'), '目标框架必须为 net6.0-windows');

const hostCsproj = read('src/Host.Desktop/Host.Desktop.csproj');
expect(hostCsproj.includes('<UseWindowsForms>true</UseWindowsForms>'), '宿主必须启用 WinForms');
expect(!/<UseWPF>true<\/UseWPF>/.test(hostCsproj), '宿主禁止引入 WPF');
expect(/<PlatformTarget[^>]*>(x64|x86)<\/PlatformTarget>/.test(hostCsproj), '宿主必须显式指定 x64 或 x86');
expect(!/AnyCPU/i.test(hostCsproj), '宿主禁止 AnyCPU');

const vite = read('src/Web.Client/vite.config.ts');
expect(/target:\s*['"]chrome109['"]/.test(vite), 'Vite target 必须为 chrome109');
expect(/cssTarget:\s*['"]chrome109['"]/.test(vite), 'Vite cssTarget 必须为 chrome109');

const productionSources = [
  'src/Web.Client/src/main.ts',
  'src/Web.Client/index.html',
  'src/Host.Desktop/wwwroot/index.html',
  'src/Host.Desktop/MainForm.cs',
]
  .map(read)
  .join('\n');
expect(!/file:\/\//.test(productionSources), '生产入口禁止 file://');
expect(/app:\/\/local\/index\.html/.test(productionSources), '生产入口必须 app://local/index.html');

const frontendSources = read('src/Web.Client/src/shared/bridge/protocol/version.ts');
expect(/BRIDGE_VERSION\s*=\s*2\b/.test(frontendSources), '前端桥协议版本必须为 2');
const backendVersion = read('src/HaloAgent.Bridge.Contracts/BridgeVersion.cs');
expect(/ProtocolVersion\s*=\s*2\b/.test(backendVersion), '后端桥协议版本必须为 2');

const allFrontend = [
  'src/Web.Client/src/features/agent/components/MessageContextMenu.vue',
  'src/Web.Client/src/features/agent/components/TimelineView.vue',
  'src/Web.Client/src/features/agent/components/MessageTimeline.vue',
]
  .map(read)
  .join('\n');
expect(!/prefers-color-scheme/.test(allFrontend), '新增功能禁止系统暗黑模式强依赖');
expect(!/navigator\.gpu|WebGPU/.test(allFrontend), '新增功能禁止 WebGPU 强依赖');

if (failures.length > 0) {
  console.error('[architecture] 架构断言失败：');
  for (const failure of failures) console.error(`  - ${failure}`);
  process.exit(1);
}
console.log('[architecture] all static assertions passed');

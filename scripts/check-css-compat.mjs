#!/usr/bin/env node
/**
 * 产物 CSS 兼容性扫描。
 *
 * 为什么需要它：
 *   Tailwind v3 + postcss-preset-env 的配置正确时不会产出这些函数，
 *   但一旦有人：
 *     - 升级了 tailwind 到 v4
 *     - 在源码里手写了 color-mix()/oklch()
 *     - 加了某个会注入现代 CSS 的插件
 *   构建仍然"成功"，而 Chrome 109 会直接忽略整条声明甚至整个规则块。
 *   表现是"样式莫名其妙不对"，极难排查。
 *
 * 这个脚本把问题拦在构建阶段，并打印出现位置。
 *
 * 用法（两种都支持）：
 *   node scripts/check-css-compat.mjs                 # 从脚本位置推断 wwwroot
 *   node scripts/check-css-compat.mjs <产物目录>       # 显式指定目录
 */
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join, relative, resolve, isAbsolute } from 'node:path'
import { dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = resolve(here, '..')

const FORBIDDEN = [
  { needle: 'color-mix(', reason: 'Chrome 111+ 才有' },
  { needle: 'oklch(', reason: 'Chrome 111+ 才有' },
  { needle: 'oklab(', reason: 'Chrome 111+ 才有' },
  { needle: 'lab(', reason: 'Chrome 111+ 才有' },
  { needle: 'light-dark(', reason: 'Chrome 123+ 才有' },
  { needle: '@property', reason: '在 Chrome 109 上对注册属性的继承行为与新版不一致' },
  { needle: '@container style(', reason: '样式查询 Chrome 111+ 才有' },
  { needle: 'view-transition', reason: 'Chrome 111+ 才有' },
  { needle: 'anchor(', reason: '锚点定位 Chrome 125+ 才有' },
  { needle: 'popover', reason: 'Popover API Chrome 114+ 才有' },
]

// 默认目录：相对脚本位置解析（无论从哪个 cwd 调用都正确）
const arg = process.argv[2]
const root = arg
  ? (isAbsolute(arg) ? arg : resolve(process.cwd(), arg))
  : resolve(repoRoot, 'src/Host.Desktop/wwwroot')

function walk(dir) {
  const out = []
  for (const name of readdirSync(dir)) {
    const p = join(dir, name)
    const st = statSync(p)
    if (st.isDirectory()) out.push(...walk(p))
    else if (name.endsWith('.css')) out.push(p)
  }
  return out
}

let failed = false
let files
try {
  files = walk(root)
} catch (e) {
  console.error(`[css-check] 找不到产物目录: ${root}`)
  console.error('[css-check] 先跑 vite build，或检查 outDir 配置')
  process.exit(1)
}

if (files.length === 0) {
  console.error(`[css-check] ${root} 下没有 CSS 产物`)
  process.exit(1)
}

for (const file of files) {
  const text = readFileSync(file, 'utf8')
  for (const { needle, reason } of FORBIDDEN) {
    let idx = text.indexOf(needle)
    while (idx !== -1) {
      // 取上下文，方便定位
      const start = Math.max(0, idx - 60)
      const end = Math.min(text.length, idx + needle.length + 60)
      const snippet = text.slice(start, end).replace(/\s+/g, ' ')
      console.error(`[css-check] ✗ ${relative(root, file)}`)
      console.error(`            出现 "${needle}"（${reason}）`)
      console.error(`            上下文: …${snippet}…`)
      failed = true
      idx = text.indexOf(needle, idx + 1)
      if (idx > 0 && !failed) break
    }
  }
}

if (failed) {
  console.error('')
  console.error('[css-check] 构建失败：产物包含 Chrome 109 不支持的语法。')
  console.error('[css-check] 常见修法：')
  console.error('  - 检查 tailwindcss 是否为 3.4.x（不是 4.x）')
  console.error('  - 检查源码是否手写了 color-mix()/oklch()，改用 rgb(var(--x) / a)')
  console.error('  - 检查是否引入了会注入现代 CSS 的插件')
  process.exit(1)
}

console.log(`[css-check] ✓ ${files.length} 个 CSS 文件全部兼容 Chrome 109`)

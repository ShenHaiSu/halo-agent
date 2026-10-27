#!/usr/bin/env node
/**
 * 从 contract/agent.schema.json 生成 TypeScript 类型。
 *
 * 为什么自己写而不引 json-schema-to-typescript：
 *   - 那个包很重（拖入一堆依赖），而我们的 schema 结构非常规整；
 *   - 生成结果要稳定（幂等），自己写能完全控制输出格式；
 *   - 我们的 schema 里混了自定义元数据（methods/errorCodes），需要特殊处理。
 *
 * 输出：src/Web.Client/src/contract/agent.d.ts
 */
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { dirname, resolve, isAbsolute } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = resolve(here, '..')

const schemaPath = resolve(repoRoot, 'contract/agent.schema.json')
const outPath = resolve(repoRoot, 'src/Web.Client/src/contract/agent.d.ts')

const schema = JSON.parse(readFileSync(schemaPath, 'utf8'))

/** JSON Schema type → TS type */
function tsType(node) {
  if (!node) return 'unknown'
  if (node.$ref) {
    const name = node.$ref.split('/').pop()
    return pascal(name)
  }
  const t = node.type
  if (Array.isArray(t)) {
    return t.map((x) => tsType({ ...node, type: x })).join(' | ')
  }
  switch (t) {
    case 'string':
      return 'string'
    case 'integer':
    case 'number':
      return 'number'
    case 'boolean':
      return 'boolean'
    case 'null':
      return 'null'
    case 'object': {
      const req = new Set(node.required ?? [])
      const props = node.properties ?? {}
      const entries = Object.entries(props)
      if (entries.length === 0) return 'Record<string, unknown>'
      const body = entries
        .map(([k, v]) => {
          const opt = req.has(k) ? '' : '?'
          const desc = v.description ? `  /** ${v.description} */\n` : ''
          return `${desc}  ${k}${opt}: ${tsType(v)}`
        })
        .join('\n')
      return `{\n${body}\n}`
    }
    case 'array':
      return `${tsType(node.items)}[]`
    default:
      return 'unknown'
  }
}

function pascal(s) {
  return s.replace(/(^\w|-\w)/g, (m) => m.replace('-', '').toUpperCase())
}

const lines = []
lines.push('// ⚠️ 本文件由 scripts/gen-contract-types.mjs 自动生成，请勿手改。')
lines.push(`// 来源: contract/agent.schema.json（协议版本 ${schema.version}）`)
lines.push('')

// 1) 信封定义
for (const [name, def] of Object.entries(schema.definitions)) {
  lines.push(`/** ${def.description ?? name} */`)
  lines.push(`export type ${pascal(name)} = ${tsType(def)}`)
  lines.push('')
}

// 2) 方法表
lines.push('export interface HaloMethods {')
for (const [method, def] of Object.entries(schema.methods ?? {})) {
  const result = def.result ? tsType(def.result) : 'void'
  lines.push(`  /** ${method} */`)
  lines.push(`  '${method}': { params: ${def.params ? tsType(def.params) : 'void'}; result: ${result} }`)
}
lines.push('}')
lines.push('')

// 3) 错误码
lines.push('export const HaloErrorCodes = {')
for (const [code, desc] of Object.entries(schema.errorCodes ?? {})) {
  lines.push(`  '${code}': '${desc}',`)
}
lines.push('} as const')
lines.push('export type HaloErrorCode = keyof typeof HaloErrorCodes')
lines.push('')

// 4) 便捷联合类型
lines.push('export type HaloMethod = keyof HaloMethods')
lines.push("export type HaloParams<M extends HaloMethod> = HaloMethods[M]['params']")
lines.push("export type HaloResult<M extends HaloMethod> = HaloMethods[M]['result']")
lines.push('')

const content = lines.join('\n')
mkdirSync(dirname(outPath), { recursive: true })

// 幂等：内容没变就不写，避免触发无意义的 watch 重建
let prev = ''
try {
  prev = readFileSync(outPath, 'utf8')
} catch {}
if (prev === content) {
  console.log('[gen-contract] 未变化，跳过写入')
} else {
  writeFileSync(outPath, content, 'utf8')
  console.log(`[gen-contract] 已生成 ${outPath}`)
}

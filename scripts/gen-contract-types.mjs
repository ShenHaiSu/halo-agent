#!/usr/bin/env node
/**
 * 从 contract/agent.schema.json 生成 TypeScript 类型。
 *
 * 为什么自己写而不引 json-schema-to-typescript：
 *   - 那个包很重（拖入一堆依赖），而我们的 schema 结构非常规整；
 *   - 生成结果要稳定（幂等），自己写能完全控制输出格式；
 *   - 我们的 schema 里混了自定义元数据（methods/errorCodes），需要特殊处理。
 *
 * 解析的顶层字段：contractVersion / envelope / types / methods / events / errorCodes。
 * 输出：src/Web.Client/src/contract/agent.d.ts
 *
 * 幂等：同样输入产生同样输出；内容无变化时跳过写入
 * （调用方用 `node scripts/gen-contract-types.mjs` 跑两遍，
 * 第二次必须输出"未变化，跳过写入"）。
 */
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = resolve(here, '..')

const schemaPath = resolve(repoRoot, 'contract/agent.schema.json')
const outPath = resolve(repoRoot, 'src/Web.Client/src/contract/agent.d.ts')

const schema = JSON.parse(readFileSync(schemaPath, 'utf8'))

if (typeof schema.contractVersion !== 'number') {
  throw new Error('schema 缺少 contractVersion（number）')
}
for (const key of ['envelope', 'types', 'methods', 'events', 'errorCodes']) {
  if (schema[key] === null || typeof schema[key] !== 'object') {
    throw new Error(`schema 缺少 ${key}（object）`)
  }
}

/** JSON Schema type → TS type */
function tsType(node) {
  if (node === null || node === undefined) return 'unknown'
  if (typeof node !== 'object') return 'unknown'
  if (typeof node.$ref === 'string') {
    return refName(node.$ref)
  }
  if (Array.isArray(node.oneOf)) {
    return node.oneOf.map(tsType).join(' | ')
  }
  if (Array.isArray(node.anyOf)) {
    return node.anyOf.map(tsType).join(' | ')
  }
  if (node.const !== undefined) {
    return JSON.stringify(node.const)
  }
  if (Array.isArray(node.enum)) {
    return node.enum.map((v) => JSON.stringify(v)).join(' | ')
  }
  const t = node.type
  if (Array.isArray(t)) {
    return t.map((x) => tsType({ ...node, type: x, enum: undefined, const: undefined, oneOf: undefined, anyOf: undefined })).join(' | ')
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
          const safe = /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(k) ? k : JSON.stringify(k)
          const desc = v && typeof v === 'object' && typeof v.description === 'string'
            ? `  /** ${v.description} */\n`
            : ''
          return `${desc}  ${safe}${opt}: ${tsType(v)}`
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

function refName(ref) {
  const name = String(ref).split('/').pop()
  if (!name) throw new Error(`无法解析 $ref：${ref}`)
  return name
}

const lines = []
lines.push('// [注意] 本文件由 scripts/gen-contract-types.mjs 自动生成，请勿手改。')
lines.push(`// 来源: contract/agent.schema.json（协议版本 ${schema.contractVersion}）`)
lines.push('')

// ---- 1) types：全部具名类型 ----
for (const [name, def] of Object.entries(schema.types ?? {})) {
  const desc = def && typeof def === 'object' && typeof def.description === 'string'
    ? def.description
    : name
  lines.push(`/** ${desc} */`)
  if (def && typeof def === 'object' && def.type === 'string' && Array.isArray(def.enum)) {
    lines.push(`export type ${name} = ${tsType(def)}`)
  } else if (def && typeof def === 'object' && (def.type === 'object' || def.type === undefined) && def.properties) {
    lines.push(`export interface ${name} ${tsType(def)}`)
  } else {
    lines.push(`export type ${name} = ${tsType(def)}`)
  }
  lines.push('')
}

// ---- 2) 信封：RequestFrame / ResponseFrame / EventFrame ----
const envelopeMap = { request: 'RequestFrame', response: 'ResponseFrame', event: 'EventFrame' }
for (const [key, typeName] of Object.entries(envelopeMap)) {
  const def = schema.envelope[key]
  if (!def) throw new Error(`schema.envelope 缺少 ${key}`)
  const desc = typeof def.description === 'string' ? def.description : `帧信封：${key}`
  lines.push(`/** ${desc} */`)
  lines.push(`export interface ${typeName} ${tsType(def)}`)
  lines.push('')
}
// 出站帧联合（事件或应答都走同一个 FIFO，顺序见 04-实现方法-下 §2.3）
lines.push('/** 出站帧：要么是事件，要么是应答（统一走一个 FIFO，保证顺序）。 */')
lines.push('export type HaloFrame = RequestFrame | ResponseFrame | EventFrame')
lines.push('')

// ---- 3) 方法表 ----
lines.push('/** 方法表：params 为 null 的方法映射为 void（调用时可省略）。 */')
lines.push('export interface HaloMethods {')
for (const [method, def] of Object.entries(schema.methods ?? {})) {
  const params = def.params === null || def.params === undefined ? 'void' : tsType(def.params)
  const result = def.result === null || def.result === undefined ? 'void' : tsType(def.result)
  lines.push(`  /** ${method} */`)
  lines.push(`  '${method}': { params: ${params}; result: ${result} }`)
}
lines.push('}')
lines.push('')

// ---- 4) 事件载荷表 ----
lines.push('/** 事件载荷表：kind → payload。ask.* 的 payload 即 PendingAsk。 */')
lines.push('export interface HaloEventPayloads {')
for (const [kind, def] of Object.entries(schema.events ?? {})) {
  const payload = def.payload === null || def.payload === undefined ? 'void' : tsType(def.payload)
  lines.push(`  /** ${kind} */`)
  lines.push(`  '${kind}': ${payload}`)
}
lines.push('}')
lines.push('')
lines.push('export type HaloEventKind = keyof HaloEventPayloads')
lines.push('export type HaloEventPayload<K extends HaloEventKind> = HaloEventPayloads[K]')
lines.push('')

// ---- 5) 错误码 ----
lines.push('/** 错误码 → 中文说明（与 schema.errorCodes 一致）。 */')
lines.push('export const HaloErrorCodes = {')
for (const [code, desc] of Object.entries(schema.errorCodes ?? {})) {
  lines.push(`  '${code}': '${desc}',`)
}
lines.push('} as const')
lines.push('export type HaloErrorCode = keyof typeof HaloErrorCodes')
lines.push('')

// ---- 6) 方法 / 事件清单 ----
lines.push('/** 全部方法名（与 schema.methods 的键集合一致）。 */')
lines.push('export const METHODS = [')
for (const method of Object.keys(schema.methods ?? {})) {
  lines.push(`  '${method}',`)
}
lines.push('] as const')
lines.push('')
lines.push('/** 全部事件 kind（与 schema.events 的键集合一致）。 */')
lines.push('export const EVENTS = [')
for (const kind of Object.keys(schema.events ?? {})) {
  lines.push(`  '${kind}',`)
}
lines.push('] as const')
lines.push('')

// ---- 7) 便捷别名 ----
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

// ⚠️ 本文件由 scripts/gen-contract-types.mjs 自动生成，请勿手改。
// 来源: contract/agent.schema.json（协议版本 1）

/** request */
export type Request = {
  t: unknown
  /** 请求序号，单调递增 */
  id: number
  /** 方法名，点分命名空间 */
  m: string
  /** 方法参数，camelCase */
  p?: Record<string, unknown> | null
}

/** response */
export type Response = {
  t: unknown
  id: number
  ok: boolean
  r?: Record<string, unknown> | null
  e?: {
  code: string
  msg: string
  hint?: string | null
} | null
}

/** event */
export type Event = {
  t: unknown
  seq: number
  k: string
  p?: Record<string, unknown> | null
}

/** frame */
export type Frame = unknown

export interface HaloMethods {
  /** system.ping */
  'system.ping': { params: void; result: {
  pong: boolean
  version: string
  protocol: number
  pid: number
  uptimeMs?: number
  serverTimeMs?: number
} }
  /** system.info */
  'system.info': { params: void; result: {
  version?: string
  protocol?: number
  pid?: number
  osVersion?: string
  is64BitOs?: boolean
  is64BitProcess?: boolean
  runtime?: string
  dataDir?: string
  startedAtMs?: number
} }
}

export const HaloErrorCodes = {
  'method.notFound': '方法不存在',
  'internal': '引擎内部错误',
  'protocol.version': '协议版本不匹配',
} as const
export type HaloErrorCode = keyof typeof HaloErrorCodes

export type HaloMethod = keyof HaloMethods
export type HaloParams<M extends HaloMethod> = HaloMethods[M]['params']
export type HaloResult<M extends HaloMethod> = HaloMethods[M]['result']

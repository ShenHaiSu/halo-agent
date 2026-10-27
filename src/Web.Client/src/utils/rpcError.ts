// src/Web.Client/src/utils/rpcError.ts
// P07 错误呈现规范：错误码 → 用户可读文案。

export interface RpcErrorLike {
  code?: string;
  message?: string;
  hint?: string;
}

/**
 * 把 RPC 错误转成用户可读文案。
 * 原则（F-2）：任何错误都必须有用户能理解的一句话 + 可选的处理建议。
 */
export function describeRpcError(e: unknown): { title: string; detail?: string } {
  const err = e as RpcErrorLike;
  const code = err?.code ?? '';

  const titles: Record<string, string> = {
    'method.notFound': '功能不存在（版本不匹配）',
    'params.invalid': '输入不合法',
    'protocol.version': '协议版本不匹配',
    'state.busy': '正在处理上一轮任务',
    'state.notConfigured': '尚未配置模型',
    'session.notFound': '会话不存在',
    'asset.notFound': '资源不存在',
    'fs.notFound': '路径不存在',
    'fs.escape': '路径越界（不允许访问）',
    'llm.error': '模型服务返回错误',
    'llm.timeout': '模型响应超时',
    'tool.failed': '工具执行失败',
    'ask.stale': '该确认已过期',
    'notSupported': '该功能暂未实现',
    'internal': '内部错误',
  };

  return {
    title: titles[code] ?? '操作失败',
    detail: err?.hint ?? err?.message,
  };
}

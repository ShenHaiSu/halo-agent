import type { StateSnapshot } from '@/contract/agent'

/**
 * 空快照（Mock 的初始状态）。
 *
 * 字段必须与 contract/agent.schema.json 的 StateSnapshot 完全一致，
 * 否则前端 store 的初始化会有 undefined 陷阱。
 */
export function buildEmptySnapshot(): StateSnapshot {
  return {
    protocol: 1,
    engine: {
      version: 'mock-0.1.0',
      pid: 9999,
      startedAtMs: Date.now(),
      osVersion: '6.1.7601',
      isWin7: true,
    },
    session: {
      id: 's-mock-1',
      cwd: 'C:\\work\\proj',
      historyPath: null,
    },
    agent: {
      busy: false,
      turnId: null,
      mode: 'ask',
      modeLabel: '询问',
      persona: '',
      model: 'mock-model',
      provider: 'mock',
      baseUrl: 'https://mock.local/v1',
      apiKeySet: true,
      contextWindow: 128000,
      maxTurns: 200,
      retry: 0,
      configured: true,
      round: { used: 0, limit: 200 },
    },
    usage: {
      inputTotal: 0,
      outputTotal: 0,
      lastPromptTokens: null,
    },
    queue: [],
    pendingAsk: null,
    messages: [],
    notes: [],
    update: { available: false, latestVersion: null },
  }
}

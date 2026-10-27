// src/Web.Client/src/bridge/mock/behaviors.ts
// P07 Mock 行为表：必须与 EngineAssembly 注册表保持方法名集合与成功/失败语义一致。

export type MockBehavior =
  | { kind: 'ok'; run: (params: unknown) => unknown }
  | { kind: 'throws'; code: string; message: string };

function mockSnapshot(): unknown {
  return {
    protocol: 1,
    engine: { version: '0.0.0-mock', pid: 0, startedAtMs: Date.now(), osVersion: 'mock', isWin7: false },
    session: { id: 's-mock-1', cwd: '(mock)', historyPath: null },
    agent: {
      busy: false,
      turnId: null,
      mode: 'ask',
      modeLabel: '询问模式',
      persona: '',
      model: 'mock-model',
      provider: 'mock',
      baseUrl: 'https://mock.local/v1',
      apiKeySet: false,
      contextWindow: 128000,
      maxTurns: 200,
      retry: 0,
      configured: false,
      round: { used: 0, limit: 200 },
    },
    usage: { input: 0, output: 0 },
    queue: { length: 0 },
    messages: [],
  };
}

function mockSettings(): unknown {
  return {
    provider: 'ollama',
    model: 'llama3',
    baseUrl: 'http://localhost:11434/v1',
    apiKeySet: false,
    apiKeyEnv: null,
    contextWindow: 128000,
    maxTurns: 200,
    retry: 0,
    proxy: { mode: 'auto', url: null },
    headers: [],
    extraHeadersEnabled: true,
    providers: [{ name: 'ollama', model: 'llama3', baseUrl: 'http://localhost:11434/v1' }],
    dataDir: '(mock)',
    configPath: '(mock)/config.json',
    network: {
      mode: 'auto',
      url: null,
      retry: 0,
      headers: [],
      headersEnabled: true,
      providerHeaders: {},
    },
  };
}

export const BEHAVIORS: Record<string, MockBehavior> = {
  'system.ping': { kind: 'ok', run: () => ({ pong: true, version: '0.0.0-mock', protocol: 1, pid: 0, uptimeMs: 12345, serverTimeMs: Date.now() }) },
  'system.info': { kind: 'ok', run: () => ({ version: '0.0.0-mock', protocol: 1, pid: 0, osVersion: 'mock', isWin7: false, is64BitOs: true, is64BitProcess: true, runtime: 'mock', dataDir: '(mock)', startedAtMs: Date.now() }) },
  'system.protocol': { kind: 'ok', run: () => ({ version: 1, encoding: 'ndjson', maxFrameBytes: 16777216, methods: Object.keys(BEHAVIORS), events: [] }) },
  'system.diagnostics': { kind: 'ok', run: () => ({}) },
  'system.shutdown': { kind: 'ok', run: () => ({ ok: true }) },

  'agent.prompt': { kind: 'ok', run: () => ({ accepted: true, turnId: 'mock-turn' }) },
  'agent.queue': { kind: 'ok', run: () => ({ queued: true, position: 1 }) },
  'agent.unqueue': { kind: 'ok', run: () => ({ removed: true }) },
  'agent.cancel': { kind: 'ok', run: () => ({ cancelled: true }) },
  'agent.answer': { kind: 'ok', run: () => ({ accepted: true, reason: null }) },
  'agent.drain': { kind: 'ok', run: () => ({ text: null }) },
  'agent.resync': { kind: 'ok', run: () => mockSnapshot() },

  'session.new': { kind: 'ok', run: () => ({ id: 's-mock' }) },
  'session.resume': { kind: 'ok', run: () => ({ count: 0, messages: [] }) },
  'session.list': { kind: 'ok', run: () => ({ items: [] }) },
  'session.rename': { kind: 'ok', run: () => ({ ok: true }) },
  'session.delete': { kind: 'ok', run: () => ({ ok: true }) },
  'session.clear': { kind: 'ok', run: () => ({ ok: true }) },
  'session.compact': { kind: 'ok', run: () => ({ removed: 0, summary: '' }) },
  'session.setCwd': { kind: 'ok', run: (p) => ({ cwd: (p as { path?: string } | null)?.path ?? '(mock)' }) },

  'state.snapshot': { kind: 'ok', run: () => mockSnapshot() },
  'state.setMode': { kind: 'ok', run: (p) => ({ mode: (p as { mode?: string } | null)?.mode ?? 'ask', label: 'Mock' }) },
  'state.setPersona': { kind: 'ok', run: (p) => ({ name: (p as { name?: string } | null)?.name ?? '' }) },
  'state.setProvider': { kind: 'ok', run: (p) => ({ provider: (p as { name?: string } | null)?.name ?? 'mock', model: 'mock-model', baseUrl: 'https://mock.local/v1' }) },
  'state.setMaxTurns': { kind: 'ok', run: (p) => ({ maxTurns: (p as { value?: number } | null)?.value ?? 200 }) },
  'state.setRetry': { kind: 'ok', run: (p) => ({ retry: (p as { value?: number } | null)?.value ?? 0 }) },

  'settings.get': { kind: 'ok', run: () => mockSettings() },
  'settings.saveModel': { kind: 'ok', run: () => ({ ok: true, notes: [] }) },
  'settings.saveNetwork': { kind: 'ok', run: () => ({ ok: true, notes: [] }) },
  'settings.probeModels': { kind: 'ok', run: () => ({ models: ['mock-model'] }) },
  'settings.saveExtraHeaders': { kind: 'ok', run: () => ({ ok: true, notes: [] }) },
  'settings.mcp.get': { kind: 'ok', run: () => ({ path: '(mock)/mcp.json', text: '{"mcpServers":{}}' }) },
  'settings.mcp.save': { kind: 'ok', run: () => ({ ok: true, notes: [] }) },

  'asset.skills.list': { kind: 'ok', run: () => ({ items: [], warnings: [] }) },
  'asset.skills.get': { kind: 'ok', run: (p) => ({ name: (p as { name?: string } | null)?.name ?? '', body: '', source: 'user' }) },
  'asset.skills.save': { kind: 'ok', run: () => ({ ok: true }) },
  'asset.skills.delete': { kind: 'ok', run: () => ({ ok: true }) },
  'asset.skills.run': { kind: 'throws', code: 'notSupported', message: '技能执行尚未接通' },
  'asset.themes.list': { kind: 'ok', run: () => ({ items: [] }) },
  'asset.themes.get': { kind: 'throws', code: 'asset.notFound', message: 'Mock 环境无用户主题' },
  'asset.personas.list': { kind: 'ok', run: () => ({ items: [] }) },
  'asset.personas.get': { kind: 'ok', run: (p) => ({ name: (p as { name?: string } | null)?.name ?? '', body: '' }) },
  'asset.personas.save': { kind: 'ok', run: () => ({ ok: true }) },
  'asset.memory.list': { kind: 'ok', run: () => ({ items: [] }) },
  'asset.memory.get': { kind: 'ok', run: () => ({ name: '', description: '', mtype: '', scope: '', body: '' }) },
  'asset.memory.save': { kind: 'ok', run: () => ({ ok: true, filename: 'mock.md' }) },
  'asset.memory.delete': { kind: 'ok', run: () => ({ ok: true }) },

  'tool.list': { kind: 'ok', run: () => ({ items: [{ name: 'read_file', description: '读文件', source: 'builtin' }, { name: 'write_file', description: '写文件', source: 'builtin' }] }) },
  'tool.undoList': { kind: 'ok', run: () => ({ items: [] }) },
  'tool.undoRollback': { kind: 'ok', run: () => ({ ok: true, orig: '' }) },
  'tool.mcpStatus': { kind: 'ok', run: () => ({ servers: [] }) },

  'fs.listDir': { kind: 'ok', run: () => ({ items: [] }) },
  'fs.completePaths': { kind: 'ok', run: () => ({ items: [] }) },
  'fs.validatePath': { kind: 'ok', run: (p) => ({ valid: true, normalized: (p as { path?: string } | null)?.path ?? '', isDir: true }) },

  'update.check': { kind: 'ok', run: () => ({ available: false, latestVersion: null, notes: 'Mock 环境无更新源' }) },
  'update.install': { kind: 'ok', run: () => ({ started: false }) },
};

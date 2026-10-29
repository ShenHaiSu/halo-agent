// src/Web.Client/src/bridge/mock/behaviors.ts
// P10 Mock 行为表：必须与 EngineAssembly 注册表保持方法名集合与成功/失败语义一致。

export type MockBehavior =
  | { kind: 'ok'; run: (params: unknown) => unknown }
  | { kind: 'throws'; code: string; message: string };

// P09-fix2 D：Mock 多会话历史（?mock=1 下可复现切换，不再恒返空历史）。
export interface MockChatMessage {
  id: string;
  role: string;
  text: string;
  tsMs: number;
}

interface MockSessionData {
  id: string;
  title: string;
  cwd: string | null;
  historyPath: string | null;
  headless: boolean;
  mtimeMs: number;
  messages: MockChatMessage[];
}

let mockMsgSeq = 0;
function mockMsg(role: string, text: string, tsMs: number): MockChatMessage {
  mockMsgSeq += 1;
  return { id: `m-mock-${mockMsgSeq}`, role, text, tsMs };
}

const mockNow = Date.now();
const mockSessions = new Map<string, MockSessionData>([
  [
    's-mock-1',
    {
      id: 's-mock-1',
      title: 'Mock 会话一',
      cwd: '(mock)',
      historyPath: '(mock)/s-mock-1.jsonl',
      headless: false,
      mtimeMs: mockNow - 3600000,
      messages: [
        mockMsg('user', '帮我看看这个项目用了哪些依赖', mockNow - 3600000),
        mockMsg('assistant', '## 分析结果\n\n- 找到 3 个文件\n- 其中 `config.json` 需要修改', mockNow - 3590000),
      ],
    },
  ],
  [
    's-mock-2',
    {
      id: 's-mock-2',
      title: 'Mock 会话二',
      cwd: '(mock)',
      historyPath: '(mock)/s-mock-2.jsonl',
      headless: false,
      mtimeMs: mockNow - 7200000,
      messages: [
        mockMsg('user', '找出所有 TODO 并整理成清单', mockNow - 7200000),
        mockMsg('assistant', '- [ ] 接入真实后端\n- [x] Mock 多会话历史', mockNow - 7190000),
        mockMsg('user', '这个文件里的报错是什么原因', mockNow - 7180000),
        mockMsg('assistant', '缺少空值检查，加上 `?? []` 兜底即可。', mockNow - 7170000),
      ],
    },
  ],
]);

let currentMockSessionId = 's-mock-1';
let mockSessionSeq = 2;

function currentMockSession(): MockSessionData {
  let s = mockSessions.get(currentMockSessionId);
  if (!s) {
    s = mockSessions.values().next().value as MockSessionData;
    currentMockSessionId = s.id;
  }
  return s;
}

function mockSummary(s: MockSessionData): unknown {
  const userMsgs = s.messages.filter((m) => m.role === 'user').length;
  return {
    id: s.id,
    title: s.title,
    note: null,
    cwd: s.cwd,
    headless: s.headless,
    mtimeMs: s.mtimeMs,
    userMsgs,
    assistantMsgs: s.messages.length - userMsgs,
  };
}

function mockMessageViews(s: MockSessionData): unknown[] {
  return s.messages.map((m) => ({ id: m.id, role: m.role, text: m.text, tsMs: m.tsMs, hasImages: false, attachmentCount: 0 }));
}

/** Mock 用户发言 / 回放落盘：追加进当前会话，切走切回仍在。 */
export function mockAppendMessage(role: string, text: string): void {
  currentMockSession().messages.push(mockMsg(role, text, Date.now()));
}

/** 当前 Mock 会话 id（供 mockAgent 保持 prompt 归属一致）。 */
export function getCurrentMockSessionId(): string {
  return currentMockSessionId;
}

function mockSnapshot(sessionId?: string): unknown {
  const s = (sessionId ? mockSessions.get(sessionId) : undefined) ?? currentMockSession();
  return {
    protocol: 1,
    engine: { version: '0.0.0-mock', pid: 0, startedAtMs: Date.now(), osVersion: 'mock', isWin7: false },
    session: { id: s.id, cwd: s.cwd, historyPath: s.historyPath },
    agent: {
      busy: false,
      turnId: null,
      mode: 'ask',
      modeLabel: '询问模式',
      persona: '',
      provider: 'deepseek',
      providerDisplay: 'DeepSeek',
      model: 'deepseek-chat',
      modelDisplay: 'deepseek-chat',
      protocol: 'chat',
      baseUrl: 'https://api.deepseek.com/v1',
      apiKeySet: true,
      contextWindow: 65536,
      maxTokens: null,
      acceptsImages: false,
      includeReasoning: true,
      proxyInUse: false,
      maxTurns: 200,
      retry: 0,
      configured: true,
      round: { used: 0, limit: 200 },
    },
    usage: { input: 0, output: 0 },
    queue: { length: 0 },
    messages: mockMessageViews(s),
  };
}

function mockSettings(): unknown {
  return {
    activeProvider: 'deepseek',
    activeModel: 'deepseek-chat',
    migrationRequired: false,
    providers: [
      {
        id: 'deepseek',
        displayName: 'DeepSeek',
        baseUrl: 'https://api.deepseek.com/v1',
        apiKeySet: true,
        headersEnabled: true,
        headers: [],
        useProxy: true,
        modelCount: 2,
        models: [
          {
            id: 'deepseek-chat',
            displayName: 'deepseek-chat',
            protocol: 'chat',
            contextWindow: 65536,
            maxTokens: null,
            acceptsImages: false,
            includeReasoningHistory: true,
          },
          {
            id: 'shared-name',
            displayName: 'Shared',
            protocol: 'chat',
            contextWindow: 128000,
            maxTokens: null,
            acceptsImages: true,
            includeReasoningHistory: true,
          },
        ],
      },
      {
        id: 'openai',
        displayName: 'OpenAI',
        baseUrl: 'https://api.openai.com/v1',
        apiKeySet: false,
        headersEnabled: true,
        headers: [],
        useProxy: true,
        modelCount: 2,
        models: [
          {
            id: 'gpt-4o',
            displayName: 'GPT-4o',
            protocol: 'chat',
            contextWindow: 128000,
            maxTokens: null,
            acceptsImages: true,
            includeReasoningHistory: true,
          },
          {
            id: 'shared-name',
            displayName: 'Shared (OpenAI)',
            protocol: 'chat',
            contextWindow: 64000,
            maxTokens: null,
            acceptsImages: false,
            includeReasoningHistory: true,
          },
        ],
      },
    ],
    proxy: { mode: 'auto', url: null },
    retry: 0,
    maxTurns: 200,
    persona: '',
    dataDir: '(mock)',
    configPath: '(mock)/config.json',
  };
}

export const BEHAVIORS: Record<string, MockBehavior> = {
  'system.ping': { kind: 'ok', run: () => ({ pong: true, version: '0.0.0-mock', protocol: 1, pid: 0, uptimeMs: 12345, serverTimeMs: Date.now() }) },
  'system.info': { kind: 'ok', run: () => ({ version: '0.0.0-mock', protocol: 1, pid: 0, osVersion: 'mock', isWin7: false, is64BitOs: true, is64BitProcess: true, runtime: 'mock', dataDir: '(mock)', startedAtMs: Date.now() }) },
  'system.protocol': { kind: 'ok', run: () => ({ version: 1, encoding: 'ndjson', maxFrameBytes: 16777216, methods: Object.keys(BEHAVIORS), events: [] }) },
  'system.diagnostics': { kind: 'ok', run: () => ({}) },
  'system.shutdown': { kind: 'ok', run: () => ({ ok: true }) },

  'agent.prompt': {
    kind: 'ok',
    run: (p) => {
      const text = (p as { text?: string } | null)?.text ?? '(空)';
      mockAppendMessage('user', text);
      return { accepted: true, turnId: 'mock-turn' };
    },
  },
  'agent.queue': { kind: 'ok', run: () => ({ queued: true, position: 1 }) },
  'agent.unqueue': { kind: 'ok', run: () => ({ removed: true }) },
  'agent.cancel': { kind: 'ok', run: () => ({ cancelled: true }) },
  'agent.answer': { kind: 'ok', run: () => ({ accepted: true, reason: null }) },
  'agent.drain': { kind: 'ok', run: () => ({ text: null }) },
  'agent.resync': { kind: 'ok', run: () => mockSnapshot() },

  'session.new': {
    kind: 'ok',
    run: () => {
      mockSessionSeq += 1;
      const id = `s-mock-${mockSessionSeq}`;
      mockSessions.set(id, {
        id,
        title: '新会话',
        cwd: '(mock)',
        historyPath: `(mock)/${id}.jsonl`,
        headless: false,
        mtimeMs: Date.now(),
        messages: [],
      });
      currentMockSessionId = id;
      return { id };
    },
  },
  'session.resume': {
    kind: 'ok',
    run: (p) => {
      const id = (p as { id?: string } | null)?.id;
      if (id && mockSessions.has(id)) currentMockSessionId = id;
      const s = currentMockSession();
      return { count: s.messages.length, messages: mockMessageViews(s) };
    },
  },
  'session.list': { kind: 'ok', run: () => ({ items: [...mockSessions.values()].map(mockSummary) }) },
  'session.rename': {
    kind: 'ok',
    run: (p) => {
      const o = (p as { id?: string; note?: string } | null) ?? {};
      const s = o.id ? mockSessions.get(o.id) : undefined;
      if (s && typeof o.note === 'string') s.title = o.note.trim() || s.title;
      return { ok: true };
    },
  },
  'session.delete': {
    kind: 'ok',
    run: (p) => {
      const id = (p as { id?: string } | null)?.id;
      if (id) {
        mockSessions.delete(id);
        if (currentMockSessionId === id) {
          currentMockSessionId = mockSessions.keys().next().value as string ?? id;
        }
      }
      return { ok: true };
    },
  },
  'session.clear': { kind: 'ok', run: () => ({ ok: true }) },
  'session.compact': { kind: 'ok', run: () => ({ removed: 0, summary: '' }) },
  'session.setCwd': { kind: 'ok', run: (p) => ({ cwd: (p as { path?: string } | null)?.path ?? '(mock)' }) },

  'state.snapshot': { kind: 'ok', run: () => mockSnapshot() },
  'state.setMode': { kind: 'ok', run: (p) => ({ mode: (p as { mode?: string } | null)?.mode ?? 'ask', label: 'Mock' }) },
  'state.setPersona': { kind: 'ok', run: (p) => ({ name: (p as { name?: string } | null)?.name ?? '' }) },
  'state.setMaxTurns': { kind: 'ok', run: (p) => ({ maxTurns: (p as { value?: number } | null)?.value ?? 200 }) },
  'state.setRetry': { kind: 'ok', run: (p) => ({ retry: (p as { value?: number } | null)?.value ?? 0 }) },

  'settings.get': { kind: 'ok', run: () => mockSettings() },
  'settings.saveProviders': { kind: 'ok', run: () => ({ ok: true, warnings: [], errors: [] }) },
  'settings.setActive': { kind: 'ok', run: () => ({ ok: true }) },
  'settings.saveNetwork': { kind: 'ok', run: () => ({ ok: true, notes: [] }) },
  'settings.probeModels': { kind: 'ok', run: () => ({ models: ['deepseek-chat', 'probe-new-a', 'probe-new-b'] }) },
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

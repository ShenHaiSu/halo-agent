/** need22-P5 J4：Mock 共享状态（原 createBrowserMockBridge 闭包的跨文件形态）。
 * - 可变闭包变量经 getter/setter 暴露为 MockState，跨 mockSystem/mockWorkspace 读写语义与原闭包一致（含重赋值）。
 * - self 为组合后的桥自引用（替代原字面量内 this. 调用），由 mock.ts 装配后回填。 */
import { haloEventBus } from './events';
import type { AgentConfigDocument, GlobalNetworkSettings, HostBridgeApi, PromptBlockPreview, PromptConfigDocument, PromptPreviewDto, ProvidersConfigDocument, SessionMetadata, UnifiedChatMessage, WorkspaceInfo } from './types';

  export function fireMockAskUser(s: MockState, sessionId: string): void {
    const ticketId = `tick_${Math.random().toString(36).substring(2, 10)}`;
    s.mockAskTickets[sessionId] = ticketId;
    haloEventBus.emitLocal('agent:ask-user-requested', {
      sessionId,
      ticketId,
      callId: 'mock_call_ask_001',
      questions: [
        {
          id: 'framework',
          question: '本轮重构优先选用哪个前端框架？',
          options: [
            { label: 'Vue 3', description: '存量一致，推荐' },
            { label: 'React', description: '需重写构建链' },
          ],
          multiSelect: false,
          allowCustom: true,
        },
        {
          id: 'notes',
          question: '还有哪些补充要求？（开放问答）',
          options: [],
          multiSelect: false,
          allowCustom: true,
        },
      ],
    });
  }

   export function estimateMockTokens(t: string): number {
     if (!t) return 0;
     let ascii = 0; let cjk = 0; let other = 0;
     for (const ch of t) { const c = ch.codePointAt(0) || 0; if (c <= 127) ascii++; else if (c >= 0x4e00 && c <= 0x9fff) cjk++; else other++; }
     return Math.max(1, Math.ceil(ascii / 3.6 + cjk * 1.55 + other * 1.2));
   };

   export function buildMockPreview(cfg: PromptConfigDocument, workspaceId: string): PromptPreviewDto {
     if (!cfg.enableHaloPrefix) {
       return { assembledText: '', blocks: [], totalEstimatedTokens: 0, stableEstimatedTokens: 0, prefixHash: 'mock', workspaceId, unknownVariables: [], isDefault: false };
     }
     const parts: Array<{ blockId: string; text: string; isStable: boolean }> = [];
     if (cfg.enableCustom && cfg.customText.trim() && cfg.customPlacement === 'head') parts.push({ blockId: 'custom', text: cfg.customText.trim(), isStable: true });
     if (cfg.enableIdentity) parts.push({ blockId: 'identity', text: cfg.identityText.trim(), isStable: true });
     if (cfg.enableRuntime) parts.push({ blockId: 'runtime', text: cfg.runtimeTemplate.trim(), isStable: true });
     if (cfg.enableCmdPython) parts.push({ blockId: 'cmdPython', text: cfg.cmdPythonText.trim(), isStable: true });
     if (cfg.enableToolGuide) parts.push({ blockId: 'toolGuide', text: cfg.toolGuideTemplate.trim(), isStable: true });
     if (cfg.enableEphemeral) parts.push({ blockId: 'ephemeral', text: cfg.ephemeralTemplate.trim(), isStable: false });
     if (cfg.enableCustom && cfg.customText.trim() && cfg.customPlacement === 'tail') parts.push({ blockId: 'custom', text: cfg.customText.trim(), isStable: false });
     const assembled = parts.length ? parts.map((p) => p.text).join('\n\n') + '\n' : '';
     const blocks: PromptBlockPreview[] = parts.map((p) => ({ blockId: p.blockId, enabled: true, renderedText: p.text, estimatedTokens: estimateMockTokens(p.text), isStable: p.isStable }));
     const stable = blocks.filter((b) => b.isStable).reduce((a, b) => a + b.estimatedTokens, 0);
     return { assembledText: assembled, blocks, totalEstimatedTokens: estimateMockTokens(assembled), stableEstimatedTokens: stable, prefixHash: 'mock', workspaceId, unknownVariables: [], isDefault: false };
   };

export function createMockState() {
  let mockDataRoot = 'C:\\HaloAgentData';
  // need09-06 Mock：问询票据簿 + 假问询回放（2 题：单选 + 开放）
  const mockAskTickets: Record<string, string> = {};
  const today = new Date().toISOString().slice(0, 10).replace(/-/g, '');

  let mockWorkspaces: WorkspaceInfo[] = [
    {
      id: 'ws_demo_default',
      name: 'halo-agent',
      rootPath: 'F:\\MyProgram\\CSharp\\halo-agent',
      workspaceHash: 'a1b2c3d4e5f60718',
      defaultProviderId: 'provider-openai',
      defaultModelId: 'gpt-4o',
      isPinned: false,
      createdAtUtc: new Date().toISOString(),
      lastOpenedAtUtc: new Date().toISOString(),
      directoryExists: true,
    },
  ];

  let mockSessions: Record<string, SessionMetadata[]> = {
    ws_demo_default: [
      {
        id: `sess_${today}_init01`,
        workspaceId: 'ws_demo_default',
        sessionHash: 'f0e1d2c3b4a59687',
        title: '新建会话',
        providerId: 'provider-openai',
        modelId: 'gpt-4o',
        messageCount: 0,
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCachedTokens: 0,
        isPinned: false,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
      },
    ],
  };

  let mockMessages: Record<string, UnifiedChatMessage[]> = {
    [`sess_${today}_init01`]: [],
  };

  let mockNetworkSettings: GlobalNetworkSettings = {
    mode: 'System',
    customProxy: {
      scheme: 'http',
      host: '127.0.0.1',
      port: 7890,
      requiresAuth: false,
      username: '',
      passwordEncrypted: '',
      passwordMasked: '',
      bypassList: 'localhost;127.*;10.*;192.168.*',
      bypassOnLocal: true,
    },
    defaultConnectTimeoutSeconds: 15,
    defaultRequestTimeoutSeconds: 300,
  };

  let mockProvidersDoc: ProvidersConfigDocument = {
    activeProviderId: 'provider-openai',
    activeModelId: 'gpt-4o',
    providers: [
      {
        id: 'provider-openai',
        name: 'OpenAI / 兼容网关',
        enabled: true,
        baseUrl: 'https://api.openai.com/v1',
        apiKeyEncrypted: 'DPAPI_MOCK_CIPHER_1',
        apiKeyMasked: 'sk-p****9876',
        hasApiKey: true,
        proxyMode: 'Inherit',
        customProxy: null,
        enableCustomHeaders: true,
        customHeaders: [
          {
            key: 'X-Session-Hash',
            valueTemplate: '${session-hash}',
            enabled: true,
            isSecret: false,
          },
          {
            key: 'x-opencode-session',
            valueTemplate: '${session-hash}',
            enabled: true,
            isSecret: false,
          },
          {
            key: 'X-Client-Workspace',
            valueTemplate: '${workspace-name}',
            enabled: true,
            isSecret: false,
          },
        ],
        models: [
           {
             id: 'gpt-4o',
             displayName: 'GPT-4o (Chat Completions)',
             protocol: 'chat',
             maxContextTokens: 128000,
             maxOutputTokens: 4096,
             temperature: 0.2,
             enabled: true,
             supportsVision: true,
             maxImagesPerTurn: 4,
             visionDetail: 'high',
           },
           {
             id: 'o3-mini',
             displayName: 'o3-mini (Responses API)',
             protocol: 'resp',
             maxContextTokens: 200000,
             maxOutputTokens: 16384,
             temperature: null,
             enabled: true,
             supportsVision: false,
             maxImagesPerTurn: 4,
             visionDetail: 'high',
           },
        ],
      },
       {
         id: 'provider-deepseek',
         name: 'DeepSeek Official',
         enabled: true,
         baseUrl: 'https://api.deepseek.com/v1',
         apiKeyEncrypted: '',
         apiKeyMasked: '',
         hasApiKey: false,
         proxyMode: 'Inherit',
         customProxy: null,
         enableCustomHeaders: false,
         customHeaders: [],
         models: [
           {
             id: 'deepseek-chat',
             displayName: 'DeepSeek-V3 (Chat)',
             protocol: 'chat',
             maxContextTokens: 64000,
             maxOutputTokens: 8192,
             temperature: 0.1,
             enabled: true,
             supportsVision: false,
             maxImagesPerTurn: 4,
             visionDetail: 'high',
           },
         ],
      },
    ],
   };
   const mockPromptDefaults: PromptConfigDocument = {
     version: 1, enableHaloPrefix: true, enableIdentity: true, enableRuntime: true,
     enableCmdPython: true, enableToolGuide: true, enableEphemeral: true, enableCustom: false,
     customPlacement: 'tail', customText: '',
     identityText: 'You are Halo-Agent, an expert software engineering and system automation AI agent running natively on Windows.',
     runtimeTemplate: '## 1. Runtime & Environment Facts\n- Host OS: {{OsVersion}} (Architecture: {{OsArch}})\n- Active Workspace Name: {{WorkspaceName}}\n- Active Workspace Root Path: {{WorkspaceRoot}}\n- Default Command Shell: `cmd.exe` (Windows Command Prompt)',
     cmdPythonText: '## 2. Command Execution Strategy (CRITICAL RULE)\nBecause PowerShell versions vary drastically, default to cmd.exe first, python second. AVOID powershell unless asked.',
     toolGuideTemplate: '## 3. Standard Tool Usage Guidelines\n- Read before modifying.',
     ephemeralTemplate: '## 4. Session Ephemeral (只供模型参考,不参与前缀命中)\n- Current Date: {{CurrentDate}}\n- Detected Python Runtime: {{PythonStatus}}',
     scope: 'global', updatedAtUtc: new Date().toISOString(),
   };
   let mockPromptConfig: PromptConfigDocument = { ...mockPromptDefaults };
    // need08 mock 仅示意，联调以真后端为准
    // need13 mock 仅示意，联调以真后端为准
    const mockAgentDefaults: AgentConfigDocument = {
      version: 1, execDefaultTimeoutMs: 30000, execMaxTimeoutMs: 300000, maxIterations: 25,
      subAgentEnabled: true, subMaxParallel: 3, subMaxIterations: 15,
      subAllowExecCommand: true, subAllowFileWrite: true,
      subAllowSkillWrite: false, subAllowMemoryWrite: false, subAllowAskUser: false,
      scope: 'global', updatedAtUtc: new Date().toISOString(),
    };
   let mockAgentConfig: AgentConfigDocument = { ...mockAgentDefaults };
  let _self: HostBridgeApi | null = null;
  return {
    get mockDataRoot(): string { return mockDataRoot; },
    set mockDataRoot(v: string) { mockDataRoot = v; },
    get mockAskTickets(): Record<string, string> { return mockAskTickets; },
    get today(): string { return today; },
    get mockWorkspaces(): WorkspaceInfo[] { return mockWorkspaces; },
    set mockWorkspaces(v: WorkspaceInfo[]) { mockWorkspaces = v; },
    get mockSessions(): Record<string, SessionMetadata[]> { return mockSessions; },
    set mockSessions(v: Record<string, SessionMetadata[]>) { mockSessions = v; },
    get mockMessages(): Record<string, UnifiedChatMessage[]> { return mockMessages; },
    set mockMessages(v: Record<string, UnifiedChatMessage[]>) { mockMessages = v; },
    get mockNetworkSettings(): GlobalNetworkSettings { return mockNetworkSettings; },
    set mockNetworkSettings(v: GlobalNetworkSettings) { mockNetworkSettings = v; },
    get mockProvidersDoc(): ProvidersConfigDocument { return mockProvidersDoc; },
    set mockProvidersDoc(v: ProvidersConfigDocument) { mockProvidersDoc = v; },
    get mockPromptDefaults(): PromptConfigDocument { return mockPromptDefaults; },
    get mockPromptConfig(): PromptConfigDocument { return mockPromptConfig; },
    set mockPromptConfig(v: PromptConfigDocument) { mockPromptConfig = v; },
    get mockAgentDefaults(): AgentConfigDocument { return mockAgentDefaults; },
    get mockAgentConfig(): AgentConfigDocument { return mockAgentConfig; },
    set mockAgentConfig(v: AgentConfigDocument) { mockAgentConfig = v; },
    get self(): HostBridgeApi | null { return _self; },
    set self(v: HostBridgeApi | null) { _self = v; },
  };
}

export type MockState = ReturnType<typeof createMockState>;

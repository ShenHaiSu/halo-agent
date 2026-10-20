/** need22-P5 J4：Mock 业务域方法（workspace/session/history/goal/sub/skill/usage；原 createBrowserMockBridge 后半，行为不变）。 */
import { haloEventBus } from './events';
import { buildMockPreview } from './mockState';
import type { MockState } from './mockState';
import type { AgentConfigDocument, HostBridgeApi, PromptConfigDocument, SessionMetadata, SkillMemoryKind, SkillMemoryListItem, SkillMemoryScope, SkillMemoryScopeFilter, UnifiedChatMessage, UsageFilter, UsageRowDto, WorkspaceInfo } from './types';

export function mockWorkspaceMethods(s: MockState): Partial<HostBridgeApi> {
  return {
    // Stage 04 Mock Implementations
    async getWorkspaces() {
      return {
        ok: true,
        data: [...s.mockWorkspaces],
      };
    },
    async addWorkspace(rootPath: string, name?: string) {
      const cleanPath = (rootPath || '').trim();
      const existing = s.mockWorkspaces.find((w) => w.rootPath.toLowerCase() === cleanPath.toLowerCase());
      if (existing) {
        if (name && name.trim()) existing.name = name.trim();
        return { ok: true, data: existing };
      }
      const dirName = cleanPath.split(/[\\/]/).filter(Boolean).pop() || 'Workspace';
      const ws: WorkspaceInfo = {
        id: `ws_${Math.random().toString(36).substring(2, 10)}`,
        name: name && name.trim() ? name.trim() : dirName,
        rootPath: cleanPath,
        workspaceHash: 'a1b2c3d4e5f60718',
        defaultProviderId: s.mockProvidersDoc.activeProviderId,
        defaultModelId: s.mockProvidersDoc.activeModelId,
        isPinned: false,
        createdAtUtc: new Date().toISOString(),
        lastOpenedAtUtc: new Date().toISOString(),
        directoryExists: true,
      };
      s.mockWorkspaces.push(ws);
      const defaultSess: SessionMetadata = {
        id: `sess_${s.today}_${Math.random().toString(36).substring(2, 8)}`,
        workspaceId: ws.id,
        sessionHash: 'f0e1d2c3b4a59687',
        title: '新建会话',
        providerId: ws.defaultProviderId,
        modelId: ws.defaultModelId,
        messageCount: 0,
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCachedTokens: 0,
        isPinned: false,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
      };
      s.mockSessions[ws.id] = [defaultSess];
      s.mockMessages[defaultSess.id] = [];
      return { ok: true, data: ws };
    },
    async removeWorkspace(workspaceId: string) {
      s.mockWorkspaces = s.mockWorkspaces.filter((w) => w.id !== workspaceId);
      delete s.mockSessions[workspaceId];
      return { ok: true, data: true };
    },
    async browseWorkspaceFolder(_initialPath?: string) {
      return {
        ok: true,
        data: 'F:\\MyProgram\\CSharp\\halo-agent',
      };
    },
    async getSessions(workspaceId: string) {
      const list = s.mockSessions[workspaceId] || [];
      return {
        ok: true,
        data: [...list],
      };
    },
    async createSession(workspaceId: string, title?: string, providerId?: string, modelId?: string) {
      const sess: SessionMetadata = {
        id: `sess_${s.today}_${Math.random().toString(36).substring(2, 8)}`,
        workspaceId,
        sessionHash: Math.random().toString(16).substring(2, 18).padEnd(16, '0'),
        title: title && title.trim() ? title.trim() : '新建会话',
        providerId: providerId || s.mockProvidersDoc.activeProviderId,
        modelId: modelId || s.mockProvidersDoc.activeModelId,
        messageCount: 0,
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCachedTokens: 0,
        isPinned: false,
        createdAtUtc: new Date().toISOString(),
        updatedAtUtc: new Date().toISOString(),
      };
      if (!s.mockSessions[workspaceId]) {
        s.mockSessions[workspaceId] = [];
      }
      s.mockSessions[workspaceId].unshift(sess);
      s.mockMessages[sess.id] = [];
      return { ok: true, data: sess };
    },
    async getSessionMessages(_workspaceId: string, sessionId: string) {
      const msgs = s.mockMessages[sessionId] || [];
      return {
        ok: true,
        data: [...msgs],
      };
    },
    async appendSessionMessage(workspaceId: string, sessionId: string, message: UnifiedChatMessage | string) {
      const msg: UnifiedChatMessage = typeof message === 'string' ? JSON.parse(message) : message;
      if (!s.mockMessages[sessionId]) {
        s.mockMessages[sessionId] = [];
      }
      s.mockMessages[sessionId].push(msg);
      const list = s.mockSessions[workspaceId] || [];
      const sess = list.find((s) => s.id === sessionId);
      if (sess) {
        sess.messageCount = s.mockMessages[sessionId].length;
        sess.updatedAtUtc = new Date().toISOString();
        if (msg.role === 'user' && sess.title === '新建会话' && msg.content) {
          const firstLine = String(msg.content).trim().split(/[\r\n]+/).map((s) => s.trim()).filter(Boolean)[0] || '';
          const firstSentence = firstLine.split(/[。！？!?;；]+/).map((s) => s.trim()).filter(Boolean)[0] || firstLine;
          sess.title = [...firstSentence.replace(/\s+/g, '')].slice(0, 5).join('') || '新建会话';
        }
      }
      return { ok: true, data: true };
    },
    async updateSessionTitle(workspaceId: string, sessionId: string, title: string) {
      const list = s.mockSessions[workspaceId] || [];
      const sess = list.find((s) => s.id === sessionId);
      if (sess) {
        sess.title = title.trim();
        sess.updatedAtUtc = new Date().toISOString();
        return { ok: true, data: true };
      }
      return { ok: false, message: '会话不存在', data: false };
    },
    // need03-03 T2 Mock：固定精炼标题短路，保证 Web 调试同样走两阶段
    async generateSessionTitle(workspaceId: string, sessionId: string) {
      const list = s.mockSessions[workspaceId] || [];
      const sess = list.find((s) => s.id === sessionId);
      if (!sess) return { ok: false, message: '会话不存在', data: '' };
      const msgs = s.mockMessages[sessionId] || [];
      const firstAssistant = [...msgs].reverse().find((m) => m.role === 'assistant');
      const src = String(firstAssistant?.content || msgs.find((m) => m.role === 'user')?.content || '');
      const firstSentence = src.trim().split(/[\r\n]+/).map((s) => s.trim()).filter(Boolean)[0]?.split(/[。！？!?;；]+/).map((s) => s.trim()).filter(Boolean)[0] || 'Mock精炼标题';
      const title = [...firstSentence.replace(/\s+/g, '')].slice(0, 6).join('') || 'Mock精炼标题';
      sess.title = title;
      sess.updatedAtUtc = new Date().toISOString();
      return { ok: true, data: title };
    },
    async setSessionPinned(workspaceId: string, sessionId: string, isPinned: boolean) {
      const list = s.mockSessions[workspaceId] || [];
      const sess = list.find((s) => s.id === sessionId);
      if (sess) {
        sess.isPinned = isPinned;
        sess.updatedAtUtc = new Date().toISOString();
        return { ok: true, data: true };
      }
      return { ok: false, message: '会话不存在', data: false };
    },
    async deleteSession(workspaceId: string, sessionId: string) {
      if (s.mockSessions[workspaceId]) {
        s.mockSessions[workspaceId] = s.mockSessions[workspaceId].filter((s) => s.id !== sessionId);
      }
      delete s.mockMessages[sessionId];
      return { ok: true, data: true };
    },
    async exportSessionMarkdown(_workspaceId: string, sessionId: string) {
      const msgs = s.mockMessages[sessionId] || [];
      let md = `# 会话记录 (${sessionId})\n\n`;
      for (const m of msgs) {
        md += `### ${m.role}\n${m.content || ''}\n\n`;
      }
      return { ok: true, data: md };
    },
    async getContextBudget(_providerId: string, _modelId: string, _workspaceId: string, sessionId: string) {
      const msgs = s.mockMessages[sessionId] || [];
      const used = msgs.reduce((acc, m) => acc + (m.content?.length || 0) * 0.5, 120);
      const rounded = Math.round(used);
      return {
        ok: true,
        data: {
          totalContextTokens: 128000,
          maxOutputTokens: 4096,
          safetyBuffer: 1024,
          usedInputTokens: rounded,
           availableInputTokens: Math.max(0, 128000 - 4096 - 1024 - rounded),
           usagePercentage: Math.round((rounded / 128000) * 1000) / 10,
           isNearLimit: false,
           sessionHash: 'f0e1d2c3b4a59687',
           protocol: 'chat',
           lastPromptTokens: rounded,
           lastCachedTokens: Math.min(rounded, 80),
           cacheHitRate: rounded > 0 ? Math.min(80, rounded) / rounded : null,
           usedInputIsEstimated: false,
           occupancyState: 'estimated',
           billedPromptTokens: 1200,
           billedCompletionTokens: 300,
           // need05-06 mock：本地预测兜底字段
           predictedCacheHitRate: rounded > 0 ? Math.min(80, rounded) / rounded : null,
           predictedCachedTokens: Math.min(rounded, 80),
           prefixDivergenceBlock: 'latest',
        },
      };
    },
    // need05-03 mock：插入一条假摘要 + 事件回放
    async compactSession(_workspaceId: string, sessionId: string) {
      if (!s.mockMessages[sessionId]) s.mockMessages[sessionId] = [];
      s.mockMessages[sessionId].push({
        id: `msg_mock_summary_${Date.now()}`,
        role: 'user',
        content: '【上下文摘要：压缩自此前 2 条，由模型自动生成，细节以备份为准】\n- Mock 要点：任务目标/进展、关键结论、用户偏好、未办事项',
        createdAtUtc: new Date().toISOString(),
      } as UnifiedChatMessage);
      setTimeout(() => {
        haloEventBus.emitLocal('agent:turn-event', { sessionId, kind: 'CompactStarted', payload: { sessionId } });
        haloEventBus.emitLocal('agent:turn-event', {
          sessionId,
          kind: 'CompactCompleted',
          payload: { removedMessages: 2, keptMessages: 4, summaryPreview: 'Mock 要点', updatedAtUtc: new Date().toISOString() },
        });
      }, 60);
      return { ok: true, data: { success: true, removedMessages: 2, keptMessages: 4, summaryPreview: 'Mock 要点' } };
    },
     async getCompactBackupList(_workspaceId: string, _sessionId: string) {
       return { ok: true, data: '[]' };
     },
     // need06 mock 仅示意，联调以真后端为准
     async getPromptConfig() {
       return { ok: true, data: { ...s.mockPromptConfig } };
     },
     async savePromptConfig(config: PromptConfigDocument | string) {
       const parsed: PromptConfigDocument = typeof config === 'string' ? JSON.parse(config) : JSON.parse(JSON.stringify(config));
       s.mockPromptConfig = parsed;
       return { ok: true, message: '提示词配置已保存，下一轮对话起生效 (Mock)', data: { ...s.mockPromptConfig } };
     },
     async resetPromptConfig(blockId?: string) {
       if (!blockId) { s.mockPromptConfig = { ...s.mockPromptDefaults }; }
       else if (blockId === 'identity') { s.mockPromptConfig.identityText = s.mockPromptDefaults.identityText; s.mockPromptConfig.enableIdentity = true; }
       else if (blockId === 'runtime') { s.mockPromptConfig.runtimeTemplate = s.mockPromptDefaults.runtimeTemplate; s.mockPromptConfig.enableRuntime = true; }
       else if (blockId === 'cmdPython') { s.mockPromptConfig.cmdPythonText = s.mockPromptDefaults.cmdPythonText; s.mockPromptConfig.enableCmdPython = true; }
       else if (blockId === 'toolGuide') { s.mockPromptConfig.toolGuideTemplate = s.mockPromptDefaults.toolGuideTemplate; s.mockPromptConfig.enableToolGuide = true; }
       else if (blockId === 'ephemeral') { s.mockPromptConfig.ephemeralTemplate = s.mockPromptDefaults.ephemeralTemplate; s.mockPromptConfig.enableEphemeral = true; }
       else if (blockId === 'custom') { s.mockPromptConfig.customText = ''; s.mockPromptConfig.enableCustom = false; s.mockPromptConfig.customPlacement = 'tail'; }
       return { ok: true, message: '已恢复默认 (Mock)', data: { ...s.mockPromptConfig } };
     },
     // need08 mock 仅示意，联调以真后端为准
     async getAgentConfig() {
       return { ok: true, data: { ...s.mockAgentConfig } };
     },
     async saveAgentConfig(config: AgentConfigDocument | string) {
       const parsed: AgentConfigDocument = typeof config === 'string' ? JSON.parse(config) : JSON.parse(JSON.stringify(config));
       s.mockAgentConfig = { ...parsed, updatedAtUtc: new Date().toISOString() };
       return { ok: true, message: '智能体行为配置已保存，下一轮对话起生效 (Mock)', data: { ...s.mockAgentConfig } };
     },
      async resetAgentConfig(field?: string) {
        const id = (field || '').trim().toLowerCase();
        if (!id || id === 'all' || id === 'default') { s.mockAgentConfig = { ...s.mockAgentDefaults }; }
        else if (['defaulttimeout', 'execdefaulttimeoutms', 'default', 'default-timeout', 'default_timeout'].includes(id)) { s.mockAgentConfig.execDefaultTimeoutMs = s.mockAgentDefaults.execDefaultTimeoutMs; }
        else if (['maxtimeout', 'execmaxtimeoutms', 'max', 'max-timeout', 'max_timeout'].includes(id)) { s.mockAgentConfig.execMaxTimeoutMs = s.mockAgentDefaults.execMaxTimeoutMs; }
        else if (['iterations', 'maxiterations', 'rounds', 'max-iterations', 'max_iterations'].includes(id)) { s.mockAgentConfig.maxIterations = s.mockAgentDefaults.maxIterations; }
        else if (['subenabled', 'subagentenabled', 'sub-enabled'].includes(id)) { s.mockAgentConfig.subAgentEnabled = s.mockAgentDefaults.subAgentEnabled; }
        else if (['subparallel', 'submaxparallel', 'sub-parallel'].includes(id)) { s.mockAgentConfig.subMaxParallel = s.mockAgentDefaults.subMaxParallel; }
        else if (['subiterations', 'submaxiterations', 'sub-iterations'].includes(id)) { s.mockAgentConfig.subMaxIterations = s.mockAgentDefaults.subMaxIterations; }
        else if (['subperms', 'sub-perms', 'subpermissions'].includes(id)) { s.mockAgentConfig.subAllowExecCommand = true; s.mockAgentConfig.subAllowFileWrite = true; s.mockAgentConfig.subAllowSkillWrite = false; s.mockAgentConfig.subAllowMemoryWrite = false; s.mockAgentConfig.subAllowAskUser = false; }
        else { return { ok: false, code: 'AGENT_VALIDATION', message: `字段 [field]：未知恢复项：${field}` }; }
        s.mockAgentConfig.updatedAtUtc = new Date().toISOString();
        return { ok: true, message: '已恢复默认 (Mock)', data: { ...s.mockAgentConfig } };
      },
      async previewPrompt(workspaceId: string) {
        return { ok: true, data: buildMockPreview(s.mockPromptConfig, workspaceId) };
      },
     async getPromptDiagnostics(workspaceId: string, sessionId?: string) {
       const currentPreview = buildMockPreview(s.mockPromptConfig, workspaceId);
       return {
         ok: true,
         data: {
           currentPreview,
           lastSent: sessionId ? { reqId: 'mock_req_001', sessionId, workspaceId, prefixHash: currentPreview.prefixHash, charLength: currentPreview.assembledText.length, sentAtUtc: new Date().toISOString() } : null,
           previewEqualsLastSent: true,
           compressorPromptPreview: '你是会话上下文压缩器。把用户提供的对话压缩成精炼的中文要点列表（Mock 预览）。',
           titlePromptPreview: '你是会话标题生成器（Mock 预览）。（对话正文已脱除）',
         },
       };
     },
     // need07 mock：与真后端同语义（轮次展开 + 前驱 user 切面）
     async truncateSessionHistory(_workspaceId: string, sessionId: string, anchorMessageId: string) {
       const msgs = s.mockMessages[sessionId] || [];
       const idx = msgs.findIndex((m) => String(m.id) === String(anchorMessageId));
       if (idx < 0) return { ok: false, code: 'ANCHOR_NOT_FOUND', message: '历史已变化，请刷新后重试' };
       const role = String(msgs[idx].role || '').toLowerCase();
       let cut = idx;
       let replayText: string | null = null;
       if (role === 'assistant') {
         let j = idx;
         while (j >= 0 && String(msgs[j].role || '').toLowerCase() !== 'user') j--;
         if (j < 0) return { ok: false, code: 'NO_PRIOR_USER', message: '找不到上一条用户消息，无法重放' };
         cut = j; replayText = String(msgs[j].content || '');
       } else {
         replayText = String(msgs[idx].content || '');
       }
       if (!replayText.trim()) return { ok: false, code: 'EMPTY_REPLAY_TEXT', message: '该消息内容为空，无法重放' };
       const kept = msgs.slice(0, cut);
       const removed = msgs.length - kept.length;
       s.mockMessages[sessionId] = kept;
       return { ok: true, data: { success: true, keptMessages: kept.length, removedMessages: removed, replayText, backupFileName: 'mock-replay-bak' } };
     },
     async branchSession(workspaceId: string, sourceSessionId: string, anchorMessageId: string, includeAnchor: boolean) {
       const msgs = s.mockMessages[sourceSessionId] || [];
       const idx = msgs.findIndex((m) => String(m.id) === String(anchorMessageId));
       if (idx < 0) return { ok: false, code: 'ANCHOR_NOT_FOUND', message: '历史已变化，请刷新后重试' };
       let end = idx;
       if (String(msgs[idx].role || '').toLowerCase() === 'assistant') {
         let t2 = idx + 1;
         while (t2 < msgs.length && String(msgs[t2].role || '').toLowerCase() === 'tool') t2++;
         end = t2 - 1;
       }
       const prefix = (includeAnchor ? msgs.slice(0, end + 1) : msgs.slice(0, idx)).map((m) => ({ ...m }));
       const res = await (this as unknown as { createSession: (w: string, t?: string) => Promise<{ ok: boolean; data: unknown }> }).createSession(workspaceId, '分支会话');
       const sess = (res as unknown as { data: SessionMetadata }).data;
       s.mockMessages[sess.id] = prefix;
       const list = s.mockSessions[workspaceId] || [];
       const hit = list.find((s) => s.id === sess.id);
       if (hit) hit.messageCount = prefix.length;
       return { ok: true, data: sess as SessionMetadata };
     },
     async healSessionHistory(_workspaceId: string, sessionId: string) {
       return { ok: true, data: { success: true, syntheticAppended: 0, orphanToolsRemoved: 0, keptMessages: (s.mockMessages[sessionId] || []).length, backupFileName: null } };
     },
      async deleteSessionMessage(_workspaceId: string, sessionId: string, messageId: string) {
        const msgs = s.mockMessages[sessionId] || [];
        const idx = msgs.findIndex((m) => String(m.id) === String(messageId));
        if (idx < 0) return { ok: false, code: 'MESSAGE_NOT_FOUND', message: '消息不存在' };
        const kept = msgs.filter((_, i) => i !== idx);
        s.mockMessages[sessionId] = kept;
        return { ok: true, data: { success: true, keptMessages: kept.length, syntheticAppended: 0, orphanToolsRemoved: 0, backupFileName: 'mock-delete-bak' } };
      },
      // need13 mock 仅示意，联调以真后端为准
      async getSubSessions(_ws: string, _parent: string) {
        return { ok: true, data: [] as SessionMetadata[] };
      },
      async cancelSubAgent(_ws: string, _sub: string) {
        return { ok: true, data: false };
      },
      async cancelAgentTree(_main: string) {
        return { ok: true, data: false };
      },
      async getSubRuntime(_ws: string, _parent: string) {
        return { ok: true, data: { subs: [], policy: { enabled: s.mockAgentConfig.subAgentEnabled, maxParallel: s.mockAgentConfig.subMaxParallel, maxIterations: s.mockAgentConfig.subMaxIterations, exec: s.mockAgentConfig.subAllowExecCommand, file: s.mockAgentConfig.subAllowFileWrite, skill: s.mockAgentConfig.subAllowSkillWrite, memory: s.mockAgentConfig.subAllowMemoryWrite }, lastRejection: null } };
      },
       async getGoal() {
         return { ok: true, data: { title: '', description: '', milestones: [], percent: 0, note: '', updatedAtUtc: new Date().toISOString() } };
       },
       async saveGoal(_ws: string, _sess: string, goalJson: string) {
         try { const p = JSON.parse(goalJson); return { ok: true, data: { title: p.title || '', description: p.description || '', milestones: p.milestones || [], percent: p.percent || 0, note: p.note || '', updatedAtUtc: new Date().toISOString() } }; } catch (e) { console.warn('[bridge] native unavailable, fallback empty state:', (e as Error)?.message ?? e); return { ok: true, data: { title: '', description: '', milestones: [], percent: 0, note: '', updatedAtUtc: new Date().toISOString() } }; }
       },
       async clearGoal() { return { ok: true, data: true }; },
       async getSubSessionDetail(_ws: string, sub: string) {
         return { ok: true, data: { subId: sub, parentSessionId: _ws, label: 'mock-sub', status: 'succeeded', taskPrompt: 'mock task', createdByCallId: null, messageCount: 0, iterations: 0, toolExecutions: 0, inputTokens: 0, outputTokens: 0, cachedTokens: 0, billedPrompt: 0, billedCompletion: 0, createdAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString() } };
       },
       async getSubSessionMessages() { return { ok: true, data: [] }; },
     // need10 mock 仅示意，联调以真后端为准
     async listSkillMemory(kind: SkillMemoryKind, scope: SkillMemoryScopeFilter) {
       const mk = (name: string, sc: SkillMemoryScope, title: string, summary: string, enabled: boolean): SkillMemoryListItem => ({
         name, scope: sc, title, summary, pin: kind === 'skill' ? 'auto' : 'pinned',
         enabled, priority: 100, version: 1, estTokens: 42, chars: summary.length,
         hash: 'mockhash16', hasAssets: false, updatedAtUtc: new Date().toISOString(), dirPath: `C:\\\\HaloAgentData\\\\${kind}\\\\${name}`,
       });
       const all: SkillMemoryListItem[] = [
         mk(kind === 'skill' ? 'demo-skill' : 'demo-memory', 'global', '演示条目', '这是一条 mock 仅示意数据', true),
         mk(kind === 'skill' ? 'project-helper' : 'project-note', 'project', '项目条目', '随工作区 .haloagent/ 走的示例', false),
       ];
       const list = scope === 'all' ? all : all.filter((i) => i.scope === scope);
       return { ok: true, data: list };
     },
     async getSkillMemoryDetail(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string) {
       const list = await (this as unknown as { listSkillMemory: (k: SkillMemoryKind, s: SkillMemoryScopeFilter) => Promise<{ ok: boolean; data: SkillMemoryListItem[] }> }).listSkillMemory(kind, scope);
       const hit = list.data.find((i) => i.name === name) || list.data[0];
       if (!hit) return { ok: false, code: 'SKILL_NOT_FOUND', message: '条目不存在，请刷新' };
       return { ok: true, data: { ...hit, bodyPreview: `# ${hit.title}\n\n${hit.summary}\n\n正文节选（mock 仅示意）。`, truncated: false, bodyTotalChars: 64 } };
     },
     async setSkillMemoryEnabled(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string, enabled: boolean) {
       const kindCn = kind === 'skill' ? '技能' : '记忆';
       return { ok: true, data: { ok: true, notice: `已${enabled ? '启用' : '停用'} ${kindCn} '${name}'（${scope}）。下一轮对话起生效。` } };
     },
     async deleteSkillMemory(kind: SkillMemoryKind, scope: SkillMemoryScope, name: string) {
       const kindCn = kind === 'skill' ? '技能' : '记忆';
       const bak = `${name}.deleted-mock.bak`;
       return { ok: true, data: { ok: true, bakFileName: bak, notice: `已删除${scope} ${kindCn} '${name}'（备份 ${bak}）。下一轮对话起生效。` } };
     },
     async openSkillMemoryFolder() {
       return { ok: true, data: true };
     },
     async getUsageFilterOptions() {
       return { ok: true, data: { providers: [{ id: 'p-openai', name: 'OpenAI' }, { id: 'p-deepseek', name: 'DeepSeek' }], models: [{ providerId: 'p-openai', providerName: 'OpenAI', modelId: 'gpt-4o', display: 'GPT-4o', key: 'k1', label: 'OpenAI/gpt-4o' }, { providerId: 'p-deepseek', providerName: 'DeepSeek', modelId: 'deepseek-chat', display: 'DeepSeek-V3', key: 'k2', label: 'DeepSeek/deepseek-chat' }] } };
     },
     async getUsageOverview() {
       return { ok: true, data: { totalRequests: 42, sumInput: 82400, sumOutput: 11200, sumCached: 56320, hitRate: 0.6838, activeModels: 3 } };
     },
     async getUsageTrend() {
       const dates = ['2026-10-11', '2026-10-12', '2026-10-13', '2026-10-14', '2026-10-15', '2026-10-16', '2026-10-17'];
       return { ok: true, data: { dates, perModel: [{ key: 'k1', providerName: 'OpenAI', modelId: 'gpt-4o', values: [1200, 1800, 900, 2400, 3100, 1500, 2100] }, { key: 'k2', providerName: 'DeepSeek', modelId: 'deepseek-chat', values: [900, 700, 1200, 600, 1100, 900, 800] }], hitRates: [0.62, 0.65, 0.58, 0.71, 0.69, 0.66, 0.68], totals: [2500, 3100, 2400, 3800, 4900, 2900, 3500] } };
     },
     async getUsageProviderShare() {
       return { ok: true, data: [{ providerId: 'p-openai', name: 'OpenAI', value: 16900, share: 0.68 }, { providerId: 'p-deepseek', name: 'DeepSeek', value: 7900, share: 0.32 }] };
     },
    async queryUsagePage(_filter: UsageFilter | string, page: number, pageSize: number) {
      void _filter;
      const rows: UsageRowDto[] = Array.from({ length: Math.min(8, pageSize) }, (_, i) => ({ id: (page - 1) * pageSize + i + 1, requestDate: '2026-10-17', requestTime: '10:20:0' + i, requestDatetime: '2026-10-17 10:20:0' + i, providerName: i % 2 ? 'DeepSeek' : 'OpenAI', modelId: i % 2 ? 'deepseek-chat' : 'gpt-4o', modelDisplay: i % 2 ? 'DeepSeek-V3' : 'GPT-4o', protocol: 'chat', inputTokens: 1200 + i * 100, outputTokens: 200 + i * 10, cachedTokens: 800 + i * 60, hitRate: 0.66, status: 'ok', source: 'main', estimated: 0 }));
      return { ok: true, data: { total: 42, page, pageSize, rows } };
     },
     async getUsageDbStatus() {
       return { ok: true, data: { dbPath: 'mock-usage.db', recordCount: 42, dbSizeBytes: 49152, oldestDate: '2026-10-11', newestDate: '2026-10-17' } };
     },
  };
}

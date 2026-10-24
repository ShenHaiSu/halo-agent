/** need21-F02: 会话域 API（旧 client.ts 工作区/会话/预算/压缩/子代理/目标/历史方法迁移，签名不变）。 */
import type { BridgeTransport } from '../transport/transport';
import type {
  BridgeResponse,
  CompactionResultDto,
  ContextBudgetStatusDto,
  HistoryDeleteResultDto,
  HistoryHealResultDto,
  HistoryTruncateResultDto,
  HostBridgeApi,
  SessionGoalDto,
  SessionMetadata,
  SubRuntimeInfo,
  SubSessionDetailDto,
  UnifiedChatMessage,
  WorkspaceInfo,
} from '../protocol/types';
import { normalizeBridgePayload } from './normalize';

export function createSessionApi(
  t: BridgeTransport
): Pick<
  HostBridgeApi,
  | 'getWorkspaces'
  | 'addWorkspace'
  | 'renameWorkspace'
  | 'removeWorkspace'
  | 'browseWorkspaceFolder'
  | 'getSessions'
  | 'createSession'
  | 'getSessionMessages'
  | 'appendSessionMessage'
  | 'updateSessionTitle'
  | 'generateSessionTitle'
  | 'setSessionPinned'
  | 'deleteSession'
  | 'exportSessionMarkdown'
  | 'getContextBudget'
  | 'compactSession'
  | 'getCompactBackupList'
  | 'getSubSessions'
  | 'cancelSubAgent'
  | 'cancelAgentTree'
  | 'getSubRuntime'
  | 'getGoal'
  | 'saveGoal'
  | 'clearGoal'
  | 'getSubSessionDetail'
  | 'getSubSessionMessages'
  | 'truncateSessionHistory'
  | 'branchSession'
  | 'healSessionHistory'
  | 'deleteSessionMessage'
> {
  return {
    async getWorkspaces() {
      return normalizeBridgePayload<BridgeResponse<WorkspaceInfo[]>>(await t.call('getWorkspaces'));
    },
    async addWorkspace(rootPath: string, name?: string) {
      return normalizeBridgePayload<BridgeResponse<WorkspaceInfo>>(await t.call('addWorkspace', rootPath, name ?? ''));
    },
    async renameWorkspace(workspaceId: string, alias?: string | null) {
      return normalizeBridgePayload<BridgeResponse<WorkspaceInfo>>(await t.call('renameWorkspace', workspaceId, alias ?? ''));
    },
    async removeWorkspace(workspaceId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('removeWorkspace', workspaceId));
    },
    async browseWorkspaceFolder(initialPath?: string) {
      return normalizeBridgePayload<BridgeResponse<string | null>>(await t.call('browseWorkspaceFolder', initialPath ?? ''));
    },
    async getSessions(workspaceId: string) {
      return normalizeBridgePayload<BridgeResponse<SessionMetadata[]>>(await t.call('getSessions', workspaceId));
    },
    async createSession(workspaceId: string, title?: string, providerId?: string, modelId?: string) {
      return normalizeBridgePayload<BridgeResponse<SessionMetadata>>(
        await t.call('createSession', workspaceId, title ?? '', providerId ?? '', modelId ?? '')
      );
    },
    async getSessionMessages(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<UnifiedChatMessage[]>>(await t.call('getSessionMessages', workspaceId, sessionId));
    },
    async appendSessionMessage(workspaceId: string, sessionId: string, message: UnifiedChatMessage | string) {
      const payload = typeof message === 'string' ? message : JSON.stringify(message);
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('appendSessionMessage', workspaceId, sessionId, payload));
    },
    async updateSessionTitle(workspaceId: string, sessionId: string, title: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('updateSessionTitle', workspaceId, sessionId, title));
    },
    async generateSessionTitle(workspaceId: string, sessionId: string, providerId?: string, modelId?: string) {
      return normalizeBridgePayload<BridgeResponse<string>>(
        await t.call('generateSessionTitle', workspaceId, sessionId, providerId ?? '', modelId ?? '')
      );
    },
    async setSessionPinned(workspaceId: string, sessionId: string, isPinned: boolean) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('setSessionPinned', workspaceId, sessionId, isPinned));
    },
    async deleteSession(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('deleteSession', workspaceId, sessionId));
    },
    async exportSessionMarkdown(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<string>>(await t.call('exportSessionMarkdown', workspaceId, sessionId));
    },
    async getContextBudget(providerId: string, modelId: string, workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<ContextBudgetStatusDto>>(
        await t.call('getContextBudget', providerId, modelId, workspaceId, sessionId)
      );
    },
    async compactSession(workspaceId: string, sessionId: string, providerId?: string, modelId?: string, keepLastGroups?: number) {
      return normalizeBridgePayload<BridgeResponse<CompactionResultDto>>(
        await t.call('compactSession', workspaceId, sessionId, providerId ?? '', modelId ?? '', keepLastGroups ?? 6)
      );
    },
    async getCompactBackupList(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<string>>(await t.call('getCompactBackupList', workspaceId, sessionId));
    },
    // need13：子代理直通（老后端缺方法时降级空态，不炸面板）
    async getSubSessions(ws: string, parent: string) {
      try {
        if (!t.supports('getSubSessions')) return { ok: true, data: [] as SessionMetadata[] };
        return normalizeBridgePayload<BridgeResponse<SessionMetadata[]>>(await t.call('getSubSessions', ws ?? '', parent ?? ''));
      } catch (e) {
        console.warn('[bridge] native unavailable, fallback empty state:', (e as Error)?.message ?? e);
        return { ok: true, data: [] as SessionMetadata[] };
      }
    },
    async cancelSubAgent(ws: string, sub: string) {
      try {
        if (!t.supports('cancelSubAgent')) return { ok: false, code: 'SUB_UNSUPPORTED', message: '子代理通道不可用' };
        return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('cancelSubAgent', ws ?? '', sub ?? ''));
      } catch (e) {
        console.warn('[bridge] SUB_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'SUB_UNSUPPORTED', message: '子代理通道不可用' };
      }
    },
    async cancelAgentTree(main: string) {
      try {
        if (!t.supports('cancelAgentTree'))
          return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('cancelAgentTurn', main ?? ''));
        return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call('cancelAgentTree', main ?? ''));
      } catch (e) {
        console.warn('[bridge] SUB_UNSUPPORTED:', (e as Error)?.message ?? e);
        return { ok: false, code: 'SUB_UNSUPPORTED', message: '子代理通道不可用' };
      }
    },
    async getSubRuntime(ws: string, parent: string) {
      try {
        if (!t.supports('getSubRuntime'))
          return {
            ok: true,
            data: {
              subs: [],
              policy: { enabled: true, maxParallel: 3, maxIterations: 15, exec: true, file: true, skill: false, memory: false },
              lastRejection: null,
            } as SubRuntimeInfo,
          };
        return normalizeBridgePayload<BridgeResponse<SubRuntimeInfo>>(await t.call('getSubRuntime', ws ?? '', parent ?? ''));
      } catch (e) {
        console.warn('[bridge] native unavailable, fallback empty state:', (e as Error)?.message ?? e);
        return {
          ok: true,
          data: {
            subs: [],
            policy: { enabled: true, maxParallel: 3, maxIterations: 15, exec: true, file: true, skill: false, memory: false },
            lastRejection: null,
          } as SubRuntimeInfo,
        };
      }
    },
    async getGoal(ws: string, sess: string) {
      try {
        if (!t.supports('getGoal'))
          return {
            ok: true,
            data: {
              title: '',
              description: '',
              milestones: [],
              percent: 0,
              note: '',
              updatedAtUtc: new Date().toISOString(),
            } as SessionGoalDto,
          };
        return normalizeBridgePayload<BridgeResponse<SessionGoalDto>>(
          await t.call<BridgeResponse<SessionGoalDto>>('getGoal', ws ?? '', sess ?? '')
        );
      } catch (e) {
        console.warn('[bridge] native unavailable, fallback empty state:', (e as Error)?.message ?? e);
        return {
          ok: true,
          data: {
            title: '',
            description: '',
            milestones: [],
            percent: 0,
            note: '',
            updatedAtUtc: new Date().toISOString(),
          } as SessionGoalDto,
        };
      }
    },
    async saveGoal(ws: string, sess: string, goalJson: string) {
      try {
        if (!t.supports('saveGoal'))
          return {
            ok: true,
            data: {
              title: '',
              description: '',
              milestones: [],
              percent: 0,
              note: '',
              updatedAtUtc: new Date().toISOString(),
            } as SessionGoalDto,
          };
        return normalizeBridgePayload<BridgeResponse<SessionGoalDto>>(
          await t.call<BridgeResponse<SessionGoalDto>>('saveGoal', ws ?? '', sess ?? '', goalJson ?? '')
        );
      } catch (e) {
        return { ok: false, code: 'GOAL_SAVE_ERROR', message: String(e) };
      }
    },
    async clearGoal(ws: string, sess: string) {
      try {
        if (!t.supports('clearGoal')) return { ok: true, data: true };
        return normalizeBridgePayload<BridgeResponse<boolean>>(await t.call<BridgeResponse<boolean>>('clearGoal', ws ?? '', sess ?? ''));
      } catch (e) {
        return { ok: false, code: 'GOAL_CLEAR_ERROR', message: String(e) };
      }
    },
    async getSubSessionDetail(ws: string, sub: string) {
      try {
        if (!t.supports('getSubSessionDetail')) return { ok: false, code: 'SUB_NOT_FOUND', message: '子会话不存在' };
        return normalizeBridgePayload<BridgeResponse<SubSessionDetailDto>>(
          await t.call<BridgeResponse<SubSessionDetailDto>>('getSubSessionDetail', ws ?? '', sub ?? '')
        );
      } catch (e) {
        return { ok: false, code: 'SUB_LIST_ERROR', message: String(e) };
      }
    },
    async getSubSessionMessages(ws: string, sub: string, limit?: number, beforeMessageId?: string) {
      try {
        if (!t.supports('getSubSessionMessages')) return { ok: true, data: [] as UnifiedChatMessage[] };
        return normalizeBridgePayload<BridgeResponse<UnifiedChatMessage[]>>(
          await t.call<BridgeResponse<UnifiedChatMessage[]>>('getSubSessionMessages', ws ?? '', sub ?? '', limit, beforeMessageId ?? '')
        );
      } catch (e) {
        console.warn('[bridge] native unavailable, fallback empty state:', (e as Error)?.message ?? e);
        return { ok: true, data: [] as UnifiedChatMessage[] };
      }
    },
    async truncateSessionHistory(workspaceId: string, sessionId: string, anchorMessageId: string, removeAnchor: boolean) {
      return normalizeBridgePayload<BridgeResponse<HistoryTruncateResultDto>>(
        await t.call('truncateSessionHistory', workspaceId ?? '', sessionId ?? '', anchorMessageId ?? '', removeAnchor ?? true)
      );
    },
    async branchSession(workspaceId: string, sourceSessionId: string, anchorMessageId: string, includeAnchor: boolean) {
      return normalizeBridgePayload<BridgeResponse<SessionMetadata>>(
        await t.call('branchSession', workspaceId ?? '', sourceSessionId ?? '', anchorMessageId ?? '', includeAnchor ?? true)
      );
    },
    async healSessionHistory(workspaceId: string, sessionId: string) {
      return normalizeBridgePayload<BridgeResponse<HistoryHealResultDto>>(
        await t.call('healSessionHistory', workspaceId ?? '', sessionId ?? '')
      );
    },
    async deleteSessionMessage(workspaceId: string, sessionId: string, messageId: string) {
      return normalizeBridgePayload<BridgeResponse<HistoryDeleteResultDto>>(
        await t.call('deleteSessionMessage', workspaceId ?? '', sessionId ?? '', messageId ?? '')
      );
    },
  };
}

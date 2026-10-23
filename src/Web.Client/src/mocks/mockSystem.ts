/** need22-P5 J4：Mock 系统域方法（system/storage/network/provider/llm/run；原 createBrowserMockBridge 前半，行为不变）。 */
import { haloEventBus } from '@shared/bridge/events';
import { fireMockAskUser } from './mockState';
import { normalizeVersionedBaseUrlClient } from '@shared/bridge/api/normalize';
import type { MockState } from './mockState';
import type {
  GlobalNetworkSettings,
  HostBridgeApi,
  ProviderConfig,
  ProvidersConfigDocument,
  UnifiedLlmRequestDto,
} from '@shared/bridge/protocol/types';

export function mockSystemMethods(s: MockState): Partial<HostBridgeApi> {
  return {
    async getSystemInfo() {
      return {
        ok: true,
        data: {
          osDescription: 'Windows 10/11 (Web Dev Mock)',
          osVersionString: 'Microsoft Windows NT 10.0.22631.0',
          isWin7: false,
          architecture: 'X64',
          processArchitecture: 'X64',
          dotnetVersion: '.NET 6.0.36 (Mock)',
          cefSharpVersion: '109.1.110.0',
          currentDataRoot: s.mockDataRoot,
          bootstrapFilePath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\bootstrap.json',
          logsDirectory: `${s.mockDataRoot}\\logs`,
          gpuAccelerationDisabled: false,
          tlsProtocols: 'Tls12, Tls13',
        },
      };
    },
    async getStorageConfig() {
      return {
        ok: true,
        data: {
          dataRootPath: s.mockDataRoot,
          defaultPath: 'C:\\HaloAgentData',
          fallbackPath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\Data',
          bootstrapFilePath: 'C:\\Users\\Developer\\AppData\\Local\\HaloAgent\\bootstrap.json',
          previousDataRootPath: null,
          lastModifiedUtc: new Date().toISOString(),
          isUsingFallback: false,
          isWritable: true,
          diskFreeSpaceMb: 131480,
          diskTotalSpaceMb: 512000,
          logRetentionDays: 14,
          logChannels: [
            {
              channel: 'app',
              label: '系统运行日志 (App)',
              fileName: `app-${s.today}.log`,
              filePath: `${s.mockDataRoot}\\logs\\app-${s.today}.log`,
              sizeBytes: 4096,
              exists: true,
              lastWriteTimeUtc: new Date().toISOString(),
            },
            {
              channel: 'llm',
              label: '大模型请求审计 (LLM Trace)',
              fileName: `llm-trace-${s.today}.log`,
              filePath: `${s.mockDataRoot}\\logs\\llm-trace-${s.today}.log`,
              sizeBytes: 1024,
              exists: true,
              lastWriteTimeUtc: new Date().toISOString(),
            },
            {
              channel: 'tool',
              label: '工具执行日志 (Tool Exec)',
              fileName: `tool-exec-${s.today}.log`,
              filePath: `${s.mockDataRoot}\\logs\\tool-exec-${s.today}.log`,
              sizeBytes: 512,
              exists: true,
              lastWriteTimeUtc: new Date().toISOString(),
            },
          ],
        },
      };
    },
    async validateStoragePath(targetPath: string) {
      return {
        ok: true,
        data: {
          isValid: !!targetPath.trim(),
          normalizedPath: targetPath.trim(),
          message: '路径校验通过 (Mock)',
          availableFreeSpaceMb: 131480,
        },
      };
    },
    async selectFolderDialog(initialPath?: string) {
      const input = window.prompt('请输入模拟选择的新存储目录路径：', initialPath || 'C:\\HaloAgentData_Test');
      return {
        ok: true,
        data: input ? input.trim() : null,
      };
    },
    async changeStoragePath(newPath: string, migrateData: boolean) {
      const oldRoot = s.mockDataRoot;
      haloEventBus.emitLocal('storage:migration-progress', {
        percent: 50,
        message: '正在复制配置文件与日志...',
        targetPath: newPath,
      });
      await new Promise((r) => setTimeout(r, 250));
      s.mockDataRoot = newPath;
      haloEventBus.emitLocal('storage:migration-progress', {
        percent: 100,
        message: '存储路径切换完成。',
        targetPath: newPath,
      });
      return {
        ok: true,
        message: `已切换至 ${newPath}`,
        data: {
          success: true,
          oldDataRootPath: oldRoot,
          newDataRootPath: newPath,
          migratedData: migrateData,
          migratedFileCount: migrateData ? 4 : 0,
          migratedTotalBytes: migrateData ? 5632 : 0,
          message: `已切换至 ${newPath}`,
        },
      };
    },
    async resetStoragePathToDefault(migrateData: boolean) {
      return s.self!.changeStoragePath('C:\\HaloAgentData', migrateData);
    },
    async openPathInExplorer() {
      return { ok: true, data: true };
    },
    async openExternalLink(href: string) {
      try {
        const link = String(href || '');
        if (/^https?:\/\//i.test(link)) window.open(link, '_blank', 'noopener');
      } catch (e) {
        console.warn('[bridge-mock] openExternalLink mock ignored [MOCK]:', (e as Error)?.message ?? e);
      }
      return { ok: true, data: true };
    },
    async cleanupExpiredLogs() {
      return { ok: true, data: 0, message: '清理完成，共移除 0 个过期日志文件。' };
    },
    async writeDiagnosticLog() {
      return { ok: true, data: true };
    },
    async setWindowTitle(title: string) {
      document.title = title;
      return { ok: true, data: true };
    },

    // Stage 02 Mock Implementations
    async getNetworkSettings() {
      return {
        ok: true,
        data: JSON.parse(JSON.stringify(s.mockNetworkSettings)),
      };
    },
    async saveNetworkSettings(settings: GlobalNetworkSettings | string) {
      const parsed: GlobalNetworkSettings = typeof settings === 'string' ? JSON.parse(settings) : JSON.parse(JSON.stringify(settings));
      if (parsed.customProxy?.password && !parsed.customProxy.password.includes('****')) {
        parsed.customProxy.passwordEncrypted = 'DPAPI_MOCK_PROXY_PWD';
        parsed.customProxy.passwordMasked = '********';
      }
      if (parsed.customProxy) {
        delete parsed.customProxy.password;
      }
      s.mockNetworkSettings = parsed;
      return {
        ok: true,
        message: '网络与代理设置已保存并立即生效 (Mock)',
        data: JSON.parse(JSON.stringify(s.mockNetworkSettings)),
      };
    },
    async inspectSystemProxy() {
      return {
        ok: true,
        data: {
          isEnabled: true,
          proxyServerRaw: '127.0.0.1:7890',
          effectiveHttpProxyUri: 'http://127.0.0.1:7890',
          autoConfigUrl: null,
          bypassOverride: 'localhost;127.*;10.*;192.168.*;<local>',
          inspectedAtUtc: new Date().toISOString(),
        },
      };
    },
    async testProxyConnectivity(targetUrl?: string, overrideSettings?: GlobalNetworkSettings | string | null) {
      const parsedSettings: GlobalNetworkSettings = overrideSettings
        ? typeof overrideSettings === 'string'
          ? JSON.parse(overrideSettings)
          : overrideSettings
        : s.mockNetworkSettings;

      const effectiveSummary =
        parsedSettings.mode === 'None'
          ? 'DIRECT (No Proxy)'
          : parsedSettings.mode === 'Custom'
            ? `CUSTOM (${parsedSettings.customProxy.scheme}://${parsedSettings.customProxy.host}:${parsedSettings.customProxy.port})`
            : 'SYSTEM (http://127.0.0.1:7890)';

      return {
        ok: true,
        message: `连通测试成功 (42 ms · ${effectiveSummary})`,
        data: {
          success: true,
          statusCode: 200,
          elapsedMs: 42,
          effectiveProxyUsed: effectiveSummary,
          targetUrl: targetUrl || 'https://api.openai.com/v1/models',
          errorMessage: null,
          diagnosticCategory: 'OK',
        },
      };
    },
    async getProvidersConfig() {
      return {
        ok: true,
        data: JSON.parse(JSON.stringify(s.mockProvidersDoc)),
      };
    },
    async saveProvidersConfig(doc: ProvidersConfigDocument | string) {
      const parsed: ProvidersConfigDocument = typeof doc === 'string' ? JSON.parse(doc) : JSON.parse(JSON.stringify(doc));
      s.mockProvidersDoc = parsed;
      return {
        ok: true,
        message: '大模型服务商与模型配置已安全加密落盘 (Mock)',
        data: JSON.parse(JSON.stringify(s.mockProvidersDoc)),
      };
    },
    async upsertProvider(provider: ProviderConfig | string) {
      const parsed: ProviderConfig = typeof provider === 'string' ? JSON.parse(provider) : JSON.parse(JSON.stringify(provider));
      if (parsed.apiKey && !parsed.apiKey.includes('****')) {
        parsed.apiKeyEncrypted = 'DPAPI_MOCK_CIPHER';
        parsed.apiKeyMasked = parsed.apiKey.length > 8 ? `${parsed.apiKey.slice(0, 4)}****${parsed.apiKey.slice(-4)}` : '********';
        parsed.hasApiKey = true;
      }
      delete parsed.apiKey;
      const idx = s.mockProvidersDoc.providers.findIndex((p) => p.id === parsed.id);
      if (idx >= 0) {
        s.mockProvidersDoc.providers[idx] = parsed;
      } else {
        s.mockProvidersDoc.providers.push(parsed);
      }
      return {
        ok: true,
        message: `服务商 [${parsed.name}] 已保存 (Mock)`,
        data: JSON.parse(JSON.stringify(s.mockProvidersDoc)),
      };
    },
    async deleteProvider(providerId: string) {
      s.mockProvidersDoc.providers = s.mockProvidersDoc.providers.filter((p) => p.id !== providerId);
      if (s.mockProvidersDoc.activeProviderId === providerId) {
        s.mockProvidersDoc.activeProviderId = s.mockProvidersDoc.providers[0]?.id || '';
        s.mockProvidersDoc.activeModelId = s.mockProvidersDoc.providers[0]?.models[0]?.id || '';
      }
      return {
        ok: true,
        message: '服务商已删除 (Mock)',
        data: JSON.parse(JSON.stringify(s.mockProvidersDoc)),
      };
    },
    async setActiveModel(providerId: string, modelId: string) {
      s.mockProvidersDoc.activeProviderId = providerId;
      s.mockProvidersDoc.activeModelId = modelId;
      return {
        ok: true,
        message: `已切换至模型 ${modelId}`,
        data: JSON.parse(JSON.stringify(s.mockProvidersDoc)),
      };
    },
    async previewProviderResolution(provider: ProviderConfig | string, modelId?: string) {
      const parsed: ProviderConfig = typeof provider === 'string' ? JSON.parse(provider) : provider;
      const prefix = normalizeVersionedBaseUrlClient(parsed.baseUrl);
      const sessionHash = '9f86d081884c7d65';
      const resolvedHeaders: Record<string, string> = {};
      if (parsed.enableCustomHeaders && Array.isArray(parsed.customHeaders)) {
        for (const h of parsed.customHeaders) {
          if (h.enabled && h.key.trim()) {
            resolvedHeaders[h.key.trim()] = (h.valueTemplate || '')
              .replace(/\$\{session-hash\}/gi, sessionHash)
              .replace(/\$\{session-id\}/gi, 'sess_stage02_preview')
              .replace(/\$\{workspace-name\}/gi, 'halo-agent')
              .replace(/\$\{workspace-hash\}/gi, 'a1b2c3d4e5f60718')
              .replace(/\$\{model-id\}/gi, modelId || parsed.models[0]?.id || 'gpt-4o')
              .replace(/\$\{provider-name\}/gi, encodeURIComponent(parsed.name))
              .replace(/\$\{provider-id\}/gi, encodeURIComponent(parsed.id));
          }
        }
      }
      return {
        ok: true,
        data: {
          baseUrl: parsed.baseUrl,
          normalizedVersionPrefix: prefix,
          chatEndpointUrl: `${prefix}/chat/completions`,
          respEndpointUrl: `${prefix}/responses`,
          modelsEndpointUrl: `${prefix}/models`,
          effectiveProxySummary:
            parsed.proxyMode === 'None'
              ? 'DIRECT (No Proxy)'
              : parsed.proxyMode === 'Custom' && parsed.customProxy
                ? `CUSTOM (${parsed.customProxy.scheme}://${parsed.customProxy.host}:${parsed.customProxy.port})`
                : 'SYSTEM (http://127.0.0.1:7890)',
          sessionId: 'sess_stage02_preview',
          sessionHash,
          workspaceName: 'halo-agent',
          workspaceHash: 'a1b2c3d4e5f60718',
          resolvedHeaders,
        },
      };
    },
    async startSessionRun(request: UnifiedLlmRequestDto | string) {
      const parsed: UnifiedLlmRequestDto = typeof request === 'string' ? JSON.parse(request) : request;
      const sessionId = parsed.headerContext?.sessionId || parsed.requestId || `mock_sess_${Date.now()}`;

      // 模拟异步流式输出，验证 Mock 模式下 UI 渲染
      setTimeout(() => {
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'StreamStarted',
        });
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'TextDelta',
          textDelta: '你好！我是 Halo Agent（测试 Mock 流式模式）。',
        });
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'UsageUpdated',
          usage: {
            inputTokens: 120,
            outputTokens: 25,
            cachedInputTokens: 80,
            totalTokens: 145,
          },
        });
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'StreamCompleted',
        });
      }, 50);

      return {
        ok: true,
        message: '流式请求已启动 (Mock)',
        data: sessionId,
      };
    },
    async startAgentTurn(
      workspaceId: string,
      sessionId: string,
      userText: string,
      providerId?: string,
      modelId?: string,
      attachmentsJson?: string,
      requestId?: string
    ) {
      void workspaceId;
      void providerId;
      void modelId;
      void requestId;
      // need09-04/05 Mock：解析附件数，回放含图轮次
      let imgCount = 0;
      try {
        const raw = String(attachmentsJson || '');
        if (raw.trim()) {
          const arr = JSON.parse(raw) as unknown;
          if (Array.isArray(arr)) imgCount = arr.length;
        } else {
          const m = /\[image:[^\]]+\]/g;
          const hits = String(userText || '').match(m);
          if (hits) imgCount = hits.length;
        }
      } catch (e) {
        console.warn('[bridge-mock] imgCount parse failed:', (e as Error)?.message ?? e);
        imgCount = 0;
      }
      const withImg = imgCount > 0 ? `（含 ${imgCount} 张图片 Mock）` : '';
      // need09-06 Mock 演示钩子：含 ask/提问/选择关键字时 1s 后回放假问询
      if (/ask|提问|选择|确认/i.test(String(userText || ''))) {
        setTimeout(() => {
          fireMockAskUser(s, sessionId);
        }, 1000);
      }
      // 模拟一轮 ReAct：思考增量 -> 文本增量 -> 工具卡片事件 -> 完成
      setTimeout(() => {
        haloEventBus.emitLocal('agent:turn-event', {
          sessionId,
          kind: 'IterationStarted',
          payload: { iteration: 1, maxIterations: 25 },
        });
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'ReasoningDelta',
          textDelta: '正在分析任务，准备调用工具...',
        });
        haloEventBus.emitLocal('llm:stream-event', {
          sessionId,
          kind: 'TextDelta',
          textDelta: `收到任务${withImg}（Mock 演示模式），正在编排工具调用。`,
        });
        haloEventBus.emitLocal('agent:turn-event', {
          sessionId,
          kind: 'ToolCallStarted',
          payload: { iteration: 1, callId: 'mock_call_001', functionName: 'list_dir' },
        });
        haloEventBus.emitLocal('agent:turn-event', {
          sessionId,
          kind: 'ToolExecutionCompleted',
          payload: { iteration: 1, callId: 'mock_call_001', functionName: 'list_dir', success: true, exitCode: 0, durationMs: 8 },
        });
        haloEventBus.emitLocal('agent:turn-event', {
          sessionId,
          kind: 'TurnCompleted',
          payload: { iterations: 1, toolExecutions: 1, protocol: 'chat', inputTokens: 120, outputTokens: 25, cachedTokens: 80 },
        });
      }, 60);

      return {
        ok: true,
        message: 'Agent 推理已启动 (Mock)',
        data: sessionId,
      };
    },
    // need09-05 Mock：1px 透明 png 占位缩略图
    async getImageThumbnail(_workspaceId: string, _sessionId: string, _imageId: string) {
      void _workspaceId;
      void _sessionId;
      void _imageId;
      return {
        ok: true,
        data: 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
      };
    },
    // need09-06 Mock：ticket 校验 + 提交回 resolved 事件
    async resolveAskUser(sessionId: string, ticketId: string, _answersJson: string) {
      void _answersJson;
      const expect = s.mockAskTickets[sessionId];
      if (!expect || expect !== ticketId) {
        return { ok: false, code: 'ASK_TICKET_MISMATCH', message: '问询票据不匹配', data: false };
      }
      delete s.mockAskTickets[sessionId];
      setTimeout(() => {
        haloEventBus.emitLocal('agent:ask-user-resolved', { sessionId, ticketId, outcome: 'submitted' });
      }, 50);
      return { ok: true, data: true };
    },
    async cancelSessionRun(_sessionId: string) {
      return {
        ok: true,
        message: '已发送取消指令 (Mock)',
        data: true,
      };
    },
    async cancelAgentTurn(_sessionId: string) {
      return {
        ok: true,
        message: '已发送取消指令 (Mock)',
        data: true,
      };
    },
  };
}

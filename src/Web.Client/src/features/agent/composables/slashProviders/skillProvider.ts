/**
 * need22-F03：默认候选池内容 = skill（走 bridge api 直取，不复用 skillMemoryStore 的 kind/scopeFilter）。
 * 通道不可用（老后端 / mock 无该方法）⇒ 返回 [] 且不抛，面板显示"技能通道不可用"。
 */
import { getHostBridge, type BridgeResponse, type SkillMemoryListItem } from '@shared/bridge/hostBridge';
import type { SlashCandidate, SlashProvider } from '@shared/utils/slashRegistry';

export const skillSlashProvider: SlashProvider = {
  id: 'skill',
  order: 10,
  async load(ctx): Promise<SlashCandidate[]> {
    let res: BridgeResponse<SkillMemoryListItem[]>;
    try {
      res = await getHostBridge().listSkillMemory('skill', 'all', ctx.workspaceId || undefined);
    } catch (e) {
      console.warn('[slash] skill provider 调用失败:', (e as Error)?.message ?? e);
      ctx.reportError?.('技能通道不可用');
      return [];
    }
    if (!res || !res.ok || !Array.isArray(res.data)) {
      ctx.reportError?.(res?.message || res?.code || '技能通道不可用');
      return [];
    }
    return res.data
      .filter((s) => s.enabled) // 只给启用的
      .map((s) => ({
        id: `skill:${s.scope}:${s.name}`,
        providerId: 'skill',
        scope: s.scope,
        prefix: `${s.scope}/`,
        label: `${s.scope}/${s.name}`,
        title: s.title,
        description: s.summary,
        keywords: [s.name, s.title, s.summary].filter(Boolean) as string[],
        payload: { scope: s.scope, name: s.name },
      }));
  },
};

// src/Web.Client/src/components/layout/engineStatusMeta.ts
// P07-Enhance 01：引擎状态 → 颜色/文案映射（纯函数，可单测）。

import type { EngineStatusPayload } from '@/bridge/types';

export type EngineState = EngineStatusPayload['state'];

export interface EngineStatusMeta {
  color: 'ready' | 'pending' | 'error';
  text: string;
}

const TEXT: Record<EngineState, string> = {
  ready: '引擎已连接',
  starting: '引擎启动中',
  restarting: '引擎重启中',
  failed: '引擎失败',
  stopped: '引擎已停止',
};

export function mapEngineState(state: EngineState): EngineStatusMeta {
  switch (state) {
    case 'ready':
      return { color: 'ready', text: TEXT.ready };
    case 'starting':
    case 'restarting':
      return { color: 'pending', text: TEXT[state] };
    case 'failed':
    case 'stopped':
    default:
      return { color: 'error', text: TEXT[state] ?? TEXT.stopped };
  }
}

/** hover 浮层首行文案：状态 + 可选 pid。 */
export function describeEngineStatus(s: EngineStatusPayload, isMock: boolean): string {
  const base = mapEngineState(s.state).text;
  const pid = typeof s.pid === 'number' ? ` · PID ${s.pid}` : '';
  const mock = isMock ? ' · Mock' : '';
  return `${base}${pid}${mock}`;
}

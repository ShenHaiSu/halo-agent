/** need22-P5 J4：宿主事件总线（自 hostBridge.ts 迁入：dispatchIncomingEvents + haloEventBus；connect 层唯一入口）。 */
import type { HostEventEnvelope } from './types';

type EventListener = (payload: any, timestamp: number) => void;
const listeners = new Map<string, Set<EventListener>>();

export function dispatchIncomingEvents(events: Array<HostEventEnvelope>): void {
  if (!Array.isArray(events)) {
    return;
  }

  for (const evt of events) {
    if (!evt || typeof evt.type !== 'string') {
      continue;
    }

    const bucket = listeners.get(evt.type);
    if (bucket) {
      for (const fn of bucket) {
        try {
          fn(evt.payload, evt.timestamp);
        } catch (err) {
          console.error('[haloEventBus] handler error:', err);
        }
      }
    }

    const wildcardBucket = listeners.get('*');
    if (wildcardBucket) {
      for (const fn of wildcardBucket) {
        try {
          fn(evt, evt.timestamp);
        } catch (err) {
          console.error('[haloEventBus] wildcard handler error:', err);
        }
      }
    }
  }
}

export const haloEventBus = {
  on<T = unknown>(eventType: string, handler: (payload: T, timestamp: number) => void): () => void {
    let set = listeners.get(eventType);
    if (!set) {
      set = new Set();
      listeners.set(eventType, set);
    }
    set.add(handler as EventListener);
    return () => {
      set?.delete(handler as EventListener);
    };
  },
  emitLocal(eventType: string, payload: unknown): void {
    dispatchIncomingEvents([
      {
        type: eventType,
        payload,
        timestamp: Date.now(),
      },
    ]);
  },
};

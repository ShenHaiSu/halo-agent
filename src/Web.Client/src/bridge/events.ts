// src/Web.Client/src/bridge/events.ts
// D4-7 宿主事件桥：注册 window.__haloHostEvent + pending 队列 + 订阅/取消订阅。
import type { HostEventKind } from './types';

type Handler = (payload: unknown) => void;

const handlers = new Map<HostEventKind, Set<Handler>>();
const pending: Array<{ kind: HostEventKind; payload: unknown }> = [];
let registered = false;

/**
 * 注册宿主事件入口 `window.__haloHostEvent`。
 *
 * ============ 这是宿主"前端就绪"的信号 ============
 *  宿主侧（P3）在页面加载前就开始产生事件，此时 __haloHostEvent 还不存在。
 *  宿主把这些事件缓存起来（最多 20 条），
 *  等前端第一次调用 haloHost.systemInfo() 时补推。
 *
 *  所以：**必须在此函数里立即调用 systemInfo()**，
 *  这样宿主才知道"可以补推了"。
 * ============ 为什么要有 pending 队列 ============
 *  如果有事件在 store 注册处理器之前到达（比如 engineStatus 在 store 初始化前），
 *  直接丢弃就会让 UI 永远显示"引擎未就绪"。
 *  所以先缓存，等处理器注册后补发。
 * ============ ============
 */
export function registerHostEventBridge(): void {
  if (registered) return;
  registered = true;

  window.__haloHostEvent = (kind: HostEventKind, payload: unknown) => {
    const set = handlers.get(kind);
    if (!set || set.size === 0) {
      // 还没有处理器：缓存起来
      if (pending.length < 50) pending.push({ kind, payload });
      return;
    }
    for (const h of set) {
      try {
        h(payload);
      } catch (e) {
        console.error(`[bridge] 事件处理器异常 kind=${kind}`, e);
      }
    }
  };

  // 关键：调用一次 systemInfo()，告诉宿主"前端就绪，请补推"
  void window.haloHost?.systemInfo().catch(() => {
    /* Mock 环境下可能不存在，忽略 */
  });
}

/** 订阅宿主事件。返回取消订阅函数。 */
export function onHostEvent(kind: HostEventKind, handler: Handler): () => void {
  let set = handlers.get(kind);
  if (!set) {
    set = new Set();
    handlers.set(kind, set);
  }
  set.add(handler);

  // 补发 pending
  for (let i = pending.length - 1; i >= 0; i--) {
    const item = pending[i]!;
    if (item.kind === kind) {
      pending.splice(i, 1);
      try {
        handler(item.payload);
      } catch (e) {
        console.error(`[bridge] pending 事件处理异常 kind=${kind}`, e);
      }
    }
  }

  return () => {
    set!.delete(handler);
    if (set!.size === 0) handlers.delete(kind);
  };
}

/** 仅测试用 */
export function __resetEventsForTest(): void {
  handlers.clear();
  pending.length = 0;
  registered = false;
  delete window.__haloHostEvent;
}

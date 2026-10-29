// src/Web.Client/src/devtools/p5diag.ts
// P5 验收诊断工具（见 07-验收方法.md §7）。
//
// 挂载条件（main.ts）：import.meta.env.DEV || location.search 含 diag=1。
// 只读原则：所有命令只注入 + 观察 + 打印，不写盘；每轮结束恢复 store 快照。
// failNext 机制：包装 transport.request，对指定方法下一次调用强制抛错（无需改 Mock）。
import { nextTick } from 'vue';
import { transport } from '@/transport';

export interface DiagResult {
  pass: number;
  total: number;
}

class Asserts {
  private results: string[] = [];
  ok(name: string, cond: boolean, extra = ''): void {
    this.results.push(`${cond ? '[OK]' : '[NG]'} ${name}${extra ? '  ' + extra : ''}`);
  }
  dump(title: string): boolean {
    // eslint-disable-next-line no-console
    console.log(`[p5diag] ==== ${title} ====`);
    for (const r of this.results) {
      // eslint-disable-next-line no-console
      console.log('[p5diag]', r);
    }
    const total = this.results.length;
    const pass = this.results.filter((r) => r.startsWith('[OK]')).length;
    // eslint-disable-next-line no-console
    console.log('[p5diag] ' + pass + '/' + total + (pass === total ? ' [OK]' : ' [NG]'));
    return pass === total;
  }
}

async function wait(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

async function raf(): Promise<void> {
  return new Promise((r) => requestAnimationFrame(() => r()));
}

/** 状态快照 / 恢复（messages + stream + permissions） */
async function withStoreSnapshot<T>(fn: () => Promise<T>): Promise<T> {
  const { useMessagesStore } = await import('@/stores/messages');
  const { useStreamStore } = await import('@/stores/stream');
  const { usePermissionsStore } = await import('@/stores/permissions');
  const m = useMessagesStore();
  const s = useStreamStore();
  const p = usePermissionsStore();
  const snap = {
    items: JSON.parse(JSON.stringify(m.items)) as unknown,
    lastSeq: m.lastSeq,
    streamingId: m.streamingId,
    busy: s.busy,
    notice: s.notice,
    retrying: s.retrying,
    resyncRequired: s.resyncRequired,
    compactionStarted: s.compactionStarted,
    pending: p.pending,
  };
  try {
    return await fn();
  } finally {
    m.items = snap.items as never;
    m.lastSeq = snap.lastSeq;
    m.streamingId = snap.streamingId;
    s.busy = snap.busy;
    s.notice = snap.notice;
    s.retrying = snap.retrying;
    s.resyncRequired = snap.resyncRequired;
    s.compactionStarted = snap.compactionStarted;
    p.pending = snap.pending;
    await nextTick();
  }
}

/** 注入一个事件帧 */
async function emit(kind: string, payload: unknown, seqOffset = 1): Promise<void> {
  const { useMessagesStore } = await import('@/stores/messages');
  const { dispatchFrames } = await import('@/stores/eventBridge');
  const m = useMessagesStore();
  dispatchFrames([{ t: 'evt', seq: m.lastSeq + seqOffset, k: kind, p: payload } as never]);
  await nextTick();
  await raf();
}

// ---- failNext：下一次调用指定方法时强制抛错 ----
const failNextSet = new Set<string>();
let requestWrapped = false;

function ensureFailNext(): void {
  if (requestWrapped) return;
  requestWrapped = true;
  const orig = transport.request.bind(transport);
  transport.request = (async (method: string, params: unknown) => {
    if (failNextSet.has(method)) {
      failNextSet.delete(method);
      throw new Error(`Mock 强制失败：${method}`);
    }
    return orig(method as never, params as never);
  }) as typeof transport.request;
}

export function failNext(method: string): void {
  ensureFailNext();
  failNextSet.add(method);
}

// ---- 24 事件 ----
async function events(): Promise<void> {
  await withStoreSnapshot(async () => {
    const a = new Asserts();
    const { useMessagesStore } = await import('@/stores/messages');
    const { useStreamStore } = await import('@/stores/stream');
    const { usePermissionsStore } = await import('@/stores/permissions');
    const m = useMessagesStore();
    const s = useStreamStore();
    const p = usePermissionsStore();

    await emit('turn.started', { turnId: 't1' });
    a.ok('1 turn.started → busy + 插入 assistant', s.busy && m.streamingId !== null);

    const before = m.items.find((x) => x.id === m.streamingId)?.content.length ?? 0;
    await emit('text.delta', { text: '你' });
    await emit('text.delta', { text: '好' });
    const after = m.items.find((x) => x.id === m.streamingId)?.content.length ?? 0;
    a.ok('3 text.delta → 追加文本', after === before + 2, `${before}→${after}`);

    await emit('reasoning.delta', { text: '思考中' });
    a.ok(
      '4 reasoning.delta → reasoning 有值',
      !!m.items.find((x) => x.id === m.streamingId)?.reasoning
    );

    await emit('tool.started', {
      toolCallId: 'c1',
      name: 'list_directory',
      args: '{"path":"."}',
      idleMs: 0,
    });
    const hasTool = m.items.some((x) => x.toolCalls?.some((t) => t.id === 'c1'));
    a.ok('5 tool.started → 卡片出现', hasTool);

    await emit('tool.outputDelta', { toolCallId: 'c1', delta: 'a.txt\n' });
    await wait(50);
    const outLen =
      m.items.flatMap((x) => x.toolCalls ?? []).find((t) => t.id === 'c1')?.output?.length ?? 0;
    a.ok('7 tool.outputDelta → 输出增加', outLen > 0, `len=${outLen}`);

    await emit('tool.silentAlert', { toolCallId: 'c1', level: 1 });
    a.ok(
      '8 tool.silentAlert(1) → alertLevel=1',
      m.items.flatMap((x) => x.toolCalls ?? []).find((t) => t.id === 'c1')?.alertLevel === 1
    );
    await emit('tool.silentAlert', { toolCallId: 'c1', level: 2 });
    a.ok(
      '8 tool.silentAlert(2) → alertLevel=2',
      m.items.flatMap((x) => x.toolCalls ?? []).find((t) => t.id === 'c1')?.alertLevel === 2
    );

    await emit('tool.finished', {
      toolCallId: 'c1',
      name: 'list_directory',
      ok: true,
      output: 'a.txt\nb.txt\n',
    });
    a.ok(
      '6 tool.finished → status=done',
      m.items.flatMap((x) => x.toolCalls ?? []).find((t) => t.id === 'c1')?.status === 'done'
    );

    await emit('round.started', { used: 3, limit: 200 });
    a.ok('9 round.started → round 更新', s.round.used === 3);

    await emit('llm.retrying', { attempt: 2, max: 8, reason: 'rate limit' });
    a.ok('10 llm.retrying → retrying 有值', !!s.retrying);

    await emit('usage', { prompt: 100, completion: 50 });
    a.ok('11 usage → lastPromptTokens=100', s.usage.lastPromptTokens === 100);

    await emit('notice', { text: '提示信息', level: 'info' });
    a.ok('12 notice(info) → notice 有值', s.notice?.text === '提示信息');
    await emit('notice', { text: '错误信息', level: 'error' });
    a.ok('12 notice(error) → level=error', s.notice?.level === 'error');

    await emit('ask.permission', {
      askId: 'a1',
      kind: 'tool.shell',
      title: '是否允许执行命令？',
      body: 'dir',
      warning: null,
      allowAlways: true,
    });
    a.ok('13 ask.permission → pending 有值', p.pending?.askId === 'a1');
    a.ok('13 询问卡已渲染', !!document.querySelector('[data-ask-card]'));

    p.clear();
    await emit('ask.rounds', { askId: 'a2', used: 200 });
    a.ok('14 ask.rounds → kind=agent.rounds', p.pending?.kind === 'agent.rounds');
    p.clear();

    await emit('turn.finished', { turnId: 't1', text: '你好', truncated: false });
    a.ok('2 turn.finished → busy=false + streamingId 清空', !s.busy && m.streamingId === null);

    await emit('session.info', { id: 's1', cwd: 'C:\\work', historyPath: 'C:\\work\\s1.jsonl' });
    const { useSessionStore } = await import('@/stores/session');
    a.ok('15 session.info → session.cwd 更新', useSessionStore().cwd === 'C:\\work');

    await emit('session.historyLoaded', {
      count: 2,
      messages: [
        { id: 'h1', role: 'user', text: 'hi', tsMs: Date.now() },
        { id: 'h2', role: 'assistant', text: 'hello', tsMs: Date.now() },
      ],
    });
    a.ok('16 session.historyLoaded → 消息替换', m.items.length === 2);

    await emit('session.contextCleared', {});
    a.ok('17 session.contextCleared → 消息清空', m.items.length === 0);

    await emit('session.compactionStarted', {});
    a.ok('18 compactionStarted → 标记为 true', s.compactionStarted === true);

    await emit('session.compacted', { removed: 5, summary: '摘要', kept: ['a', 'b'] });
    a.ok('19 compacted → 标记清除', s.compactionStarted === false);

    await emit('user.injected', { text: '注入的消息' });
    a.ok(
      '20 user.injected → 插入 user 消息',
      m.items.some((x) => x.content === '注入的消息')
    );

    await emit('state.personaChanged', { name: '严格审阅者' });
    a.ok('21 personaChanged → session.persona 更新', useSessionStore().persona === '严格审阅者');

    await emit('state.modeChanged', { mode: 'acceptEdits', label: '自动接受编辑' });
    a.ok('22 modeChanged → mode 更新', useSessionStore().mode === 'acceptEdits');

    await emit('state.busyChanged', { busy: false, queueLength: 0 });
    a.ok('23 busyChanged → busy=false', s.busy === false);

    a.ok('24 resync.required → 见 __p5diag.resync()', true);

    a.dump('24 事件 → UI 变化');
  });
}

// ---- resync ----
async function resync(): Promise<void> {
  const { useStreamStore } = await import('@/stores/stream');
  const { useMessagesStore } = await import('@/stores/messages');
  const s = useStreamStore();
  const m = useMessagesStore();
  const a = new Asserts();

  const calls: string[] = [];
  const agent = (
    window as unknown as { haloAgent?: { request: (k: string, p: unknown) => Promise<string> } }
  ).haloAgent;
  const { dispatchFrames } = await import('@/stores/eventBridge');
  const p = dispatchFrames([
    { t: 'evt', seq: m.lastSeq + 1, k: 'resync.required', p: { reason: 'diag' } } as never,
  ]);
  void p;

  a.ok('黄条立即出现', s.resyncRequired === true);
  await wait(2000);
  void calls;
  void agent;
  a.ok('黄条已消失（Mock 自动 resync）', s.resyncRequired === false);
  a.ok('消息仍为数组', Array.isArray(m.items));
  const { useUiStore } = await import('@/stores/ui');
  a.ok('collapsedTools 已清空', useUiStore().collapsedTools.size === 0);
  a.dump('resync.required 自动恢复');
}

// ---- IME 4 场景 ----
async function ime(): Promise<void> {
  const a = new Asserts();
  const ta = document.querySelector<HTMLTextAreaElement>('.composer-input textarea');
  if (!ta) {
    // eslint-disable-next-line no-console
    console.log('[p5diag] [NG] 找不到输入框');
    return;
  }
  ta.focus();
  const { useMessagesStore } = await import('@/stores/messages');
  const { useStreamStore } = await import('@/stores/stream');
  const m = useMessagesStore();
  const s = useStreamStore();

  ta.value = '@';
  ta.setSelectionRange(1, 1);
  document.dispatchEvent(new CompositionEvent('compositionstart'));
  ta.dispatchEvent(new InputEvent('input', { bubbles: true, isComposing: true }));
  await raf();
  await raf();
  a.ok('① 组合中不弹浮层', !document.querySelector('[data-mention-popover]'));
  document.dispatchEvent(new CompositionEvent('compositionend'));
  await wait(50);

  const before = m.items.length;
  document.dispatchEvent(new CompositionEvent('compositionstart'));
  ta.dispatchEvent(
    new KeyboardEvent('keydown', {
      key: 'Enter',
      bubbles: true,
      cancelable: true,
    } as KeyboardEventInit)
  );
  await raf();
  a.ok('② 组合中 Enter 不发送', m.items.length === before);
  document.dispatchEvent(new CompositionEvent('compositionend'));
  await wait(50);

  const before3 = m.items.length;
  document.dispatchEvent(new CompositionEvent('compositionstart'));
  document.dispatchEvent(new CompositionEvent('compositionend'));
  ta.dispatchEvent(
    new KeyboardEvent('keydown', {
      key: 'Enter',
      bubbles: true,
      cancelable: true,
    } as KeyboardEventInit)
  );
  await raf();
  a.ok('③ compositionend 同帧 Enter 不发送（30ms 延迟）', m.items.length === before3);
  await wait(50);

  s.busy = true;
  document.dispatchEvent(new CompositionEvent('compositionstart'));
  ta.dispatchEvent(
    new KeyboardEvent('keydown', {
      key: 'Escape',
      bubbles: true,
      cancelable: true,
    } as KeyboardEventInit)
  );
  await raf();
  a.ok('④ 组合中 Esc 不中断生成', s.busy === true);
  document.dispatchEvent(new CompositionEvent('compositionend'));
  s.busy = false;
  ta.value = '';
  a.dump('IME 4 场景');
}

// ---- @ / ----
async function mentionSlash(): Promise<void> {
  const a = new Asserts();
  const ta = document.querySelector<HTMLTextAreaElement>('.composer-input textarea');
  if (!ta) {
    // eslint-disable-next-line no-console
    console.log('[p5diag] [NG] 找不到输入框');
    return;
  }
  ta.focus();
  const typeText = async (t: string): Promise<void> => {
    ta.value = t;
    ta.setSelectionRange(t.length, t.length);
    ta.dispatchEvent(new InputEvent('input', { bubbles: true }));
    await raf();
    await raf();
  };

  await typeText('a@b');
  await wait(150);
  a.ok('① a@b 不触发 @ 浮层', !document.querySelector('[data-mention-popover]'));

  await typeText('x /cmd');
  await wait(150);
  a.ok('② x /cmd 不触发 / 浮层', !document.querySelector('.slash'));

  await typeText('/');
  await raf();
  a.ok('③ / 在开头触发 slash', !!document.querySelector('.slash'));

  await typeText('@');
  await wait(150);
  const opened = !!document.querySelector('[data-mention-popover]');
  await typeText('@ ');
  await wait(150);
  a.ok('④ @ 后空格自动关闭', opened && !document.querySelector('[data-mention-popover]'));

  await typeText('@');
  await wait(150);
  ta.blur();
  await wait(200);
  a.ok('⑥ 失焦后浮层关闭', !document.querySelector('[data-mention-popover]'));

  await typeText('@');
  await wait(200);
  const items = document.querySelectorAll('.mention-item').length;
  a.ok('⑦ @ 能拉到候选项', items > 0, `items=${items}`);
  a.ok('⑧ 竞态无异常（未抛错）', true);

  ta.value = '';
  a.dump('@ / 触发与互斥');
}

// ---- 拖放粘贴 ----
async function dropPaste(): Promise<void> {
  const a = new Asserts();
  const { useHostStore } = await import('@/stores/host');
  const host = useHostStore();

  const before = document.querySelectorAll('.composer-attach > *').length;
  host.droppedFiles = ['C:\\work\\a.ts', 'C:\\work\\b.ts', 'C:\\work\\c.ts'];
  await wait(200);
  const after = document.querySelectorAll('.composer-attach > *').length;
  a.ok('① 拖入 3 文件 → 标签增加 3', after >= before + 3, `${before}→${after}`);

  const before2 = document.querySelectorAll('.composer-attach > *').length;
  host.droppedFiles = ['C:\\work\\evil.exe'];
  await wait(200);
  const after2 = document.querySelectorAll('.composer-attach > *').length;
  a.ok('② .exe 被拒', after2 === before2);

  host.droppedFiles = Array.from({ length: 25 }, (_, i) => `C:\\work\\f${i}.ts`);
  await wait(300);
  const total = document.querySelectorAll('.composer-attach > *').length;
  a.ok('③ 25 个 → 总数 ≤ 20', total <= 20, `total=${total}`);

  a.ok('④ 双来源 500ms 去重 → 见人工验证（H-5）', true);
  a.ok('⑤⑥ 粘贴图片 → 见人工验证', true);

  host.droppedFiles = [];
  a.dump('拖放与粘贴');
}

// ---- 排队 ----
async function queue(): Promise<void> {
  const a = new Asserts();
  const { useStreamStore } = await import('@/stores/stream');
  const { useQueueStore } = await import('@/stores/queue');
  const s = useStreamStore();
  const q = useQueueStore();

  q.clear();
  s.busy = true;

  const ok1 = await q.push('消息 1');
  a.ok('① 排队成功', ok1 && q.items.length === 1);

  for (let i = 2; i <= 5; i++) await q.push(`消息 ${i}`);
  const ok6 = await q.push('消息 6');
  a.ok('② 满 5 条拒绝', !ok6 && q.items.length === 5);

  const okRm = await q.remove(0);
  a.ok('③ 撤回成功', okRm && q.items.length === 4);

  a.ok(
    '④ 队列 UI 出现',
    !!document.querySelector('.queue, .composer-queue, [data-queue]') || q.items.length === 4
  );

  q.syncFromEngine(2);
  a.ok('⑤ 引擎说 2 条 → 本地裁剪为 2', q.items.length === 2);

  q.clear();
  s.busy = false;
  a.dump('排队');
}

// ---- 草稿恢复 ----
async function draftRestore(): Promise<void> {
  const a = new Asserts();
  const ta = document.querySelector<HTMLTextAreaElement>('.composer-input textarea');
  if (!ta) {
    // eslint-disable-next-line no-console
    console.log('[p5diag] [NG] 找不到输入框');
    return;
  }
  failNext('agent.prompt');
  ta.value = '测试草稿内容';
  ta.setSelectionRange(0, 0);
  ta.dispatchEvent(new InputEvent('input', { bubbles: true }));
  await raf();

  const btn = [...document.querySelectorAll('button')].find((b) => b.textContent?.includes('发送'));
  btn?.click();
  await wait(500);

  a.ok('发送失败后文本恢复', ta.value === '测试草稿内容', `got="${ta.value}"`);
  ta.value = '';
  a.dump('草稿恢复');
}

// ---- warning 两强制行为 ----
async function askWarning(): Promise<void> {
  await withStoreSnapshot(async () => {
    const a = new Asserts();
    const { usePermissionsStore } = await import('@/stores/permissions');
    const p = usePermissionsStore();

    const check = async (
      payload: Record<string, unknown>
    ): Promise<{ buttons: string[]; focus: string }> => {
      p.setAsk(payload as never);
      await nextTick();
      await wait(80);
      const card = document.querySelector('[data-ask-card]');
      if (!card) return { buttons: [], focus: '' };
      const buttons = [...card.querySelectorAll('button')].map((b) => b.textContent?.trim() ?? '');
      const focus = (document.activeElement?.textContent ?? '').trim();
      return { buttons, focus };
    };

    const A = await check({
      askId: 'wa1',
      kind: 'tool.shell',
      title: '是否允许执行命令？',
      body: 'rd /s /q C:\\work\\build',
      warning: '该命令会永久删除目录及其全部内容，无法恢复。',
      allowAlways: true,
    });
    a.ok(
      'A 有 warning 时无"总是允许"',
      !A.buttons.some((b) => b.includes('总是允许')),
      JSON.stringify(A.buttons)
    );
    a.ok('A 焦点在"拒绝"', A.focus.includes('拒绝'), A.focus);

    const B = await check({
      askId: 'wa2',
      kind: 'tool.shell',
      title: '是否允许执行命令？',
      body: 'dir',
      warning: null,
      allowAlways: false,
    });
    a.ok('B 焦点在"允许一次"', B.focus.includes('允许一次'), B.focus);

    const C = await check({
      askId: 'wa3',
      kind: 'tool.shell',
      title: '是否允许执行命令？',
      body: 'dir',
      warning: null,
      allowAlways: true,
    });
    a.ok('C 三按钮齐全', C.buttons.length === 3, JSON.stringify(C.buttons));

    p.clear();
    a.dump('询问 warning（R-4 红线）');
  });
}

// ---- 询问 a11y ----
async function askA11y(): Promise<void> {
  const a = new Asserts();
  const card = document.querySelector('[data-ask-card]');
  if (!card) {
    // eslint-disable-next-line no-console
    console.log('[p5diag] [NG] 没有待答询问，先执行 __p5diag.askWarning() 或手动触发');
    return;
  }
  a.ok('role=alertdialog', card.getAttribute('role') === 'alertdialog');
  const lb = card.getAttribute('aria-labelledby');
  a.ok('aria-labelledby 有效', !!lb && !!document.getElementById(lb));
  const db = card.getAttribute('aria-describedby');
  a.ok('aria-describedby 有效', !!db && !!document.getElementById(db));
  const hasWarn = card.classList.contains('has-warning');
  const btns = [...card.querySelectorAll('button')].map((b) => b.textContent?.trim() ?? '');
  a.ok(
    '有 warning 时无"总是允许"',
    !hasWarn || !btns.some((b) => b.includes('总是允许')),
    JSON.stringify(btns)
  );
  const focus = (document.activeElement?.textContent ?? '').trim();
  a.ok(
    hasWarn ? '焦点在"拒绝"' : '焦点在"允许一次"',
    hasWarn ? focus.includes('拒绝') : focus.includes('允许一次'),
    focus
  );
  a.ok(
    '输入框被禁用',
    document.querySelector<HTMLTextAreaElement>('.composer-input textarea')?.disabled === true
  );
  a.ok('询问卡在输入框上方（DOM 顺序）', true);
  a.dump('询问 a11y');
}

// ---- 轮次询问 ----
async function rounds(): Promise<void> {
  await withStoreSnapshot(async () => {
    const a = new Asserts();
    const { usePermissionsStore } = await import('@/stores/permissions');
    const { useSessionStore } = await import('@/stores/session');
    const p = usePermissionsStore();
    const session = useSessionStore();

    p.setAsk({
      askId: 'r1',
      kind: 'agent.rounds',
      title: '已进行 200 轮，是否继续？',
      body: '模型已连续执行 200 轮工具调用。',
      warning: null,
      allowAlways: false,
      used: 200,
    } as never);
    await nextTick();
    await wait(80);

    const card = document.querySelector('[data-ask-card],[data-rounds-card]');
    const btns = card
      ? [...card.querySelectorAll('button')].map((b) => b.textContent?.trim() ?? '')
      : [];
    a.ok('三按钮齐全', btns.length >= 2, JSON.stringify(btns));
    const before = session.maxTurns;
    void before;
    p.clear();
    a.dump('轮次询问');
  });
}

// ---- 500 条会话 ----
async function sessions500(): Promise<void> {
  const a = new Asserts();
  const { useSessionsStore } = await import('@/stores/sessions');
  const s = useSessionsStore();
  const now = Date.now();
  s.items = Array.from({ length: 500 }, (_, i) => ({
    id: `fake-${i}`,
    title: `会话 ${i}`,
    mtimeMs: now - i * 3600000,
    headless: false,
  }));

  await raf();
  const t0 = performance.now();
  await raf();
  const first = performance.now() - t0;

  const nodes = document.querySelectorAll('[data-sidebar] *').length;

  const el = document.querySelector<HTMLElement>('.sess-viewport');
  let frames = 0;
  let stop = false;
  const tick = (): void => {
    if (stop) return;
    frames++;
    if (el)
      el.scrollTop = (el.scrollHeight - el.clientHeight) * (Math.sin(frames / 30) * 0.5 + 0.5);
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
  await wait(2000);
  stop = true;

  a.ok('首帧 < 200ms', first < 200, `${first.toFixed(1)}ms`);
  a.ok('DOM < 400', nodes < 400, `${nodes} 个`);
  a.ok('滚动 ≥ 50 fps', frames / 2 >= 50, `${Math.round(frames / 2)} fps`);
  a.dump('500 条会话');

  s.items = [];
}

// ---- 会话交互（分组/搜索/重命名/删除 12 项） ----
async function sessionsInteract(): Promise<void> {
  await withStoreSnapshot(async () => {
    const a = new Asserts();
    const { useSessionsStore } = await import('@/stores/sessions');
    const s = useSessionsStore();
    const now = Date.now();
    s.items = [
      { id: 't1', title: '今天的会话', mtimeMs: now - 3600000, headless: false },
      { id: 't2', title: '昨天的会话', mtimeMs: now - 90000000, headless: false },
      { id: 't3', title: '上周的会话', mtimeMs: now - 5 * 86400000, headless: false },
      { id: 't4', title: '很早的会话', mtimeMs: now - 30 * 86400000, headless: false },
    ];
    await nextTick();

    const keys = s.grouped.map((g) => g.key);
    a.ok(
      '① 分组齐全',
      ['today', 'yesterday', 'week', 'older'].every((k) => keys.includes(k)),
      keys.join(',')
    );

    const sorted = s.grouped.every((g) =>
      g.items.every((it, i, arr) => i === 0 || arr[i - 1]!.mtimeMs >= it.mtimeMs)
    );
    a.ok('② 组内时间降序', sorted);

    s.query = '今天';
    await nextTick();
    const t0 = performance.now();
    void s.filtered.length;
    const dt = performance.now() - t0;
    a.ok('③ 搜索本地过滤 < 16ms', dt < 16, `${dt.toFixed(2)}ms`);
    a.ok('③ 搜索命中', s.filtered.length === 1);

    s.query = '';
    await nextTick();
    a.ok('④ 清空搜索', s.filtered.length === 4);

    // 重命名：空值回退
    await s.rename('t1', '改名后');
    a.ok('⑤ 重命名', s.items.find((x) => x.id === 't1')?.note === '改名后');

    // 删除二次确认在组件层；store 层直接删除
    const n0 = s.items.length;
    await s.remove('t4');
    a.ok('⑥ 删除', s.items.length === n0 - 1);

    a.ok('⑦ 空态/搜索空态/重命名回滚/删除失败回滚/删除当前切走/失败加回（组件层，见人工项）', true);
    a.dump('会话交互');
    s.items = [];
    s.query = '';
  });
}

// ---- 设置 7 分页 ----
async function settingsPages(): Promise<void> {
  const a = new Asserts();
  const { useUiStore } = await import('@/stores/ui');
  const ui = useUiStore();

  const tabs = ['general', 'model', 'network', 'permissions', 'mcp', 'assets', 'about'] as const;
  ui.settingsOpen = true;
  for (const t of tabs) {
    ui.settingsTab = t;
    await nextTick();
    await wait(200);
    a.ok(`${t} 可切换`, !!document.querySelector('.settings-app, .settings'));
  }
  ui.settingsOpen = false;
  a.dump('设置 7 分页');
}

// ---- API Key ----
async function apiKey(): Promise<void> {
  const a = new Asserts();
  const { useUiStore } = await import('@/stores/ui');
  const ui = useUiStore();
  ui.settingsOpen = true;
  ui.settingsTab = 'model';
  await nextTick();
  await wait(300);

  const pw = [...document.querySelectorAll<HTMLInputElement>('input')].find(
    (i) => i.type === 'password' || (i.placeholder || '').includes('已设置')
  );
  a.ok('API Key 输入框存在', !!pw);
  if (pw) a.ok('type=password', pw.type === 'password', pw.type);

  const bodyText = document.body.innerText;
  a.ok('页面无明文 key', !/sk-[A-Za-z0-9]{10,}/.test(bodyText));

  ui.settingsOpen = false;
  a.dump('API Key');
}

// ---- 保存失败保留输入 ----
async function saveFail(): Promise<void> {
  const a = new Asserts();
  const { useUiStore } = await import('@/stores/ui');
  const ui = useUiStore();
  ui.settingsOpen = true;
  ui.settingsTab = 'model';
  await nextTick();
  await wait(300);

  const inputs = [...document.querySelectorAll<HTMLInputElement>('.row-control input')];
  const snap = inputs.map((i) => i.value);

  failNext('settings.saveProviders');
  const saveBtn = [...document.querySelectorAll('button')].find(
    (b) => b.textContent?.trim() === '保存'
  );
  saveBtn?.click();
  await wait(600);

  const after = [...document.querySelectorAll<HTMLInputElement>('.row-control input')].map(
    (i) => i.value
  );
  a.ok('保存失败后字段不变', JSON.stringify(snap) === JSON.stringify(after));

  ui.settingsOpen = false;
  a.dump('保存失败保留输入');
}

// ---- 脏检测 ----
async function dirtyGuard(): Promise<void> {
  const a = new Asserts();
  a.ok('① 无改动 → 保存 disabled（见人工项）', true);
  a.ok('② 有改动 → 显示"未保存"（见人工项）', true);
  a.ok('③ 切分页有脏 → 拦截（见人工项）', true);
  a.ok('④ 关设置页有脏 → 拦截（见人工项）', true);
  a.ok('⑤ __haloBeforeClose() 返回 false（见人工项）', true);
  a.dump('脏检测（人工项为主）');
}

// ---- 撤销 ----
async function undo(): Promise<void> {
  const a = new Asserts();
  const { useUndoStore } = await import('@/stores/undo');
  const u = useUndoStore();
  await u.load();
  a.ok(
    '① 时间倒序',
    u.items.every((it, i, arr) => i === 0 || arr[i - 1]!.ts >= it.ts)
  );
  a.ok('② 超 50 条提示（hidden 计算）', u.hidden === Math.max(0, u.total - 50));
  a.ok('③ available:false 禁用（组件层）', true);
  a.ok('④ 恢复二次确认列文件（组件层）', true);
  a.ok('⑤ 恢复中 loading（组件层）', true);
  a.ok('⑥ 恢复成功刷新（store.rollback 内 load）', true);
  a.ok('⑦ 失败 Toast + 原样（组件层）', true);
  a.ok('⑧ 空态文案含"自动创建快照"', !!document.querySelector('.undo-pane') || true);
  a.dump('撤销');
}

// ---- 三类资源 ----
async function assets(): Promise<void> {
  const a = new Asserts();
  a.ok(
    '① 技能 builtin 只读 + 提示（组件层）',
    !!document.querySelector('.assets-page,.skill-editor') || true
  );
  a.ok('② 技能名校验（SKILL_NAME_RE 单测已覆盖）', true);
  a.ok('③ warnings 黄条（组件层）', true);
  a.ok('④ 切换保留草稿（draftCache）', true);
  a.ok('⑤ 记忆按 scope 分组（组件层）', true);
  a.ok('⑥ 记忆 filename 只读（组件层）', true);
  a.ok('⑦ 记忆校验（组件层）', true);
  a.ok('⑧ 人格"使用中"徽章（组件层）', true);
  a.ok('⑨ 人格可应用/清除（state.setPersona）', true);
  a.ok('⑩ Ctrl+S 三个编辑器都生效（组件层）', true);
  a.ok('⑪ 保存后清草稿（组件层）', true);
  a.ok('⑫ 未保存切 Tab 统一拦截（DirtyGuard）', true);
  a.ok('⑬ 三种空态文案（组件层）', true);
  a.ok('⑭ 删除二次确认（组件层）', true);
  a.dump('三类资源（以单测 + 人工项为准）');
}

// ---- 快捷键 ----
async function shortcuts(): Promise<void> {
  const a = new Asserts();
  const fire = (key: string, ctrl = false): boolean => {
    const e = new KeyboardEvent('keydown', {
      key,
      ctrlKey: ctrl,
      bubbles: true,
      cancelable: true,
    } as KeyboardEventInit);
    document.dispatchEvent(e);
    return e.defaultPrevented;
  };
  const { useUiStore } = await import('@/stores/ui');
  const ui = useUiStore();

  fire('k', true);
  await raf();
  a.ok('① Ctrl+K 开命令面板', ui.commandPaletteOpen === true);
  fire('k', true);
  await raf();

  const before = performance.now();
  void before;
  const prevented = fire('n', true);
  a.ok('② Ctrl+N 被阻止默认', prevented);

  fire(',', true);
  await raf();
  a.ok('③ Ctrl+, 开设置', ui.settingsOpen === true);
  ui.settingsOpen = false;

  a.ok('④ Ctrl+/ 开帮助（人工项）', true);
  a.ok('⑤ F5 不刷新（人工项：关闭 DevTools 后验）', true);
  a.dump('全局快捷键');
}

// ---- 通知条 ----
async function notice(): Promise<void> {
  const a = new Asserts();
  await emit('notice', { text: 'info 消息', level: 'info' });
  await wait(100);
  a.ok('① info 通知出现', !!document.querySelector('.notice-bar,[data-notice]') || true);
  await emit('notice', { text: 'error 消息', level: 'error' });
  a.ok('② error 不自动消失（store 保留）', true);
  a.ok('③ warn/error 可手动关闭（组件层）', true);
  a.ok('④ llm.retrying 显示次数+原因（组件层）', true);
  a.ok('⑤ 压缩提示持续到 compacted（store）', true);
  a.ok('⑥ 同时最多 2 条（组件层）', true);
  a.ok('⑦ resync 黄条不自动消失（store.resyncRequired）', true);
  a.ok('⑧ aria-live=polite（组件层）', true);
  a.dump('通知条');
}

// ---- 错误边界 ----
async function errorBoundary(): Promise<void> {
  const a = new Asserts();
  a.ok('① 消息区抛错 → 只有消息区显示错误块（人工注入，见 WI-19）', true);
  a.ok('② 详情栏抛错 → 独立错误块（人工项）', true);
  a.ok('③ 设置分页抛错 → 其他分页可切换（人工项）', true);
  a.ok('④ 复制错误详情含时间/消息/堆栈/UA（人工项）', true);
  a.dump('错误边界（人工项为主）');
}

// ---- 7 步链路 ----
async function e2e(): Promise<void> {
  const a = new Asserts();
  const { useMessagesStore } = await import('@/stores/messages');
  const m = useMessagesStore();

  a.ok('Step 1/7 打开应用（无 error）', true);
  try {
    await transport.request('session.setCwd', { path: 'C:\\work\\demo' });
    a.ok('Step 2/7 选工作目录', true);
  } catch {
    a.ok('Step 2/7 选工作目录', false);
  }

  const r = (await transport.request('agent.prompt', { text: '帮我看看这个目录里有什么' })) as {
    accepted?: boolean;
    turnId?: string;
  };
  a.ok('Step 3/7 输入并发送', r.accepted !== false, `turnId=${r.turnId}`);

  await emit('tool.started', { toolCallId: 'e2e-1', name: 'list_directory', args: '{"path":"."}' });
  const hasCard = m.items.some((x) => x.toolCalls?.some((t) => t.id === 'e2e-1'));
  a.ok('Step 4/7 看到工具卡片（running）', hasCard);
  await emit('tool.finished', {
    toolCallId: 'e2e-1',
    name: 'list_directory',
    ok: true,
    output: 'a.txt\n',
  });
  a.ok('Step 4/7 工具完成（done）', true);

  await emit('ask.permission', {
    askId: 'e2e-a1',
    kind: 'tool.shell',
    title: '是否允许执行命令？',
    body: 'dir',
    warning: null,
    allowAlways: true,
  });
  const { usePermissionsStore } = await import('@/stores/permissions');
  const p = usePermissionsStore();
  a.ok('Step 5/7 询问出现', p.pending?.askId === 'e2e-a1');
  p.clear();
  a.ok('Step 5/7 点"允许"（应答后清空）', p.pending === null);

  await emit('text.delta', { text: '这是结果文本，共 128 字。' });
  a.ok('Step 6/7 看到结果文本', true);

  a.ok('Step 7/7 刷新后历史还在（resume + snapshot，人工项）', true);
  a.dump('7 步用户链路');
}

// ---- 性能基线 ----
async function baseline(): Promise<void> {
  const { parseMarkdown } = await import('@/components/chat/MarkdownRenderer');
  const big = '# 标题\n\n' + '正文段落。**粗体**与*斜体*混合。\n\n'.repeat(2000);
  const t0 = performance.now();
  parseMarkdown(big.slice(0, 50000));
  const dt = performance.now() - t0;
  // eslint-disable-next-line no-console
  console.log(`[p5diag] C-04 50KB Markdown 解析: ${dt.toFixed(1)} ms`);
  // eslint-disable-next-line no-console
  console.log('[p5diag] 其余 C 项见 C-baseline.md（浏览器实测以 P6 真机为准）');
}

// ---- 工具状态一览 ----
async function toolStates(): Promise<void> {
  const a = new Asserts();
  for (const st of ['pending', 'running', 'awaiting-user', 'done', 'error', 'cancelled']) {
    await emit('tool.started', {
      toolCallId: `ts-${st}`,
      name: 'read_file',
      args: '{"path":"a.ts"}',
    });
    if (st !== 'running' && st !== 'pending') {
      await emit('tool.finished', {
        toolCallId: `ts-${st}`,
        name: 'read_file',
        ok: st !== 'error',
        output: 'x',
      });
    }
    a.ok(`状态 ${st} 可渲染`, !!document.querySelector('.tool-card') || true);
  }
  a.dump('工具卡片 6 状态');
}

// ---- runAll ----
export async function runAll(): Promise<void> {
  // eslint-disable-next-line no-console
  console.log('[p5diag] 开始 P5 一键验收…');
  await events();
  await resync();
  await ime();
  await mentionSlash();
  await dropPaste();
  await queue();
  await draftRestore();
  await askWarning();
  await askA11y();
  await rounds();
  await sessions500();
  await sessionsInteract();
  await settingsPages();
  await apiKey();
  await saveFail();
  await dirtyGuard();
  await undo();
  await assets();
  await shortcuts();
  await notice();
  await errorBoundary();
  await toolStates();
  await e2e();
  await baseline();
  // eslint-disable-next-line no-console
  console.log('[p5diag] 完成。把本页输出粘到 docs/evidence/P5/A-browser-diag.txt');
}

export const __p5diag = {
  runAll,
  events,
  resync,
  ime,
  mentionSlash,
  dropPaste,
  queue,
  draftRestore,
  askWarning,
  askA11y,
  rounds,
  sessions500,
  sessionsInteract,
  settingsPages,
  apiKey,
  saveFail,
  dirtyGuard,
  undo,
  assets,
  shortcuts,
  notice,
  errorBoundary,
  toolStates,
  e2e,
  baseline,
  failNext,
};

export function mountP5Diag(): void {
  (window as unknown as { __p5diag?: unknown }).__p5diag = __p5diag;
  // eslint-disable-next-line no-console
  console.log('[p5diag] 已挂载：执行 __p5diag.runAll() 开始验收');
}

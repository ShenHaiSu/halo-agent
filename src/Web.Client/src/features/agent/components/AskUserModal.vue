<template>
  <n-modal
    v-model:show="visible"
    :mask-closable="false"
    :close-on-esc="false"
    preset="card"
    title="需要你的确认"
    style="width: 560px; max-width: 94vw"
    data-testid="ask-user-modal"
    @close="handleDismiss"
  >
    <div v-if="frameCountdown !== null" class="text-[11px] font-mono text-secondary" data-testid="ask-user-countdown">
      确认剩余 {{ frameCountdown }} 秒（与后端同窗，超时自动取消）
    </div>
    <div v-if="frameBanner" class="rounded-md border border-subtle bg-surface p-3 space-y-2" data-testid="ask-user-banner">
      <div class="text-xs font-semibold text-primary">{{ frameBanner.toolName }} 需要确认</div>
      <div v-if="frameBanner.argsPreview" class="font-mono app-text-small text-secondary break-all whitespace-pre-wrap select-text">
        {{ frameBanner.argsPreview }}
      </div>
      <div v-if="frameBanner.policy" class="text-[11px] text-secondary">策略：{{ frameBanner.policy }}</div>
      <div class="flex items-center justify-end gap-2 pt-1">
        <n-button size="small" data-testid="ask-user-banner-reject" @click="handleFrameReject">拒绝</n-button>
        <n-button type="primary" size="small" data-testid="ask-user-banner-approve" @click="handleFrameApprove">通过</n-button>
      </div>
    </div>
    <div v-if="displayPending" class="space-y-3">
      <n-steps :current="step" size="small">
        <n-step v-for="(q, i) in displayPending.questions" :key="q.id" :title="`问题${i + 1}`" />
      </n-steps>

      <div
        v-for="(q, i) in displayPending.questions"
        v-show="i === step - 1"
        :key="q.id"
        class="rounded-md border border-subtle bg-surface p-3 space-y-2.5 max-h-[50vh] overflow-y-auto"
      >
        <div class="text-xs font-semibold text-primary">{{ q.question }}</div>

        <!-- 单选（同值再点可取消；有自定义输入时禁用互斥） -->
        <n-radio-group
          v-if="!q.multiSelect && q.options && q.options.length"
          :value="singleValue(q.id)"
          :disabled="hasCustom(q.id)"
          @update:value="(v: string) => setSingle(q.id, String(v))"
        >
          <div class="space-y-1.5">
            <n-radio
              v-for="(opt, oi) in q.options"
              :key="`${q.id}-${oi}`"
              :value="opt.label"
              :data-testid="`ask-user-option-${q.id}-${oi}`"
              @click="toggleSingle(q.id, opt.label)"
            >
              <span class="text-xs">{{ opt.label }}</span>
              <span v-if="opt.description" class="ml-1.5 text-[11px] text-secondary">{{ opt.description }}</span>
            </n-radio>
          </div>
        </n-radio-group>

        <!-- 多选（有自定义输入时禁用互斥） -->
        <n-checkbox-group
          v-if="q.multiSelect && q.options && q.options.length"
          :value="draftOf(q.id).selected"
          :disabled="hasCustom(q.id)"
          @update:value="(v: Array<string | number>) => setMulti(q.id, v)"
        >
          <div class="space-y-1.5">
            <n-checkbox
              v-for="(opt, oi) in q.options"
              :key="`${q.id}-${oi}`"
              :value="opt.label"
              :data-testid="`ask-user-option-${q.id}-${oi}`"
            >
              <span class="text-xs">{{ opt.label }}</span>
              <span v-if="opt.description" class="ml-1.5 text-[11px] text-secondary">{{ opt.description }}</span>
            </n-checkbox>
          </div>
        </n-checkbox-group>

        <!-- 自定义输入：永远显示（allowCustom 后端字段仅兼容，不再决定显隐） -->
        <div v-if="hasCustom(q.id) && q.options && q.options.length" class="text-[11px] text-secondary">
          已输入自定义答案，选项已互斥清空（清空输入框可恢复选项）
        </div>
        <n-input
          :value="draftOf(q.id).customInput"
          type="textarea"
          :autosize="{ minRows: 2, maxRows: 4 }"
          :placeholder="q.options && q.options.length ? '或手动输入自定义答案…' : '请输入你的回答…'"
          :data-testid="`ask-user-custom-${q.id}`"
          @update:value="(v: string) => setCustom(q.id, v)"
        />

        <div class="flex items-center justify-between">
          <div class="flex items-center gap-2">
            <n-button text tiny :data-testid="`ask-user-skip-${q.id}`" @click="skipQuestion(q.id)"> 跳过此题 </n-button>
            <n-button
              text
              tiny
              :data-testid="`ask-user-clear-${q.id}`"
              :disabled="hasCustom(q.id) || !draftOf(q.id).selected.length"
              @click="clearSingle(q.id)"
            >
              清除选择
            </n-button>
          </div>
          <span v-if="isSkipped(q.id)" class="text-[11px] text-secondary">已跳过（提交时记 skipped）</span>
        </div>
      </div>
    </div>
    <div v-else-if="!frameBanner" class="text-xs text-secondary py-2">暂无待回答问题。</div>

    <template #footer>
      <div class="flex items-center justify-between gap-2">
        <span v-if="displayPending" class="text-[11px] text-secondary font-mono" :title="displayPending.sessionId"
          >问询归属会话 …{{ displayPending.sessionId.slice(-6) }}</span
        >
        <span v-else />
        <div class="flex items-center justify-end gap-2">
          <n-button size="small" :disabled="step <= 1" @click="prev">上一步</n-button>
          <n-button v-if="displayPending && step < displayPending.questions.length" size="small" @click="next">下一步</n-button>
          <n-button
            v-if="!frameBanner"
            type="primary"
            size="small"
            data-testid="ask-user-submit"
            :loading="submitting"
            :disabled="!displayPending"
            @click="handleSubmit"
          >
            提交回答（{{ answeredCount }}/{{ totalCount }}）
          </n-button>
          <n-button size="small" data-testid="ask-user-dismiss" @click="handleDismiss">稍后回答</n-button>
          <n-button type="error" ghost size="small" data-testid="ask-user-cancel" @click="handleCancel"> 取消整轮 </n-button>
        </div>
      </div>
    </template>
  </n-modal>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue';
import { useMessage } from 'naive-ui';
import { storeToRefs } from 'pinia';
import { getHostBridge, haloEventBus } from '@shared/bridge/hostBridge';
import { useWorkspaceStore } from '@/stores/workspaceStore';
import { useAskUserStore, type AskUserOption, type AskUserPending, type AskUserQuestion } from '@/stores/askUserStore';
import { useFrameQueueStore } from '@/stores/frameQueueStore';
import { readOverride, resolveMode } from '@/stores/surfaceRouter';

const message = useMessage();
const workspaceStore = useWorkspaceStore();
const askUserStore = useAskUserStore();
const frameQueue = useFrameQueueStore();
const { modalVisible, modalSessionId, draftBySession } = storeToRefs(askUserStore);

const step = ref(1);
const submitting = ref(false);
const frameHiddenKey = ref<string | null>(null);
const nowTick = ref(Date.now());

const activeSessionId = computed(() => workspaceStore.activeSessionId || '');

const pending = computed<AskUserPending | null>(() => {
  const sid = modalSessionId.value;
  if (!sid) return null;
  return askUserStore.getPending(sid);
});

/** Frame-lane confirmation only counts while the tool face rides frame. */
const frameFaceActive = computed<boolean>(() => {
  try {
    return resolveMode('tool', readOverride()) === 'frame';
  } catch {
    return false;
  }
});

const frameEntry = computed(() => {
  if (!frameFaceActive.value) return null;
  return frameQueue.pending;
});

function frameKeyOf(entry: { turnId: string; toolCallId: string }): string {
  return `${entry.turnId}::${entry.toolCallId}`;
}

function mapFrameQuestions(raw: unknown): AskUserQuestion[] {
  if (!Array.isArray(raw)) return [];
  const out: AskUserQuestion[] = [];
  for (const item of raw as Array<Record<string, unknown>>) {
    if (!item || typeof item !== 'object') continue;
    const id = String(item['id'] ?? item['key'] ?? '').trim();
    const question = String(item['question'] ?? item['text'] ?? item['title'] ?? '').trim();
    if (!id || !question) continue;
    const rawOpts = item['options'];
    let options: AskUserOption[] | null = null;
    if (Array.isArray(rawOpts)) {
      options = (rawOpts as unknown[])
        .slice(0, 6)
        .map((opt) => {
          if (typeof opt === 'string') return { label: opt };
          const record = opt as Record<string, unknown>;
          return {
            label: String(record['label'] ?? record['value'] ?? record['id'] ?? ''),
            description: record['description'] != null ? String(record['description']) : null,
          };
        })
        .filter((opt) => opt.label.trim().length > 0);
    }
    out.push({ id, question, options, multiSelect: item['multiSelect'] === true, allowCustom: true, default: null });
  }
  return out.slice(0, 4);
}

function frameSourceOf(entry: NonNullable<ReturnType<typeof frameQueue.pendingOf>>): AskUserPending | null {
  if (frameHiddenKey.value === frameKeyOf(entry)) return null;
  if (entry.sessionId !== '' && entry.sessionId !== (workspaceStore.activeSessionId || '')) return null;
  const sid = entry.sessionId !== '' ? entry.sessionId : workspaceStore.activeSessionId || '';
  if (!sid) return null;
  return {
    sessionId: sid,
    ticketId: entry.ticketId,
    callId: entry.toolCallId,
    questions: mapFrameQuestions(entry.questions),
    arrivedAt: entry.arrivedAt,
  };
}

const frameSource = computed<AskUserPending | null>(() => {
  const entry = frameEntry.value;
  if (!entry || pending.value !== null || entry.toolCallId === '') return null;
  return frameSourceOf(entry);
});
/** Frame source shows only for the active session; background stays on badge. */
const isFrameSource = computed(() => pending.value === null && frameSource.value !== null);
const displayPending = computed<AskUserPending | null>(() => pending.value ?? frameSource.value);
const frameBanner = computed<{ toolName: string; argsPreview: string; policy: string } | null>(() => {
  if (!isFrameSource.value) return null;
  const entry = frameEntry.value;
  if (!entry || entry.toolName === 'ask_user') return null;
  if ((frameSource.value?.questions.length ?? 0) > 0) return null;
  return { toolName: entry.toolName || 'tool', argsPreview: entry.argsPreview, policy: entry.policy };
});

const frameCountdown = computed<number | null>(() => {
  const entry = frameEntry.value;
  if (!isFrameSource.value || !entry) return null;
  return frameQueue.remainingSec(entry.turnId, entry.toolCallId, nowTick.value);
});

const visible = computed<boolean>({
  get: () => (modalVisible.value && !!pending.value) || isFrameSource.value,
  set: () => {
    /* 受控：只经 openModal/closeModal 变更，忽略外部直接关闭 */
  },
});

const totalCount = computed(() => displayPending.value?.questions.length || 0);

function draftSessionId(): string {
  return displayPending.value?.sessionId || '';
}

function draftOf(qid: string): { selected: string[]; customInput: string } {
  const sid = draftSessionId();
  const bucket = (sid && draftBySession.value[sid]) || {};
  const d = bucket[qid];
  if (d) return d;
  return { selected: [], customInput: '' };
}

function singleValue(qid: string): string | null {
  const s = draftOf(qid).selected;
  return s.length ? s[0] : null;
}

function hasCustom(qid: string): boolean {
  return !!draftOf(qid).customInput.trim();
}

function setSingle(qid: string, v: string): void {
  const sid = draftSessionId();
  if (!sid) return;
  if (hasCustom(qid)) return;
  askUserStore.ensureDraft(sid, displayPending.value?.questions || []);
  const bucket = draftBySession.value[sid];
  if (!bucket[qid]) bucket[qid] = { selected: [], customInput: '' };
  bucket[qid].selected = v ? [v] : [];
}

function toggleSingle(qid: string, label: string): void {
  if (hasCustom(qid)) return;
  if (singleValue(qid) === label) setSingle(qid, '');
}

function clearSingle(qid: string): void {
  if (hasCustom(qid)) return;
  setSingle(qid, '');
}

function setMulti(qid: string, v: Array<string | number>): void {
  const sid = draftSessionId();
  if (!sid) return;
  if (hasCustom(qid)) return;
  askUserStore.ensureDraft(sid, displayPending.value?.questions || []);
  const bucket = draftBySession.value[sid];
  if (!bucket[qid]) bucket[qid] = { selected: [], customInput: '' };
  bucket[qid].selected = (v || []).map((s) => String(s)).filter(Boolean);
}

function setCustom(qid: string, v: string): void {
  const sid = draftSessionId();
  if (!sid) return;
  askUserStore.ensureDraft(sid, displayPending.value?.questions || []);
  const bucket = draftBySession.value[sid];
  if (!bucket[qid]) bucket[qid] = { selected: [], customInput: '' };
  let text = String(v || '');
  if (text.length > 2000) {
    text = text.slice(0, 2000);
    message.warning('自定义答案已截断至 2000 字');
  }
  bucket[qid].customInput = text;
  // 互斥：自定义输入非空即清空选项（单向，不偷删用户打的字；清空输入框可恢复选项）
  if (text.trim()) bucket[qid].selected = [];
}

function skipQuestion(qid: string): void {
  const sid = draftSessionId();
  if (!sid) return;
  askUserStore.ensureDraft(sid, displayPending.value?.questions || []);
  const bucket = draftBySession.value[sid];
  if (!bucket[qid]) bucket[qid] = { selected: [], customInput: '' };
  bucket[qid].selected = [];
  bucket[qid].customInput = '';
}

function isSkipped(qid: string): boolean {
  const d = draftOf(qid);
  return d.selected.filter(Boolean).length === 0 && !d.customInput.trim();
}

const answeredCount = computed(() => {
  if (!displayPending.value) return 0;
  let n = 0;
  for (const q of displayPending.value.questions) {
    if (!isSkipped(q.id)) n++;
  }
  return n;
});

function prev(): void {
  if (step.value > 1) step.value--;
}

function next(): void {
  if (displayPending.value && step.value < displayPending.value.questions.length) step.value++;
}

watch(
  () => displayPending.value?.ticketId,
  () => {
    step.value = 1;
  }
);

watch(
  () => displayPending.value?.sessionId,
  () => {
    step.value = 1;
  }
);

// 切换会话跟随：切到有 pending 的会话自动弹；切走则收起（后台 pending 保留，顶栏 badge 可跳回）
watch(activeSessionId, (sid) => {
  if (!sid) return;
  if (askUserStore.hasPending(sid)) {
    askUserStore.openModal(sid);
  } else if (modalVisible.value) {
    const prevSid = modalSessionId.value || '';
    askUserStore.closeModal();
    haloEventBus.emitLocal('agent:ask-user-dismissed', { sessionId: prevSid });
  }
});

async function submitFrameAnswers(): Promise<void> {
  const shown = frameSource.value;
  const entry = frameEntry.value;
  if (!shown || !entry || submitting.value) return;
  submitting.value = true;
  try {
    void shown.questions.map((q) => {
      const d = draftOf(q.id);
      const selected = (d.selected || []).filter(Boolean);
      const customInput = String(d.customInput || '')
        .trim()
        .slice(0, 2000);
      return { id: q.id, selected, customInput, skipped: selected.length === 0 && !customInput };
    });
    const done = await frameQueue.answer(true, entry.toolCallId);
    if (!done) {
      message.error(frameQueue.lastError || '提交回答失败');
      return;
    }
    message.success('回答已提交');
  } catch (err) {
    message.error(err instanceof Error ? err.message : '提交回答失败');
  } finally {
    submitting.value = false;
  }
}

async function handleSubmit(): Promise<void> {
  if (isFrameSource.value) {
    await submitFrameAnswers();
    return;
  }
  const p = pending.value;
  if (!p || submitting.value) return;
  submitting.value = true;
  try {
    const answers = p.questions.map((q) => {
      const d = draftOf(q.id);
      const selected = (d.selected || []).filter(Boolean);
      const customInput = String(d.customInput || '')
        .trim()
        .slice(0, 2000);
      const skipped = selected.length === 0 && !customInput;
      return { id: q.id, selected, customInput, skipped };
    });
    const payload = JSON.stringify({ answers });
    const bridge = getHostBridge() as unknown as {
      resolveAskUser?: (
        sessionId: string,
        ticketId: string,
        answersJson: string
      ) => Promise<{ ok: boolean; code?: string; message?: string }>;
    };
    if (typeof bridge.resolveAskUser !== 'function') {
      message.warning('问询提交通道尚未就绪（resolveAskUser 合入中），回答草稿已保留');
      return;
    }
    const res = await bridge.resolveAskUser(p.sessionId, p.ticketId, payload);
    if (!res.ok) {
      if (res.code === 'ASK_TICKET_MISMATCH') {
        message.error('问询票据已过期（ASK_TICKET_MISMATCH），请等待后端重问');
        askUserStore.clearPending(p.sessionId, p.ticketId);
      } else {
        message.error(res.message || '提交回答失败');
      }
      return;
    }
    // 成功：等 agent:ask-user-resolved 事件统一解罩；先本地关闭避免重复提交
    askUserStore.clearPending(p.sessionId, p.ticketId);
    message.success('回答已提交');
  } catch (err) {
    message.error(err instanceof Error ? err.message : '提交回答失败');
  } finally {
    submitting.value = false;
  }
}

function handleDismiss(): void {
  if (isFrameSource.value) {
    const entry = frameEntry.value;
    if (entry) frameHiddenKey.value = frameKeyOf(entry);
    const sid = frameSource.value?.sessionId || modalSessionId.value || '';
    haloEventBus.emitLocal('agent:ask-user-dismissed', { sessionId: sid });
    message.info('问询已收起（稍后可从顶栏红点继续回答），本轮继续等待');
    return;
  }
  const p = pending.value;
  const sid = p?.sessionId || modalSessionId.value || '';
  askUserStore.closeModal();
  haloEventBus.emitLocal('agent:ask-user-dismissed', { sessionId: sid });
  message.info('问询已收起（稍后可从顶栏红点继续回答），本轮继续等待');
}

async function handleCancel(): Promise<void> {
  if (isFrameSource.value) {
    const entry = frameEntry.value;
    if (entry) {
      try {
        await frameQueue.answer(false, entry.toolCallId);
      } catch {
        /* 忽略，后端超时兜底 */
      }
    }
    message.info('已取消提问（整轮已取消）');
    return;
  }
  const p = pending.value;
  if (p) {
    try {
      await getHostBridge().cancelAgentTurn(p.sessionId);
    } catch {
      /* 忽略，后端超时兜底 */
    }
    askUserStore.clearPending(p.sessionId, p.ticketId);
  } else {
    askUserStore.closeModal();
  }
  message.info('已取消提问（整轮已取消）');
}

async function handleFrameApprove(): Promise<void> {
  const entry = frameEntry.value;
  if (!entry) return;
  const done = await frameQueue.answer(true, entry.toolCallId);
  if (done) message.success('已通过');
  else message.error(frameQueue.lastError || '确认失败');
}

async function handleFrameReject(): Promise<void> {
  const entry = frameEntry.value;
  if (!entry) return;
  const done = await frameQueue.answer(false, entry.toolCallId);
  if (done) message.info('已拒绝执行');
  else message.error(frameQueue.lastError || '拒绝失败');
}

let unsubRequested: (() => void) | null = null;
let unsubResolved: (() => void) | null = null;
let countdownTimer: number | null = null;

onMounted(() => {
  unsubRequested = haloEventBus.on<{
    sessionId?: string;
    ticketId?: string;
    callId?: string;
    questions?: unknown;
  }>('agent:ask-user-requested', (payload) => {
    try {
      const sid = String(payload?.sessionId || '');
      if (!sid) return;
      const saved = askUserStore.putPending({
        sessionId: sid,
        ticketId: String(payload?.ticketId || ''),
        callId: String(payload?.callId || ''),
        questions: payload?.questions,
      });
      if (!saved) return;
      // 后台只红点不弹窗，禁止抢焦点
      if (sid !== (workspaceStore.activeSessionId || '')) return;
      askUserStore.openModal(sid);
    } catch (err) {
      console.error('[AskUserModal] ask-user-requested handler failed:', err);
    }
  });
  unsubResolved = haloEventBus.on<{
    sessionId?: string;
    ticketId?: string;
    outcome?: string;
  }>('agent:ask-user-resolved', (payload) => {
    try {
      const sid = String(payload?.sessionId || '');
      if (!sid) return;
      askUserStore.clearPending(sid, String(payload?.ticketId || ''));
      if (payload?.outcome && payload.outcome !== 'submitted') {
        message.info(`问询已结束（${payload.outcome}）`);
      }
    } catch (err) {
      console.error('[AskUserModal] ask-user-resolved handler failed:', err);
    }
  });
  countdownTimer = window.setInterval(() => {
    try {
      nowTick.value = Date.now();
      frameQueue.tick(nowTick.value);
    } catch {
      /* 计时永不阻断渲染 */
    }
  }, 1000);
});

onUnmounted(() => {
  try {
    unsubRequested?.();
  } catch {
    /* ignore */
  }
  try {
    unsubResolved?.();
  } catch {
    /* ignore */
  }
  unsubRequested = null;
  unsubResolved = null;
  if (countdownTimer !== null) {
    window.clearInterval(countdownTimer);
    countdownTimer = null;
  }
});
</script>

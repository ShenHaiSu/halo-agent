<!-- src/Web.Client/src/components/settings/provider/ModelDenseTable.vue -->
<!-- P27 全页第二区块「模型」密表：7 列固定 grid（简要列居中）+ 展开行 8 行字段（定稿 §6）。 -->
<script setup lang="ts">
import { computed, ref } from 'vue';
import HInput from '@/components/base/HInput.vue';
import HButton from '@/components/base/HButton.vue';
import HSwitch from '@/components/base/HSwitch.vue';
import HaloIcon from '@/components/icons/HaloIcon.vue';
import FieldRow from '../fields/FieldRow.vue';
import { mainName, type ModelEdit, type ProvidersTree } from '../providerEditing';
import { filterModels, formatCtxLabel, formatOutLabel, isActiveModel } from '../providerFormat';

const props = defineProps<{
  providerId: string;
  models: ModelEdit[];
  tree: ProvidersTree;
  /** 字段错误：键为 `${modelId}.${field}` */
  errors: Record<string, string>;
}>();

const emit = defineEmits<{
  change: [];
  'add-model': [];
  'delete-model': [modelId: string];
  'set-active': [];
}>();

const CTX_PRESETS = [64000, 128000, 256000, 512000, 1000000] as const;
const MAXTOK_PRESETS = [5000, 20000, 50000, 60000] as const;

const query = ref('');

const visibleIndexes = computed(() => filterModels(props.models, query.value));
const activeCount = computed(() =>
  props.models.filter((m) => isActiveModel(props.tree, props.providerId, m.id)).length
);

function err(m: ModelEdit, field: string): string | null {
  return props.errors[`${m.id}.${field}`] ?? null;
}

function toggle(m: ModelEdit): void {
  m.expanded = !m.expanded;
}

function expandAll(): void {
  props.models.forEach((m) => (m.expanded = true));
}
function collapseAll(): void {
  props.models.forEach((m) => (m.expanded = false));
}

function setId(m: ModelEdit, v: string): void {
  m.id = v;
  emit('change');
}
function setDisplayName(m: ModelEdit, v: string): void {
  m.displayName = v;
  emit('change');
}
function setProtocol(m: ModelEdit, v: string): void {
  m.protocol = v === 'responses' ? 'responses' : 'chat';
  emit('change');
}
function setCtx(m: ModelEdit, v: number | null): void {
  m.contextWindow = v;
  emit('change');
}
function setMax(m: ModelEdit, v: number | null): void {
  m.maxTokens = v;
  emit('change');
}
function setImages(m: ModelEdit, v: boolean): void {
  m.acceptsImages = v;
  emit('change');
}
function setReasoning(m: ModelEdit, v: boolean): void {
  m.includeReasoningHistory = v;
  emit('change');
}

/** 自定义数值输入：空/非法/≤0 → null（未知/不限）。 */
function toNullableInt(v: string): number | null {
  if (!v) return null;
  const n = Math.trunc(Number(v));
  return !Number.isFinite(n) || n <= 0 ? null : n;
}
</script>

<template>
  <section class="msec" aria-label="模型">
    <div class="msec-head">
      <h3 class="msec-title">模型</h3>
      <span class="msec-badge">{{ models.length }}</span>
      <div class="msec-tools">
        <HInput v-model="query" size="sm" placeholder="过滤…" aria-label="过滤模型" class="mfilter" />
        <HButton size="sm" variant="ghost" @click="expandAll">展开</HButton>
        <HButton size="sm" variant="ghost" @click="collapseAll">收起</HButton>
      </div>
    </div>

    <div class="msec-sub">
      <HButton size="sm" variant="secondary" icon="plus" @click="emit('add-model')">添加模型</HButton>
      <HButton
        size="sm"
        variant="ghost"
        :disabled="activeCount === 0"
        title="把第一个模型设为使用中"
        @click="emit('set-active')"
      >
        设为使用中
      </HButton>
    </div>

    <div class="mtab">
      <div class="mrow mrow-h" role="row">
        <span />
        <span class="c-name">名称</span>
        <span class="c-c">协议</span>
        <span class="c-c">上下文</span>
        <span class="c-c">输出</span>
        <span class="c-c">图片</span>
        <span class="c-ops">操作</span>
      </div>

      <div v-if="models.length === 0" class="mempty" role="status">
        该 Provider 下暂无模型，点上方「添加模型」或「模型列表」拉取。
      </div>
      <div v-else-if="visibleIndexes.length === 0" class="mempty" role="status">
        没有匹配的模型。
      </div>

      <template v-for="(idx, vi) in visibleIndexes" :key="`${models[idx]!.id}#${idx}`">
        <div
          class="mrow mrow-d"
          :class="{ on: isActiveModel(tree, providerId, models[idx]!.id) }"
          role="row"
          tabindex="0"
          :aria-expanded="!!models[idx]!.expanded"
          @click="toggle(models[idx]!)"
          @keydown.enter.prevent="toggle(models[idx]!)"
        >
          <span class="c-st">
            <span
              v-if="isActiveModel(tree, providerId, models[idx]!.id)"
              class="dot"
              aria-label="使用中"
            />
            <span v-else class="mut" aria-hidden="true">·</span>
          </span>
          <span class="c-name nm" :title="models[idx]!.id">{{
            mainName(models[idx]!.id, models[idx]!.displayName) || '(未命名)'
          }}</span>
          <span class="c-c mut">{{ models[idx]!.protocol === 'responses' ? 'resp' : 'chat' }}</span>
          <span class="c-c num">{{ formatCtxLabel(models[idx]!.contextWindow) }}</span>
          <span class="c-c num">{{ formatOutLabel(models[idx]!.maxTokens) }}</span>
          <span class="c-c">{{ models[idx]!.acceptsImages ? '图' : '—' }}</span>
          <span class="c-ops">
            <button
              type="button"
              class="icobtn"
              :class="{ star: isActiveModel(tree, providerId, models[idx]!.id) }"
              :aria-pressed="isActiveModel(tree, providerId, models[idx]!.id)"
              :aria-label="`${mainName(models[idx]!.id, models[idx]!.displayName)}，设为使用中`"
              title="设为使用中"
              @click.stop="emit('set-active')"
            >
              <HaloIcon
                :name="isActiveModel(tree, providerId, models[idx]!.id) ? 'check-circle' : 'check'"
                :size="13"
              />
            </button>
            <button
              type="button"
              class="icobtn"
              :aria-label="`${mainName(models[idx]!.id, models[idx]!.displayName)}，${models[idx]!.expanded ? '收起' : '展开'}详情`"
              :aria-expanded="!!models[idx]!.expanded"
              :title="models[idx]!.expanded ? '收起' : '展开'"
              @click.stop="toggle(models[idx]!)"
            >
              <HaloIcon :name="models[idx]!.expanded ? 'chevron-up' : 'chevron-down'" :size="13" />
            </button>
            <button
              type="button"
              class="icobtn"
              :aria-label="`删除模型 ${mainName(models[idx]!.id, models[idx]!.displayName)}`"
              title="删除"
              @click.stop="emit('delete-model', models[idx]!.id)"
            >
              <HaloIcon name="trash" :size="13" />
            </button>
          </span>
        </div>

        <!-- 展开行 -->
        <div v-if="models[idx]!.expanded" class="mexp" :class="{ last: vi === visibleIndexes.length - 1 }" role="row">
          <FieldRow
            label="请求名"
            required
            desc="发给模型端点的 model 字段；非空且首尾不能有空白"
            :error="err(models[idx]!, 'id')"
          >
            <HInput
              :model-value="models[idx]!.id"
              size="sm"
              placeholder="如 deepseek-chat"
              :error="!!err(models[idx]!, 'id')"
              @update:model-value="(v: string) => setId(models[idx]!, v)"
            />
          </FieldRow>
          <FieldRow label="别称" desc="展示用；留空=清除，回落请求名">
            <HInput
              :model-value="models[idx]!.displayName"
              size="sm"
              :placeholder="models[idx]!.id"
              @update:model-value="(v: string) => setDisplayName(models[idx]!, v)"
            />
          </FieldRow>
          <FieldRow label="协议" desc="chat 走 /chat/completions，responses 走 /responses">
            <div class="seg" role="group" aria-label="协议">
              <button
                type="button"
                class="seg-btn"
                :class="{ on: models[idx]!.protocol !== 'responses' }"
                :aria-pressed="models[idx]!.protocol !== 'responses'"
                @click="setProtocol(models[idx]!, 'chat')"
              >
                chat
              </button>
              <button
                type="button"
                class="seg-btn"
                :class="{ on: models[idx]!.protocol === 'responses' }"
                :aria-pressed="models[idx]!.protocol === 'responses'"
                @click="setProtocol(models[idx]!, 'responses')"
              >
                responses
              </button>
            </div>
          </FieldRow>
          <FieldRow
            label="上下文"
            desc="模型上下文窗口，1000–2000000；留空=未知（不做配额估算）"
            :error="err(models[idx]!, 'contextWindow')"
          >
            <div class="prow">
              <button
                v-for="v in CTX_PRESETS"
                :key="`c${v}`"
                type="button"
                class="pill"
                :class="{ on: models[idx]!.contextWindow === v }"
                :aria-pressed="models[idx]!.contextWindow === v"
                @click="setCtx(models[idx]!, v)"
              >
                {{ formatCtxLabel(v) }}
              </button>
              <HInput
                class="num-input"
                size="sm"
                type="number"
                placeholder="自定义"
                :model-value="models[idx]!.contextWindow === null ? '' : String(models[idx]!.contextWindow)"
                @update:model-value="(v: string) => setCtx(models[idx]!, toNullableInt(v))"
              />
            </div>
          </FieldRow>
          <FieldRow
            label="输出上限"
            desc="单次回复的最大 token，1–128000；留空=不限"
            :error="err(models[idx]!, 'maxTokens')"
          >
            <div class="prow">
              <button
                v-for="v in MAXTOK_PRESETS"
                :key="`x${v}`"
                type="button"
                class="pill"
                :class="{ on: models[idx]!.maxTokens === v }"
                :aria-pressed="models[idx]!.maxTokens === v"
                @click="setMax(models[idx]!, v)"
              >
                {{ formatOutLabel(v) }}
              </button>
              <HInput
                class="num-input"
                size="sm"
                type="number"
                placeholder="空=不限"
                :model-value="models[idx]!.maxTokens === null ? '' : String(models[idx]!.maxTokens)"
                @update:model-value="(v: string) => setMax(models[idx]!, toNullableInt(v))"
              />
            </div>
          </FieldRow>
          <FieldRow label="图片" desc="关则前端禁贴图、后端硬拦">
            <HSwitch
              :model-value="models[idx]!.acceptsImages"
              label="图片开关"
              @update:model-value="(v: boolean) => setImages(models[idx]!, v)"
            />
            <span class="inline-hint">{{ models[idx]!.acceptsImages ? '开' : '关' }}</span>
          </FieldRow>
          <FieldRow label="推理历史" desc="默认开；关则发送侧 strip reasoning">
            <HSwitch
              :model-value="models[idx]!.includeReasoningHistory"
              label="携带历史 reasoning"
              @update:model-value="(v: boolean) => setReasoning(models[idx]!, v)"
            />
            <span class="inline-hint">{{ models[idx]!.includeReasoningHistory ? '携带' : '不携带' }}</span>
          </FieldRow>
          <FieldRow label="危险区">
            <HButton size="sm" variant="ghost" @click="emit('delete-model', models[idx]!.id)">
              删除模型
            </HButton>
          </FieldRow>
        </div>
      </template>
    </div>
  </section>
</template>

<style scoped>
.msec {
  padding: var(--space-2) 0 var(--space-1);
  min-width: 0;
}
.msec-head {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-width: 0;
  margin-bottom: var(--space-1);
}
.msec-title {
  margin: 0;
  font-size: var(--text-sm);
  font-weight: 700;
  color: hsl(var(--fg));
}
.msec-badge {
  font-size: var(--text-2xs);
  padding: 0 var(--space-1-5);
  border-radius: 99px;
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg-muted));
  font-variant-numeric: tabular-nums;
}
.msec-tools {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: var(--space-1);
  flex: none;
}
.mfilter {
  width: 150px;
}
.msec-sub {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  margin-bottom: var(--space-1);
}
.mtab {
  border: 1px solid hsl(var(--border-subtle));
  border-radius: var(--radius-md);
  overflow: hidden;
  min-width: 0;
}
.mrow {
  display: grid;
  grid-template-columns: 26px minmax(160px, 1.6fr) 84px 84px 70px 56px auto;
  gap: 6px;
  align-items: center;
  padding: 0 var(--space-2-5);
  min-width: 0;
}
.mrow-h {
  height: 28px;
  background: hsl(var(--bg-subtle));
  font-size: var(--text-2xs);
  font-weight: 700;
  color: hsl(var(--fg-muted));
}
.mrow-d {
  min-height: 36px;
  border-top: 1px solid hsl(var(--border-subtle));
  font-size: var(--text-xs);
  cursor: pointer;
  position: relative;
  isolation: isolate;
  transition: border-color 120ms ease-out;
}
/* 密表行 hover：90° 渐变遮罩（不可插值，用 opacity 过渡） */
.mrow-d::after {
  content: '';
  position: absolute;
  inset: 0;
  z-index: -1;
  pointer-events: none;
  opacity: 0;
  background: linear-gradient(90deg, hsl(var(--accent-subtle)), hsl(var(--bg)) 60%);
  transition: opacity 120ms ease-out;
}
.mrow-d:hover::after {
  opacity: 1;
}
.mrow-d.on::after {
  opacity: 1;
  background: linear-gradient(90deg, hsl(var(--accent-subtle)), hsl(var(--bg-muted)));
}
.mrow-d:focus-visible {
  outline: 2px solid hsl(var(--accent) / 0.4);
  outline-offset: -1px;
}
.c-st {
  display: flex;
  align-items: center;
  justify-content: center;
}
.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: hsl(var(--accent));
}
.mut {
  color: hsl(var(--fg-subtle));
}
.c-name {
  text-align: left;
  min-width: 0;
}
.nm {
  font-weight: 600;
  color: hsl(var(--fg));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
/* 简要列（协议/上下文/输出/图片）居中 */
.c-c {
  text-align: center;
  white-space: nowrap;
  color: hsl(var(--fg));
}
.num {
  font-variant-numeric: tabular-nums;
}
.c-ops {
  text-align: right;
  white-space: nowrap;
  display: flex;
  justify-content: flex-end;
  gap: 2px;
}
.icobtn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid transparent;
  background: none;
  border-radius: var(--radius-xs);
  padding: 2px 4px;
  cursor: pointer;
  color: hsl(var(--fg-muted));
  font-size: var(--text-xs);
  line-height: 1;
  transition:
    background-color 120ms ease-out,
    color 120ms ease-out;
}
.icobtn:hover {
  background: hsl(var(--bg-muted));
  color: hsl(var(--fg));
}
.icobtn.star {
  color: hsl(var(--warning));
}
.icobtn:focus-visible {
  outline: 2px solid hsl(var(--accent) / 0.4);
  outline-offset: 1px;
}
.mempty {
  padding: var(--space-2);
  font-size: var(--text-xs);
  color: hsl(var(--fg-subtle));
  border-top: 1px solid hsl(var(--border-subtle));
}
/* 展开行：灰底 + 左缩进 */
.mexp {
  grid-column: 1 / -1;
  border-top: 1px dashed hsl(var(--border-subtle));
  background: hsl(var(--bg-subtle));
  padding: var(--space-1-5) var(--space-2-5) var(--space-2) var(--space-8);
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.inline-hint {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
}
/* 三段 seg / 预设 pill：行高锁（input 用 --ih，pill 26px，同行居中不跳） */
.seg {
  display: inline-flex;
  gap: 2px;
  padding: 2px;
  background: hsl(var(--bg-muted));
  border-radius: var(--radius-md);
  flex: none;
}
.seg-btn {
  height: 24px;
  padding: 0 var(--space-2-5);
  border: 0;
  border-radius: var(--radius-sm);
  background: transparent;
  color: hsl(var(--fg-muted));
  font-size: var(--text-xs);
  line-height: 1;
  cursor: pointer;
  white-space: nowrap;
  transition:
    background-color 130ms ease-out,
    color 130ms ease-out;
}
.seg-btn:hover {
  color: hsl(var(--fg));
}
.seg-btn.on {
  background: hsl(var(--bg-elevated));
  color: hsl(var(--accent));
  font-weight: 600;
  box-shadow: var(--shadow-sm);
}
.seg-btn:focus-visible {
  outline: 2px solid hsl(var(--accent) / 0.4);
  outline-offset: 1px;
}
.prow {
  display: flex;
  align-items: center;
  gap: var(--space-1-5);
  flex-wrap: wrap;
  min-width: 0;
}
.pill {
  height: 26px;
  display: inline-flex;
  align-items: center;
  padding: 0 var(--space-2-5);
  border: 1px solid hsl(var(--border));
  border-radius: 99px;
  background: hsl(var(--bg-elevated));
  color: hsl(var(--fg));
  font-size: var(--text-xs);
  line-height: 1;
  cursor: pointer;
  white-space: nowrap;
  transition:
    background-color 130ms ease-out,
    border-color 130ms ease-out,
    color 130ms ease-out;
}
.pill:hover {
  border-color: hsl(var(--border-strong));
}
.pill.on {
  background: hsl(var(--accent));
  border-color: hsl(var(--accent));
  color: hsl(var(--accent-fg));
  font-weight: 600;
}
.pill:focus-visible {
  outline: 2px solid hsl(var(--accent) / 0.4);
  outline-offset: 1px;
}
.num-input {
  max-width: 130px;
}
/* 窄窗：简要列藏进展开行，只留 状态/名称/操作 */
@media (max-width: 760px) {
  .mrow {
    grid-template-columns: 26px minmax(0, 1fr) auto;
    padding: var(--space-1) var(--space-2);
  }
  .mrow-h {
    display: none;
  }
  .c-c {
    display: none;
  }
  .mexp {
    padding-left: var(--space-2);
  }
}
@media (prefers-reduced-motion: reduce) {
  .mrow-d,
  .mrow-d::after,
  .icobtn,
  .seg-btn,
  .pill {
    transition: none;
  }
  .mrow-d:hover,
  .mrow-d.on {
    background: hsl(var(--accent-subtle));
  }
  .mrow-d::after {
    display: none;
  }
}
</style>

<!-- src/Web.Client/src/components/settings/provider/ProviderConnForm.vue -->
<!-- P27 全页第一区块「连接」：基本字段（双列密排）+ 网络 + 扩展收纳（存储/请求头/危险区/探活）
     + 模型列表拉取行。说明文字只以 ⓘ 存在（定稿 §5）。 -->
<script setup lang="ts">
import { computed } from 'vue';
import HInput from '@/components/base/HInput.vue';
import HButton from '@/components/base/HButton.vue';
import HSwitch from '@/components/base/HSwitch.vue';
import FieldRow from '../fields/FieldRow.vue';
import HeadersEditor, { type HeaderRow } from '../fields/HeadersEditor.vue';
import ModelProbe from '../fields/ModelProbe.vue';
import type { ProviderEdit } from '../providerEditing';
import { headersCount, headersSummary } from '../providerFormat';

const props = defineProps<{
  provider: ProviderEdit;
  /** 字段错误（键为 field，如 baseUrl / id / network） */
  errors: Record<string, string>;
  /** 全局代理模式（仅用于「生效」提示） */
  globalMode: string;
  /** 拉取状态文案（未拉取 / 拉取中… / 已拉取到 N 个） */
  fetchStatus: string;
}>();

const emit = defineEmits<{
  change: [];
  'update:clear-key': [boolean];
  'update:headers-enabled': [boolean];
  'update:headers': [rows: HeaderRow[]];
  'probe-confirm': [names: string[]];
  'request-delete': [];
}>();

const p = computed(() => props.provider);

/** 生效态（只读透传）：表达走哪条路，不占额外行。 */
const effectiveText = computed(() => {
  const e = p.value.effective;
  if (!e) {
    if (p.value.network === 'follow') return `跟随全局（${props.globalMode}）`;
    return p.value.network === 'proxy' ? '走代理' : '直连';
  }
  if (!e.inUse) {
    return e.source === 'bypass-local' ? '本地直连' : `直连（${e.source}）`;
  }
  return e.source === 'global-system' ? '跟随系统走代理' : `走代理（${e.source}）`;
});

const keySet = computed(() => p.value.apiKeySet);
const keyPlaceholder = computed(() =>
  keySet.value ? '●●●●（已设，留空=不改）' : '请输入'
);
const keyDesc = computed(() =>
  keySet.value
    ? '已设置（不回显明文）；留空=不改，填写则覆盖'
    : '未设置；保存后不再回显'
);
const headerText = computed(() => headersSummary(p.value.headers));
const headerN = computed(() => headersCount(p.value.headers));

function setBaseUrl(v: string): void {
  p.value.baseUrl = v;
  emit('change');
}
function setDisplayName(v: string): void {
  p.value.displayName = v;
  emit('change');
}
function setNewKey(v: string): void {
  p.value.newKey = v;
  emit('change');
}
function setNetwork(v: 'follow' | 'proxy' | 'direct'): void {
  p.value.network = v;
  emit('change');
}
function setStore(v: 'default' | 'true' | 'false'): void {
  p.value.responseStore = v;
  emit('change');
}
</script>

<template>
  <section class="psec" aria-label="连接">
    <div class="psec-head">
      <h3 class="psec-title">连接</h3>
      <span class="psec-sub">地址 · Key · 标识 · 网络 · 存储 · 请求头 · 模型拉取</span>
    </div>

    <div class="grid2">
      <FieldRow
        label="地址"
        required
        desc="绝对 http(s)；保存时 Trim 并去尾 /。responses 协议在该地址下走 /responses 路径"
        :error="errors['baseUrl'] ?? null"
      >
        <HInput
          :model-value="p.baseUrl"
          placeholder="https://"
          size="sm"
          :error="!!errors['baseUrl']"
          @update:model-value="setBaseUrl"
        />
      </FieldRow>

      <FieldRow label="Key" :desc="keyDesc">
        <HInput
          :model-value="p.newKey"
          type="password"
          size="sm"
          :placeholder="keyPlaceholder"
          autocomplete="off"
          @update:model-value="setNewKey"
        />
        <span v-if="keySet" class="inline-hint">已设</span>
        <HSwitch
          v-if="keySet"
          :model-value="p.clearKey"
          label="保存时清除 Key"
          @update:model-value="(v: boolean) => emit('update:clear-key', v)"
        />
        <span v-if="keySet" class="inline-hint">清除</span>
      </FieldRow>

      <FieldRow label="注册名" desc="不可改。改名=新建+删旧；正则 ^[a-z0-9][a-z0-9-_]{0,63}$">
        <HInput :model-value="p.id" size="sm" disabled />
      </FieldRow>

      <FieldRow label="别称" desc="展示用；留空=清除，回落注册名">
        <HInput
          :model-value="p.displayName"
          :placeholder="p.id"
          size="sm"
          @update:model-value="setDisplayName"
        />
      </FieldRow>
    </div>

    <FieldRow label="网络" desc="全局决定走哪条路，单个服务可覆盖；本地/内网地址永远直连">
      <div class="seg" role="group" aria-label="网络">
        <button
          type="button"
          class="seg-btn"
          :class="{ on: p.network === 'follow' }"
          :aria-pressed="p.network === 'follow'"
          @click="setNetwork('follow')"
        >
          跟随全局
        </button>
        <button
          type="button"
          class="seg-btn"
          :class="{ on: p.network === 'proxy' }"
          :aria-pressed="p.network === 'proxy'"
          @click="setNetwork('proxy')"
        >
          走代理
        </button>
        <button
          type="button"
          class="seg-btn"
          :class="{ on: p.network === 'direct' }"
          :aria-pressed="p.network === 'direct'"
          @click="setNetwork('direct')"
        >
          直连
        </button>
      </div>
      <span class="inline-hint" :title="effectiveText">生效：{{ effectiveText }}</span>
      <span v-if="p.effective?.warning" class="inline-warn" role="status">
        {{ p.effective.warning }}
      </span>
    </FieldRow>

    <!-- 扩展收纳：细分隔线，不另起区块 -->
    <div class="subdiv">
      <div class="subdiv-title">扩展</div>

      <div class="grid2">
        <FieldRow
          label="存储"
          desc="控制 responses 请求体的 store 字段：缺省=不发送（兼容最好）；开=服务端持久化省 token；关=显式不持久化"
        >
          <div class="seg" role="group" aria-label="服务端存储">
            <button
              type="button"
              class="seg-btn"
              :class="{ on: p.responseStore === 'default' }"
              :aria-pressed="p.responseStore === 'default'"
              @click="setStore('default')"
            >
              缺省
            </button>
            <button
              type="button"
              class="seg-btn"
              :class="{ on: p.responseStore === 'true' }"
              :aria-pressed="p.responseStore === 'true'"
              @click="setStore('true')"
            >
              开
            </button>
            <button
              type="button"
              class="seg-btn"
              :class="{ on: p.responseStore === 'false' }"
              :aria-pressed="p.responseStore === 'false'"
              @click="setStore('false')"
            >
              关
            </button>
          </div>
        </FieldRow>

        <FieldRow
          label="请求头"
          :desc="`该 Provider 专属；空名称保存时过滤。当前 ${headerN} 个`"
        >
          <span class="inline-hint hdr-summary" :title="headerText">{{ headerText }}</span>
          <HSwitch
            :model-value="p.headersEnabled"
            label="启用自定义请求头"
            @update:model-value="(v: boolean) => emit('update:headers-enabled', v)"
          />
          <span class="inline-hint">{{ p.headersEnabled ? '启用' : '停用' }}</span>
        </FieldRow>
      </div>

      <FieldRow label="危险区">
        <HButton size="sm" variant="danger" @click="emit('request-delete')">删除 Provider</HButton>
      </FieldRow>

      <FieldRow label="探活" desc="连通性检查由下方模型列表统一拉取（/models）">
        <span class="inline-hint">由下方「模型列表」统一拉取</span>
      </FieldRow>

      <!-- 模型列表拉取行：复用 ModelProbe（探测弹窗 + 勾选并入） -->
      <FieldRow
        label="模型列表"
        desc="/models 探活：带自定义头、走对代理；结果勾选后去重并入下方模型区"
      >
        <ModelProbe
          :base-url="p.baseUrl"
          :api-key="p.newKey"
          :provider-id="p.id"
          :headers="p.headers as HeaderRow[]"
          :network="p.network"
          :clear-key="p.clearKey"
          :existing-models="p.models.map((m) => m.id)"
          @probe-confirm="(names: string[]) => emit('probe-confirm', names)"
        />
        <span class="inline-hint" role="status">{{ fetchStatus }}</span>
      </FieldRow>

      <!-- 请求头明细：展开式（默认收起，摘要在上方行内） -->
      <details class="hdr-box">
        <summary class="hdr-summary-btn">
          请求头明细（{{ headerN }}）· {{ p.headersEnabled ? '已启用' : '已停用' }}
        </summary>
        <div class="hdr-box-body">
          <HeadersEditor
            :model-value="p.headers as HeaderRow[]"
            @update:model-value="(rows: HeaderRow[]) => emit('update:headers', rows)"
          />
        </div>
      </details>
    </div>
  </section>
</template>

<style scoped>
.psec {
  padding: var(--space-1) 0 var(--space-2);
  border-bottom: 1px dashed hsl(var(--border-subtle));
  min-width: 0;
}
.psec:last-child {
  border-bottom: 0;
}
.psec-head {
  display: flex;
  align-items: baseline;
  gap: var(--space-2);
  margin: 2px 0 var(--space-1-5);
  min-width: 0;
}
.psec-title {
  margin: 0;
  font-size: var(--text-sm);
  font-weight: 700;
  color: hsl(var(--fg));
}
.psec-sub {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}
.grid2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0 var(--space-4);
  min-width: 0;
}
.subdiv {
  margin: var(--space-1-5) 0 0;
  padding-top: var(--space-1-5);
  border-top: 1px solid hsl(var(--border-subtle));
  min-width: 0;
}
.subdiv-title {
  font-size: var(--text-2xs);
  font-weight: 700;
  color: hsl(var(--fg-muted));
  letter-spacing: 0.03em;
  margin-bottom: 2px;
}
.inline-hint {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
  min-width: 0;
}
.inline-warn {
  font-size: var(--text-2xs);
  color: hsl(var(--warning));
}
.hdr-summary {
  font-family: var(--font-mono);
}
/* 三段 seg：底 bg-muted，选中白底 accent 字 */
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
.hdr-box {
  margin-top: var(--space-1);
  min-width: 0;
}
.hdr-summary-btn {
  font-size: var(--text-2xs);
  color: hsl(var(--fg-subtle));
  cursor: pointer;
  padding: 2px 0;
}
.hdr-summary-btn:hover {
  color: hsl(var(--fg));
}
.hdr-box-body {
  padding: var(--space-1) 0 var(--space-1) 86px;
  min-width: 0;
}
/* 窄窗：双列变单列；请求头明细不再缩进 */
@media (max-width: 640px) {
  .grid2 {
    grid-template-columns: 1fr;
  }
  .hdr-box-body {
    padding-left: 0;
  }
}
@media (prefers-reduced-motion: reduce) {
  .seg-btn {
    transition: none;
  }
}
</style>

<template>
  <div class="space-y-4 min-w-0 max-w-full overflow-x-hidden" data-testid="prompt-settings-tab">
    <!-- A. 总览与总开关区 -->
    <n-card title="总览与总开关" size="small">
      <n-alert
        v-if="config?.fallbackReason"
        type="warning"
        :show-icon="true"
        class="mb-3"
      >
        {{ config?.fallbackReason }}
      </n-alert>
      <div class="flex items-center gap-3 flex-wrap">
        <n-switch
          v-model:value="masterSwitch"
          data-testid="prompt-master-switch"
          @update:value="onMasterSwitch"
        />
        <span class="text-sm font-medium">发送 Halo-Agent 系统前置提示词</span>
        <n-tag :type="masterSwitch ? 'success' : 'warning'" size="small" :bordered="false">
          {{ masterSwitch ? '已启用' : '已关闭（本轮起不再发送任何系统提示词）' }}
        </n-tag>
      </div>
      <p class="text-xs text-secondary mt-2">
        生效范围：全局 <code>config/prompt.json</code>，保存后下一轮对话起生效；流式中的本轮不受影响。预览即时渲染不落盘。
      </p>
      <div class="flex items-center gap-2 mt-3 flex-wrap">
        <n-button
          type="primary"
          size="small"
          data-testid="prompt-save"
          :loading="promptStore.saving"
          :disabled="!promptStore.dirty || overLimit"
          @click="onSave"
        >
          保存
        </n-button>
        <n-button size="small" data-testid="prompt-reset-all" @click="onResetAll">
          恢复全部默认
        </n-button>
        <span v-if="promptStore.dirty" class="text-xs text-amber-500">尚未保存，离开将丢失</span>
      </div>
    </n-card>

    <!-- B. 分块开关与编辑区 -->
    <n-card title="分块开关与编辑" size="small">
       <n-collapse>
         <n-collapse-item
           v-for="meta in BLOCK_META"
           :key="meta.id"
           :name="meta.id"
         >
           <template #header>
             <div class="flex items-center gap-2 min-w-0" :data-testid="`prompt-block-${meta.id}`">
               <n-switch
                 :value="blockEnabled(meta.id)"
                 size="small"
                 @update:value="(v: boolean) => onBlockSwitch(meta.id, v)"
                 @click.stop
               />
               <span class="text-sm">{{ meta.name }}</span>
               <n-tag size="small" :bordered="false" class="font-mono">≈{{ draftTokens(meta.id) }}</n-tag>
               <n-tag :type="blockStable(meta.id) ? 'info' : 'warning'" size="small" :bordered="false">
                 {{ blockStable(meta.id) ? 'stable' : 'ephemeral' }}
               </n-tag>
             </div>
           </template>
           <div class="space-y-2 min-w-0">
             <div v-if="meta.id === 'custom'" class="flex items-center gap-2 flex-wrap">
               <n-radio-group
                 :value="config?.customPlacement || 'tail'"
                 data-testid="prompt-custom-placement"
                 size="small"
                 @update:value="onPlacement"
               >
                 <n-radio value="head">放在系统前置之前</n-radio>
                 <n-radio value="tail">放在系统前置之后</n-radio>
               </n-radio-group>
               <div class="flex items-center gap-1 flex-wrap">
                 <n-button
                   v-for="v in varButtons"
                   :key="v"
                   size="tiny"
                   @click="insertVar(meta.id, v)"
                 >
                   {{ v }}
                 </n-button>
               </div>
             </div>
             <div v-else-if="meta.hasVars" class="flex items-center gap-1 flex-wrap">
               <n-button
                 v-for="v in varButtons"
                 :key="v"
                 size="tiny"
                 @click="insertVar(meta.id, v)"
               >
                 {{ v }}
               </n-button>
             </div>
             <n-input
               :value="draftText(meta.id)"
               type="textarea"
               :autosize="{ minRows: 3, maxRows: 10 }"
               :placeholder="meta.id === 'custom' ? '填写 Markdown 自定义前置（团队规范/角色约束）…' : ''"
               @update:value="(v: string) => onDraftInput(meta.id, v)"
               @focus="() => onDraftFocus(meta.id)"
               @blur="() => commitBlock(meta.id)"
             />
             <div class="flex items-center gap-2 flex-wrap">
               <span
                 class="text-xs font-mono"
                 :class="draftLen(meta.id) > 8000 ? 'text-red-500' : draftLen(meta.id) > 7800 ? 'text-amber-500' : 'text-secondary'"
               >
                 {{ draftLen(meta.id) }}/8000
               </span>
               <n-button size="tiny" @click="copyText(draftText(meta.id))">复制</n-button>
               <n-popconfirm @positive-click="onResetBlock(meta.id)">
                 <template #trigger>
                   <n-button size="tiny">恢复本块默认</n-button>
                 </template>
                 确认恢复「{{ meta.name }}」为默认文本与开关？
               </n-popconfirm>
               </div>
             </div>
             </n-collapse-item>
         </n-collapse>
       </n-card>

    <!-- C. 预发送预览区 -->
    <n-card title="预发送预览" size="small">
      <div class="flex items-center gap-2 flex-wrap mb-2">
        <n-select
          v-model:value="previewWorkspaceId"
          :options="workspaceOptions"
          placeholder="选择工作区预览"
          size="small"
          style="min-width: 220px; max-width: 320px;"
        />
        <n-button
          size="small"
          data-testid="prompt-preview-refresh"
          :loading="promptStore.previewing"
          @click="onRefreshPreview"
        >
          刷新预览
        </n-button>
        <n-button
          size="small"
          data-testid="prompt-preview-copy"
          :disabled="!promptStore.preview?.assembledText"
          @click="copyText(promptStore.preview?.assembledText || '')"
        >
          复制
        </n-button>
      </div>
      <n-alert
        v-if="promptStore.previewStale"
        type="warning"
        :show-icon="false"
        class="mb-2"
      >
        配置已更改，预览可能过期，请点刷新预览
      </n-alert>
      <div v-if="!masterSwitch" class="text-sm text-secondary py-4 text-center">
        前置已关闭：本轮起不再发送任何系统提示词（双协议均无 system 字段）
      </div>
      <pre
        v-else-if="promptStore.preview?.assembledText"
        data-testid="prompt-preview-text"
        class="prompt-preview-pre"
        :class="{ 'opacity-50': promptStore.previewStale }"
      >{{ promptStore.preview?.assembledText }}</pre>
      <div v-else class="text-sm text-secondary py-4 text-center">
        {{ previewEmptyHint }}
      </div>
      <div v-if="promptStore.preview?.unknownVariables?.length" class="mt-2">
        <n-alert type="warning" :show-icon="true">
          未知变量已原样保留：{{ promptStore.preview?.unknownVariables.join(', ') }}
        </n-alert>
      </div>
      <p class="text-xs font-mono mt-2 break-words">
        共 {{ promptStore.preview?.assembledText.length || 0 }} 字符 · 约 {{ promptStore.preview?.totalEstimatedTokens || 0 }} token · stable 约 {{ promptStore.preview?.stableEstimatedTokens || 0 }} token · prefixHash {{ promptStore.preview?.prefixHash || '-' }}
      </p>
      <n-collapse class="mt-2">
        <n-collapse-item title="分块明细（stable 蓝头 / ephemeral 黄头 / custom 紫头）" name="blocks">
          <div class="space-y-1.5">
            <div
              v-for="blk in promptStore.preview?.blocks || []"
              :key="blk.blockId"
              class="prompt-block-row"
              :class="blockColorClass(blk.blockId, blk.isStable)"
            >
              <div class="flex items-center gap-2 text-xs font-mono">
                <span>{{ blk.blockId }}</span>
                <span>≈{{ blk.estimatedTokens }}</span>
                <span>{{ blk.isStable ? 'stable' : 'ephemeral' }}</span>
              </div>
              <pre class="prompt-block-pre">{{ blk.renderedText }}</pre>
            </div>
          </div>
        </n-collapse-item>
        <n-collapse-item title="双协议渲染对照" name="protocol">
          <p class="text-xs mb-1">Chat：请求体 messages[0] = { role:"system", content:"&lt;上方便是此 content&gt;" }；总开关关时 messages 无首条 system。</p>
          <p class="text-xs">Resp：请求体顶层 instructions = "&lt;上方便是此 instructions&gt;"；总开关关时无 instructions 字段。</p>
        </n-collapse-item>
      </n-collapse>
    </n-card>

    <!-- D. 诊断区 -->
    <n-card title="诊断对照" size="small" data-testid="prompt-diagnostics">
       <table class="prompt-diagnostics-table">
         <tbody>
           <tr>
             <th scope="row">上轮 reqId</th>
             <td><span class="font-mono text-xs cell-value">{{ diagnostics?.lastSent?.reqId || '-' }}</span></td>
             <th scope="row">上轮 prefixHash</th>
             <td><span class="font-mono text-xs cell-value">{{ diagnostics?.lastSent?.prefixHash || '-' }}</span></td>
           </tr>
           <tr>
             <th scope="row">上轮长度</th>
             <td><span class="font-mono text-xs cell-value">{{ diagnostics?.lastSent?.charLength ?? '-' }}</span></td>
             <th scope="row">上轮时间</th>
             <td><span class="font-mono text-xs cell-value">{{ diagnostics?.lastSent?.sentAtUtc || '-' }}</span></td>
           </tr>
         </tbody>
       </table>
      <n-alert
        v-if="!diagnostics?.lastSent"
        type="info"
        :show-icon="false"
        class="mt-2"
      >
        暂无本会话发送记录：发起一轮对话后此处显示 reqId/prefixHash/长度/时间。
      </n-alert>
      <n-alert
        v-else-if="diagnostics?.previewEqualsLastSent"
        type="success"
        :show-icon="false"
        class="mt-2"
      >
        当前配置预览与上轮实际发送一致（prefixHash 相同）。
      </n-alert>
      <n-alert
        v-else
        type="warning"
        :show-icon="false"
        class="mt-2"
      >
        当前配置与上轮实际发送不一致：预览 prefixHash={{ promptStore.preview?.prefixHash }}，上轮={{ diagnostics?.lastSent?.prefixHash }}。保存后下一轮起生效，流式中的本轮不受影响。
      </n-alert>
      <p class="text-xs text-secondary mt-2">
        提示：开关或文本变更会改变请求前缀字节，上游 prompt cache 需一轮预热；易变行（日期/Python）始终只在尾部，不影响 stable 段命中。
      </p>
      <n-collapse class="mt-2">
        <n-collapse-item title="压缩提示词（本期仅预览）" name="compressor">
          <pre class="prompt-block-pre">{{ diagnostics?.compressorPromptPreview || '' }}</pre>
        </n-collapse-item>
        <n-collapse-item title="标题提示词（本期仅预览）" name="title">
          <pre class="prompt-block-pre">{{ diagnostics?.titlePromptPreview || '' }}</pre>
        </n-collapse-item>
      </n-collapse>
    </n-card>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { useMessage, useDialog } from 'naive-ui';
import { usePromptStore } from '@/stores/promptStore';
import { useWorkspaceStore } from '@/stores/workspaceStore';

const message = useMessage();
const dialog = useDialog();
const promptStore = usePromptStore();
const workspaceStore = useWorkspaceStore();

const previewWorkspaceId = ref<string>('');
const previewEmptyHint = ref('选择工作区并点刷新预览，查看按当前配置实际会发出的完整 system 文本。');

const varButtons = ['{{WorkspaceName}}', '{{WorkspaceRoot}}', '{{OsVersion}}', '{{OsArch}}', '{{PythonStatus}}', '{{CurrentDate}}'];

const config = computed(() => promptStore.config);
const diagnostics = computed(() => promptStore.diagnostics);

const masterSwitch = computed({
  get: () => config.value?.enableHaloPrefix ?? true,
  set: () => {},
});

 const overLimit = computed(() => {
   return BLOCK_META.some((m) => draftLen(m.id) > 8000);
 });
 
 const workspaceOptions = computed(() =>
   (workspaceStore.workspaces || []).map((w) => ({ label: `${w.name}（${w.rootPath}）`, value: w.id }))
 );
 
 interface BlockMeta { id: string; name: string; textKey: string; enableKey: string; stable: boolean; hasVars: boolean; }
 
 // 静态块元数据（非响应式派生）：编辑框只跟本地草稿走，不跟随 store 重算，从根上断开逐字回写链路
 const BLOCK_META: BlockMeta[] = [
   { id: 'identity', name: 'Halo 身份', textKey: 'identityText', enableKey: 'enableIdentity', stable: true, hasVars: false },
   { id: 'runtime', name: '运行环境事实', textKey: 'runtimeTemplate', enableKey: 'enableRuntime', stable: true, hasVars: true },
   { id: 'cmdPython', name: 'cmd/py 优先铁律', textKey: 'cmdPythonText', enableKey: 'enableCmdPython', stable: true, hasVars: false },
   { id: 'toolGuide', name: '工具指南', textKey: 'toolGuideTemplate', enableKey: 'enableToolGuide', stable: true, hasVars: false },
   { id: 'ephemeral', name: '尾部易变（日期/Python）', textKey: 'ephemeralTemplate', enableKey: 'enableEphemeral', stable: false, hasVars: true },
   { id: 'custom', name: '用户自定义', textKey: 'customText', enableKey: 'enableCustom', stable: false, hasVars: true },
 ];
 
 // 本地编辑草稿：输入只写草稿（与主聊天框 draft 同模式），400ms 防抖 + 失焦即时落盘到 config
 const drafts = ref<Record<string, string>>({});
 const focusedBlockId = ref<string | null>(null);
 const commitTimers: Record<string, number> = {};
 
 function configTextOf(meta: BlockMeta): string {
   const c = config.value;
   if (!c) return '';
   const v = (c as unknown as Record<string, unknown>)[meta.textKey];
   return typeof v === 'string' ? v : '';
 }
 
 function syncDraftsFromConfig(): void {
   const next: Record<string, string> = {};
   for (const m of BLOCK_META) next[m.id] = configTextOf(m);
   drafts.value = next;
 }
 
 function draftText(id: string): string {
   return drafts.value[id] ?? '';
 }
 
 function draftLen(id: string): number {
   return draftText(id).length;
 }
 
 function draftTokens(id: string): number {
   return promptStore.estimateTokensLocal(draftText(id));
 }
 
 function blockEnabled(id: string): boolean {
   const c = config.value;
   if (!c) return false;
   const meta = BLOCK_META.find((m) => m.id === id);
   if (!meta) return false;
   return !!((c as unknown as Record<string, unknown>)[meta.enableKey]);
 }
 
 function blockStable(id: string): boolean {
   if (id === 'custom') return (config.value?.customPlacement || 'tail') === 'head';
   return BLOCK_META.find((m) => m.id === id)?.stable ?? true;
 }
 
 function writeConfigText(id: string, text: string): void {
   const c = config.value;
   if (!c) return;
   const meta = BLOCK_META.find((m) => m.id === id);
   if (!meta) return;
   (c as unknown as Record<string, unknown>)[meta.textKey] = text;
 }
 
 function onDraftFocus(id: string): void {
   focusedBlockId.value = id;
   if (!(id in drafts.value)) drafts.value[id] = configTextOf(BLOCK_META.find((m) => m.id === id)!);
 }
 
 function onDraftInput(id: string, v: string): void {
   drafts.value[id] = v;
   if (commitTimers[id]) window.clearTimeout(commitTimers[id]);
   commitTimers[id] = window.setTimeout(() => commitBlock(id), 400);
 }
 
 function commitBlock(id: string): void {
   if (commitTimers[id]) {
     window.clearTimeout(commitTimers[id]);
     delete commitTimers[id];
   }
   if (focusedBlockId.value === id) focusedBlockId.value = null;
   const c = config.value;
   if (!c || !(id in drafts.value)) return;
   if (draftText(id) !== configTextOf(BLOCK_META.find((m) => m.id === id)!)) {
     writeConfigText(id, drafts.value[id]);
     promptStore.touchDirty();
   }
 }
 
 function flushAllDrafts(): void {
   for (const m of BLOCK_META) {
     if (commitTimers[m.id]) {
       window.clearTimeout(commitTimers[m.id]);
       delete commitTimers[m.id];
     }
     if (m.id in drafts.value) writeConfigText(m.id, drafts.value[m.id]);
   }
   focusedBlockId.value = null;
 }

function blockColorClass(blockId: string, isStable: boolean): string {
  if (blockId === 'custom') return 'prompt-block-custom';
  return isStable ? 'prompt-block-stable' : 'prompt-block-ephemeral';
}

function onMasterSwitch(v: boolean) {
  if (!config.value) return;
  config.value.enableHaloPrefix = v;
  promptStore.touchDirty();
}

function onBlockSwitch(id: string, v: boolean) {
  const c = config.value;
  if (!c) return;
  if (id === 'identity') c.enableIdentity = v;
  else if (id === 'runtime') c.enableRuntime = v;
  else if (id === 'cmdPython') c.enableCmdPython = v;
  else if (id === 'toolGuide') c.enableToolGuide = v;
  else if (id === 'ephemeral') c.enableEphemeral = v;
  else if (id === 'custom') c.enableCustom = v;
   promptStore.touchDirty();
 }
 function onPlacement(v: 'head' | 'tail') {
   if (!config.value) return;
   config.value.customPlacement = v;
   promptStore.touchDirty();
 }
 
 function insertVar(id: string, v: string) {
   drafts.value[id] = draftText(id) + v;
   commitBlock(id);
   if (focusedBlockId.value === id) focusedBlockId.value = null;
 }
 
 async function onSave() {
   flushAllDrafts();
   const res = await promptStore.saveConfig();
   if (res.ok) {
     syncDraftsFromConfig();
     message.success('提示词配置已保存，下一轮对话起生效');
     await onRefreshPreview();
   } else {
     message.error(res.message || '保存失败');
   }
  }

function onResetAll() {
  dialog.error({
    title: '恢复全部默认',
    content: '确认将 6 个块全部恢复为默认文本与开关？当前编辑将丢失。',
    positiveText: '确认恢复',
    negativeText: '取消',
     onPositiveClick: async () => {
       const res = await promptStore.resetAll();
       if (res.ok) {
         syncDraftsFromConfig();
         message.success('已恢复全部默认');
         await onRefreshPreview();
       } else {
         message.error(res.message || '恢复失败');
       }
     },
   });
 }
 
 async function onResetBlock(id: string) {
   commitBlock(id);
   const res = await promptStore.resetBlock(id);
   if (res.ok) {
     syncDraftsFromConfig();
     message.success('已恢复本块默认');
     await onRefreshPreview();
   } else {
     message.error(res.message || '恢复失败');
   }
 }
 
 async function onRefreshPreview() {
   const wid = previewWorkspaceId.value || workspaceStore.activeWorkspaceId || workspaceStore.workspaces[0]?.id || '';
   if (!wid) {
     previewEmptyHint.value = '当前无工作区，为中性预览';
   }
   await promptStore.refreshPreview(wid);
   await promptStore.loadDiagnostics(wid, workspaceStore.activeSessionId || undefined);
 }

async function copyText(text: string) {
  try {
    await navigator.clipboard.writeText(text);
    message.success('已复制');
  } catch {
    message.error('复制失败');
  }
}

watch(
  () => workspaceStore.activeWorkspaceId,
  (v) => {
    if (v && !previewWorkspaceId.value) previewWorkspaceId.value = v;
  },
  { immediate: true }
);

 onMounted(async () => {
   if (!promptStore.config) await promptStore.loadConfig();
   syncDraftsFromConfig();
  if (workspaceStore.workspaces.length === 0) {
    try { await workspaceStore.loadWorkspaces(); } catch { /* 忽略 */ }
  }
  if (!previewWorkspaceId.value) {
    previewWorkspaceId.value = workspaceStore.activeWorkspaceId || workspaceStore.workspaces[0]?.id || '';
  }
  await onRefreshPreview();
});
</script>

<style scoped>
 .prompt-preview-pre {
   white-space: pre-wrap;
   word-break: break-word;
   max-height: 420px;
   overflow-y: auto;
   overflow-x: hidden;
   font-size: 12px;
   line-height: 1.6;
   background: var(--code-bg);
   color: var(--code-text);
   border: 1px solid var(--code-border);
   border-radius: 6px;
   padding: 10px 12px;
   margin: 0;
   min-width: 0;
   max-width: 100%;
 }
 /* 原生诊断表：与 sys-runtime-telemetry-table 同策略，固定布局 + 全单元格框线，框线色随主题 */
 .prompt-diagnostics-table { table-layout: fixed; width: 100%; max-width: 100%; box-sizing: border-box; border-collapse: collapse; border: 1px solid var(--border-subtle); border-radius: 6px; overflow: hidden; }
 .prompt-diagnostics-table th {
   width: 108px; min-width: 108px; max-width: 108px;
   text-align: left; font-weight: 500; font-size: 12px;
   padding: 6px 10px; border: 1px solid var(--border-subtle);
   white-space: normal; overflow-wrap: break-word; word-break: normal;
   line-height: 1.5; vertical-align: top;
 }
 .prompt-diagnostics-table td {
   padding: 6px 10px; border: 1px solid var(--border-subtle);
   white-space: normal; overflow-wrap: break-word; word-break: normal;
   line-height: 1.6; vertical-align: top; min-width: 0; max-width: 100%;
 }
 .prompt-diagnostics-table .cell-value {
   min-width: 0; white-space: normal; overflow-wrap: break-word; word-break: break-all;
 }
.prompt-block-pre {
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 12px;
  line-height: 1.6;
  margin: 4px 0 0 0;
  min-width: 0;
  max-width: 100%;
}
.prompt-block-row {
  border-left: 3px solid transparent;
  padding: 6px 8px;
  border-radius: 0 6px 6px 0;
  background: rgba(128, 128, 128, 0.08);
  min-width: 0;
  max-width: 100%;
  overflow-x: hidden;
}
.prompt-block-stable {
  border-left-color: #2080f0;
}
.prompt-block-ephemeral {
  border-left-color: #f0a020;
}
.prompt-block-custom {
  border-left-color: #8e44ad;
}
</style>

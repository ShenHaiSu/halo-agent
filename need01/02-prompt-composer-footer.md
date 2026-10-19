# 任务2：输入框底栏——删除「项目文件夹名 + 默认Runner」

> 文件：`src/Web.Client/src/components/chat/PromptComposer.vue`（全文件 105 行）
> 目标：底栏只剩发送/停止按钮，不再显示左侧两块信息胶囊。

## 1. 现状代码（已通读）

```vue
<!-- L17-L47：底栏 -->
<div class="flex items-center justify-between gap-2">
  <!-- 左半区：待删除 -->
  <div class="flex items-center gap-2 text-[11px] text-secondary min-w-0">
    <span class="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-elevated font-mono truncate"
          :title="workspacePath">
      <FolderGit2 class="w-3 h-3 text-accent shrink-0" />
      <span class="truncate">{{ workspaceName }}</span>
    </span>
    <span class="hidden sm:inline-flex items-center gap-1 px-2 py-0.5 rounded bg-elevated font-mono shrink-0">
      <Terminal class="w-3 h-3 text-emerald-400" />
      默认Runner: CMD优先/PY次之
    </span>
  </div>
  <!-- 右半区：保留 -->
  <n-button v-if="!agentStore.isGenerating" ...>发送 (Enter)</n-button>
  <n-button v-else ...>停止生成 (Esc)</n-button>
</div>
```

```ts
// L52-L68：与左半区绑定的脚本
import { FolderGit2, SendHorizontal, Square, Terminal } from 'lucide-vue-next';
import { useWorkspaceStore } from '@/stores/workspaceStore';
const workspaceStore = useWorkspaceStore();
const { activeWorkspace } = storeToRefs(workspaceStore);
const workspaceName = computed(() => activeWorkspace.value?.name || '未绑定工作区');
const workspacePath = computed(() => activeWorkspace.value?.rootPath || '');
const canSend = computed(() => !!(workspaceStore.activeSessionId || workspaceStore.activeSession?.id));
```

注意：左半区信息在别处仍有冗余出口——顶栏左侧（`WorkbenchTopBar.vue` L7-L17）已显示 `workspace.name / session.title`，底栏再显示一次纯属重复；`默认Runner` 属于实现细节，不应常驻输入舱。

## 2. 施工方案

### 2.1 模板：删除左半区，父容器改为右对齐

```diff
-      <div class="flex items-center justify-between gap-2">
-        <div class="flex items-center gap-2 text-[11px] text-secondary min-w-0">
-          <span ... :title="workspacePath">
-            <FolderGit2 ... />
-            <span class="truncate">{{ workspaceName }}</span>
-          </span>
-          <span class="hidden sm:inline-flex ...">
-            <Terminal ... />
-            默认Runner: CMD优先/PY次之
-          </span>
-        </div>
+      <div class="flex items-center justify-end gap-2">
         <n-button v-if="!agentStore.isGenerating" ...>发送 (Enter)</n-button>
         <n-button v-else ...>停止生成 (Esc)</n-button>
       </div>
```

- 位置：`PromptComposer.vue` L17-L27 整块删除，L17 的父容器 `justify-between` 改为 `justify-end`。
- 为什么用 `justify-end` 而不是删空父容器：保留一行右对齐的按钮行，发送/停止按钮位置与原来右半区完全一致，输入舱高度、`max-w-4xl` 居中、`p-2.5 space-y-2` 节奏都不变，视觉最稳。
- 备选（不推荐）：整个底栏 `<div>` 删除、按钮上浮——会改变输入舱内边距节奏，且 `Enter/Esc` 按钮失去独立行，在窄窗下易与输入框粘连，故否决。

### 2.2 脚本：清理死代码（与模板同一次提交）

```diff
-import { computed, onMounted, onUnmounted, ref } from 'vue';
-import { storeToRefs } from 'pinia';
-import { FolderGit2, SendHorizontal, Square, Terminal } from 'lucide-vue-next';
-import { useWorkspaceStore } from '@/stores/workspaceStore';
+import { computed, onMounted, onUnmounted, ref } from 'vue';
+import { SendHorizontal, Square } from 'lucide-vue-next';
 import { useAgentStore } from '@/stores/agentStore';
+import { useWorkspaceStore } from '@/stores/workspaceStore';
 
-const workspaceStore = useWorkspaceStore();
 const agentStore = useAgentStore();
-const { activeWorkspace } = storeToRefs(workspaceStore);
+const workspaceStore = useWorkspaceStore(); // 仅保留 canSend 所需
 ...
-const workspaceName = computed(() => ...);
-const workspacePath = computed(() => ...);
 const canSend = computed(() => !!(workspaceStore.activeSessionId || workspaceStore.activeSession?.id));
```

- 删除：`FolderGit2`、`Terminal` 图标引入，`storeToRefs`（若无他用），`activeWorkspace` 解构，`workspaceName`、`workspacePath` 两个 computed。
- 保留：`workspaceStore` 实例（`canSend` 仍依赖它做发送门禁）、`SendHorizontal`、`Square`、键盘事件（Enter 发送 / Esc 停止）原样不动。
- 若 lint 开启 `no-unused-vars`，不清理会导致 `vue-tsc -b` 告警/失败，所以模板与脚本必须同批改。

### 2.3 样式与布局确认

- 外层 `shrink-0 px-6 pb-4 pt-2` 与内层 `rounded-xl border border-subtle bg-surface ... p-2.5` 均不动。
- `n-input[type=textarea]`（`minRows:2 maxRows:8`）、`focus-within` 高亮、`disabled=isGenerating` 均不动。
- 删除后底栏高度由双胶囊时的约 28px 收窄为单按钮行约 26px，属预期收紧，无需补占位。

## 3. 验收

- [ ] 输入舱底栏无文件夹名、无 `默认Runner` 文案（全局搜 `默认Runner` 在 `src/` 零命中）。
- [ ] 有草稿+有效会话时 `发送 (Enter)` 可点；无会话时仍被 `canSend` 禁用；生成中切换为 `停止生成 (Esc)`，Enter/Esc 快捷键行为不变。
- [ ] 顶栏左侧仍可见 `工作区 / 会话` 路径（信息未丢失，只是去重）。
- [ ] `vue-tsc -b && vite build` 通过；`data-testid="prompt-composer"` 未改名。

## 4. 回归风险

- 极低。唯一逻辑依赖是 `canSend`，其数据源（`activeSessionId/activeSession`）不受本次删除影响；`agentStore.bind()` 与全局 Esc 监听原样保留。

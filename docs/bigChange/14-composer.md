# 14 Composer + Slash + 附件（P2-8）

第二参考：`uiuxAndStyle/07-composer.md`。前置：`composerEditorDom/useComposer/useSlashMenu` 现状保留骨架、行为重写。

## 1. 前置检查

- [ ] 通读 `PromptComposer*.vue + useComposer.ts + composerEditorDom.ts + useSlashMenu.ts + useComposerAttachments.ts + SkillPillChip/SlashMenu.vue`：标出发送门控、Enter 规则、chip token、附件链。
- [ ] 确认 `AskUserModal/PlanApproval` 挂点是否在 composer dock（见 12 live-only 要求）。
- [ ] 确认 `largePasteThreshold` 与图片预算（`TurnPlanner` 8MB）是否一致。

## 2. 阶段目标

- 发送/停止/排队语义与 pi 一致；Enter/IME/Alt+Enter 正确。
- Slash 分组 + 文件模式 + 分发；chip 原子不可编辑。
- 拖放/粘贴/附件单飞行，大文本转附件，会话链接转 chip。

## 3. 实现办法

### 3.1 发送/停止/排队（改 `useComposer/useSendTurn`）

- 富文本 contenteditable，draft 在外；`hasDraftContent（trim 或 refs） && !sendBlocked && (modelReady || 首字 /)` 才可发；`sendBlocked = approvalPending | pasting | nativeInputBlocked`。
- 提交串行化行内 refs，乐观清 draft，`!accepted` 恢复 + 记历史；Alt+Enter 运行中走 steering（`steerPrompt`）。
- Stop：`runActive && !hasDraftContent` 才替发送， gated `canStop`。
- 排队行：上/下移、立即发、编辑（输入非空则 toast 拒）、删除；pending/promoted 锁动作。

### 3.2 按键/尺寸

- Enter：`!Shift && (enterToSend || Cmd/Ctrl)` 发；IME composing/229 全拦；Alt+Enter 有 AC 也照发 steering。
- `beforeInput insertParagraph/LineBreak` → `insertNewline`；多行粘贴 `execCommand insertHTML + BR` + caret 修。
- 高：min 28px / max 7 行，超 `overflowY:auto`；input 无 maxLength；大粘贴阈值默认 600（1..1M），读 settings。

### 3.3 Slash（改 `useSlashMenu + SlashMenu.vue`）

- 触发：draft+caret 检测，composing 冻结；分组 `template/builtin/plugin/extension/skill/mcp`；面板 `side=top + matchAnchorWidth`；焦点留输入；mousedown 接受；hover 高亮；上下循环；Enter/Tab 接受；Esc 关+回 suggestion。
- 空态：slashEmpty/fileEmpty/fileNoWorkspace + 截断注；文件模式 50 行 + TTL 10s 缓存 + 150ms 防抖；全角 `、` 首字重写 `/`。
- 分发：`/name body` 解析；template/未知 → prompt；builtin/extension/mode 按 body；不可读源 toast `slashCommandSourceUnavailable`；插件组归插件名下，失败组自折叠。

### 3.4 Chip/附件（改 `composerEditorDom + useComposerAttachments`）

- chip：`contenteditable=false` 原子 + 私有区 token 字符；图标按 family（image/code/archive/sheet/audio/video/session/plugin/fold/x）；`.txt` 展可编辑文本；图开预览；会话开会话；余列表项；删除钮 mousedown 防跳 caret；Enter 不冒泡发；`acceptCompletion` 吞 `@` 插 token；hover 图预览 + pan。
- 拖放：shell dragEnter/Over/Leave/Drop + copy effect + `is-drop-target`；文件夹 drop 出行：打开为项目/引用文件夹/忽略。
- 文件经 pick/import/paste 在 caret 处入队，会话无则建；toast `filesAttached/filesPasted/largeTextPasted`；大文本超阈 → `.txt`；会话链接 → chip；小文本强制纯文本；图+文偏文本；粘贴期 `aria-busy + sendBlocked` toast；附件禁于 pasting/inputBlocked/无权；picker 单飞行。

## 4. 验收标准

- [ ] 发送门控/乐观清/失败恢复/steering/Stop/排队全行为；IME/Alt/Enter 无串发。
- [ ] Slash 分组/文件 50/TTL/防抖/全角改写/分发/toast 全覆盖。
- [ ] chip 原子、附件单飞行、大文本转附件、文件夹行、粘贴 aria-busy。
- [ ] 12 的 Permission/Ask/Plan 只在 dock live 出现（本阶段挂点验收）。
- [ ] 旧 `PromptComposer` 简易 textarea（如有）删除，统一富文本（破坏性）。

## 5. 风险

- 富文本 contenteditable 在 CefSharp 下 IME/caret 与 Chromium 差异，需真机测中文输入；允许修 caret，不兼容旧粘贴行为。

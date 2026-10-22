# 07 — Composer · Slash · Attachments

> Source: `components/Composer.tsx`, `features/chat/composer/*` (`ComposerInput.tsx`, `ComposerToolbar.tsx`, `ComposerStatus.tsx`, `editor.ts`, `model.ts`, `slash-dispatch.ts`, `hooks/useComposerSubmit.ts`, `useComposerCompletions.ts`, `useComposerAttachments.ts`), `components/ComposerAutocomplete.tsx`, `hooks/use-composer-autocomplete.ts`, `packages/shared/src/composer-trigger.ts`, `styles/composer.css`. Behavior / thresholds / copy only.

## 1. Send / stop

- Rich contenteditable input, draft state outside (`ComposerInput.tsx:56`). Send disabled unless `hasDraftContent` (trim or refs) and `!sendBlocked` and `modelReady` unless leading `/` (`ComposerToolbar.tsx:247-267`). `sendBlocked = approvalPending | pasting | nativeInputBlocked` (`Composer.tsx:250`).
- Submit serializes inline refs, clears draft optimistically, restores on `!accepted`, records history (`useComposerSubmit.ts:70-94,180-191`). Steering via Alt+Enter uses `steerPrompt` when runActive (`ComposerInput.tsx:134-139`; `ComposerToolbar.tsx:112`).
- Stop replaces send when `runActive && !hasDraftContent`, gated `canStop` (`ComposerToolbar.tsx:235-246`).
- Queued rows: move up/down, sendNow, edit (requires empty input else toast), remove; pending/promoted lock actions (`ComposerStatus.tsx:48-144`).

## 2. Keys / sizing

- Enter sends if `!Shift && (enterToSend || Cmd/Ctrl)` (`ComposerInput.tsx:186-193`). IME composing/229 blocks all (`:132-133`). Alt+Enter submits steering even with AC open (`:134-139`).
- `beforeInput insertParagraph/LineBreak` → `insertNewline` (`:107-116`). Multiline paste via execCommand `insertHTML` with BR, caret fix (`editor.ts:454-473`).
- Autosize min 28px, max 7 rows, then `overflowY:auto` else hidden (`Composer.tsx:274-316`; `model.ts:18-19`; `composer.css:303-307`).
- No maxLength on input. Large-paste threshold normalize 1..1M default 600 (`composer-trigger.ts:29-44`); Composer reads `settings.largePasteThreshold` (`Composer.tsx:213-215`).

## 3. Slash UX

- Trigger detect over draft+cursor, freezes during composing (`use-composer-autocomplete.ts:193-202`). Groups ordered template / builtin / plugin / extension / skill / mcp (`:54-61`; `ComposerAutocomplete.tsx:47-54`).
- Panel full-width above input `side=top matchAnchorWidth`, focus stays in textarea, mousedown accept, hover highlight, arrows cycle, Enter/Tab accept, Esc closes then suggestion (`ComposerAutocomplete.tsx:18-22,99-160`; `ComposerInput.tsx:140-161`).
- Empty states slashEmpty / fileEmpty / fileNoWorkspace + truncated note (`:181-186,:217-219`). File mode max 50 rows, TTL 10s cache, 150ms debounce main search past truncated index (`use-composer-autocomplete.ts:33-36,238-320`).
- Leading IDEOGRAPHIC_COMMA 、 rewritten to `/` (`composer-trigger.ts:50-64`).
- Dispatch: parse `/name body` (`slash-dispatch.ts:24-30`); template/unknown → prompt; builtin/extension/mode with body handling; unreadable source → blocked toast `slashCommandSourceUnavailable` (`useComposerSubmit.ts:98-175`; `slash-dispatch.ts:39-64`).
- Plugin trigger group after host rows under plugin name; failing provider collapses own group (`useComposerCompletions.ts:1-12,132-147`).

## 4. Chips / preview / remove

- Atomic `contenteditable=false .composer-chip` with private-use token char (`editor.ts:73-81,285-373`). Icons by family image/code/archive/sheet/audio/video/session/plugin/fold/x (`:259-278`). Text `.txt` expands to editable text; image opens preview; session opens conversation; others listitem only (`:303-336`).
- Remove btn mousedown-prevent caret jump; Enter stops send bubbling (`:346-361`). Image hover preview + pan controllers (`ComposerInput.tsx:87-88`). `acceptCompletion` strips `@` token, inserts chip token (`useComposerCompletions.ts:195-224`).

## 5. Drag-drop / paste / attachments

- Shell dragEnter/Over/Leave/Drop, copy dropEffect, `is-drop-target` class (`useComposerAttachments.ts:437-484`; `Composer.tsx:560-568`). Folder drop → status row: Open as project / Reference folder / dismiss (`ComposerStatus.tsx:146-181`; `useComposerAttachments.ts:455-471`).
- Files attach via pickFiles / importFiles / pasteFiles at caret, session materialized if home; toasts filesAttached / filesPasted / largeTextPasted (`useComposerAttachments.ts:95-162,267-314,350-435`).
- Large text paste > threshold → `.txt` attachment (`:234-266`). Session-link paste → session chip (`:176-212,323-329`). Small text forced plain, no rich HTML (`:331-347`). Word image+text prefers text (`editor.ts:433-447`).
- Attach blocked when pasting / inputBlocked / `!attachmentsAllowed`; picker single-flight (`useComposerAttachments.ts:90,96-99,227`). Pasting sets `aria-busy` + `sendBlocked` toast `pasteInProgress` (`ComposerInput.tsx:98`; `useComposerSubmit.ts:78-80`).

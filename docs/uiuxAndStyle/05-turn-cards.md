# 05 — Turn + Tool Card States

> Source: `lib/turn-process.ts`, `features/chat/transcript/TurnProcess.tsx`, `AssistantTurn.tsx`, `ToolRow.tsx`, `components/ToolDetails.tsx`, `TurnOutcomeCard.tsx`, `PermissionCard.tsx`, `AskToolCard.tsx`, `PlanApprovalBar.tsx`, `ReviewChangeCard.tsx`, `shared.tsx`. States / buttons / copy only.

## 1. Turn states

- Phase computer (`turn-process.ts:207-240`): `failed` (turn recorded own failure) > `stopped` > `running{phase?}` > `completed`.
- Header copy (`TurnProcess.tsx:123`): failed / stopped / completed (+`For` variant with duration when `startedAt` known and >0s); running shows compacting / recovering / retrying / waiting-subagents labels, else Thinking vs generic running (`:36,:137`). Error-count badge when issues>0 and not failed (`:161`); tool-count badge (`:167`). Active turn: `active` class, spinner dot, per-second timer (`:98,:145,:160`).
- Turn flags (`AssistantTurn.tsx:284-294`): `complete = !isActive && !hasError && hasContent && actionMessage`; `streaming = isActive && summary.streaming`; row gets `streaming` class (`:389`).
- Error card (`shared.tsx:195`): shown unless dismissed; localized `errors.*` (cert variant), code + networkCode, details disclosure default open; actions: Continue (`chat.continueCurrentTaskPrompt` send), Open settings (config errors only), dismiss, copy-error-details.

## 2. TurnOutcomeCard (failed-only)

- Renders ONLY when `result && status !== completed`, no inline error already shown, and visible (non-empty/tool/error) tail exists (`TurnOutcomeCard.tsx:30-46`). Single `failed` style, `role=status`.
- Copy keys: `resultNeedsAttention` / `resultFailedBody` / `resultSteps{count}`; one action `resultContinue` → sends `continueUnfinishedTaskPrompt`.
- Hidden entirely inside readingWindow (`ChatTranscript.tsx:306`).

## 3. Tool card states

- Status (`ToolRow.tsx:189-194`): `status = message.toolStatus` (running / error / denied / success); for Bash, shell exit outranks call status (`runOutcome`). `failed = error || run===failed`.
- Head copy (`ToolRow.tsx:214,280,294`): running → per-action `TOOL_RUNNING_KEYS` (reading/listing/searching/writing/editing/running/fetching/using/delegating/updating); past → `TOOL_ACTION_KEYS` (read/listed/searched/wrote/edited/ran/fetched/used/delegated/updated); Task-topology → `subagentStatus.*` or roster rollup, `subagentCreating` while spawning; run-head → `chat.running / toolFailed / toolDenied / toolCompleted`.
- Head right (`:531-566`): chips always; Bash head shows state pill (`tool-row-state` + dot) with live `role=status`; others show spinner (running) / error icon + `toolFailed` / `toolDenied` text + sr-only live status. Bash head pins command + copy button + outcome in head since body is output-only (`:222,:565`); caret toggle pointer-only (`:566`).
- Body (`:585-591`): `ToolDetailBlocks plain={runHead} streaming=(status===running)`; plain drops headings/frames keeping sr-only channel names (`ToolDetails.tsx:304`). Blocks (`ToolDetails.tsx:21`): content / written / command / stdout / stderr / diff / files / matches / details / notice / error / output / input; each with localized heading + per-block copy (Copy `label`; copy text = code / diff-copy / paths / `path:line: text` / fields / note) (`:37,:62`); overflow via `toolBlockMore{count}` (`:80`). Chips row (`:320`): exit / matches / files / replacements / truncated / scratch / lines / size.
- AskTool history block renders Q/A list with skipped state (`ToolDetails.tsx:97`).
- File/match blocks: workspace-relative paths become preview buttons + file context menu; others plain text (`:133,:168`).

## 4. Action cards — buttons + copy (i18n keys)

- PermissionCard (`:31-106`): `risk-low|medium|high`, queued badge (`permission.queued{count}`), subagent origin (`fromSubagent`), prompt `allowPrompt{tool}`, reason, structured args via `ToolDetailBlocks`, workspace line. Actions: Deny (ghost) / Allow session (secondary) / Allow once (primary); all disabled while resolving; error → toast; focus returns to composer.
- AskToolCard (`:31-166`): multi-question wizard with per-question answered/unanswered/skipped dots (clickable), progress `progress{current,total}` + `queued{count}`; single/multi-select options, custom option + text input, Next auto-skips empty, Skip clears, last Next submits `answers: string[][]|null`; header Decline (all-null submit) / Skip / Next|Submit; resolving disables all, error → toast.
- PlanApprovalBar (`:55-267`): plan|goal share bar, copy under `{kind}.*`. Pending-only action row: Reject + split Approve (main + mode menu ask / acceptEdits / auto: `approveAsk|approveAcceptEdits|approveAuto`, check on current). Approve persists mode; busy disables + `aria-busy`, `approving` label; artifact button opens work-panel tab when present. Mounted in composer dock only when `planCheckpoint.status===pending` (`Composer.tsx:536`); AskToolCard likewise live-only (`:539`) — never inside transcript.
- ReviewChangeCard (`:17-199`): collapsed header button (caret + A/M/D mark + path + +add/−del counts, full accessible label incl. rolledBack); expands to hunk diff or binary / tooLarge / noLineDetails note; actions: Rollback (reversible + localToolsAllowed, `rollingBack` busy) / `rolledBack` check / `rollbackUnavailable` note + Copy diff (`chat.copy`→`chat.copied`, disabled when empty).

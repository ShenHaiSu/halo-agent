# 04 — Search · ReadingWindow Downgrades · Disclosure

> Source: `components/SearchDialog.tsx`, `hooks/use-session-search.ts`, `lib/session-search.ts`, `components/SearchSessionResults.tsx`, `SearchHighlight.tsx`, `lib/transcript-search-context.ts`, `features/chat/transcript/disclosure.tsx`, `SessionPane.tsx:81-93`, `ChatTranscript.tsx:101-327`. Behavior / states / copy only.

## 1. Search UX flow

- Entry: `open` triggers `refreshSessions()` catch-and-ignore + resets `active=0` (`SearchDialog.tsx:105`); query persists in zustand `useSessionSearchState` after close (`use-session-search.ts:8`).
- Debounce: palette command search 80ms (`SearchDialog.tsx:118`); session search 150ms + cancel on query/close change (`use-session-search.ts:19`).
- Flat listbox order: option 0 = New Task, then session rows (+1 per message-match sub-row), LoadMore, pages, settings, commands (`SearchDialog.tsx:177,221,301`). `active` clamped to `optionCount-1`; hover sets active; Enter runs active; arrows move; Escape closes; input scrolled into view (`SearchDialog.tsx:231,239,362`).
- Recents (empty query): first 30 by `updatedAt` desc; untitled drafts excluded; archived hidden until user types (`SearchDialog.tsx:153,157,161,177`). Groups: Today / Yesterday / prev7 / prev30 / Earlier (`SearchDialog.tsx:59,184`).
- Query mode: sessions from controller hits only; scheduled-run transcripts filtered out in both paths (`SearchDialog.tsx:153`; `use-session-search.ts:33`). Message hit navigates via `navigateTranscript({sessionId, messageId, query})` (`SearchDialog.tsx:263`); stale selection guarded by request counter (`SearchDialog.tsx:257`).
- Scope beyond sessions: pages (scheduled/plugins) by localized label match (`:206`); settings via `searchSettings` with dev-mode gate (`:212`); commands via `api.searchCommands` per query (`:119`).

## 2. Search result states

- Controller (`session-search.ts:44-107`): `loading` (query non-empty incl. stale-query), `error` + `retry`, `nextOffset` + `loadMore`. Hook masks hits/error/nextOffset when query moved on (`use-session-search.ts:33`).
- Dialog renders: `search.loading` status row (`:403`); `search.failed` alert + Retry button (`:408`); `Load more` only when `nextOffset !== null` (`:416`); `search.empty` only when all lists empty AND not loading/error (`:520`).
- Row badges (`SearchSessionResults.tsx:58-73`): metadataMatch, N message matches, archived; running dot for live sessions. Message sub-rows: role + timestamp + snippet (`:93`). Highlight via `searchMatchRanges` wrapped in `<mark class="search-hit">` (`SearchHighlight.tsx:4`).
- Transcript targeting: `TranscriptSearchContext {messageId, requestId}` (`transcript-search-context.ts:4`); `useMessageRevealRequest` maps to `revealRequest` at message precision only (`shared.tsx:70`).

## 3. Disclosure collapsed / expanded rules

- Store: per-session `Map<key,{open,revealRequest}>`, explicit choices only, survives pane eviction, never persisted to host (`disclosure.tsx:19,59`). Keyed by `disclosureKey(kind,...ids)` (`:66`).
- Default: `requestedOpen = choice?.open ?? (revealRequest mismatch ? true : automaticOpen)` — pending reveal forces open on first paint (`:92`). Search nav therefore auto-opens target.
- Nesting: `open = requestedOpen && scope.visible`; collapsed parent renders nothing below but preserves child choices; reopen restores user-opened rows (`:99,:50`). Claim propagates upward so opening child keeps ancestors open (`:110`).
- Focus/selection guard: completing (`auto-open true→false`) never closes over focused element or active text selection; pins explicit open instead (`:123`). Manual toggle focuses title when collapsing away from focused body, notifies anchor for reading-position hold (`:133`).
- Whole-turn process (`TurnProcess.tsx:88-113`): `automaticOpen = isActive`; completed starts closed, active starts open, user choice wins; failed-but-still-active stays open. `visibleSteps===0` renders no disclosure.
- Tool rows: `automaticOpen = false` always — never self-open; failure/denial shown in head regardless (`ToolRow.tsx:196`). Body blocks built only when `open && hasDetails && parentVisible` (`:259`).
- Thinking rows: `autoOpen` prop-driven, default closed; `Show thinking` off collapses to compact live indicator only when thinking active, else nothing — stored text kept (`shared.tsx:696`).
- Group rows live one level under turn scope, inherit its visibility (`ProcessActivityGroup.tsx:18,24`).
- Affordance: `DisclosureCollapseRail` left rail + header caret/chevron everywhere (`shared.tsx:653`; `TurnProcess.tsx:170-181`; `ToolRow.tsx:559,587`).

## 4. ReadingWindow downgrades

- Source: `SessionPane` passes `readingWindow={transcript.historical}` (`SessionPane.tsx:81`). Historical pane = search/scroll-back window, not live tail.
- `transcriptRunning = isRunning && !readingWindow` (`ChatTranscript.tsx:101`): kills run-activity / planning / working indicators (`:173,:193,:319`); tail forced `isActive=false` (`:292`); live delegation collection frozen (`:162`).
- Hidden in window: `TurnOutcomeCard` entirely (`:306`). Downgraded via undefined/false props: `pendingPermission`, `askPending`, `planningState` (`SessionPane.tsx:88-91`) — no permission / ask / plan actions against old messages.
- Scroll handler pins unpinned + forces jump-to-latest affordance (`useTranscriptScroll.ts:405`; `ChatTranscript.tsx:363`). Window projection uses full history length (`useTranscriptScroll.ts:592`).
- Exception: copy-conversation reads full session via API without disturbing window (`ChatTranscript.tsx:201`).
- Markdown has NO details/collapsible handling — only `useCopy` (1500ms `copied` flip), code-block + diagram-source copy buttons (`Markdown.tsx:113,238,299`).

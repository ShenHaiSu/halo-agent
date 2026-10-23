# 01 — Transcript History vs Tail · Follow / Free-Read · ShowJump

> Source: pi-desktop `apps/desktop/src/features/chat/transcript/*`, `lib/transcript-*.ts`, `components/ChatTranscript.tsx`, `SessionPane.tsx`, `ChatSurface.tsx`, `styles/chat-shell.css`. Behavior / thresholds / transitions / copy only.

## 1. History vs Tail

- Projection: `getTranscriptProjection(messages, compactions)` → `{ entries, visible, history }` (`lib/transcript-projection.ts:18-22`; `hooks/useTranscriptScroll.ts:515-526`). `tailEntry = entries.at(-1)`; history = all but tail.
- Render split (`ChatTranscript.tsx:287-295`, `AssistantTurn.tsx:184-240`):
  - `TranscriptHistory entries={history} isActive={false}` — memo, bails on identity / `transcriptEntryEqual`; does not re-render on stream.
  - `TranscriptTail entry={tail} isActive={transcriptRunning && kind==="assistant-turn"}` — only live part re-renders.
  - `transcriptRunning = isRunning && !readingWindow` (`ChatTranscript.tsx:101`).
- Mounted window (`lib/transcript-window.ts:22-28`): `INITIAL_MOUNT=15`, `WINDOW_MIN=60`, `WINDOW_STEP=40`. First commit mounts trailing `min(len,15)` + `transcript-hydration-spacer min-height:100vh` (`chat-shell.css:300-302`); expands to full window next frame, re-bottoms only if still pinned (`useTranscriptScroll.ts:544-637`).
- Deferred render: `useDeferredValue` for history except when `readingWindow || firstCommit || paneRevealed` (`useTranscriptScroll.ts:520-521`).
- Top escalation, 2-stage (`useTranscriptScroll.ts:338-372`): near-top scroll/boundary → first `growTranscriptWindow(+40)`; only when window covers all loaded history → `loadOlder()` fetch. Prepend anchors viewport via `scrollTop += scrollHeight delta` in layout phase.
- Reading window (`hooks/use-transcript-view.ts:6-23`, `runtime/transcript-reading-runtime.ts`): `transcriptViews[session]` gives `{ hasMoreBefore/After, focus, historical, loading }`. `SessionPane.tsx:30-93` passes `readingWindow / onRestoreReading / onLoadAfter / onReturnToLatest`. While `readingWindow`: scroll handler forces `pinned=false, showJump=true` (`useTranscriptScroll.ts:405-410`); tail cards / permission / planning suppressed (`ChatTranscript.tsx:306-327`).

## 2. Follow / Free-Read state machine

- States: `pinnedRef:boolean` (follow) + `showJump:boolean`. Pure reducer `reduceTranscriptScroll` → `{ distanceFromBottom, movedUp/Down, releasedFollow, pinned, showJump:!pinned }` (`lib/transcript-scroll.ts:66-104`).
- Mount: `pinned = saved?.pinned ?? true`, `showJump = !pinned`, bottom if pinned (`useTranscriptScroll.ts:221-227`).
- Transitions:
  - Send / retry / regenerate (`isRunning` rising edge) → cancel holds, `pinned=true, showJump=false`, `scrollToBottom()` in layout phase (`useTranscriptScroll.ts:457-478`).
  - New message / stream commit / ResizeObserver growth while pinned → `scrollToBottom()` in observer callback pre-paint (`useTranscriptScroll.ts:291-320,480-508`).
  - `releaseFollow()` (minimap jump, disclosure open, search nav) → `pinned=false, showJump=true` (`useTranscriptScroll.ts:152-176,606-613`).
  - `jumpToLatest()` → `pinned=true, showJump=false`, `scrollTo({top, smooth|auto})` + `onReturnToLatest()` clears `transcriptViews[session]` (`useTranscriptScroll.ts:712-722`; `transcript-reading-runtime.ts:52-56`).
  - Disclosure hold (#324): opening tool / thinking / activity title calls `releaseFollow` then holds title position; held position wins over queued follow frames until real input releases it (`useTranscriptScroll.ts:159-176`; `hooks/use-disclosure-anchor.ts:28-32`).
  - Hidden pane: never follow / page / sample settle while `!paneVisible` (`useTranscriptScroll.ts:286-301,646,735-739`).
  - Pane hide/show preserves `lastLaidOutScrollTop` (content-visibility guard) + `transcriptRevealOwnership: reading-position|pinned|retained` (`useTranscriptScroll.ts:229-284`).

## 3. Thresholds

| Constant | Value | Use |
|---|---|---|
| `REPIN_THRESHOLD_PX` | 48 | near-bottom re-pin (`lib/transcript-scroll.ts:1-53`) |
| `ROUNDING_TOLERANCE_PX` | 1 (0 on gesture path) | 1px scroll unpins on gesture; slack otherwise |
| `HISTORY_REVEAL_THRESHOLD_PX` | 120 | top band; mirrors IO `rootMargin:120px 0 0 0` |
| `GESTURE_WINDOW_MS` | 200 | `isRecentScrollGesture` window |
| overflow epsilon | `scrollHeight-clientHeight>1` | overflow gate (`ConversationMinimap.tsx:33`) |
| `TRANSCRIPT_PAGE_SIZE` | 100 | paging (`lib/transcript-reading.ts:32-34`) |
| `SEARCH_PAGE_SIZE` | 60 | search paging |
| `CONTENT_LIMIT` | 64K | transcript content cap |

- Gesture attribution (`lib/transcript-scroll.ts:126-203`; `useTranscriptScroll.ts:186-217`): `wheel/touchstart/touchmove/pointerdown/keydown` on wrap → `readScrollInputContext` + `isScrollGestureInput`; keys `ArrowUp/Down, PageUp/Down, Home, End, Space`; pointer must be on scroll surface; editable fields / nested-owner-consumed gestures excluded.

## 4. ShowJump (jump-to-latest) UI

- Visible when `(showJump || readingWindow) && !veilCovering` (`ChatTranscript.tsx:363-375`).
- Style: `.jump-latest-btn` 32px circle, `IconArrowDown size=14`, bottom `composer-dock-height+12px`, `jump-latest-in` animation (`chat-shell.css:242-280`). `TooltipButton`.
- Click = `onReturnToLatest?.(); jumpToLatest()`. Context-menu background also offers Return-to-latest (`ChatTranscript.tsx:216-234`).
- `hasMoreAfter` → `chat.loadLaterMessages` button (`ChatTranscript.tsx:296-305`); top `role=status aria-live=polite` boundary row while loading earlier (`ChatTranscript.tsx:271-279`).

## 5. Copy text (i18n keys)

- `chat.scrollToBottom` — jump tooltip + aria-label.
- `chat.loadingEarlierMessages` — top boundary row + minimap earlier-dash busy label.
- `chat.showEarlierMessages` — minimap earlier-dash idle label.
- `chat.loadLaterMessages` — hasMoreAfter button.
- `chat.loadingSession` — settle veil aria-label / aria-busy + `transcript-navigation-loading role=status`.

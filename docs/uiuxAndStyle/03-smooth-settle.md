# 03 — Smooth Streaming Text + Settle Veil

> Source: `hooks/useSmoothText.ts`, `features/chat/transcript/AssistantTurnParts.tsx:13-32`, `lib/render-content-limits.ts`, `lib/transcript-settle.ts`, `hooks/useTranscriptScroll.ts:639-677`, `ChatTranscript.tsx:243-262,330-362`, `styles/chat-shell.css:304-320`. Behavior / thresholds / copy only.

## 1. Smooth-text gate

- Enabled = `settings.smoothStreaming !== false && !prefersReducedMotion`; per-bubble `streaming = isActive && message.status === "streaming"` (`AssistantTurnParts.tsx:17-25`). Toggle UI: `settings.smoothStreaming` (`SettingsPage.tsx:576-582`).
- Bypass when `source.length > MAX_SMOOTH_TEXT_CODE_UNITS = 32*1024` → return full source (`useSmoothText.ts:19-22`; `render-content-limits.ts:4`).
- Related caps (`render-content-limits.ts:2-8`): `MAX_STREAMING_MARKDOWN_TAIL=32K`, `MAX_SYNC_MARKDOWN=128K`, `MAX_RENDERED_TEXT_PAGE=32K`, highlight `100K / 800 lines / 2000 per line`.
- Reduced-motion → `behavior:"auto"`, no smoothing.

## 2. Reveal loop

- rAF at most 60Hz (`elapsed < 1000/60` skip); `dt = min(elapsed,100)/1000` (`useSmoothText.ts:44-122`).
- `base = 60 chars/s`, `maxLag = 30 chars`; if `backlog > 30`, `speed = backlog/0.5` (clears in 500ms); fractional carry; UTF-16 surrogate-pair guard; flush full text on disable / complete / unmount.
- Cursor: `showCursor = streaming && enabled && displayContent` → `.smooth-cursor` (`AssistantTurnParts.tsx:22-25`).

## 3. Settle veil (bounded first commits only)

- Phases `covering → leaving → off`; `veilCovering` blocks minimap mount and jump pill (`ChatTranscript.tsx:243-260,330-359`; `chat-shell.css:304-320`).
- Veil: `position:absolute inset:0`, opaque skeleton, `pointer-events:auto`, sits above scroller but below composer so typing still works; `data-transcript-settling` on wrap.
- Detector: sample `{scrollHeight, clientHeight}` each rAF from expansion commit; settled when identical for `STABLE_FRAMES=3` or `MAX_MS=600`; fade `VEIL_FADE_MS=160` (matches CSS transition) (`transcript-settle.ts:18-25,52-74`; `useTranscriptScroll.ts:639-677`).
- Each sample re-pins if still pinned so revealed frame is at newest turn. Short transcripts never veil (`hydrationBounded=false → "off"`).
- `navigationLoading` shows separate `transcript-navigation-loading` status (`ChatTranscript.tsx:360-362`).

## 4. Reading-position alignment

- Bookmark restore holds up to `1500ms` (`alignmentUntilRef = now+1500`), re-aligns on ResizeObserver until timer / cancel on real input, nav, new turn (`hooks/use-transcript-reading-position.ts:91-117`).
- `transcriptRevealOwnership: reading-position | pinned | retained` (`lib/transcript-reading-position.ts:31-45`).

## 5. Copy text (i18n keys)

- `chat.loadingSession` — veil `aria-label` + `aria-busy`, and navigation-loading `role=status`.
- `chat.loadingEarlierMessages` — top boundary row during `loadOlder`.

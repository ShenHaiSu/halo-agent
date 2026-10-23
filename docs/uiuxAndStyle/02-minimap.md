# 02 — Conversation Minimap

> Source: `apps/desktop/src/components/ConversationMinimap.tsx`, `lib/conversation-minimap.ts`, `styles/chat-shell.css:520-646`, `features/chat/transcript/hooks/useTranscriptScroll.ts:679-710`, `ChatTranscript.tsx:245-260`, `test/conversation-minimap.test.mjs`. Behavior / thresholds / copy only.

## 1. What it visualizes

- Codex-style packed vertical dash stack on thread left edge, one marker per turn: one per user message + one merged marker per assistant response (`ConversationMinimap.tsx:20-24`; `conversation-minimap.ts:34-39`).
- Role encoded by dash width: user 13px, assistant 8px; earlier-history marker 13px dashed / repeating-linear-gradient (`chat-shell.css:568-587`).
- Assistant fragments within a turn merge into one marker anchored at first contentful fragment; empty / thinking-only / tool roles produce no marker; preview capped at 280 chars joined with blank line (`conversation-minimap.ts:41-76`, const `:10`).
- Hover popover: role header + up-to-5-line clamped text preview, 248px card at `left:34px`, `pointer-events:none` (`tsx:421-432`; `css:616-646`).

## 2. Placement / layout

- Absolute rail: `left:8px`, top = toolbar height → bottom above composer dock, width 26px, vertically centered stack; gap shrinks with marker count via `--minimap-marker-count` (`css:525-543`; `tsx:376-380`).
- Dashes grow horizontally only (`--magnify` scales width), magnification never reflows stack (`tsx:26-28,291-294`; `css:520-524,558-566`).

## 3. Interaction — click only, no drag

- No drag / scrollbar-drag: no pointer/drag handlers. Only click, hover (mousemove→rAF), focus/blur.
- Click dash → `scrollTo(target.offset - 24px)` after `onReleaseFollow()` so follow doesn't re-bottom; honors `prefers-reduced-motion` (`tsx:270-289`).
- Top dashed history marker (when `hasEarlier`) → `onRevealEarlier`: scrolls to top / `reachTop` (`tsx:384-398`; `useTranscriptScroll.ts:694-710`).
- Loading history marker: disabled + `aria-busy` + progress cursor, dimmed (`tsx:391-396`; `css:604-610`).

## 4. Hover / focus / magnification

- macOS-Dock cosine falloff: `MAGNIFY_RADIUS=46px`, peak `BOOST=1.3×` (`tsx:27-28,295-317`). Applied imperatively via `--magnify`; width transition 90ms (`css:564-565`).
- Popover snap: nearest dash within `POPOVER_SNAP=24px` of cursor (`tsx:30,312`). Clamped: `top = clamp(center-36, 0, railHeight-132)` (`tsx:322-325`; `POPOVER_HEIGHT=132` at `:31`).
- Keyboard: `focus` shows popover anchored at `offsetTop-36`; `blur` hides (`tsx:411-417`). Hover colors dash to secondary (`css:589-602`).

## 5. Active viewport indicator

- Active = last marker whose transcript offset ≤ `scrollTop + 30% viewport height`, binary search over cached offsets (`tsx:150-175`, anchor `:161`).
- Styled full-primary vs 14%-primary idle (`css:558-566,612-614`); `aria-current="true"` on active button (`tsx:406-410`).
- Offsets cached, recomputed on resize / marker change / content resize, not per scroll frame (`tsx:88-107,222-268`).

## 6. Show / hide gates

- Render gate: `hasEarlier OR (≥2 markers AND scroller overflows)` (`conversation-minimap.ts:12-22`). Overflow = `scrollHeight-clientHeight > 1px` (`tsx:33,177-192`).
- Mount gate: only when `paneVisible && !veilCovering` — hidden panes / settling veils would cache junk offsets (`ChatTranscript.tsx:245-260`; `SessionPane.tsx:92`).
- Data scope: markers built from mounted rows only (bounded window), not all loaded messages, so every dash resolves to a node (`useTranscriptScroll.ts:679-691`).
- Rail height tracks composer dock height via ResizeObserver; dash centers re-measured on rail resize (`tsx:200-220`).
- Edge: 0–1 markers without earlier history → hidden even if overflowing; earlier history alone (0 markers) → rail with only history dash (`test:21-60`).

## 7. Accessibility / copy (i18n keys)

- Rail: `<nav aria-label={t("chat.minimap")}>` (`tsx:371-375`).
- Dashes: `aria-label = t("chat.userMessage") / t("chat.assistantMessage")` (`tsx:365-366,409`).
- History dash: tooltip + aria-label `t("chat.showEarlierMessages")`, or `t("chat.loadingEarlierMessages")` while loading (`tsx:367-369,392-393`).
- Popover: `role="tooltip"`; role header repeats user/assistant label (`tsx:421-431`).

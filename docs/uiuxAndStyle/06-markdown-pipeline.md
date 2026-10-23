# 06 — Markdown Block Pipeline · Shiki / Mermaid Gates · Overflow Limits

> Source: `components/Markdown.tsx`, `lib/markdown-blocks.ts`, `lib/render-content-limits.ts`, `lib/shiki.ts`, `lib/mermaid.ts`, `lib/block-renderer.ts`, `components/MarkdownTable.tsx`, `LargeTextPreview.tsx`, `ToolDetails.tsx:209-223`, `styles/prose.css`, `markdown-table.css`. Behavior / thresholds / copy only.

## 1. Block pipeline

- Remark: `remarkGfm + remarkMath` (`Markdown.tsx:843-846`, `markdown-blocks.ts:33-34`); raw HTML via rehypeRaw then sanitize + KaTeX (`:870`). Component map: pre / code / a / img / audio / video / table (`:829-837`).
- Tables wrapped in `.table-wrap` + `MarkdownTable` (`:783-799`). Audio/video players (`:801-827`).
- Inline single-line code without class → workspace-file / URL / session chip button opening work panel (`:517-572`). Local images resolve to bounded data URL else chip fallback; remote images inline + click to browser tab (`:699-781`).
- Plugin fence `pluginId:lang` via `blockRenderer` slot with host CodeBlock fallback (`:477-503`, `block-renderer.ts:26-28`).
- Splitting: unified/remark-parse block splitter slices at top-level node offsets, whitespace attached to preceding block (`markdown-blocks.ts:59-99`). Streaming reuses settled prefix, re-parses tail only; doc with link/footnote definition stays undivided `singleUnit`, latched one-way (`:166-214`, `Markdown.tsx:878-888`). Context provides `closedFence / renderDiagrams / originalRaw` (`:425-429,905-912`).

## 2. Shiki highlight gate

- JS regex engine, lazy per-language import, plain-text until `readyLangs` + version notify (`shiki.ts:12-23,194-221`).
- `resolveLang` null for plain/text/txt/plaintext/ansi or unknown (`:170-177`); `useHighlightedTokens` null → plain text (`Markdown.tsx:182-234`).
- `tokenizeIncremental` per-line cache + GrammarState chaining, cache-hit returns same ref (`shiki.ts:234-297`).
- Hard limits pre-tokenize via `inspectHighlightLimits`: length 100K, 800 lines, 2000/line (`render-content-limits.ts:5-7,61-98`; `shiki.ts:253-257`).
- Themes one-light / one-dark-pro by `data-theme` (`shiki.ts:25-31`; `Markdown.tsx:148-154`). ~50 langs incl. mermaid/mmd alias (`shiki.ts:39-133`).

## 3. Mermaid gate

- Only closed ```mermaid fence when `renderDiagrams` true (`Markdown.tsx:482-488`; `isClosedFencedCodeBlock` in `mermaid.ts:25-42`).
- Pre-check length > 20000 → too-large without render (`Markdown.tsx:307-311`; `mermaid.ts:1,53-55`). Serialized queue, strict + DOMPurify svg-only sanitize, must return `<svg` root (`mermaid.ts:15-23,57-112`).
- Lazy nearViewport 240px margin via IntersectionObserver (`Markdown.tsx:261-282,306`).
- States: loading skeleton (3 dots, `aria-busy`); svg `role=img`; error `role=status` diagramTooLarge / diagramUnavailable; source toggle showDiagram / showDiagramSource; copyDiagramSource with 1.5s copied (`:284-421`; `prose.css:559-630`). Theme re-render on light/dark (`:336`).

## 4. Overflow limits

| Gate | Value | Fallback |
|---|---|---|
| full source | >128KB `MAX_SYNC_MARKDOWN` | plain `<pre>` `markdownPlainTextFallback` (`:987-1025`) |
| streaming tail | >32KB `MAX_STREAMING_MARKDOWN_TAIL` | that tail block plain fallback (`:988-1043`) |
| smooth text | >32KB | no smoothing, full source |
| tool code pages | 32KB/page `MAX_RENDERED_TEXT_PAGE` UTF-16-safe | Prev/Next + count, `followLatest` for live output (`render-content-limits.ts:8-43`; `LargeTextPreview.tsx:19-73`) |
| plugin block | height >4000px | host fallback (`block-renderer.ts:9-19`) |

- Code-block chrome: header lang + copy btn, hover/focus reveals, copied check 1.5s (`Markdown.tsx:113-125,236-259`; `prose.css:442-528`). `pre` overflow-x auto, `.table-wrap` overflow-x auto (`prose.css:342-349,374-378`). KaTeX display scroll-x (`:423-428`).
- Tables: toolbar copy-markdown (toast), export table.csv via blob + 1s revoke, expand to modal `TablePreview` dialog with focus trap / Esc / backdrop / body-scroll lock (`MarkdownTable.tsx:9-152`; `markdown-table.css:8-13,31`).
- Transcript assistant Markdown wrapped `prose-chat`, streaming flag controls tail limit (`AssistantTurnParts.tsx:13-28`). ToolDetails code uses `LargeTextPreview` + `HighlightedCode` per page (`ToolDetails.tsx:213-223`).
- Sanitized schema allows only audio/video/source + math-inline/display classes (`Markdown.tsx:848-868`).

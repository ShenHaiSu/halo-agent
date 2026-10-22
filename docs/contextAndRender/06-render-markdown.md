# 高性能渲染（下）：流式 Markdown 与重活隔离

源码：`apps/desktop/src/lib/markdown-blocks.ts`，
`components/Markdown.tsx`，`lib/shiki.ts`，
`lib/mermaid.ts`，`lib/latex-math.ts`，
`lib/render-content-limits.ts`，`lib/render-diagnostics.ts`。

## 1. `advanceMarkdownBlocks`：只重解析 tail

`markdown-blocks.ts:166-214`，渲染文法即切分文法（`unified + remark-parse + remark-gfm + remark-math`，与 `ReactMarkdown` 同一份，避免第二套文法分歧）：

```ts
appended = blocks.length>0 && source.startsWith(consumed)
if (appended && cache.singleUnit) return {consumed: source, blocks:[source]} // 定义锁存单向
stable = cache.blocks.slice(0, -1)       // 除最后一块全保留
tail = source.slice(lastStart)           // 只重切尾巴
if (tail > maxTailCodeUnits) return {blocks:[...stable, tail]} // 超长尾 raw 直出
split = parseMarkdownBlocks(tail)
```

- 正常多块流式每帧只 parse 短 tail（约 0.1ms/帧）；唯一二次形状是“单个超长未闭合 fence”，46KB 约 2.4ms/帧（文件头注释实测）。
- 含 link/footnote definition 的源退化 `singleUnit`（整源一个块）：definition 跨 slice 解析 + footnote 全局编号，强行切分会 Classic 错误（`[^1]` 变字面量）。正确优先于速度，且锁存单向（变回段落也不再拆，避免每帧全量 parse 抢回切分）。
- 切片按原文 bytes 切（含 CRLF/缩进，L82-98），`slices.join == source`，`data-source-*` 偏移永远对得上。

`Markdown.tsx :: useBlocks`（L878-888）：`useRef(cache) + useMemo([source])` 驱动，`cache.blocks` 即渲染块数组。

## 2. `Block memo`： settled 块零重渲染

`Markdown.tsx:890-953 :: Block = memo(...)`，props `raw/originalRaw/sourceOffset/renderDiagrams/workspaceRoot/baseDir`：

- `raw` 不变则 memo 命中，`ReactMarkdown` 不跑，`remark/rehype` 全跳过。流式只有最后一块 `raw` 在变。
- 每个 Block 包 `Profiler onRender → recordRenderDiagnostic`，`beginRenderDiagnostic("markdown-split"/"markdown-normalize")` 度量切分/归一化耗时，长会话可定位掉帧块。
- 上限（`render-content-limits.ts`）：`MAX_STREAMING_MARKDOWN_TAIL_CODE_UNITS`（流）/ `MAX_SYNC_MARKDOWN_CODE_UNITS`（静态）。超限块降级纯文本 `<pre>` + 状态提示，不再进 markdown 管道；整源超限直接空渲染 + fallback。

`normalizeLatexMathDelimiters` 长度保持（L991-993），归一化不移动偏移，bracket-display 插件仍能定位原文 TeX。

## 3. Shiki：增量 token + 行 memo

`Markdown.tsx:156-234`：

- `useHighlightedTokens(code, lang)`：`resolveLang → ensureLang(lazy) → tokenizeIncremental(cacheRef.current, code, ...)`。`cacheRef` 行级缓存，settled 行按引用复用，`TokenLine = memo`（L170-180）直接跳过。
- `useSyncExternalStore(subscribeHighlighter, getHighlightVersion)`：语言包懒加载完成后触发一次重跑（`version` 依赖），之前先原文直出，不阻塞流。
- 主题跟随 `documentElement[data-theme]` 经 `MutationObserver + useSyncExternalStore`（L129-154），明暗切换只重跑 token 颜色，不重切块。

## 4. Mermaid/KaTeX：视口门控 + 失败隔离

`MermaidBlock`（L284-421）：

- `useNearViewport(rootRef)`：`IntersectionObserver rootMargin 240px`，进视口前不 `renderMermaidSvg`。`code.length > MAX_MERMAID_SOURCE_LENGTH` 直接 `too-large`，不调渲染器。
- `renderedSourceRef` 记已渲染源，`code` 不变不重渲；主题变才重渲；`active` 标志防异步竞态（切块后晚到的 svg 不覆盖）。
- 失败态（invalid/too-large）切源码 `<pre>` 展示 + `role=status`，不炸整树。`loading` 骨架三圆点，`svg refreshing` 态保留旧图。
- KaTeX 经 `rehype-katex` + `remark-math`，sanitize schema 显式放行 `math-inline/math-display`（L852-862），否则 `\[...\]` 单行/段中公式退化行内。

## 5. 安全与外链：rehype 管道

`rehypePlugins = [rehypeRaw, [rehypeSanitize, schema], rehypeKatex]`（L870）：先 raw 再消毒再套公式。schema 在 default 上只加 `audio/video/source + img/code` 最小增量。本地图片不走 renderer origin：host 解析成 bounded dataURL 内联（`MarkdownImage` + `useReferencedImageDataUrl`），缺失/超大回退 chip。外链点击走 `openHttpUrl` / workpanel，`target=_blank + noopener`。

## 复刻清单

1. 切分器与渲染器共用同一 remark 文法；append 只重切 tail；definition 源退化整块。
2. 块级 `memo(raw)` + traveling `sourceOffset`，settled 块零重跑。
3. 代码高亮行缓存 + 语言懒加载 + 主题订阅三件套。
4. mermaid 进视口才渲染 + 长度上限 + 失败回源码。
5. 流/静双上限 + 超限纯文本降级，保证最坏情况不卡死。
## 6. 增补：行比较器 / 内容上限 / 渲染块（子代理调研回填）

- 行比较器（`AssistantTurn.tsx:68-240`）：`assistantTurnPropsEqual / transcriptEntryEqual`，`TranscriptHistory` 自定义比较器（isRunning / liveDelegations / 逐行 entry 比较）与 `TranscriptTail` 分离，历史行排除 streaming 协调路径；key = `message.id / turn.id / mark.id`（`partKey = message.id + activity 首 row`）；`AssistantTurnParts.tsx` `SmoothMessageBubble（useSmoothText + rAF）` + `TurnPartBlock memo` + `useRenderBlocks`。
- 内容上限（`lib/render-content-limits.ts:2-98`）：128KB sync / 32KB 流 tail / 32KB smooth-text / 100k highlight / 800 行 × 2000 列 + `renderedTextPage` 分页，超限逐级降级（tail 原文 → 全文 pre），最坏情况不卡死。
- 渲染块（`transcript/render-blocks.ts`）：`TRANSCRIPT_RENDER_BLOCK_SIZE = 64`，`reconcileRenderBlocks / useRenderBlocks` 稳定成员、无 prepend remount；`lib/transcript-summary.ts` WeakMap contentFacts + turn-summary 缓存（不重扫旧文本）；`MessageRow.tsx:73-130` memo + runtime-chunk 直渲 + `useMemo` 切分。
- Shiki 真相（`lib/shiki.ts:12-297`）：单例 core + JS 引擎，语言按需动态 import、未就绪纯文本 fallback、`subscribe/notify` + `useSyncExternalStore(version)` 就绪重跑；`tokenizeIncremental` 按行复用 GrammarState 链，首分歧后仅重分词 tail 行。
- Mermaid 真相（`lib/mermaid.ts`）：20k 源码 / 500 边上限，全局配置串行队列（mermaid 全局配置互斥），闭合 fence 门控，动态 import + DOMPurify strict sanitize + `<svg` 根二次校验。

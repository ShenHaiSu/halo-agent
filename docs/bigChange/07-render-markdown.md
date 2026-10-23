# 07 流式 Markdown 与重活隔离（P2-2）

第二参考：`contextAndRender/06-render-markdown.md`。本阶段替换渲染管线，破坏性。

## 1. 前置检查

- [ ] 通读 `markdownRender.ts`：确认当前整源 `md.render` + 整源 `blockHash` 缓存 + 流式纯 `escapeHtml`，无块级 memo、无 tail 切分。
- [ ] 确认依赖只有 `markdown-it + hljs`，无 shiki/mermaid/katex；确认 `html:false` 与外链 `target=_blank` 现状。
- [ ] 06 已合入：`TranscriptTail` 已隔离出 tail 文本，本阶段只重解析 tail。

## 2. 阶段目标

- 切分器与渲染器同一文法；append 只重切 tail；definition 源退化整块。
- settled 块零重渲染；超限逐级降级，最坏不卡死。
- 高亮/图/公式全部懒加载 + 视口门控 + 行级缓存。

## 3. 实现办法

### 3.1 切分 `markdownBlocks.ts`（新建，`features/session/utils/`）

- 与渲染同一 `markdown-it` 实例（GFM 插件与渲染共用，不开第二套文法）。
- `advanceBlocks(cache, source)`：`appended = source.startsWith(consumed)`；正常只 `slice(lastStart)` 重切 tail；除末块全保留。
- 含 link/footnote definition 退化 `singleUnit`（整源一块，锁存单向）；切片按原文 bytes 切（含 CRLF/缩进），`slices.join == source`。
- `useBlocks(source)`：`ref(cache) + computed` 驱动，`cache.blocks` 即渲染数组。

### 3.2 渲染 `MarkdownBlock.vue`（新建，替换 `renderMarkdown` 整源调用）

- `Block memo(props: raw/sourceOffset)`：`raw` 不变跳过；`ReactMarkdown`→`markdown-it` 全跳过；`Profiler` 计 `split/normalize` 耗时。
- `normalizeLatexDelimiters` 长度保持，不移动偏移。
- 上限（`renderContentLimits.ts` 新建）：sync 128KB / 流 tail 32KB / smooth-text 32KB / 高亮 100k / 800 行×2000 列；超限块降级 `<pre>` + 提示；整源超限空渲染 + fallback。

### 3.3 高亮与重活

- hljs 二选一（本阶段必须定，不兼容旧同步高亮）：
  - A（推荐）：换 `shiki` 单例 + 按语言动态 import + `tokenizeIncremental` 行复用 GrammarState + `useHighlighterVersion` 就绪重跑 + 主题 MutationObserver 只重跑颜色。
  - B（妥协）：保留 hljs 但加行缓存 + 语言懒加载 + 未就绪纯文本 fallback。性能约为 A 的 60%。
- `MermaidBlock`：`IntersectionObserver rootMargin 240px` 进视口才 render；`code.length > 20k / 边 >500` 直接 too-large 回源码；`renderedSourceRef` 去重 + `active` 防竞态 + 失败回 `<pre> + role=status`；mermaid 全局串行队列 + DOMPurify + `<svg` 根校验。
- KaTeX（如需）：rehype 等价物按白名单放行 `math-inline/display`，否则退化行内。
- 安全：先 raw 再 sanitize 再公式；schema 最小增量（`audio/video/source/img/code`）；本地图转 bounded dataURL，缺失/超大回 chip；外链走 `openHttpUrl`，`target=_blank + noopener`。

## 4. 验收标准

- [ ] 性能：正常多块流式每帧只 parse 短 tail（median ≤1ms）；46KB 未闭合 fence ≤2.5ms/帧；settled 块零重跑。
- [ ] 正确：`[^1]` footnote 不变字面量；definition 跨 slice 正确；归一化不移动偏移。
- [ ] 行为：mermaid 未进视口零渲染；超限源降级 pre 且有提示；失败不炸整树。
- [ ] 旧 `renderMarkdown` 整源语义删除，调用点全部换 `useBlocks + Block`（破坏性）；`MD_CACHE_MAX=200` 整源缓存删除。
- [ ] 与 06 联调：200 轮会话流式无掉帧，tail 帧预算内。

## 5. 风险

- 换 shiki 首包变大、首屏高亮闪一下（先纯文本后重跑），允许。mermaid 新增依赖体积，允许。

# 13 Markdown 门控 + 代码表图 + 溢出降级（P2-7）

第二参考：`uiuxAndStyle/06-markdown-pipeline.md`。前置：07 块管线已合入（本阶段为 UX 门控层）。

## 1. 前置检查

- [ ] 确认 07 已有：`markdownBlocks advanceBlocks/singleUnit + Block memo + renderContentLimits + 长度保持 normalize`。
- [ ] 确认缺失：shiki/mermaid/表格/音视频/插件 fence/文件 chip（当前只有 `markdown-it+hljs+html:false`）。
- [ ] 定 shiki 路线（见 07 二选一），本阶段按定案实现门控。

## 2. 阶段目标

- 同文法渲染：remark（GFM+math）切分 = 渲染；sanitize 白名单最小增量。
- 高亮/图/表/音视频/插件全部门控 + 懒加载 + 失败回退。
- 溢出逐级降级，永不卡死整页。

## 3. 实现办法

### 3.1 管线（改 `MarkdownBlock`）

- 插件：`remarkGfm + remarkMath`；`rehypeRaw → sanitize → KaTeX`；组件映射 `pre/code/a/img/audio/video/table`。
- 表格包 `.table-wrap + MarkdownTable`；音视频播放器；`pluginId:lang` fence 走 `blockRenderer` 槽 + 宿主 CodeBlock 回退（高 >4000px 回退宿主）。
- 行内无类名单行 code → workspace 文件/URL/会话 chip 钮开 work-panel；本地图 bounded dataURL 否则 chip；远图内联 + 点开浏览器 tab。
- 切分：顶层节点偏移切，空白归前块；流式复 settled 前缀只重切 tail；definition 源 `singleUnit` 单向锁存；context 透 `closedFence/renderDiagrams/originalRaw`。

### 3.2 高亮门

- shiki 单例 + JS 正则引擎 + 按语懒 import；`resolveLang` 碰 `plain/text/txt/ansi`/未知 → null 走纯文本；未就绪纯文本 + 版本 notify 重跑。
- `tokenizeIncremental` 行缓存 + GrammarState 链，命中返同引用；预检 `>100K / >800 行 / >2000/行` 直接纯文本。
- 主题 `data-theme` 切 light/dark 只重跑颜色。

### 3.3 图门

- 仅闭合 mermaid fence + `renderDiagrams=true` 才渲；预检 `>20000ch` 直接 too-large。
- 串行队列 + 严格配置 + DOMPurify svg-only + 必 `<svg` 根；懒加载 `IntersectionObserver rootMargin 240px`。
- 态：loading 三点骨架 `aria-busy` / svg `role=img` / 错 `role=status`（tooLarge/unavailable）/ 源码切换 showDiagram/showSource / 复制 1.5s flipped；主题变重渲。

### 3.4 溢出与 chrome

- `>128KB` 整源 → `<pre>` 纯文本；流 tail `>32KB` → 该块纯文本；平滑 `>32KB` 见 10；工具代码按 32KB 分页 Prev/Next + 计数 + live `followLatest`（UTF-16 安全）。
- 代码头：lang + 复制钮（hover/focus 现，copied 1.5s）；`pre/.table-wrap`/KaTeX-display 横滚；表格工具条 复制 markdown（toast）/ 导出 csv（blob + 1s revoke）/ 展开 modal（焦点陷阱/Esc/背景锁滚）。
- assistant 用 `prose-chat` 包裹 + streaming 尾限；ToolDetails 代码走分页 + 高亮每页。
- sanitize 仅放 `audio/video/source + math-inline/display` 类。

## 4. 验收标准

- [ ] 同源：definition 跨 slice 正确；footnote 不变字面；normalize 不移位。
- [ ] 门控：mermaid 未进视口零渲染；超限/失败全回退且有状态；主题切不重切块。
- [ ] chrome：复制/导出/分页/展开/modal 全可用；失败不炸树。
- [ ] 旧 `hljs` 同步全量 + 整源 `html:false` 一刀切删除（破坏性，按定案替换）。
- [ ] 与 10 联调：200 轮流式 median ≤1ms/帧，46KB 未闭合 fence ≤2.5ms。

## 5. 风险

- shiki 首包 + 语言包体积；mermaid 新增依赖；KaTeX 字体。允许体积涨，不做兼容旧包体积。

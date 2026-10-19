# 02 · 会话消息 Markdown 排版补齐方案（列表/表格/分割线/标题）

## 1. 现状（已读代码）

- 渲染：`MessageTimeline.vue` L158-172 `new MarkdownIt({ html:false, linkify:true, breaks:true, highlight })` + `v-html="renderMarkdown(...)"`，容器类 `markdown-body`（持久 L44-48 / 流式 L103-107）。
- 样式：`main.css` L130-137 仅覆盖 `pre/code/hljs`，`MessageTimeline.vue` scoped L216-228 仅覆盖 `pre/p(margin 4px)`。
- 结果：`markdown-it` 输出的 `ul/ol/li/table/thead/hr/h1-h6/blockquote/a` 全部裸奔——Tailwind preflight 已把它们 reset（`list-style:none/margin:0/table-border:0/h1字号继承`），又无人补回排版，所以列表无符号、表格无线、标题不分级、分割线挤成细缝。

## 2. 设计目标

1. GitHub 观感的子集（够用、克制，与 IDE 暗黑视觉一致），不引入 `github-markdown-css` 整包（体积 + Win7 Cef109 兼容风险）。
2. 暗亮同源：所有颜色走 `main.css` 语义变量，新增 `--md-*` 一组，暗黑默认 `:root`，明亮 `html.light` 覆盖。
3. 流式安全：样式只依赖标签选择器，不依赖 JS；`streamingText` 半截 markdown（未闭合 ` ```/table`）不崩、不横溢。

## 3. 详细设计（全部落在 `main.css`，`MessageTimeline.vue` scoped 只删不加）

### 3.1 新增变量（`main.css :root + html.light`）

```css
:root {
  --md-h-text: var(--text-primary);
  --md-body-text: var(--text-primary);
  --md-muted-text: var(--text-secondary);
  --md-link: var(--accent-primary);
  --md-divider: var(--border-subtle);
  --md-table-header-bg: rgba(255,255,255,.04);
  --md-table-row-alt: rgba(255,255,255,.02);
  --md-quote-border: var(--accent-primary);
  --md-quote-bg: rgba(59,130,246,.08);
  --md-inline-code-bg: rgba(148,163,184,.16);
}
html.light {
  --md-table-header-bg: #f1f5f9;
  --md-table-row-alt: #f8fafc;
  --md-quote-bg: #eff6ff;
  --md-inline-code-bg: #f1f5f9;
}
```

### 3.2 排版规则（`.markdown-body` 下，建议 90 行内一次收敛）

| 元素 | 规则（Chrome109 安全） |
|---|---|
| `h1-h6` | `font-weight:700; color:var(--md-h-text); line-height:1.4; margin:12px 0 6px;` 字号阶梯 `h1:17px h2:16px h3:15px h4-h6:13px(app字号)`；首个 `h1:first-child margin-top:0`；`h1/h2` 加 `padding-bottom:4px; border-bottom:1px solid var(--md-divider)`（一眼分级） |
| `p` | 现有 `margin:4px 0` 保留，改为 `margin:6px 0; line-height:1.7`（与分割线拉开，见下） |
| `ul/ol` | `list-style: disc/decimal outside; padding-left:22px; margin:6px 0;` 嵌套 `ul ul{list-style:circle} ul ul ul{list-style:square}`；`ol` 保持 decimal |
| `li` | `margin:3px 0; line-height:1.65;` `li > p{margin:2px 0}`；`li::marker{color:var(--md-muted-text)}`（109 支持 `::marker` 颜色） |
| `table` | 外包滚动：CSS 做不到 wrapper，改为 `table{display:block; overflow-x:auto; max-width:100%; border-collapse:collapse; margin:8px 0; font-size:12px}`；`th,td{border:1px solid var(--md-divider); padding:6px 10px; text-align:left; white-space:nowrap}` 首列允许 `white-space:normal`？统一 `nowrap + 外层横滚` 防撑破时间轴；`thead th{background:var(--md-table-header-bg); font-weight:600}`；`tbody tr:nth-child(even){background:var(--md-table-row-alt)}`（109 支持） |
| `hr` | `border:none; border-top:1px solid var(--md-divider); margin:14px 0;`（用户点的“上下间距”核心就是这里：原来 preflight `hr{margin:0}` 贴成一条缝） |
| `blockquote` | `margin:8px 0; padding:6px 10px; border-left:3px solid var(--md-quote-border); background:var(--md-quote-bg); color:var(--md-muted-text); border-radius:0 6px 6px 0` |
| `a` | `color:var(--md-link); text-decoration:none` `hover:underline`；`break-all` 防长 URL 撑破 |
| `code(行内)` | `background:var(--md-inline-code-bg); border:1px solid var(--md-divider); border-radius:4px; padding:1px 5px; font-size:12px`；`pre code` 反重置（已有 L133，保持） |
| `img` | `max-width:100%; border-radius:6px; border:1px solid var(--md-divider)` |
| `input[type=checkbox]` | `accent-color:var(--accent-primary)`（任务列表 `- [ ]` 渲染可用，原样保留禁用态） |

### 3.3 必须同步的清理

- `MessageTimeline.vue` scoped L225-227 的 `.markdown-body :deep(p){margin:4px 0}` 删除（收敛到 `main.css` 全局，避免 scoped 与全局打架导致 `hr+p` 间距时大时小）。
- `pre` 保留在 `main.css`（已有），scoped L218-224 删到只剩注释或删除整块 `<style scoped>`（推荐删除，避免后来者误往 scoped 加全局排版）。
- `html:false` 保持（防 XSS），`linkify/breaks` 保持；不新增插件（`footnote/task-lists` 等本次不做，避免 scope 膨胀）。

## 4. 施工步骤

1. `main.css` 追加 `/* need04-02 markdown-body 排版 */` 一段（变量 + 上表全部规则，注明暗/亮两套）。
2. `MessageTimeline.vue`：删 scoped `p/pre` 重复段（或整块删除），确认两处 `markdown-body` 类名不变；`renderMarkdown/markdown-it` 配置不动。
3. 自测页：用同一段 fixture（含 `h1-h3/ul嵌套/ol/table 3列/hr/blockquote/行内code/代码块/链接`）在暗/亮各截一图；窄窗 900px 下表格横滚不撑破时间轴。

## 5. 验收清单

- [ ] 有序列表显示 `1.2.3.`，无序显示 `•`，二级嵌套符号变化且缩进正确。
- [ ] 表格有完整外框 + 内格线，表头有底色，列多时容器内横滚、时间轴无横向滚动条。
- [ ] `hr` 上下各有约一行空（`14px`），不与上下段落粘连。
- [ ] `h1>h2>h3` 字号/粗细肉眼可辨（`17/16/15`），`h1/h2` 有底部分隔线。
- [ ] 暗黑/明亮各一截图；`vue-tsc -b && vite build` 通过。

## 6. 风险与回退

- 纯 CSS 追加，无 JS/后端改动；回退即删除新增段，preflight 裸奔恢复，一分钟可逆。
- `table{display:block}` 会使 `table-layout` 语义变化，仅用于展示态消息流；设置中心 Drawer 表格（need02-02 的 `table-layout:fixed`）限定在 `.settings-drawer-body` 下，不受影响（施工时核对选择器前缀均为 `.markdown-body`）。

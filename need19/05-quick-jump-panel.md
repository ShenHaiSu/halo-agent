# need19-05 快速目录面板（历史气泡目录）规格与纯函数设计

> 对应总览：[00-README](./00-README.md) R3/R8/R9。前置：[01](./01-architecture-design.md) §4.1/§4.3，[03](./03-history-anchor-and-scroll.md) §6。
> 本篇定义：气泡数据怎么算（纯函数）、气泡长什么样（视觉与交互）、长会话怎么不卡（窗口化）、各类边界文本怎么处理。

---

## 1. 面板结构

```text
HistoryTocPanel
├─ [头部]  标题「历史目录」 + 统计「共 N 条」 + 过滤下拉（全部/只看提问/只看回复）+「只看每轮首条」开关
│           + 右侧：置顶/置底快捷按钮（⇡ ⇣）
├─ [真空态]  会话无消息 → 图标 + 「当前会话还没有消息」+ 提示「发送一条指令试试」
├─ [加载态]  workspaceStore.loadingMessages → 3~5 个骨架气泡
└─ [气泡列表]  自己的 overflow-y-auto 容器
     ├─ 每条气泡：HistoryBubble（左侧 assistant / 右侧 user / 居中 system）
     ├─ 相邻 user 之间可插「轮次分隔」（可选，默认关）
     └─ [流式占位]  agentStore.hasActiveTurn → 末尾一个脉冲气泡「正在生成…」
```

## 2. 纯函数：`historyToc.ts`

> 位置：`src/utils/historyToc.ts`。**零 Vue 依赖、零 DOM 依赖**，可直接在 node 里跑断言（[06](./06-implementation-and-acceptance.md) §4）。

### 2.1 类型

```ts
import type { TimelineEntry } from '@/utils/turnGroup';

export type TocRole = 'user' | 'assistant' | 'system';
export interface TocItem {
  /** 与 TimelineEntry.key 完全一致，也是锚点 key */
  anchorKey: string;
  role: TocRole;
  /** 用于气泡显示的标签（正文前 15 字，已清洗） */
  label: string;
  /** 原始全文（仅供 tooltip / aria-label，不渲染） */
  fullText: string;
  /** 字符数（清洗后），用于 tooltip「共 N 字」 */
  charCount: number;
  /** 序号（在过滤前全局递增，1-based，显示为 #n） */
  index: number;
  /** 所属轮次序号（1-based；user 与其后 assistant 同轮；system=0） */
  round: number;
  /** assistant 轮的附加信息 */
  toolCount: number;
  hasReasoning: boolean;
  /** 消息创建时间（可能为空串） */
  createdAtUtc: string;
  /** 是否为该轮第一条（用于"只看每轮首条"） */
  isRoundHead: boolean;
}
export interface TocFilter {
  role: 'all' | 'user' | 'assistant';
  roundHeadsOnly: boolean;
}
```

### 2.2 `toDisplayText(raw)`：清洗成"正文"

顺序**严格**按下列 9 步（顺序错了会出怪文本）：

```ts
export function toDisplayText(raw: string | null | undefined): string {
  let s = String(raw ?? '');
  // 1) 去掉图片占位：[image:xxx] / ![alt](path) / <img ...>
  s = s.replace(/\[image:[^\]]*\]/gi, ' ')
       .replace(/!\[[^\]]*\]\([^)]*\)/g, ' ')
       .replace(/<img\b[^>]*>/gi, ' ');
  // 2) 去掉代码围栏标记（保留代码内容文字）
  s = s.replace(/```[a-zA-Z0-9_+-]*\s*/g, ' ').replace(/```/g, ' ');
  // 3) 行内代码反引号
  s = s.replace(/`{1,3}/g, '');
  // 4) 去掉 HTML 标签（markdown-it 允许内联 HTML）
  s = s.replace(/<[^>]{1,200}>/g, ' ');
  // 5) 链接 [text](url) → text
  s = s.replace(/\[([^\]]*)\]\([^)]*\)/g, '$1');
  // 6) 强调/标题/引用/列表符
  s = s.replace(/^\s{0,3}#{1,6}\s+/gm, '')
       .replace(/^\s{0,3}>\s?/gm, '')
       .replace(/^\s{0,3}([-*+]|\d{1,3}[.)])\s+/gm, '');
  s = s.replace(/[*_~]{1,3}/g, '');
  // 7) 换行/连续空白 → 单空格（气泡是单行/两行，不能保留换行）
  s = s.replace(/\s+/g, ' ').trim();
  // 8) 控制字符与零宽字符
  s = s.replace(/[\u0000-\u001F\u007F\u200B-\u200F\uFEFF]/g, '');
  // 9) 若清洗后为空但原文有图 → 给「[图片]」
  if (!s && /\[image:|!\[|<img\b/i.test(String(raw ?? ''))) return '[图片]';
  return s;
}
```

### 2.3 `bubbleLabel(text, max = 15)`：正文前 15 字

```ts
export function bubbleLabel(text: string, max = 15): string {
  const s = (text || '').trim();
  if (!s) return '';
  const chars = Array.from(s);          // 码点切分：中文/emoji/代理对安全
  if (chars.length <= max) return s;
  return chars.slice(0, max).join('') + '…';
}
```

**口径说明（R9 的精确解释）**：

| 问题 | 决策 |
|---|---|
| 「15 个字符」是 UTF-16 码元还是码点？ | **码点**（`Array.from`），否则 emoji 会被劈成半个 |
| 是否在空格处断词？ | **不**。要求"前 15 字"，硬截断 + `…`；中文场景更符合直觉 |
| 省略号算不算 15 字之内？ | **不算**，输出长度 16（15 + `…`） |
| 是否清洗 Markdown？ | **清洗**（§2.2）。否则气泡会显示 `### 标题`、`**加粗**` 之类噪音 |
| 空正文（纯图片/纯工具调用） | `[图片]` / `[工具调用]` / `[空消息]` 兜底（§2.4） |

### 2.4 角色兜底文案

| 情形 | 标签 |
|---|---|
| user 空正文 + 有图片 | `[图片]` |
| user 空正文 | `[空消息]` |
| turn 组 `content` 为空但有 `toolMessages` | `🔧 调用了 N 个工具` |
| turn 组 `content` 为空但有 `reasoning` | `🧠 仅思考未输出` |
| turn 组全空 | `[空回复]` |
| system | 直接 `toDisplayText` 后截断；若为空 → `[系统消息]` |
| 正文以压缩摘要特征开头（`[上下文压缩`/`压缩摘要`） | 前缀 `Σ ` |

### 2.5 `buildHistoryToc(entries, filter)`：条目 → 气泡

```ts
export function buildHistoryToc(entries: TimelineEntry[], filter: TocFilter): TocItem[] {
  const out: TocItem[] = [];
  let round = 0;
  let index = 0;
  let sawHeadInRound = false;
  for (const e of entries) {
    index += 1;
    let role: TocRole;
    if (e.kind === 'user') { role = 'user'; round += 1; sawHeadInRound = false; }
    else if (e.kind === 'turn') role = 'assistant';
    else role = 'system';

    const isRoundHead = role === 'assistant' && !sawHeadInRound;
    if (role === 'assistant') sawHeadInRound = true;

    // 过滤：角色
    if (filter.role === 'user' && role !== 'user') continue;
    if (filter.role === 'assistant' && role !== 'assistant') continue;
    if (filter.roundHeadsOnly && role === 'assistant' && !isRoundHead) continue;
    if (filter.roundHeadsOnly && role === 'system') continue;

    out.push({ anchorKey: e.key, role, label: labelOf(e), fullText: textOf(e), charCount: ...,
               index, round: role === 'system' ? 0 : round, toolCount, hasReasoning,
               createdAtUtc: ..., isRoundHead });
  }
  return out;
}
```

> **`index` 在过滤前递增**：这样"#12"永远指会话里的第 12 条，切过滤不跳号，用户心智稳定。

## 3. 气泡 UI（`HistoryBubble.vue`）

### 3.1 DOM

```vue
<button
  class="toc-bubble"
  :class="[`toc-bubble--${item.role}`, { 'is-active': active }]"
  :title="tooltipText"
  :aria-label="`跳转到第 ${item.index} 条：${item.label}`"
  :data-testid="`toc-bubble-${item.anchorKey}`"
  @click="$emit('jump', item)"
>
  <span class="toc-seq">{{ item.index }}</span>
  <span class="toc-label">{{ item.label }}</span>
  <span v-if="item.toolCount" class="toc-badge">🔧{{ item.toolCount }}</span>
</button>
```

### 3.2 视觉细则

| 属性 | user | assistant | system |
|---|---|---|---|
| 对齐 | `margin-left:auto`（右） | `margin-right:auto`（左） | 居中 |
| 背景 | `rgba(59,130,246,.16)`（亮 `#eff6ff`） | `var(--bg-elevated)` | 透明 + 虚线边框 |
| 边框 | 左 2px `var(--accent-primary)` | 右 2px `var(--border-subtle)` | 1px dashed `var(--border-subtle)` |
| 圆角 | `10px 10px 2px 10px` | `10px 10px 10px 2px` | `6px` |
| 序号 | `11px` mono，`var(--text-secondary)`，`opacity:.6`，与文本同排（左内侧） | 同 | 隐藏 |
| 文本 | `calc(var(--app-font-size) - 1px)`，`var(--text-primary)`，**单行省略**（`white-space:nowrap; overflow:hidden; text-overflow:ellipsis`）或**两行**？ | 同 | `-2px`，mono，单行 |
| 悬停 | 背景 +8% 亮度，`cursor:pointer` | 同 | 同 |
| 活跃 | `border-color: var(--accent-primary)` + `box-shadow: 0 0 0 2px rgba(59,130,246,.22)` | 同 | 同 |
| 聚焦（键盘） | `outline: 2px solid var(--accent-primary); outline-offset: 1px` | 同 | 同 |

**单行 vs 两行**：15 个字在 360px 抽屉（内容区约 300px，气泡 78% ≈ 234px）下，13px 字号 1 行约 17~18 个汉字 → **单行足够**，采用 `nowrap + ellipsis` 作为极端兜底（如全角符号 + emoji）。

### 3.3 tooltip 内容

```text
#12 · 用户 · 14:32:10
修复登录页在 IE 11 下白屏的问题，先看……

共 186 字
```

* 第一行：序号 + 角色 + 时间（`utils/timeFormat.ts` 的 `toLocale`，已有：`timeFormat.ts:18-25`）；
* 第二行：前 60 字（`bubbleLabel(text, 60)`）；
* 第三行：总字数。

> tooltip 用 `n-tooltip`（`delay=400`，`placement=left`，`max-width: 280px`）。**不要在列表里给每个气泡都包 `n-tooltip` 的复杂 slot**——用 `title` 属性做零成本兜底，`n-tooltip` 只给「有徽标/有截断」的气泡加，控制 DOM 量。

## 4. 长会话性能（无新增依赖）

### 4.1 规模评估

| 场景 | 消息数 | 目录条目数（约） | 一次性渲染 DOM 节点（≈ 4/条） |
|---|---|---|---|
| 普通 | 20~80 | 10~40 | 40~160 ✅ |
| 长跑 | 200~800 | 100~400 | 400~1600 ⚠️ 可接受但开始卡 |
| 极端 | 2000+ | 1000+ | 4000+ ❌ |

**朴素渲染在 500 条以内完全没问题**（每个气泡 3~4 个 span，无复杂 slot）。因此采取**渐进策略**：

### 4.2 三级方案

| 级别 | 触发条件 | 做法 | 代码量 |
|---|---|---|---|
| L0 | `items.length <= 400` | 全量渲染 | 0 |
| L1 | `> 400` | **分页批渲染**：首屏 200 条 + 底部「加载更早/更晚」按 200 批；进入面板默认定位到**最新一屏**（历史目录最常看最近） | ~30 行 |
| L2（可选，P3） | `> 1500` | **窗口化**：`scrollTop` 估算 → 只渲染可视区 ±20 条（固定行高 30px 时最简单；本设计气泡高度受 `nowrap` 约束基本恒定 → 可用固定高度） | ~60 行 |

> **决策**：本期实现 **L0 + L1**；L2 列为可选。理由：L1 之后 400~2000 条的 DOM 保持在 400 以内，足够流畅；L2 引入固定行高假设，一旦气泡高度变化（两行、徽标）就会错位，性价比低。

### 4.3 其他性能约束

1. **`v-for :key` 用 `anchorKey`**（稳定），不要用索引。
2. **不要在列表里做 `computed` 里的对象深拷贝**；`buildHistoryToc` 返回新对象是必要的（含派生态），但每个对象只 11 个字段，1k 条 = 1k 小对象，可接受。
3. **不要监听滚动去算 "active 气泡"**（会每秒几十次渲染）。若要"滚动主界面时高亮对应气泡"，用 `IntersectionObserver`（Chrome109 支持，零 scroll 事件），`throttle` 到 200ms，且仅在 Drawer 打开时启用（见 §5.2）。
4. **流式期间不重算**：`agentStore.streamingText` 的变化不影响 `sessionMessages`；只有 `AssistantMessagePersisted` 后 `loadSessionMessages` 才会更新数组 → 目录条目在流式期间保持不变（符合直觉），只在末尾显示一个脉冲占位气泡。

## 5. 交互细节

### 5.1 点击 → 跳转

```ts
async function onJump(item: TocItem) {
  const ok = nav.scrollToAnchor(item.anchorKey, { behavior: 'smooth', highlight: true });
  if (!ok) {
    // 可能是子会话尚未加载完 / 目标被过滤掉
    message.warning('未找到该消息，可能正在加载或已被压缩');
    return;
  }
  activeKey.value = item.anchorKey;
}
```

* 跳转**不关闭** Drawer（用户可能连续跳多个）。
* 跳转后气泡保持高亮 `activeKey`，直到用户点了别的气泡或手动滚动主界面（可选）。

### 5.2 反向联动（主界面滚动 → 高亮气泡）【可选】

* 用 `IntersectionObserver` 观察所有已注册锚点（`registry`），`root` = Timeline 滚动容器，`rootMargin: '-45% 0px -45% 0px'` → 视口中线附近的那条成为 active。
* 只在 `ui.rightRailOpen && ui.rightRailTab==='toc'` 时启用，关闭时 `disconnect()`。
* 可选把 active 气泡 `scrollIntoView({block:'nearest'})` 到目录可视区（**注意**会干扰用户浏览，加 `if (!panelHovered)` 保护）。默认**不做自动滚动**，只做高亮。

### 5.3 键盘

| 键 | 行为 |
|---|---|
| `↑` / `↓`（焦点在气泡上） | 移动到上/下一个气泡（`tabindex` roving：容器 `tabindex=-1`，仅 active 气泡 `tabindex=0`） |
| `Enter` / `Space` | 跳转 |
| `Home` / `End` | 第一个 / 最后一个气泡 |

> 若不做 roving tabindex，退化为浏览器原生 `Tab` 逐个气泡（条数多时体验差）。**建议**实现 roving（约 15 行）。

### 5.4 头部快捷操作

| 按钮 | 行为 |
|---|---|
| `⇡ 置顶` | `nav.scrollToAnchor(entries[0].key)` |
| `⇣ 置底` | `nav.scrollToAnchor(lastKey, {paddingTop: Number.MAX_SAFE_INTEGER})` → 实际用 `container.scrollTop = scrollHeight`（专门方法 `scrollToBottom()`） |
| 过滤 `全部/只看提问/只看回复` | `ui.tocFilter.role` |
| `只看每轮首条` 开关 | `ui.tocFilter.roundHeadsOnly`（在 assistant 轮数很多时把目录压缩成"一问一答"） |
| `跳过 system` | 默认开启（system 消息通常无价值）；开关放过滤器菜单 |

## 6. 边界与文本处理清单

| # | 输入 | 处理 | 输出示例 |
|---|---|---|---|
| E1 | `# 标题\n\n正文` | 去 `#` 与换行 | `标题 正文` |
| E2 | `` ```ts\nconst a=1\n``` `` | 去围栏，保留内容 | `const a=1` |
| E3 | `[链接](https://x.y/z)` | 只留文字 | `链接` |
| E4 | `![图](a.png)` 或 `[image:abc]`（本项目图片占位符，见 `MessageTimeline.vue:23-37` 与 need09） | 去掉；若全文只有图 → `[图片]` | `[图片]` |
| E5 | 含 `**粗体**` | 去标记 | `粗体` |
| E6 | 大量空白 / 换行开头 | 压缩为单空格 | 无前导空格 |
| E7 | emoji `👨‍👩‍👧 家庭` | 码点切分不劈半 | `👨‍👩‍👧 家庭` |
| E8 | 超长单行 2000 字 | 只取前 15 | `xxxxxxxxxxxxxxx…` |
| E9 | 全角空格 `\u3000` | `\s` 已覆盖（JS `\s` 含 `\u3000`） | 正常 |
| E10 | 只有 reasoning 没有 content 的 turn | `🧠 仅思考未输出` | 同 |
| E11 | 只有工具调用的 turn | `🔧 调用了 3 个工具` | 同 |
| E12 | 孤立 tool 组（`group.key = tool-<id>`，无 assistantId） | 角色记为 `assistant`，标签 `🔧 工具结果` | 同 |
| E13 | system 消息（如压缩提示） | 默认隐藏；开启后居中显示 | `已压缩：12 条→摘要` |
| E14 | 内容含 `<script>` 等内容 | 已由 `toDisplayText` 步骤 4 去掉标签；且气泡用插值 `{{ }}` 渲染**非 v-html** | 纯文本 |
| E15 | `undefined`/`null` content | 按角色兜底 | `[空消息]` |

## 7. 验收点（本篇专属）

| # | 检查 | 通过标准 |
|---|---|---|
| J1 | 20 条消息的会话 | 气泡数与 `timelineItems` 条数一致；user 全在右、assistant 全在左 |
| J2 | 每条气泡文本 | 严格等于「清洗后正文前 15 码点 + …（超长时）」；单测覆盖 E1~E15 |
| J3 | 点击任意气泡 | 见 [03](./03-history-anchor-and-scroll.md) A1 |
| J4 | 500 条会话打开面板 | 首屏 ≤200 条；滚动到底出现"加载更晚"；内存无明显增长（DevTools Memory 前后差 < 15MB） |
| J5 | 流式生成中 | 目录末尾出现脉冲气泡「正在生成…」，生成结束后自动变成真实条目（对账后），不重复 |
| J6 | 切换会话 | 目录立即清空并重建（无旧会话残留）；滚动位置回到最新 |
| J7 | 子会话模式 | 标题变为「子会话目录（只读）」；条目来自 `subSessionStore.messages` |
| J8 | 过滤 | 只看提问/只看回复/只看每轮首条 三种组合正确；序号不重排 |
| J9 | 键盘 | Tab 进入气泡、↑↓ 移动、Enter 跳转 |
| J10 | 无消息空态 | 显示空态插图 + 文案，不出现空白面板 |

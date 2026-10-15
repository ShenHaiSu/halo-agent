# 04 · 明暗切换下代码块与 tool 结果样式修复方案

## 1. 现状（已读代码）——硬编码是病根

| 位置 | 硬编码 | 后果 |
|---|---|---|
| `MessageTimeline.vue` L25/L84 思考舱 `bg-slate-900/60` + L27-31/L86-92 `text-slate-300/400` | 深底+浅字写死 | 明亮模式下深底块突兀（用户未点名但同病） |
| `MessageTimeline.vue` L72 tool 结果 `text-slate-300` | 浅字写死，父 `bg-surface` 明亮=`#fff` | 明亮下浅灰字 on 白底 → “浅得太多看不清” ✔ offences reported |
| `MessageTimeline.vue` L217-224 `.markdown-body pre { background:#090d12 }` + 无 code 颜色 | 黑底写死，`code` 继承 `text-primary`（明亮=`#0f172a` 深色） | 明亮下黑底+深色字 → 无法阅读 ✔ reported |
| `ToolCallCard.vue` L64/67 diff `text-rose-200/emerald-200/slate-400` | 浅字写死 | 明亮下 on 白底对比度不足 |
| `ToolCallCard.vue` L85/L89/L109 `text-slate-300/200` + `bg-[#090d12]` | 终端黑底+浅字写死 | 明亮下黑底块孤立 + 浅字在非黑底行（L85 命令行无底）看不清 |
| `main.css` 仅定义 `--bg-*/--text-*`，无 code/tool 语义变量 | 无变量可切 | 切换主题时硬编码纹丝不动，符合“样式没自动切换”描述 |

结论：不是“切换逻辑坏了”（`appearanceStore.applyToDom` 的 `dark/light` class 切换正常），是**内容色写死 slate 系 + pre 背景写死**，自然不跟随。

## 2. 设计：引入三对语义变量 + 双主题赋值

- `main.css` `:root`（暗黑默认）新增：
```css
--code-bg: #090d12; --code-text: #e6edf3; --code-border: #283241;
--tool-output-bg: #090d12; --tool-output-text: #cbd5e1; /* slate-300 等价 */
--reasoning-bg: rgba(15,23,42,.6); --reasoning-text: #cbd5e1;
```
- `html.light` 覆盖：
```css
--code-bg: #f1f5f9; --code-text: #0f172a; --code-border: #e2e8f0;
--tool-output-bg: #f8fafc; --tool-output-text: #334155; /* slate-700 */
--reasoning-bg: #f1f5f9; --reasoning-text: #334155;
```
- 规则：**黑底只在暗黑保留**；明亮一律浅底+深字。终端输出（用户明确“黑底没问题”）→ 暗黑保持黑底，**明亮也改为浅底**（否则黑块在白页中割裂且 hljs 主题仍是暗色）。如产品坚持明亮保留黑终端，则 code-text 在黑底上强制浅色（见备选）。
- highlight.js 主题：当前未引入任何 hljs CSS（`hljs.highlight().value` 裸 span，无颜色类生效？实际 `hljs` 输出带 `hljs-keyword` 等类但无样式表 → 颜色全靠继承）。方案：**不引入整套 hljs 主题包**（减少体积），用 6 行自定义 `.markdown-body :deep(.hljs-*)` 映射到变量（keyword→accent，string→emerald，comment→secondary），暗亮共用变量自动适配。

## 3. 施工步骤（按文件）

1. `main.css`：加 §2 六变量 + `html.light` 覆盖；加 hljs 映射 + `.markdown-body pre/code` 收敛：
```css
.markdown-body pre { background: var(--code-bg); border:1px solid var(--code-border); color: var(--code-text); }
.markdown-body code { color: var(--code-text); font-family: var(--font-mono); }
.markdown-body pre code { background: transparent; }
```
（原 `MessageTimeline.vue <style scoped>` 的 pre 硬编码删除，移入全局以便 light 覆盖；scoped 与全局重复时以全局为准。）
2. `MessageTimeline.vue`：
   - L25/L84 思考舱：`bg-slate-900/60 → bg-[var(--reasoning-bg)]`，`text-slate-300/400 → text-[var(--reasoning-text)]`（Tailwind 任意值写法，Chrome109 + TW3 支持）。
   - L66-74 tool 结果：正文 `text-slate-300 → text-[var(--tool-output-text)]`，容器加 `bg-[var(--tool-output-bg)]`（或保持 `bg-surface` + 文字变量，二选一；推荐文字变量即可，底保持 surface 以不引入第三种灰）。
   - L37/L92 思考正文同上替换。
   - 删 `<style scoped>` pre 硬编码（移全局）。
3. `ToolCallCard.vue`：
   - L64-67 diff 行：`text-rose-200 → text-[var(--diff-del-text)]` 等，或最简：del/add 行在 light 下加 `html.light` 覆盖类。推荐新增 `--diff-add-text/--diff-del-text/--diff-ctx-text` 三变量（dark: `#a7f3d0/#fecdd3/#94a3b8`，light: `#047857/#be123c/#64748b`）。
   - L89/L109 终端/输出块：`bg-[#090d12] text-slate-200/300 → bg-[var(--tool-output-bg)] text-[var(--tool-output-text)]`。
   - L85 命令行 `text-slate-300 → text-primary`（无底行必须跟随主文字）。
   - L26/L28 `text-emerald-400/rose-400` 为统计数字，在 light 下可读（emerald-400 on 白底对比度 ~2.5 不足）→ 改 `text-emerald-600/rose-600` 或变量化；选变量化一步到位。
4. 全文件 `grep -rn "slate-\|#090d12\|#0d1117" src/Web.Client/src/components/chat` 清零为目标（思考舱/终端/输出块除外已变量化）。

## 4. 备选（若产品坚持“明亮保留黑终端”）

- 终端块保留 `bg-[#090d12]`，但其内文字强制 `!text-slate-200`（`text-[var(--code-text)]` 在 light 下是深色 → 黑底不可读，所以必须反向固定浅色）。即：**有黑底的地方文字必须锁浅色**，不可跟随变量。此备选增加心智负担，**不推荐**，仅当验收坚持黑终端时启用。

## 5. 验收清单

- [ ] `grep -rn "text-slate-\(200\|300\)\|bg-\[#090d12\]\|bg-slate-900" src/Web.Client/src/components/chat` 零命中（或仅备选锁定的终端块残留并注释说明）。
- [ ] 暗黑截图：观感与现状一致（黑底+浅字）；明亮截图：tool 输出深灰字 on 浅底、代码块浅底+深色字+边框，均可读；思考舱不再是深色孤岛。
- [ ] 同一会话暗↔亮来回切换 3 次，无需刷新即时跟随（变量驱动，无 JS 状态）。
- [ ] 长输出（4000+ 字截断）与 diff 15 行折叠在两种主题下无横溢；`vue-tsc -b && vite build` 通过。

## 6. 风险与回退

- Tailwind 任意值 `bg-[var(--x)]` 在 TW3 + Chrome109 安全；若构建警告则改内联 `:style` 绑定同变量。
- hljs 类名映射若漏类，最坏是部分 token 继承正文色（仍可读），不崩。回退即恢复 scoped 硬编码 pre，一分钟可逆。

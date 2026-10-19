# 任务3附属B：亮暗主题选中反转与 Stage 文案下线（痛点c+d）

> 对应痛点：c. 亮色下 active 逻辑要反过来；d. 前端不再显示 `Stage xx`。
> 基座确认：`systemStore.themeMode: 'dark'|'light'` + `documentElement.classList light/dark`（`systemStore.ts` L30-L46），CSS 变量两套（`main.css` `:root` 暗 / `html.light` 亮），Naive 两套 overrides（`naiveTheme.ts` `haloDarkThemeOverrides/haloLightThemeOverrides`）均已就绪——缺的只是“自定义选中态”没跟上这套体系。

## 1. 痛点c：现状为什么在亮色下是错的

硬编码清单（全局搜 `bg-black` 即中，8 处）：

```
ProviderManagerView.vue L28-L32：
  选中   border-blue-500/60 bg-blue-500/10
  未选中 border-subtle bg-black/15 hover:bg-white/[0.03]

NetworkProxySettings.vue L24-L73（三块模式卡同式）：
  选中   border-blue-500/60 bg-blue-500/10
  未选中 border-subtle bg-black/10 hover:bg-white/[0.02]

ProviderManagerView.vue L122/L185/L333/L370、NetworkProxySettings.vue L94/L148、
SystemRuntimePanel.vue L65、StorageSettingsPanel.vue L184/L194：
  预览/信息条 bg-black/15、bg-black/20、bg-black/10
```

- `bg-black/15` 的语义是“在深底上压一层更深的黑”——暗黑下成立（未选中更深、选中用蓝色提亮，符合“选中变浅”）。
- 亮色底是 `#ffffff/#f8fafc`，压一层黑变成灰色块；`hover:bg-white` 在白底上完全不可见；于是亮色下“未选中反而比选中更显眼”，与“选中应更深、未选中更浅”的反转期望正好相反。
- `naiveTheme.ts` 的亮色 `cardColor #fff / border #e2e8f0` 只管 Naive 组件（`n-card/n-input`），管不到手写的 Tailwind 类，所以必须单独立语义类。

## 2. 方案：收敛到两个语义类，反转逻辑只写在 CSS 里

### 2.1 `main.css` 追加（chrome109 安全，只用 `var()`）

```css
/* 设置中心可选中卡片：主题自适应，模板里只写这两个类 */
.setting-option {
  border: 1px solid var(--border-subtle);
  border-radius: 8px;
  transition: background-color .15s ease, border-color .15s ease;
}
/* 暗黑（默认 :root）：未选中 = 更深，选中 = 提亮 */
.setting-option { background-color: rgba(0, 0, 0, 0.15); }
.setting-option:hover { background-color: rgba(255, 255, 255, 0.03); }
.setting-option-active {
  border-color: rgba(59, 130, 246, 0.6);
  background-color: rgba(59, 130, 246, 0.10);
}
/* 亮色（html.light）：逻辑反转——未选中 = 最浅，选中 = 加深蓝底 */
html.light .setting-option { background-color: #f8fafc; }
html.light .setting-option:hover { background-color: #f1f5f9; }
html.light .setting-option-active {
  border-color: #93c5fd;
  background-color: #eff6ff;
}
/* 预览/信息条同理：深底用黑叠加，浅底用灰底 */
.setting-inset { border: 1px solid var(--border-subtle); border-radius: 6px; }
.setting-inset { background-color: rgba(0, 0, 0, 0.20); }
html.light .setting-inset { background-color: #f1f5f9; }
```

- 模板侧改法示例（`ProviderManagerView.vue` L24-L35）：

```diff
-<div class="p-3 rounded-lg border cursor-pointer transition-colors"
-     :class="selectedProviderId === item.id
-       ? 'border-blue-500/60 bg-blue-500/10'
-       : 'border-subtle bg-black/15 hover:bg-white/[0.03]'">
+<div class="setting-option p-3 cursor-pointer"
+     :class="{ 'setting-option-active': selectedProviderId === item.id }">
```

- 同式替换 `NetworkProxySettings.vue` 三块模式卡（L24-L73）；预览条 `bg-black/20/bg-black/15` 换 `setting-inset`（Provider L122/L333/L370、Network L94/L148、SystemRuntime L65、Storage L184/L194，共 7 处，可批量）。
- 为什么不用 `dark:` 变体：项目暗黑是默认值（`:root` 即暗），亮色靠 `html.light` 切换（见 `tailwind.config.js darkMode:'class'` + `systemStore.applyThemeClass`），手写 `html.light` 覆盖比 Tailwind `dark:` 心智更直，且与现有 `main.css` 变量体系一致。
- 为什么不用 Naive `Tabs` 主题变量解决：Tabs 管的是顶部 Tab 条，管不到业务卡片的选中态；且 `naiveTheme.ts` 已有亮暗两套，无需动。

### 2.2 验收（痛点c）

- [ ] 暗黑：选中卡蓝边+淡蓝底（视觉更浅），未选中近黑（更深），hover 有轻微提亮。
- [ ] 亮色：选中卡蓝边+`#eff6ff`（比白底更深），未选中 `#f8fafc`/白（更浅），hover 为 `#f1f5f9` 肉眼可见；无任何灰黑残留（全局搜 `bg-black/` 在 `settings/` 零命中）。
- [ ] 顶栏主题按钮（`WorkbenchTopBar.vue` L139-L150 `toggleTheme`）切换即时生效，无需刷新；CefSharp 109 下无闪烁。

## 3. 痛点d：`Stage xx` 下线清单（用户可见层全清）

| # | 位置 | 现状 | 改为 |
|---|------|------|------|
| 1 | `SettingsCenterModal.vue` L10-L14（→新 Drawer 同位） | `<n-tag>Stage 02 Config & ProxyHub</n-tag>`（`header-extra`） | **整块删除**。Drawer 标题即 `设置与诊断中心`，无需副标；若想保留版本信息，改用中性 `v1.0` 或直接留空，本次推荐留空 |
| 2 | `SystemRuntimePanel.vue` L60 | `n-card title="架构宪章合规性自检清单 (Stage 01)"` | `架构合规性自检清单`（去括号后缀） |
| 3-5 | `StorageSettingsPanel.vue` L359/L362/L364 | 测试日志文案 `Stage 01 diagnostic check: App/…Bearer…/cmd.exe…` | `diagnostic check: App channel write test.` / `diagnostic check: Authorization masking test.` / `diagnostic check: tool channel write test.`（保留脱敏验证语义，去 Stage 前缀；Bearer 掩码测试串保留但去 Stage） |

- 不动的：`App.vue` L32、`WorkbenchView.vue` L2 等 HTML 注释里的 `Stage 06`，`hostBridge.ts` 注释里的 `Stage 01/02/03/04/06`，`docs/develop/stage*` 历史文档——均非用户可见，不在需求范围内，动了反而污染 git blame。
- 验证命令（施工后必跑）：`grep -rn "Stage" src/Web.Client/src --include="*.vue" --include="*.ts"` 应只剩注释行；更严可用 `grep -rn "Stage" src/Web.Client/src/components` 要求零命中。

### 验收（痛点d）

- [ ] 设置中心 Drawer 内、四个 Tab 内、日志写入后刷新出的文件名/内容中，肉眼与 DOM 均无 `Stage` 字符串。
- [ ] `handleWriteDiagnosticLogs` 三通道仍可写入并脱敏（LLM 通道的 `sk-1234…` 掩码行为不变，只是前缀文案变了）。

## 4. 联合回归（c+d 同批）

- [ ] `vue-tsc -b && vite build` 通过；`main.css` 无 chrome109 非法语法（`vite build.cssTarget=chrome109` 会直接报错兜底）。
- [ ] 暗/亮各截一版 Drawer 全高图，选中卡各点一遍，确认反转逻辑与文案。

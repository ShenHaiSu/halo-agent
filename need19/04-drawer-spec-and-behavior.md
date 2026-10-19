# need19-04 Drawer 规格与交互行为（点击外部不关闭的完整实现）

> 对应总览：[00-README](./00-README.md) R5/R6/R7。前置：[01](./01-architecture-design.md) §1/§3.3。
> 本篇把 Drawer 的**每一个开关路径**列清楚，并给出源码级依据，确保「点击外部不会主动收缩，只有右上角关闭按钮才收缩」这条需求被**穷尽**验证。

---

## 1. Naive UI Drawer 关闭路径的源码实证

来自 `node_modules/naive-ui/es/drawer/src/Drawer.mjs`（实测版本 `naive-ui ^2.40.1`）：

```js
// 默认值
maskClosable: { type: Boolean, default: true },
closeOnEsc:   { type: Boolean, default: true },
trapFocus:    { type: Boolean, default: true },
blockScroll:  { type: Boolean, default: true },
showMask:     { type: [Boolean, String], default: true },
displayDirective: { type: String, default: "if" },

// 关闭入口 ①：点遮罩
function handleMaskClick(e) { const { onMaskClick, maskClosable } = props;
  if (maskClosable) doUpdateShow(false); if (onMaskClick) onMaskClick(e); }
function handleOutsideClick(e) { handleMaskClick(e); }     // 遮罩是兄弟节点，内部点击不会冒泡到遮罩

// 关闭入口 ②：Esc
function handleEsc(e) { props.onEsc?.();
  if (props.show && props.closeOnEsc && eventEffectNotPerformed(e)) { if (!isComposingRef.value) doUpdateShow(false); } }

// 关闭入口 ③：内容区 X 按钮（n-drawer-content 的 closable，走 inject 的 doUpdateShow）
provide(drawerInjectionKey, { ..., doUpdateShow, ... });
```

以及 `DrawerBodyWrapper.mjs`：

```js
const directives = [[vShow, show]];
if (!props.showMask) directives.push([clickoutside, props.onClickoutside, void 0, { capture: true }]);
```

> ⚠️ **重要陷阱**：当 `showMask=false` 时，Naive 会挂 **`clickoutside` 指令**，**点击 Drawer 外部任何位置都会关闭**——这正好与用户需求相反。
> 所以**绝不能**用 `:show-mask="false"` 来实现「点外部不关闭」。正确做法是 `showMask` 保持默认（有遮罩）+ `:mask-closable="false"`。

## 2. 需求 → 配置映射

| 需求 | 配置 | 说明 |
|---|---|---|
| R5 用 Drawer 实现，从右侧滑出 | `placement="right"`（默认即是，但显式写上便于阅读） | — |
| R6 点击外部**不**收缩 | `:mask-closable="false"`；`show-mask` 用默认 `true` | 点遮罩完全无反应（不触发 `doUpdateShow`） |
| R7 右上角关闭按钮才收缩 | `n-drawer-content` 上 `closable` | 自带 X，行为来自 `drawerInjectionKey.doUpdateShow(false)`，不受 `maskClosable` 影响 |
| 附加：再次点触发按钮关闭 | `RightRailTrigger` 的 `@click="ui.toggleRightRail()"` | 辅助路径，不违背 R7（用户显式点同一个按钮） |
| 附加：Esc 关闭 | `close-on-esc` 默认 `true`，**保留** | 见 §6 决策说明 |
| 不抢焦点 | `:auto-focus="false"`、`:trap-focus="false"` | 见 §5 |
| 不锁页面滚动 | `:block-scroll="false"` | 见 §5.4 |
| 内容区自己滚动 | `:native-scrollbar="false"` + 内容区 `overflow-y-auto` | 与 `SettingsCenterDrawer` 一致；且便于目录面板做虚拟化 |
| 不透明遮罩（视觉上仍是"浮层"） | `show-mask="transparent"` 或 `true`？ | 见 §4 决策表 |

## 3. 组件骨架（可直接照抄实现）

```vue
<!-- src/components/rightRail/RightRailDrawer.vue -->
<template>
  <n-drawer
    v-model:show="ui.rightRailOpen"
    placement="right"
    :width="ui.rightRailWidth"
    :min-width="320"
    :max-width="720"
    resizable
    :to="to"
    :mask-closable="false"
    :auto-focus="false"
    :trap-focus="false"
    :block-scroll="false"
    :native-scrollbar="false"
    :z-index="2000"
    content-class="right-rail-drawer"
    @update:width="onWidth"
  >
    <n-drawer-content
      :title="null"
      closable
      :native-scrollbar="false"
      body-content-style="padding:0;height:100%;"
      class="right-rail-drawer-content"
    >
      <div class="right-rail-root" data-testid="right-rail-drawer">
        <!-- 左：icon rail -->
        <nav class="right-rail-tabs" aria-label="右侧栏切换">
          <n-tooltip v-for="t in tabs" :key="t.key" placement="left" :delay="400">
            <template #trigger>
              <button
                class="rail-tab"
                :class="{ 'is-active': ui.rightRailTab === t.key }"
                :data-testid="`rail-tab-${t.key}`"
                :aria-current="ui.rightRailTab === t.key ? 'page' : undefined"
                @click="ui.rightRailTab = t.key"
              >
                <component :is="t.icon" class="w-[18px] h-[18px]" />
                <span v-if="t.badge" class="rail-badge">{{ t.badge }}</span>
              </button>
            </template>
            {{ t.label }}
          </n-tooltip>
        </nav>

        <!-- 右：内容区 -->
        <section class="right-rail-content">
          <GoalPanelContent v-if="ui.rightRailTab === 'goal'" mode="drawer" :readonly="isSubReadonly" />
          <HistoryTocPanel v-else-if="ui.rightRailTab === 'toc'" />
        </section>
      </div>
    </n-drawer-content>
  </n-drawer>
</template>
```

> `:title="null"` + `closable`：只保留右上角 X，不要 "标题栏 + 关闭按钮" 的双标题，panel 自己画标题行（更省高度）。若验收要求"关闭按钮必须在最右上角且醒目"，`n-drawer-content` 的 X 已满足（默认 `closeIcon` 24px 区域）。

## 4. 遮罩与视觉决策

| 选项 | 效果 | 结论 |
|---|---|---|
| A. `show-mask=true`（默认，接近不透明） | 主界面被压暗，**交互被遮罩吞掉**（点主界面无反应） | 会让用户觉得"点外面没反应是 bug"，且不能一边看目录一边滚动主界面。**仅作为「专注模式」可选开关保留** |
| **B. `show-mask="transparent"`（本期采用）** | 遮罩存在（点击可被吞，符合"点外部不关闭且不误触"），但视觉透明 | 主界面清晰可见 → 便于"看目录 → 点心智模型中的位置"；点击主界面不会误触（遮罩捕获），也不会关闭 | 
| C. 去掉遮罩（`show-mask=false`） | 主界面可交互 | **禁止**：触发 `clickoutside` 指令 → 点外面就关（违反 R6） |

**B 的副作用与对策**：

| 副作用 | 对策 |
|---|---|
| Drawer 打开时无法操作主界面的输入框 / 按钮 | 这是有意的「模态专注」；关闭后恢复。若要"边看边写"，见 §6 Q3 |
| 透明遮罩仍吃掉 `wheel`？ | 遮罩 `pointer-events` 默认 auto，会捕获滚轮 → 主界面不滚。**可接受**（Drawer 打开时用户关注抽屉内容）。若需要主界面可滚，见 §6 Q3 的替代方案 |
| 用户可能以为"写死的 UI" | 首次打开给一次性引导：「点击右上角 ✕ 关闭（点击外部不会关闭）」，`localStorage` 记录 `halo.ui.rightRail.hintShown` |

> **实现方式**：`showMask="transparent"` 会渲染 `<div class="n-drawer-mask n-drawer-mask--invisible" @click="handleMaskClick">`，`handleMaskClick` 因 `maskClosable=false` 不做任何事 → 满足 R6。视觉上无压暗。

### 4.1 内容区与 rail 的布局（`RightRailDrawer` 的 `<style scoped>`）

```css
.right-rail-root { display:flex; height:100%; min-height:0; }
.right-rail-tabs { width:48px; flex:0 0 48px; display:flex; flex-direction:column; gap:6px;
  padding:10px 6px; background:var(--bg-sidebar); border-right:1px solid var(--border-subtle); }
.right-rail-content { flex:1 1 auto; min-width:0; display:flex; flex-direction:column; min-height:0; }
.right-rail-drawer-content :deep(.n-drawer-body) { padding:0 !important; height:100%; }
.right-rail-drawer-content :deep(.n-drawer-header) { display:none; }  /* 用 X 悬浮按钮时 */
.right-rail-drawer-content :deep(.n-drawer-header__close) { display:flex; }  /* 仅保留关闭按钮 */
```

> 若隐藏 header 会连带隐藏 X，**不要**用 `display:none` 整块隐藏 header。正确做法：保留 header 但压缩高度（`padding: 4px 8px; border-bottom:none;`），或 `:title="null"`（Naive 在 title 为空时仍渲染 header 以承载 close 按钮——**施工时用 dev 工具实测一次**，若 header 不渲染则改回显式 `title=" "`）。

## 5. 焦点、键盘与滚动锁

### 5.1 焦点

| 配置 | 原因 |
|---|---|
| `:auto-focus="false"` | 避免打开抽屉时光标从输入框跳走；目录的气泡本身 `tabindex=0`，键盘用户可 `Tab` 进入 |
| `:trap-focus="false"` | FocusTrap 会把焦点困在抽屉内（`DrawerBodyWrapper.mjs:219` `disabled: !showMask || !trapFocus`）；我们不需要困住，因为主界面被透明遮罩挡住，焦点跑到主界面也无法操作；关掉能减少"Esc/Tab 行为异常"的坑 |
| 关闭后焦点归还 | `RightRailTrigger` 在 Drawer 关闭后 `ref.focus()`（可选，提升键盘体验）；若不实现，焦点留在 body（可接受） |

### 5.2 键盘

| 键 | 行为 | 说明 |
|---|---|---|
| `Esc` | 关闭 Drawer（`close-on-esc` 默认 true） | **与 R7 不冲突**：R7 说的是"点击外部不收缩"，Esc 是显式键盘操作。若产品坚持"只有 X 能关"，置 `:close-on-esc="false"`（本文档默认保留 Esc，见 §6 Q1） |
| `Tab` | 在抽屉内顺序移动（rail → 内容区控件 → 气泡） | — |
| `Enter` / `Space`（气泡聚焦时） | 触发跳转 | `HistoryBubble` 用 `<button>` 语义 |
| `Ctrl+Shift+H`（可选快捷键） | 切换「历史目录」Tab 并打开 Drawer | 需确认无既有快捷键冲突（全仓 `keydown` 仅 `MessageTimeline` 的 Esc 关菜单、`GoalFloatPanel` 的 Esc 取消拖拽） |

### 5.3 Esc 与输入法

Naive 的 `handleEsc` 有 `isComposingRef` 保护（输入法组合中不误关），因此 Goal 编辑区不会因为中文输入按 Esc 而关闭抽屉。✅

### 5.4 滚动锁 `block-scroll`

* 默认 `true` 会给 `html` 加 `overflow:hidden`（`useLockHtmlScroll`）。本项目 `body` 已经 `overflow:hidden`（`main.css:44-53`），锁定无实际收益，反而可能在 Drawer 关闭后残留（历史 bug 高发区）。
* **采用 `:block-scroll="false"`**，滚动由内容区自己的 `overflow-y-auto` 负责。

## 6. 待定项与决策（已给出默认答案，用户可推翻）

| Q | 问题 | 默认决策 | 理由 |
|---|---|---|---|
| Q1 | Esc 是否允许关闭？ | **允许** | 桌面端用户习惯；且不与"点击外部不关闭"冲突。若"只能点 X"是硬要求，改 `:close-on-esc="false"` 一行 |
| Q2 | 遮罩是否压暗？ | **透明**（`show-mask="transparent"`） | 见 §4 B；若要求"聚焦模式"，加一个 `ui.rightRailDimMask` 开关（默认 false） |
| Q3 | 是否允许 Drawer 打开时操作主界面？ | **不允许**（透明遮罩吞点击） | 简单、稳定；"边看边操作"需要 `show-mask=false` + 自己实现 clickoutside 抑制，复杂度与收益不匹配。若后续需要：改用 `show-mask=false` 并在 Drawer 内部之外放置一个**同大小透明捕获层**不可行（反正都要捕获）——真要做应改成"非模态侧栏"（flex 轨道），但那会引起 [00](./00-README.md) §4.4 的排版回归 |
| Q4 | 宽度默认值/范围 | 360px，320~720 | 目录气泡 `78%` 宽下约 28 字符/行，15 字标签单行可读；Goal 里程碑不折行 |
| Q5 | 是否持久化"打开状态" | **不持久化** | 启动默认关闭，避免遮挡主界面 |
| Q6 | 是否持久化当前 Tab | **持久化** | 用户上次看目录，下次点开还应是目录 |
| Q7 | 默认 Tab | `toc`（历史目录） | 用户描述中"快速跳转"是主诉求 |

## 7. 触发按钮（`RightRailTrigger`）

```text
             ┌──┐
  Timeline ──│◧ │   ← 固定在 WorkbenchView 右侧中部（position:absolute; right:12px; top:50%; translateY(-50%)）
             └──┘      z-index: 60，w-8 h-8，圆角 8px，半透明底色 + hover 提亮
```

| 规格 | 值 |
|---|---|
| 位置 | `absolute; right: 14px; top: 50%; transform: translateY(-50%)`（**不用 fixed**，否则会盖左侧栏和状态栏） |
| 图标 | `PanelRightOpen`（关闭态）/ `PanelRightClose`（打开态） |
| 徽标 | Goal 未设定时显示 `◎` 灰点；已设定显示百分比小圆点（`goalStore.pct`），与浮卡 mini 文案呼应 |
| tooltip | 「历史目录与目标（Ctrl+Shift+H）」 |
| 与浮卡位置冲突 | 浮卡默认 `right:16px; bottom:88px`（`goalStore.ts:13`），垂直中部不冲突；但用户可能把浮卡拖到中部 → 触发按钮 `z-index` 更高（60 > 40），点得到 |
| Drawer 打开时 | 按钮图标翻转（视觉状态同步）；点击 = 关闭 |

## 8. 验收点（本篇专属）

| # | 检查 | 通过标准 |
|---|---|---|
| D1 | 点触发按钮 | Drawer 从右滑入，宽 360，rail 48，内容区正确 |
| D2 | 点击 Drawer 外部（透明遮罩区域）任意位置 20 次 | Drawer **始终打开**；无闪烁、无“消失再出现” |
| D3 | 点击右上角 X | Drawer 收起；再次打开状态（Tab/宽度）保持 |
| D4 | 再次点触发按钮 | 收起（等价 X） |
| D5 | 拖拽 Drawer 左边缘 | 宽度连续变化，320~720 夹紧；松开后重启应用仍是上次宽度 |
| D6 | 打开时聚焦输入框再打开 Drawer | 输入框内容与光标不丢（`auto-focus=false`；遮罩不破坏 contenteditable） |
| D7 | Esc | 关闭（若 Q1 保持默认） |
| D8 | 中文输入法编辑目标描述时按 Esc | 只关输入法候选，不关 Drawer |
| D9 | 打开 Drawer 后拖 `n-split` 分隔条 | 分隔条仍在遮罩下方不可拖（遮罩覆盖右工作台），关闭后可拖 —— 且 Drawer 本身位置随右工作台尺寸变化不错位 |
| D10 | 窗口缩放到 1024×700 | Drawer 不超出窗口（`max-width:720` 且 `width=min(360, 86vw)` 兜底）；rail 不被挤压 |
| D11 | 打开设置中心（同为 right drawer） | 两者可同时打开，设置中心在上层（后打开者胜），关闭后右侧栏恢复正常 |
| D12 | AskUser 弹窗出现时 | 弹窗在最上层，可正常作答 |

# 任务3总览：设置与诊断中心 Modal → Drawer 彻底重构

> 外壳：`src/Web.Client/src/components/settings/SettingsCenterModal.vue`（80 行）
> 调用方：`src/Web.Client/src/App.vue` L76-L80，L101-L119
> 内容面板：`ProviderManagerView.vue` / `NetworkProxySettings.vue` / `StorageSettingsPanel.vue` / `SystemRuntimePanel.vue`
> 基座：`src/Web.Client/src/styles/main.css`、`src/Web.Client/src/theme/naiveTheme.ts`、`src/Web.Client/src/stores/systemStore.ts`
> 细分：布局与溢出见 `04`，主题与 Stage 清洗见 `05`。本文只讲“为什么换 Drawer + 换成什么样 + 怎么落地”。

## 1. 现状与痛点映射（已通读原文）

```vue
<!-- SettingsCenterModal.vue L1-L46 现状 -->
<n-modal v-model:show="visible" preset="card" title="Halo-Agent 设置与诊断中心"
         style="width: 960px; max-width: 95vw" :bordered="true" size="small">
  <template #header-extra>
    <n-tag ...>Stage 02 Config & ProxyHub</n-tag>   <!-- 痛点d -->
  </template>
  <n-tabs v-model:value="activeTab" type="line" placement="left" style="min-height: 520px">
    <n-tab-pane name="providers" tab="模型与服务商"><div class="pl-2"><ProviderManagerView /></div></n-tab-pane>
    <n-tab-pane name="network" tab="网络与代理">...</n-tab-pane>
    <n-tab-pane name="storage" tab="存储与日志">...</n-tab-pane>
    <n-tab-pane name="runtime" tab="系统运行环境">...</n-tab-pane>
  </n-tabs>
</n-modal>
```

| 痛点 | 现状根因 |
|------|---------|
| a. Dialog 居中、960px 不够大，不好做 | `n-modal preset=card` 居中弹窗，可用宽=960-2×padding(约24px)-左侧纵向 Tab 栏(约110px)-`pl-2` 缩进≈820px；`ProviderManagerView` 内部又是 `grid-cols-12` 嵌套三层，820px 下每个输入列仅 ~150px，必然拥挤。居中弹窗还会遮挡主工作台，边改边看不可能。 |
| b. 内部组件超出 Dialog 宽度 | 见 `04` 清单：长 endpoint URL（`select-all` 无断行）、`customHeaders` 的 `grid-cols-12`（`col-span-2` 操作列塞了 checkbox+删除按钮）、模型行 `3+3+2+2+2` 列的 `n-input-number` 最小宽度、`n-descriptions column=2/3` 长 mono 路径、三个 `bg-black/*` 预览条无 `break-all`。本质是“固定 12 列网格 + 不可压缩的 Naive 输入 + 不换行文本”三者叠加。 |
| c. 亮色下选中态仍是深色底 | `ProviderManagerView.vue L28-L32`、`NetworkProxySettings.vue L24-L73` 把选中/未选中写死为 `bg-blue-500/10` vs `bg-black/15 + hover:bg-white`。`bg-black/15` 在亮色白底上是一层灰，`hover:bg-white` 在白底上不可见；且亮色下“选中应更深、未选中更浅”的反转逻辑完全没有分支（`systemStore.themeMode` 与 `html.light` 变量均未被使用）。 |
| d. 前端出现 `Stage xx` | 用户可见 5 处：Modal 右上角 `Stage 02 Config & ProxyHub`、运行时卡标题 `架构宪章合规性自检清单 (Stage 01)`、存储面板写入测试日志的 3 条 `Stage 01 diagnostic check ...`。注释里的 Stage（`App.vue`、`hostBridge.ts`）不在此列。 |

## 2. 架构决策（只做这 5 个决定，不发散）

1. **外壳换 `n-drawer`，`placement="right"`，全高右侧滑入。** 否决“把 Modal 调大到 1280px”：再大的居中弹窗依然遮挡主工作台、依然只有一半高度可用；Drawer 天然全高、可边改配置边看左侧会话/顶栏水位，符合“设置与诊断中心”的工作流。
2. **新建 `SettingsCenterDrawer.vue` 替换 `SettingsCenterModal.vue`，保留同名 props 接口。** 接口保持 `show: boolean` + `defaultTab?: 'providers'|'network'|'storage'|'runtime'` + `update:show` 事件不变，`App.vue` 只需换 import 与标签名，`openSettingsModal(tab)` 逻辑零改。老文件删除或留作过渡（建议删除，避免双入口）。
3. **Tabs 由 `placement="left"` 纵向改为顶部横向。** Drawer 是纵长空间，左侧纵 Tab 会再吃掉 110px 宽度；顶部横 Tab（`type="line" placement="top"`）+ 内容区独立滚动最省宽。可加 `sticky top-0` 让 Tab 在滚动时保持可见。
4. **选中态不再写 Tailwind 硬编码，收敛到 `main.css` 语义类。** 详见 `05`。所有 `bg-black/* / hover:bg-white` 一次性替换，后续主题只改 CSS 变量。
5. **`Stage` 文案一次性下线，不做兼容开关。** 用户可见层零保留；日志测试文案改为中性英文；注释里的 Stage 可保留（不影响 UI），避免误伤 `docs/develop` 历史文档。

## 3. 目标结构（伪代码，施工时照抄骨架）

```vue
<template>
  <n-drawer v-model:show="visible" placement="right" :width="1020" :mask-closable="true">
    <n-drawer-content title="设置与诊断中心" :native-scrollbar="false" closable>
      <template #header> ... 标题 + 关闭（用 drawer-content 自带 closable 即可，不自造） ... </template>
      <n-tabs v-model:value="activeTab" type="line" placement="top" class="sticky top-0 z-10 bg-[var(--bg-surface)]">
        <n-tab-pane name="providers" tab="模型与服务商">
          <div class="pt-3 min-w-0 max-w-full overflow-x-hidden"><ProviderManagerView /></div>
        </n-tab-pane>
        <!-- network / storage / runtime 同理，禁止再包 pl-2 -->
      </n-tabs>
    </n-drawer-content>
  </n-drawer>
</template>
<script setup lang="ts">
 // 与老 Modal 完全同构：props show/defaultTab、emit update:show、activeTab ref + watch
</script>
```

- 宽度策略：`width=1020` + CSS `max-width: 94vw`（小窗自动收缩，大屏固定 1020，比原来可用宽多约 200px 且全高可滚）。
- 滚动策略：外层 `n-drawer-content` 负责纵滚，内层各面板 `max-h` 限制（如 `max-h-[540px]`）全部拆除，改为自然高度随 drawer 滚动；横向一律 `overflow-x: hidden`。
- `App.vue` 改动仅 3 行：import 路径、`SettingsCenterModal` → `SettingsCenterDrawer` 标签、`showSettingsModal/settingsModalTab` 原样复用。`StorageSettingsPanel` 内部还有一个 `n-modal preset=dialog`（迁移确认 540px），那是二级确认框，保留为 dialog，不并入 drawer。

## 4. 落地步骤（按序，不可并行改同一文件）

1. 新建 `SettingsCenterDrawer.vue`（外壳先跑通，内容面板暂不改）→ `App.vue` 切换引用 → 确认开/关、遮罩、Esc、四个 Tab 可切。
2. 按 `04` 清单逐面板修溢出（Provider → Network → Storage → Runtime）。
3. 按 `05` 清单做主题语义化 + Stage 清洗。
4. 双主题 × 双宽度（1280/1600）回归 + `vue-tsc -b && vite build`。

## 5. 约束与红线

- `naive-ui 2.45.3` 原生支持 `n-drawer / n-drawer-content`，不加依赖；所有新增 CSS 只用 chrome109 安全特性（`var()`、`flex/grid`、`min()`），禁用容器查询、`:has()`、`color-mix()`。
- 既有 `data-testid`（`provider-manager-panel`、`network-proxy-settings-panel`、`open-settings-btn` 等）一律保留；`openSettingsModal('providers'|'network'|'storage'|'runtime')` 四路入口行为不变。
- 焦点管理：Drawer 默认 trap-focus，打开时不抢 `PromptComposer` 的 Enter/Esc（输入舱在 drawer 下层，遮罩关闭后焦点自动归还，无需额外处理）。

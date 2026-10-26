# need02-P0 · 基线录制表（手工）

> 来源：`docs/need-02/04-P0-基线与总开关.md §基线录制`。
> 目标：P1–P4 动工前把“当前行为”钉死。P0 本身**零行为变更**——开关全 `off`
>（`data-need02="p1:off,p2:off,p3:off,p4:off"`）时必须与本基线逐像素一致。

## 0. 开关准备

```js
// 全关（= 基线行为）：逐个设 off
localStorage.setItem('halo-need02-p1', 'off');
localStorage.setItem('halo-need02-p2', 'off');
localStorage.setItem('halo-need02-p3', 'off');
localStorage.setItem('halo-need02-p4', 'off');
// 回默认（全开）：['halo-need02-p1','halo-need02-p2','halo-need02-p3','halo-need02-p4'].forEach(k => localStorage.removeItem(k))
```

验证点：时间轴根节点（`data-testid="message-timeline"`）应带有
`data-need02="p1:off,p2:off,p3:off,p4:off"`（全开时为 `p1:on,p2:on,p3:on,p4:on`）。

## 1. 基线场景

| # | 场景 | 步骤 | 留存物 | 基线结果（填写） |
| :--- | :--- | :--- | :--- | :--- |
| B1 | 普通问答（1 user + 1 正文） | 新会话 → 发送一句话 → 等终态 | Performance profile + 截图 + `sessionMessages.json` | _待录_ |
| B2 | 8 工具无工具正文轮 | 触发多工具任务（无正文）→ 等终态 | 同上 + `timelineItems` 快照 | _待录_ |
| B3 | 3 iteration 混合（正文+工具+失败1） | 触发混合任务 → 等终态 | 同上（P2 对比母版） | _待录_ |
| B4 | 图片 2 张 + 超限 1 张 | 发送含图消息（含超限图） | 缩略图时序录屏 | _待录_ |
| B5 | 切会话/重载/compact 各 1 次 | 流中切走切回；重载页面；触发 compact | veil/跟随行为录屏 | _待录_ |

## 2. P0 验收门

- [ ] 开关全 `on` / 全 `off` 各跑一次既有全量测试（`pnpm test`），零回退。
- [ ] `data-need02` 全 `off` 时，B1–B5 与本基线截图逐像素一致。
- [ ] `need02.spec.ts`（开关矩阵）全绿。

## 3. 回滚（P0）

- 运行期：四个 `localStorage` 键全部 `removeItem` 即回默认全开。
- 代码级：删除 `src/features/need02/utils/` 下 3 个新增文件
 （`need02Flags.ts` / `need02Debug.ts` / `need02.spec.ts`），
  并撤销 `useTimeline.ts` 的开关透传行、`TimelineView.vue` 的 `need02Attr`
  prop + `data-need02` 绑定、`MessageTimeline.vue` 的 `:need02-attr` 透传。

# need22-04c P3 性能与桥接验收与回滚

> 对应方案：[04b](./04b-p3-perf-plan.md)。

## 1. 前置检查

- [ ] P2 验收通过；`npx vue-tsc -b` 绿。
- [ ] Performance 录制环境就绪（Chrome + 200 消息 fixture）。

## 2. 验收矩阵

| # | 验收项 | 标准 | 方法 |
|---|---|---|---|
| D1 长会话 | 200 消息 + 3k 字流式，无 >200ms 长任务；单帧增量 <5ms | DevTools Performance 录制 + 断言 | fixture 回放 |
| D2 选择器 | `data-testid/row-id/kind` 一个不少；折叠/右键/复制不变 | 回归截图 + 选择器计数 | 截图 diff |
| D3 图片 | 10 张 5MB 图轮内存增量 <200MB 且切会话后回收 | 任务管理器快照 ×3 | 手工 |
| D4 子会话 | 失活页签无 3s 轮询；事件驱动实时性 <1s | Network 面板 + 时间戳 | 观察 |
| D5 桥接 | 断桥接红色横幅 + 重试按钮，不再静默 Mock；`BRIDGE_VERSION` 握手失败有明确错误 | 杀 `BindObjectAsync` fixture + 截图 | fixture |
| D6 错误 | 断网/429 有 toast + 重试；`turnNotice` 可查；`budget` 失败有角标 | 拔网 + mock 429 | 手工 |

## 3. 验证命令

```bash
npx vue-tsc -b
ls -lh ../Host.Desktop/wwwroot/assets
# + Performance 录制 + Network 观察（存档截图）
```

## 4. 回滚表

| 场景 | 动作 |
|---|---|
| G1 虚拟列表跳动/白屏 | 回滚 `g1-vlist`，恢复全量渲染，其余保留 |
| G5 拆桥接导致调用失败 | 回滚 `g5-bridge-split`，恢复单文件（保留错误码） |
| G3 压缩导致预览模糊被吐槽 | 调压缩阈值（5MB→10MB），不回滚 |

## 5. 准出 gate

* D1–D6 全过才能进 P4/P5。

# HaloAgent

Windows 7 SP1 / 10 / 11 桌面 AI Agent：用自然语言下达任务，自主调用工具一步步完成。

> 架构铁律见 [CEFSHARP_ARCHITECTURE_STANDARD.md](docs/CEFSHARP_ARCHITECTURE_STANDARD.md)（最高优先级文档）。
> 各阶段计划见 `docs/P0-骨架与地基/` ～ `docs/P6-打包分发与Win7验收/`。

## 快速开始

```powershell
# 一条命令：契约生成 → 前端 → 后端 → 测试 → 锁校验
pwsh -File scripts/build-all.ps1
```

产物：

- 宿主：`src/Host.Desktop/bin/Release/net6.0-windows/HaloAgent.exe`
- 引擎：`src/Engine.Server/bin/Release/net6.0-windows/HaloAgent.Engine.exe`
- 无头：`src/Engine.Cli/bin/Release/net6.0-windows/HaloAgent.Cli.exe`

## 常用命令

```powershell
# 仅引擎往返（无 UI）
'{"t":"req","id":1,"m":"system.ping"}' | & src\Engine.Server\bin\Release\net6.0-windows\HaloAgent.Engine.exe --stdio

# 无头单发
src\Engine.Cli\bin\Release\net6.0-windows\HaloAgent.Cli.exe -p system.ping

# 双端诊断
src\Host.Desktop\bin\Release\net6.0-windows\HaloAgent.exe --diagnostics
src\Engine.Server\bin\Release\net6.0-windows\HaloAgent.Engine.exe --diagnostics

# 前端独立运行（Mock，无需宿主/引擎）
cd src\Web.Client; pnpm dev   # http://127.0.0.1:5173

# 版本锁校验
pwsh -File scripts/verify-locks.ps1
```

## 目录

- `src/Engine.Abstractions` — 帧/DTO/枚举/错误码（零依赖）
- `src/Engine.Core` — agent 循环与工具（P2）
- `src/Engine.Storage` — 配置/会话/记忆（P2）
- `src/Engine.Server` — 管道帧服务 + 生命周期
- `src/Engine.Cli` — 无头入口
- `src/Host.Desktop` — WinForms + CefSharp 宿主
- `src/Web.Client` — Vue 3 + Tailwind v3 前端
- `contract/agent.schema.json` — 唯一通信契约真源
- `tests/` — 契约一致性 + stdio 往返测试

# need18-04 老窗口唤起：NativeWindowActivator + WM_SHOWME

> 对应总览：[00-README](./00-README.md)。依赖 [03](./03-program-integration.md) 的调用点 `NativeWindowActivator.TryBringToFront("Halo-Agent Desktop")`。改动：新增 1 文件 + `MainForm.cs` 加 `WndProc`（约 20 行）。

## 1. 为什么需要两级唤起

第二实例退出前若只弹框不唤起，用户会困惑“我点了图标没反应”。唤起分两级，可靠性递进：

```text
L1 FindWindow（同步，必做）：第二实例用 user32.FindWindow 找老窗口 → ShowWindow(SW_RESTORE) → SetForegroundWindow
   └─ 优点：零改老进程，单文件即可；缺点：标题被改/最小化到托盘时可能找不到（本项目标题固定 "Halo-Agent Desktop"，见 MainForm.cs:32，够用）。

L2 WM_SHOWME 广播（增强，建议做）：第二实例 PostMessage(HWND_BROADCAST, WM_SHOWME) → 老 MainForm.WndProc 收到 → 还原+置顶
   └─ 优点：标题无关，必达；缺点：需改 MainForm（20 行），需定义私有消息号。
```

本篇两级都做，`TryBringToFront` 内部先 L2 广播再 L1 精确，任一成功即返回 `true`。

## 2. 新增文件规约

```text
develop/src/Host.Desktop/Infrastructure/Process/NativeWindowActivator.cs
namespace Host.Desktop.Infrastructure.Process;
public static class NativeWindowActivator
{
    public const int WM_SHOWME = 0x0400 + 0x1F; // WM_APP+0x1F，私有区，不与系统冲突
    public static bool TryBringToFront(string windowTitle); // L2+L1，先广播后精确
    public static bool BroadcastShowMe();  // L2：PostMessage(HWND_BROADCAST, WM_SHOWME, IntPtr.Zero, IntPtr.Zero)
    public static bool BringByTitle(string title); // L1：FindWindow+ShowWindow+SetForeground
}
```

P/Invoke（Win7 全兼容，`user32.dll` 常驻）：

```csharp
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
[DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
const int SW_RESTORE = 9;
static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
```

`BringByTitle` 流程：

```csharp
var h = FindWindow(null, title);
if (h == IntPtr.Zero) return false;
if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
SetForegroundWindow(h);
return true;
```

全部包 try/catch，失败返回 `false`（唤起失败不阻塞第二实例退出，只 trace `activate-failed`）。

## 3. MainForm 改动（L2 接收端，约 20 行）

在 `develop/src/Host.Desktop/MainForm.cs` 加：

```csharp
protected override void WndProc(ref Message m)
{
    if (m.Msg == NativeWindowActivator.WM_SHOWME)
    {
        try {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true; TopMost = false; // 闪置顶一次，抢回任务栏焦点
            Activate();
            HaloLogger.App.Info("MainForm activated by second-instance WM_SHOWME.");
        } catch { /* 关闭期吞掉 */ }
        return;
    }
    base.WndProc(ref m);
}
```

* 不改构造签名，不改 `OnFormClosing`（`JsEventDispatcher.Shutdown` 等原样）。
* `using Host.Desktop.Infrastructure.Process;` 加在文件头（`MainForm.cs:1-8` 区）。
* 若 review 坚持最小 diff，可只做 L1（不改 MainForm），L2 记为可选增强，但本文档默认两级全做。

## 4. 时序与 trace

```text
第二实例：single-instance-blocked → BroadcastShowMe → BringByTitle → MessageBox → Exit(42)
第一实例：WndProc(WM_SHOWME) → 还原+Activate → App.log "activated by second-instance"
```

新增关键字（进 06 §4 表）：`activate-ok title|broadcast`、`activate-failed`。唤起失败不改变退出语义（第二实例照样退 42）。

## 5. Win7/权限注意

* `SetForegroundWindow` 在 Win7 下若调用进程非前台可能被闪烁抑制，属系统行为，接受（任务栏闪烁即成功信号）。
* `HWND_BROADCAST` 广播需老进程消息泵存活，`Application.Run` 中必然存活；若老进程卡死在 `Cef.Initialize`，L1/L2 都失败，属可接受降级（用户看 MessageBox 后手动杀进程，05 覆盖）。

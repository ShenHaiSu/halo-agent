using System.Drawing;
using System.Text.Json;
using CefSharp;
using CefSharp.WinForms;
using Host.Desktop.Bridge;
using Host.Desktop.Handlers;
using Host.Desktop.Infrastructure.Logging;
using Host.Desktop.Infrastructure.Process;
using Host.Desktop.Infrastructure.Storage;

namespace Host.Desktop;

/// <summary>
/// 全屏承载 ChromiumWebBrowser 的无冗余宿主窗体。
/// </summary>
public sealed class MainForm : Form
{
    private readonly StoragePathManager _storageManager;
    private readonly bool _gpuDisabled;
    private readonly bool _isDevelopment;
    private readonly ChromiumWebBrowser _chromeBrowser;
    private readonly HostBridgeFacade _hostBridgeFacade;
    private readonly CancellationTokenSource _orphanScanCts = new();
    private int _autoVerifyTriggered;

    public MainForm(StoragePathManager storageManager, bool gpuDisabled = false)
    {
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _gpuDisabled = gpuDisabled;

        var envMode = Environment.GetEnvironmentVariable("HALO_ENV");
        _isDevelopment = string.Equals(envMode, "Development", StringComparison.OrdinalIgnoreCase);

        Text = "Halo-Agent Desktop";
        try
        {
            // 窗口/任务栏图标与 exe 文件图标保持一致（exe 图标来自 Resources/app.ico）
            var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (exeIcon is not null)
            {
                Icon = (Icon)exeIcon.Clone();
                exeIcon.Dispose();
            }
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("Load window icon failed, fallback to default.", ex);
        }
        Width = 1320;
        Height = 840;
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 17, 23); // #0d1117 防止启动瞬间白屏闪烁

        var initialUrl = ResolveInitialUrl();
        HaloLogger.App.Info($"MainForm initializing ChromiumWebBrowser (isDevelopment={_isDevelopment}, url={initialUrl})");

        CefSharpSettings.ConcurrentTaskExecution = true;

         _chromeBrowser = new ChromiumWebBrowser(initialUrl)
         {
             Dock = DockStyle.Fill,
             MenuHandler = new CustomMenuHandler(_isDevelopment),
             LifeSpanHandler = new CustomLifeSpanHandler(),
             // 导航守卫：外部链接永不进主帧（http(s) 转系统浏览器），杜绝 markdown 链接点击白屏
             RequestHandler = new ExternalNavigationRequestHandler()
         };
        _hostBridgeFacade = new HostBridgeFacade(this, _storageManager, _gpuDisabled);

        // 注册异步 JS Bridge 对象至 window.hostBridge
        _chromeBrowser.JavascriptObjectRepository.Settings.LegacyBindingEnabled = false;
        _chromeBrowser.JavascriptObjectRepository.Register(
            "hostBridge",
            _hostBridgeFacade,
            options: BindingOptions.DefaultBinder);

        _chromeBrowser.IsBrowserInitializedChanged += OnBrowserInitializedChanged;
        _chromeBrowser.FrameLoadEnd += OnFrameLoadEnd;
        _chromeBrowser.LoadError += OnLoadError;

        Controls.Add(_chromeBrowser);
        // need22-P4 H9：启动时孤儿 tool_calls 全量扫描（后台执行，不阻塞 UI；随窗体关闭取消；异常进日志不吞）
        _ = Task.Run(async () =>
        {
            try
            {
                var response = await _hostBridgeFacade.ScanAndHealAllSessions(_orphanScanCts.Token).ConfigureAwait(false);
                if (!response.Ok)
                {
                    HaloLogger.App.Error($"[History] startup orphan scan reported failure: {response.Code}");
                }
            }
            catch (OperationCanceledException) { HaloLogger.App.Info("[History] startup orphan scan cancelled."); }
            catch (Exception ex) { HaloLogger.App.Error("[History] startup orphan scan failed", ex); }
        }, _orphanScanCts.Token);
        JsEventDispatcher.AttachBrowser(_chromeBrowser);
    }

    private string ResolveInitialUrl()
    {
        if (_isDevelopment)
        {
            var devUrl = Environment.GetEnvironmentVariable("HALO_DEV_SERVER_URL");
            return !string.IsNullOrWhiteSpace(devUrl) ? devUrl : "http://localhost:5173";
        }

        return "app://local/index.html";
    }

    private void OnBrowserInitializedChanged(object? sender, EventArgs e)
    {
        if (!_chromeBrowser.IsBrowserInitialized)
        {
            return;
        }

        HaloLogger.App.Info("ChromiumWebBrowser initialized.");
        if (_isDevelopment)
        {
            _chromeBrowser.ShowDevTools();
        }
    }

    private void OnFrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain)
        {
            return;
        }

        HaloLogger.App.Info($"Main frame loaded: url={e.Url}, httpStatus={e.HttpStatusCode}");

        var autoVerifyOutput = Environment.GetEnvironmentVariable("HALO_AUTOVERIFY_OUTPUT");
        if (!string.IsNullOrWhiteSpace(autoVerifyOutput) &&
            Interlocked.CompareExchange(ref _autoVerifyTriggered, 1, 0) == 0)
        {
            _ = RunAutoVerifyProbeAsync(autoVerifyOutput);
        }
    }

    private void OnLoadError(object? sender, LoadErrorEventArgs e)
    {
        if (e.ErrorCode == CefErrorCode.Aborted)
        {
            return;
        }

        HaloLogger.App.Error($"Browser LoadError: url={e.FailedUrl}, code={e.ErrorCode}, text={e.ErrorText}");
    }

    /// <summary>
    /// 自动化验收探针（仅当设置了 HALO_AUTOVERIFY_OUTPUT 环境变量时运行）：
    /// 在真实 Chromium 109 渲染帧中检验协议地址、Naive UI + Tailwind 样式隔离、SVG 图标与 hostBridge 双向通信，随后优雅关闭窗口。
    /// </summary>
    private async Task RunAutoVerifyProbeAsync(string reportFilePath)
    {
        try
        {
            // 等待 Vue 3 挂载与 CefSharp.BindObjectAsync('hostBridge') 完成初始化
            await Task.Delay(1800).ConfigureAwait(false);

            const string inspectScript = @"
(async function() {
  if (window.CefSharp && !window.hostBridge) {
    await window.CefSharp.BindObjectAsync('hostBridge');
  }

  // 先触发向三通道写入诊断日志
  if (window.hostBridge) {
    await window.hostBridge.writeDiagnosticLog('app', 'Stage 01 E2E probe: App channel verified.');
    await window.hostBridge.writeDiagnosticLog('llm', 'Stage 01 E2E probe: Authorization: Bearer sk-1234567890abcdef9876 verified.');
    await window.hostBridge.writeDiagnosticLog('tool', 'Stage 01 E2E probe: cmd.exe /c ver verified.');
  }

  const sysRes = window.hostBridge ? await window.hostBridge.getSystemInfo() : null;
  const storageRes = window.hostBridge ? await window.hostBridge.getStorageConfig() : null;
  const networkRes = window.hostBridge ? await window.hostBridge.getNetworkSettings() : null;
  const providersRes = window.hostBridge ? await window.hostBridge.getProvidersConfig() : null;
  const sysProxyRes = window.hostBridge ? await window.hostBridge.inspectSystemProxy('https://api.openai.com') : null;

  let previewRes = null;
  if (window.hostBridge && providersRes && providersRes.ok && providersRes.data && providersRes.data.providers && providersRes.data.providers.length > 0) {
    const firstProv = providersRes.data.providers[0];
    const firstModelId = firstProv.models && firstProv.models.length > 0 ? firstProv.models[0].id : 'gpt-4o';
    previewRes = await window.hostBridge.previewProviderResolution(
      JSON.stringify(firstProv),
      firstModelId,
      'sess_e2e_stage02',
      'F:\\MyProgram\\CSharp\\halo-agent'
    );
  }

  // 触发打开设置弹窗以便一并校验 NModal 与 NTabs 渲染
  const settingsBtn = document.querySelector('[data-testid=""open-settings-btn""]');
  if (settingsBtn instanceof HTMLElement) {
    settingsBtn.click();
    await new Promise(r => setTimeout(r, 450));
  }

  const hasProvidersTabRendered = !!document.querySelector('[data-testid=""provider-manager-panel""]') || !!document.querySelector('[data-testid=""btn-add-provider""]');

  // 切换到网络与代理 Tab 校验 NetworkProxySettings 组件渲染
  const networkTabHeader = Array.from(document.querySelectorAll('.n-tabs-tab')).find(el => el.textContent && el.textContent.includes('网络与代理'));
  if (networkTabHeader instanceof HTMLElement) {
    networkTabHeader.click();
    await new Promise(r => setTimeout(r, 350));
  }
  const hasNetworkTabRendered = !!document.querySelector('[data-testid=""network-proxy-panel""]') || !!document.querySelector('[data-testid=""proxy-mode-system""]');

  const primaryBtn = document.querySelector('.n-button--primary-type');
  const primaryBtnBg = primaryBtn ? window.getComputedStyle(primaryBtn).backgroundColor : null;
  const naiveMeta = document.querySelector('meta[name=""naive-ui-style""]');
  const svgIcons = document.querySelectorAll('svg').length;

  return JSON.stringify({
    href: window.location.href,
    protocol: window.location.protocol,
    hasNaiveStyleMeta: !!naiveMeta,
    hasConfigProvider: !!document.querySelector('.n-config-provider'),
    hasSplitPane: !!document.querySelector('.n-split'),
    hasPrimaryButton: !!primaryBtn,
    primaryButtonBackgroundColor: primaryBtnBg,
    isPrimaryButtonTransparent: primaryBtnBg === 'rgba(0, 0, 0, 0)' || primaryBtnBg === 'transparent',
    hasModalRendered: !!document.querySelector('.n-modal'),
    hasTabsRendered: !!document.querySelector('.n-tabs'),
    hasProvidersTabRendered: hasProvidersTabRendered,
    hasNetworkTabRendered: hasNetworkTabRendered,
    svgIconCount: svgIcons,
    bodyFontFamily: window.getComputedStyle(document.body).fontFamily,
    systemInfoResponse: sysRes,
    storageConfigResponse: storageRes,
    networkSettingsResponse: networkRes,
    providersConfigResponse: providersRes,
    systemProxySnapshotResponse: sysProxyRes,
    providerPreviewResponse: previewRes
  });
})();";

            var evalResponse = await _chromeBrowser.EvaluateScriptAsync(inspectScript, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var jsonPayload = evalResponse.Success && evalResponse.Result is string str
                ? str
                : JsonSerializer.Serialize(new
                {
                    error = evalResponse.Message,
                    success = evalResponse.Success
                });

            var dir = Path.GetDirectoryName(Path.GetFullPath(reportFilePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(reportFilePath, jsonPayload).ConfigureAwait(false);
            HaloLogger.App.Info($"AutoVerify probe report written to '{reportFilePath}'.");
            await HaloLogger.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HaloLogger.App.Error("AutoVerify probe failed", ex);
        }
        finally
        {
            if (!IsDisposed)
            {
                BeginInvoke(new Action(Close));
            }
        }
    }

    // need18-04 L2 接收端：第二实例广播 WM_SHOWME 时还原+置顶（不改构造签名）
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeWindowActivator.WM_SHOWME)
        {
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                TopMost = true; TopMost = false; // 闪置顶一次，抢回任务栏焦点
                Activate();
                HaloLogger.App.Info("MainForm activated by second-instance WM_SHOWME.");
            }
            catch { /* 关闭期吞掉 */ }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { _orphanScanCts.Cancel(); } catch { /* 关闭期忽略 */ }
        _orphanScanCts.Dispose();
        JsEventDispatcher.Shutdown();
        _hostBridgeFacade.HttpClientFactory.Dispose();
        if (!_chromeBrowser.IsDisposed)
        {
            _chromeBrowser.Dispose();
        }
        base.OnFormClosing(e);
    }
}

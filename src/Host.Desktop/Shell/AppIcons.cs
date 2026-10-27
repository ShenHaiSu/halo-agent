using System.Reflection;
using System.Windows.Forms;

namespace HaloAgent.Desktop.Shell;

/// <summary>应用图标：从嵌入资源加载（不依赖外部 .ico，防安装漏拷/误删）。.ico 应含多尺寸。</summary>
public sealed class AppIcons : IDisposable
{
    public static AppIcons Default { get; } = new();

    public Icon AppIcon { get; }
    public Icon TrayIcon { get; }

    private AppIcons()
    {
        AppIcon = LoadEmbedded("app.ico") ?? SystemIcons.Application;
        TrayIcon = LoadEmbedded("tray.ico") ?? AppIcon;
    }

    private static Icon? LoadEmbedded(string name)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var full = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            if (full is null) return null;
            using var s = asm.GetManifestResourceStream(full);
            return s is null ? null : new Icon(s);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        // Default 是静态单例，进程退出时不必 Dispose；留空实现以便 using 不告警。
    }
}

namespace HaloAgent.Desktop.Diagnostics;

public sealed record MissingPrerequisite(string Title, string Detail, string DownloadHint);

/// <summary>运行前依赖探测：只在确实缺时弹窗，且必须中文+可操作。</summary>
public static class PrerequisiteProbe
{
    public static IReadOnlyList<MissingPrerequisite> Check()
    {
        var missing = new List<MissingPrerequisite>();

         // VC++ 运行库：用真实 LoadLibrary 探测（文件存在≠能加载）。
         // 注意：vcruntime140_1.dll 仅由 x64 运行库提供（VS2019+ x64 异常处理专用），
         // x86 运行库不含该文件。32 位进程要求它会造成健康机器也误报缺失。
         var vcChecks = Environment.Is64BitProcess
             ? new (string Dll, string Friendly)[]
             {
                 ("vcruntime140.dll", "Visual C++ 2015+ 运行库"),
                 ("vcruntime140_1.dll", "Visual C++ 2015+ 运行库（更新版，仅 64 位）"),
                 ("msvcp140.dll", "Visual C++ 2015+ 标准库"),
             }
             : new (string Dll, string Friendly)[]
             {
                 ("vcruntime140.dll", "Visual C++ 2015+ 运行库"),
                 ("msvcp140.dll", "Visual C++ 2015+ 标准库"),
             };
        var bad = vcChecks.Where(x => !NativeMethods.Present(x.Dll)).Select(x => x.Dll).ToList();
        if (bad.Count > 0)
        {
            missing.Add(new MissingPrerequisite(
                Title: "缺少 Microsoft Visual C++ 运行库",
                Detail: "程序需要以下文件，但在系统中未找到：\n  " + string.Join("\n  ", bad),
                DownloadHint: "请安装“Microsoft Visual C++ 2015-2022 运行库”后重试。安装包通常随本程序一起提供（目录下的 vcredist 文件夹）。"));
        }

        // Win7 的 KB2533623。
        if (IsWindows7() && !NativeMethods.HasAddDllDirectory())
        {
            missing.Add(new MissingPrerequisite(
                Title: "Windows 7 缺少必要安全更新",
                Detail: "检测到系统未安装 KB2533623 更新，本程序依赖该更新提供的系统 API。",
                DownloadHint: "请先安装 Windows 更新 KB2533623（可通过 Windows Update 或从 Microsoft 下载中心获取），然后重新启动本程序。"));
        }

        return missing;
    }

    public static bool IsWindows7() =>
        Environment.OSVersion.Version.Major == 6 &&
        Environment.OSVersion.Version.Minor == 1;
}

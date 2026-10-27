namespace HaloAgent.Desktop;

/// <summary>宿主退出码。P6 安装器与自动化测试据此区分失败原因。</summary>
public static class ExitCodes
{
    public const int Success = 0;
    /// <summary>命令行参数非法。</summary>
    public const int BadArguments = 2;
    /// <summary>另一个实例已在运行，本次请求转发成功（不是失败）。</summary>
    public const int AnotherInstance = 3;
    /// <summary>libcef.dll 缺失 / 位数不符 / 子进程缺失。</summary>
    public const int CefBinaryInvalid = 4;
    /// <summary>VC++ / KB2533623 缺失。</summary>
    public const int PrerequisiteMissing = 5;
    /// <summary>Cef.Initialize 返回 false。</summary>
    public const int CefInitFailed = 6;
    /// <summary>主窗口构造或运行异常。</summary>
    public const int MainFormFailed = 7;
    /// <summary>被信号式终止。</summary>
    public const int Terminated = 8;
}

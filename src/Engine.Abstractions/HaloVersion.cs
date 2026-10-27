namespace HaloAgent.Engine.Abstractions;

public static class HaloVersion
{
    /// <summary>引擎程序集版本（由 Directory.Build.props 的 Version 决定）</summary>
    public static string Engine { get; } =
        typeof(HaloVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>帧协议版本。**不兼容变更才 +1**，用于握手校验。</summary>
    public const int ProtocolVersion = 1;
}

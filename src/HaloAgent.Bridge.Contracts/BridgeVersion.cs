namespace HaloAgent.Bridge.Contracts;

/// <summary>
/// need21-B01: 桥协议版本。B02 保持方法签名/JSON 不变时此值不变；
/// B05 用 BridgeVersionTests 断言前后端一致（前端见 src/Web.Client/src/shared/bridge/protocol/version.ts）。
/// </summary>
public static class BridgeVersion
{
    public const string Version = "1.0.0";

    /// <summary>need21-B05: 与前端 BRIDGE_VERSION 对齐的数值协议版本（见 bridge/types.ts 与 shared/bridge/protocol/version.ts）。</summary>
    public const int ProtocolVersion = 2;
}

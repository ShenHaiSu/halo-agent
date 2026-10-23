namespace HaloAgent.Tools;

/// <summary>
/// need21-B04: 工具声明特性。新增工具只加 1 个文件（类上标 [Tool("name")] 即自动注册）。
/// 名称必须与既有 ToolRegistry 注册字符串一致（扫描证据见 Stage17Declaration）。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ToolAttribute : Attribute
{
    public string Name { get; }

    public ToolAttribute(string name) => Name = name;
}

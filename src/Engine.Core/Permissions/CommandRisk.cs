namespace HaloAgent.Engine.Core.Permissions;

public enum CommandRiskKind
{
    /// <summary>没解析出危险点</summary>
    Safe,
    /// <summary>目标无法确定，或属于需点头的中风险</summary>
    Uncertain,
    /// <summary>目标解析出来确实高危</summary>
    Blocked,
}

/// <summary>
/// 一次命令风险判定结果。
///
/// 设计要点：
///   - 用 sealed record 层次（不是 enum + 字段），让"每级都带自己的说明"成为类型保证
///   - Rank 用于"多段取最严重"
///   - Describe() 输出给人看的文案（前端红色框直接用它）
/// </summary>
public abstract record CommandRisk
{
    public abstract CommandRiskKind Kind { get; }
    public abstract int Rank { get; }

    /// <summary>给人看的说明（含具体原因与目标）</summary>
    public abstract string Describe();

    public static readonly SafeRisk Safe = new();

    public static CommandRisk Uncertain(string reason, string detail) => new UncertainRisk(reason, detail);
    public static CommandRisk Blocked(string reason, string target) => new BlockedRisk(reason, target);
}

public sealed record SafeRisk : CommandRisk
{
    public override CommandRiskKind Kind => CommandRiskKind.Safe;
    public override int Rank => 0;
    public override string Describe() => "安全";
}

public sealed record UncertainRisk(string Reason, string Detail) : CommandRisk
{
    public override CommandRiskKind Kind => CommandRiskKind.Uncertain;
    public override int Rank => 1;
    public override string Describe() =>
        $"⚠ {Reason}\n{Detail}\n（含变量/通配符或间接调用，判定看不穿）";
}

public sealed record BlockedRisk(string Reason, string Target) : CommandRisk
{
    public override CommandRiskKind Kind => CommandRiskKind.Blocked;
    public override int Rank => 2;
    public override string Describe() => $"⚠ {Reason}\n解析出的目标：{Target}";
}

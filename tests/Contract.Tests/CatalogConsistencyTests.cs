using HaloAgent.Bridge.Contracts;
using Xunit;

namespace HaloAgent.Contract.Tests;

/// <summary>
/// Three-way consistency: the schema file, the C# catalog and the generated
/// frontend types must describe the same frozen method/event/error sets and
/// the same contract version. Any drift fails the build.
/// </summary>
public sealed class CatalogConsistencyTests
{
    [Fact]
    public void SchemaVersion_MatchesCSharpCatalog()
    {
        using var schema = ContractFiles.LoadSchema();
        string version = schema.RootElement.GetProperty("contractVersion").GetString()!;
        Assert.Equal(AgentContract.ContractVersion, version);
    }

    [Fact]
    public void SchemaMethods_MatchCSharpCatalog()
    {
        using var schema = ContractFiles.LoadSchema();
        var fromSchema = ContractFiles.ReadStringArray(schema.RootElement, "methods");
        Assert.Equal(AgentContract.Methods, fromSchema);
    }

    [Fact]
    public void SchemaEvents_MatchCSharpCatalog()
    {
        using var schema = ContractFiles.LoadSchema();
        var fromSchema = ContractFiles.ReadStringArray(schema.RootElement, "events");
        Assert.Equal(AgentContract.Events, fromSchema);
    }

    [Fact]
    public void SchemaErrorCodes_MatchCSharpCatalog()
    {
        using var schema = ContractFiles.LoadSchema();
        var fromSchema = ContractFiles.ReadStringArray(schema.RootElement, "errorCodes");
        Assert.Equal(AgentContract.ErrorCodes, fromSchema);
    }

    [Fact]
    public void SchemaEnums_MatchTopLevelLists()
    {
        using var schema = ContractFiles.LoadSchema();
        var definitions = schema.RootElement.GetProperty("definitions");
        Assert.Equal(
            ContractFiles.ReadStringArray(schema.RootElement, "methods"),
            ReadEnum(definitions, "methodName")
        );
        Assert.Equal(
            ContractFiles.ReadStringArray(schema.RootElement, "events"),
            ReadEnum(definitions, "eventKind")
        );
        Assert.Equal(
            ContractFiles.ReadStringArray(schema.RootElement, "errorCodes"),
            ReadEnum(definitions, "errorCode")
        );
    }

    [Fact]
    public void GeneratedTypes_ContainVersionAndEveryName()
    {
        string generated = ContractFiles.ReadGeneratedTypes();
        Assert.Contains(AgentContract.ContractVersion, generated, StringComparison.Ordinal);
        foreach (string name in AgentContract.Methods)
        {
            Assert.Contains($"\"{name}\"", generated, StringComparison.Ordinal);
        }

        foreach (string name in AgentContract.Events)
        {
            Assert.Contains($"\"{name}\"", generated, StringComparison.Ordinal);
        }

        foreach (string code in AgentContract.ErrorCodes)
        {
            Assert.Contains($"\"{code}\"", generated, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GeneratedTypes_AreMarkedMachineOutput()
    {
        string generated = ContractFiles.ReadGeneratedTypes();
        Assert.Contains("generated", generated, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadEnum(
        System.Text.Json.JsonElement definitions,
        string name
    )
    {
        var node = definitions.GetProperty(name);
        return ContractFiles.ReadStringArray(node, "enum");
    }
}

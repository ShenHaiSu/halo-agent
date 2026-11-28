using System.Text.Json;
using Host.Desktop.Core.Models;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.ModelCatalog;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// 供应商模型解析单测：只信 data[].id，外层 success/object 与深层 cost/limit 一律忽略。
/// </summary>
public sealed class ProviderModelsParserTests
{
    private static string LoadFixture(string fileName)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "TestData", fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            string slnProbe = Path.Combine(dir, "HaloAgent.sln");
            if (File.Exists(slnProbe))
            {
                candidate = Path.Combine(dir, "tests", "HaloAgent.Tests", "TestData", fileName);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                break;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new FileNotFoundException($"找不到解析固件：{fileName}");
    }

    [Fact]
    public void Parse_CodexPackage_ShouldReturnElevenSortedModels()
    {
        string json = LoadFixture("catalog-codex2-models.json");

        var models = ProviderModelsParser.Parse(json, out int skipped);

        Assert.Equal(11, models.Count);
        Assert.Equal(0, skipped);
        Assert.Contains(models, m => m.Id == "gpt-6-astra");
        Assert.Contains(models, m => m.Id == "codex-auto-review");
        for (int i = 1; i < models.Count; i++)
        {
            Assert.True(
                string.Compare(models[i - 1].Id, models[i].Id, StringComparison.Ordinal) < 0
            );
        }
    }

    [Fact]
    public void Parse_OpencodePackage_ShouldReturnThirtySevenModels()
    {
        string json = LoadFixture("catalog-opencode-models.json");

        var models = ProviderModelsParser.Parse(json, out int skipped);

        Assert.Equal(37, models.Count);
        Assert.Equal(0, skipped);
        Assert.Contains(models, m => m.Id == "beam-free");
        Assert.Contains(models, m => m.Id == "deepseek-v4-pro");
    }

    [Fact]
    public void Parse_DirtyPayload_ShouldSkipInvalidAndDeduplicate()
    {
        string longId = new('x', 300);
        string json =
            "{\"data\": ["
            + "{\"id\": \"  glm-5.3  \"},"
            + "{\"id\": \"glm-5.3\"},"
            + "{\"id\": \"GLM-5.3\"},"
            + "{\"id\": 123},"
            + "{\"id\": null},"
            + "{\"id\": \"   \"},"
            + "\"trimmed-id\","
            + "{\"owned_by\": \"openai\"},"
            + "{\"id\": \""
            + longId
            + "\"}"
            + "]}";

        var models = ProviderModelsParser.Parse(json, out int skipped);

        Assert.Equal(3, models.Count);
        Assert.Contains(models, m => m.Id == "glm-5.3");
        Assert.Contains(models, m => m.Id == "GLM-5.3");
        Assert.Contains(models, m => m.Id == "trimmed-id");
        Assert.Equal("GLM-5.3", models[0].Id);
        Assert.Equal("glm-5.3", models[1].Id);
        Assert.True(skipped >= 5);
    }

    [Fact]
    public void Parse_MissingOrNonArrayData_ShouldReturnEmpty()
    {
        Assert.Empty(ProviderModelsParser.Parse(null));
        Assert.Empty(ProviderModelsParser.Parse(string.Empty));
        Assert.Empty(ProviderModelsParser.Parse("not-json", out int skippedBad));
        Assert.Empty(ProviderModelsParser.Parse("{\"object\":\"list\"}"));
        Assert.Empty(ProviderModelsParser.Parse("{\"data\":{\"id\":\"x\"}}"));
        Assert.Equal(0, skippedBad);
    }

    [Fact]
    public void Parse_OwnedByAndCreatedMutations_ShouldNotChangeResult()
    {
        string json = LoadFixture("catalog-codex2-models.json");

        var baseline = ProviderModelsParser.Parse(json);
        using var doc = JsonDocument.Parse(json);
        var mutated = new List<object?>();
        foreach (JsonElement el in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            mutated.Add(
                new Dictionary<string, object?>
                {
                    ["id"] = el.GetProperty("id").GetString(),
                    ["owned_by"] = "mutated-owner",
                    ["created"] = 1
                }
            );
        }

        string mutatedJson = JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["data"] = mutated },
            HaloJsonOptions.IndentedCamelCase
        );
        var mutatedModels = ProviderModelsParser.Parse(mutatedJson);

        Assert.Equal(
            baseline.Select(m => m.Id).ToArray(),
            mutatedModels.Select(m => m.Id).ToArray()
        );
    }
}

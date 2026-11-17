#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Host.Desktop.Tools.BuiltIn;
using Xunit;

namespace HaloAgent.Tests;

/// <summary>
/// Unified Diff 生成器的展示层回归守卫：新增行计数、片段合并、补丁可应用性与边界行不断裂。
/// </summary>
public sealed class EditDiffBuilderTests
{
    private static string JoinLf(params string[] lines) => string.Join("\n", lines) + "\n";

    private static int CountRows(string diff, char prefix, string header)
    {
        return diff.Split('\n')
            .Count(l =>
                l.Length > 0 && l[0] == prefix && !l.StartsWith(header, StringComparison.Ordinal)
            );
    }

    private static int CountAdded(string diff) => CountRows(diff, '+', "+++");

    private static int CountRemoved(string diff) => CountRows(diff, '-', "---");

    private static int CountHunks(string diff) =>
        diff.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal));

    private static readonly Regex HunkHeader =
        new(@"^@@ -(\d+),(\d+) \+(\d+),(\d+) @@", RegexOptions.Compiled | RegexOptions.Multiline);

    private static List<(int OldStart, int OldCount, int NewStart, int NewCount)> ParseHeaders(
        string diff
    )
    {
        return HunkHeader
            .Matches(diff)
            .Select(m =>
                (
                    int.Parse(m.Groups[1].Value),
                    int.Parse(m.Groups[2].Value),
                    int.Parse(m.Groups[3].Value),
                    int.Parse(m.Groups[4].Value)
                )
            )
            .ToList();
    }

    /// <summary>断言每个片段头部声称的行数与紧随其后的正文行数一致。</summary>
    private static void VerifyHeadersMatchBody(string diff)
    {
        string[] lines = diff.Split('\n');
        var headers = ParseHeaders(diff);
        Assert.NotEmpty(headers);

        int headerIndex = 0;
        int oldSide = 0;
        int newSide = 0;
        bool inBody = false;

        void Flush()
        {
            if (inBody)
            {
                Assert.Equal(headers[headerIndex].OldCount, oldSide);
                Assert.Equal(headers[headerIndex].NewCount, newSide);
                headerIndex++;
            }

            oldSide = 0;
            newSide = 0;
            inBody = false;
        }

        foreach (string line in lines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                inBody = true;
            }
            else if (inBody && line.StartsWith(" ", StringComparison.Ordinal))
            {
                oldSide++;
                newSide++;
            }
            else if (
                inBody
                && line.StartsWith("-", StringComparison.Ordinal)
                && !line.StartsWith("---", StringComparison.Ordinal)
            )
            {
                oldSide++;
            }
            else if (
                inBody
                && line.StartsWith("+", StringComparison.Ordinal)
                && !line.StartsWith("+++", StringComparison.Ordinal)
            )
            {
                newSide++;
            }
        }

        Flush();
        Assert.Equal(headers.Count, headerIndex);
    }

    /// <summary>用旧行逐片段重放补丁，断言能精确得到新行。</summary>
    private static void VerifyPatchReplaysToNew(
        string oldContentLf,
        string newContentLf,
        string diff
    )
    {
        List<string> oldLines = oldContentLf.EndsWith("\n", StringComparison.Ordinal)
            ? oldContentLf.Substring(0, oldContentLf.Length - 1).Split('\n').ToList()
            : oldContentLf.Split('\n').ToList();
        List<string> newLines = newContentLf.EndsWith("\n", StringComparison.Ordinal)
            ? newContentLf.Substring(0, newContentLf.Length - 1).Split('\n').ToList()
            : newContentLf.Split('\n').ToList();

        var headers = ParseHeaders(diff);
        string[] lines = diff.Split('\n');

        var bodies = new List<List<string>>();
        List<string>? current = null;
        foreach (string line in lines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = new List<string>();
                bodies.Add(current);
            }
            else if (
                current != null
                && line.Length > 0
                && (line[0] == ' ' || line[0] == '-' || line[0] == '+')
            )
            {
                if (
                    line.StartsWith("---", StringComparison.Ordinal)
                    || line.StartsWith("+++", StringComparison.Ordinal)
                )
                {
                    continue;
                }

                current.Add(line);
            }
        }

        Assert.Equal(headers.Count, bodies.Count);

        var rebuilt = new List<string>(oldLines);
        for (int h = headers.Count - 1; h >= 0; h--)
        {
            var header = headers[h];
            List<string> body = bodies[h];

            var expectOld = new List<string>();
            var replacement = new List<string>();
            foreach (string row in body)
            {
                if (row[0] == ' ')
                {
                    expectOld.Add(row.Substring(1));
                    replacement.Add(row.Substring(1));
                }
                else if (row[0] == '-')
                {
                    expectOld.Add(row.Substring(1));
                }
                else
                {
                    replacement.Add(row.Substring(1));
                }
            }

            Assert.Equal(header.OldCount, expectOld.Count);
            Assert.Equal(header.NewCount, replacement.Count);

            List<string> actualOld = rebuilt
                .Skip(header.OldStart - 1)
                .Take(header.OldCount)
                .ToList();
            Assert.Equal(expectOld, actualOld);

            rebuilt.RemoveRange(header.OldStart - 1, header.OldCount);
            rebuilt.InsertRange(header.OldStart - 1, replacement);
        }

        Assert.Equal(newLines, rebuilt);
    }

    [Fact]
    public void BuildMulti_PlusRowsEqualNewCount()
    {
        string oldContent = JoinLf(
            "line01: alpha",
            "line02: BETA",
            "line03: gamma",
            "line04: DELTA",
            "line05: epsilon",
            "line06: zeta"
        );
        string newContent = JoinLf(
            "line01: ALPHA",
            "line02: BETA",
            "line03: gamma",
            "line04: DELTA",
            "line05: EPSILON",
            "line06: zeta"
        );
        var spans = new List<EditDiffBuilder.BatchSpan> { new(1, 1, 1), new(5, 1, 1), };

        var (diff, _) = EditDiffBuilder.BuildMulti("sample.txt", oldContent, newContent, spans);

        Assert.Equal(2, CountAdded(diff));
        Assert.Equal(2, CountRemoved(diff));
        Assert.Contains(" ", diff);
        VerifyHeadersMatchBody(diff);
        VerifyPatchReplaysToNew(oldContent, newContent, diff);
    }

    [Fact]
    public void BuildMulti_AdjacentSpansMergeToOneHunk()
    {
        string oldContent = JoinLf("A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8");
        string newContent = JoinLf("A1", "B2", "A3", "A4", "B5", "A6", "A7", "A8");
        var spans = new List<EditDiffBuilder.BatchSpan> { new(2, 1, 1), new(5, 1, 1), };

        var (diff, _) = EditDiffBuilder.BuildMulti("sample.txt", oldContent, newContent, spans);

        Assert.Equal(1, CountHunks(diff));
        Assert.Equal(2, CountAdded(diff));
        VerifyHeadersMatchBody(diff);
        VerifyPatchReplaysToNew(oldContent, newContent, diff);
    }

    [Fact]
    public void BuildMulti_DiffAppliesCleanly()
    {
        string oldContent = JoinLf(
            "line01: alpha",
            "line02: BETA",
            "line03: gamma",
            "line04: DELTA",
            "line05: epsilon",
            "line06: zeta"
        );
        string newContent = JoinLf(
            "line01: ALPHA",
            "line02: BETA",
            "line03: gamma",
            "line04: DELTA",
            "line05: EPSILON",
            "line06: zeta"
        );
        var spans = new List<EditDiffBuilder.BatchSpan> { new(1, 1, 1), new(5, 1, 1), };

        var (diff, _) = EditDiffBuilder.BuildMulti("sample.txt", oldContent, newContent, spans);
        VerifyHeadersMatchBody(diff);

        string dir = Path.Combine(Path.GetTempPath(), "DiffCheck_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "sample.txt"), oldContent);
            File.WriteAllText(Path.Combine(dir, "change.diff"), diff);

            try
            {
                var start = new ProcessStartInfo("git", "apply --check change.diff")
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(start);
                Assert.NotNull(proc);
                string error = proc.StandardError.ReadToEnd();
                bool exited = proc.WaitForExit(15000);
                Assert.True(exited, "git apply --check timed out");
                Assert.True(proc.ExitCode == 0, "git apply --check rejected diff: " + error);
            }
            catch (Exception ex)
                when (ex is System.ComponentModel.Win32Exception || ex is FileNotFoundException)
            {
                VerifyPatchReplaysToNew(oldContent, newContent, diff);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    [Fact]
    public void Build_PureInsertKeepsFollowingContext()
    {
        string oldContent = JoinLf("A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8");
        string newContent = JoinLf("A1", "A2", "A3", "INSERTED", "A4", "A5", "A6", "A7", "A8");

        var (diff, _) = EditDiffBuilder.Build("t.txt", oldContent, newContent);

        Assert.Contains("\n+INSERTED\n", diff);
        Assert.Contains("\n A4\n", diff);
        var headers = ParseHeaders(diff);
        Assert.Single(headers);
        Assert.Equal(headers[0].OldCount + 1, headers[0].NewCount);
        VerifyHeadersMatchBody(diff);
        VerifyPatchReplaysToNew(oldContent, newContent, diff);
    }

    [Fact]
    public void Build_PureDeleteDecrementsPlusSide()
    {
        string oldContent = JoinLf("A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8");
        string newContent = JoinLf("A1", "A2", "A3", "A5", "A6", "A7", "A8");

        var (diff, _) = EditDiffBuilder.Build("t.txt", oldContent, newContent);

        Assert.Contains("\n-A4\n", diff);
        var headers = ParseHeaders(diff);
        Assert.Single(headers);
        Assert.Equal(headers[0].OldCount - 1, headers[0].NewCount);
        VerifyHeadersMatchBody(diff);
        VerifyPatchReplaysToNew(oldContent, newContent, diff);
    }

    [Fact]
    public void Build_TrailingNewlineAddsNoEmptyContext()
    {
        string oldContent = "aaa\nbbb\nccc\n";
        string newContent = "aaa\nBBB\nccc\n";

        var (diff, _) = EditDiffBuilder.Build("t.txt", oldContent, newContent);

        Assert.DoesNotContain("\n \n", diff);
        Assert.False(
            diff.Split('\n').Any(l => l == " "),
            "diff must not render a blank context row"
        );
        VerifyHeadersMatchBody(diff);
        VerifyPatchReplaysToNew(oldContent, newContent, diff);
    }

    [Fact]
    public void BuildMulti_FirstLastLineInsertNoOverflow()
    {
        string oldContent = JoinLf("A1", "A2", "A3");

        string insertFirst = JoinLf("HEAD", "A1", "A2", "A3");
        var firstSpans = new List<EditDiffBuilder.BatchSpan> { new(1, 0, 1) };
        var (firstDiff, _) = EditDiffBuilder.BuildMulti(
            "sample.txt",
            oldContent,
            insertFirst,
            firstSpans
        );
        Assert.Equal(1, CountAdded(firstDiff));
        Assert.DoesNotContain("\n \n", firstDiff);
        VerifyHeadersMatchBody(firstDiff);
        VerifyPatchReplaysToNew(oldContent, insertFirst, firstDiff);

        string insertLast = JoinLf("A1", "A2", "A3", "TAIL");
        var lastSpans = new List<EditDiffBuilder.BatchSpan> { new(4, 0, 1) };
        var (lastDiff, _) = EditDiffBuilder.BuildMulti(
            "sample.txt",
            oldContent,
            insertLast,
            lastSpans
        );
        Assert.Equal(1, CountAdded(lastDiff));
        Assert.DoesNotContain("\n \n", lastDiff);
        VerifyHeadersMatchBody(lastDiff);
        VerifyPatchReplaysToNew(oldContent, insertLast, lastDiff);
    }
}

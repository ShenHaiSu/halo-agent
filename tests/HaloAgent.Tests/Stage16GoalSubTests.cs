 using System;
 using System.IO;
 using System.Linq;
 using System.Text.Json;
 using System.Threading.Tasks;
 using Xunit;
 using Host.Desktop.Core.Models;
 using Host.Desktop.Services;
 using Host.Desktop.Tools;
 using Host.Desktop.Tools.BuiltIn;
 using Host.Desktop.Infrastructure.Storage;

namespace HaloAgent.Tests;

/// <summary>need16 Stage16 Goal + SubDetail 后端单测（对应 02 §8 + 05 §7）。</summary>
public sealed class Stage16GoalTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static GoalService NewSvc(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "halo16_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new GoalService(root);
    }

    [Fact]
    public void Stage16Goal_ApplyAction_SevenMatrix()
    {
        var svc = NewSvc(out _);
        var cur = new SessionGoal();
        cur = svc.ApplyAction(cur, "set", Args("{\"title\":\"T\",\"description\":\"D\",\"milestones\":[\"a\",\"b\",\"c\"],\"percent\":10}"));
        Assert.Equal("T", cur.Title);
        Assert.Equal(3, cur.Milestones.Count);
        Assert.Equal(10, cur.Percent);
        cur = svc.ApplyAction(cur, "progress", Args("{\"percent\":40}"));
        Assert.Equal(40, cur.Percent);
        cur = svc.ApplyAction(cur, "complete_milestone", Args("{\"milestone\":\"a\"}"));
        Assert.True(cur.Milestones[0].Done);
        Assert.Equal(33, cur.Percent); // 1/3*100=33
        cur = svc.ApplyAction(cur, "progress", Args("{\"percent\":150}"));
        Assert.Equal(100, cur.Percent);
        cur = svc.ApplyAction(cur, "update", Args("{\"note\":\"n\"}"));
        Assert.Equal("n", cur.Note);
        cur = svc.ApplyAction(cur, "add_milestone", Args("{\"title\":\"d\"}"));
        Assert.Equal(4, cur.Milestones.Count);
        var got = svc.ApplyAction(cur, "get", Args("{}"));
        Assert.Same(cur, got);
        var cleared = svc.ApplyAction(cur, "clear", Args("{}"));
        Assert.False(cleared.HasContent);
        Assert.Throws<GoalValidationException>(() => svc.ApplyAction(cur, "complete_milestone", Args("{\"milestone\":\"missing\"}")));
        Assert.Throws<GoalValidationException>(() => svc.ApplyAction(cur, "bogus", Args("{}")));
    }

    [Fact]
    public async Task Stage16Goal_SubWriteDenied_NoPersist()
    {
         var svc = NewSvc(out string root);
         string ws = "ws1", sess = "sess_main", sub = "sess_sub";
         svc.SaveGoal(ws, sess, new SessionGoal { Title = "Parent" }, "main");
         string path = svc.GoalFilePath(ws, sess);
         var mtime = File.GetLastWriteTimeUtc(path);
         await Task.Delay(50);
         var tool = new GoalTool((w, s) => svc);
         var ctx = new ToolContext { WorkspaceId = ws, SessionId = sub, ParentSessionId = sess, AgentRole = "sub", DataRoot = root };
         var r = await tool.ExecuteAsync(new ToolCallDto("c1", "goal_tool", "{\"action\":\"set\",\"title\":\"HACK\"}"), ctx);
         Assert.False(r.Success);
         Assert.Contains("[GOAL_DENIED]", r.Error ?? r.Output);
         Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
        var g = await tool.ExecuteAsync(new ToolCallDto("c2", "goal_tool", "{\"action\":\"get\"}"), ctx);
        Assert.True(g.Success);
        Assert.Contains("Parent", g.Output);
    }

    [Fact]
    public void Stage16Goal_Corrupt_SelfHeals()
    {
        var svc = NewSvc(out _);
        string ws = "ws", sess = "s";
        string path = svc.GoalFilePath(ws, sess);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{bad json{{{");
        var (goal, corrupted) = svc.TryLoadGoal(ws, sess);
        Assert.True(corrupted);
        Assert.False(goal.HasContent);
        Assert.Throws<GoalCorruptException>(() => svc.LoadGoal(ws, sess));
    }

    [Fact]
    public void Stage16Goal_BuildGoalBlock_EmptyFullLong()
    {
        Assert.Equal(string.Empty, SystemPromptBuilder.BuildGoalBlock(null));
        Assert.Equal(string.Empty, SystemPromptBuilder.BuildGoalBlock(new SessionGoal()));
        var g = new SessionGoal { Title = "T", Description = "D", Percent = 50, Note = "n" };
        for (int i = 0; i < 50; i++) g.Milestones.Add(new GoalMilestone { Title = "m" + i });
        string block = SystemPromptBuilder.BuildGoalBlock(g);
        Assert.Contains("Session Goal", block);
        Assert.Contains("…余", block);
    }

    [Fact]
    public void Stage16Goal_Registry_Counts()
    {
        var main = new ToolRegistry(registerBuiltIns: true);
        Assert.Contains("goal_tool", main.ToolNames);
        Assert.Equal(15, main.ToolNames.Count);
        var sub = ToolRegistryFactory.ForSub(new SubPolicySnapshot());
        Assert.Contains("goal_tool", sub.ToolNames);
        var def = sub.GetDefinitions().First(d => d.Name == "goal_tool");
        Assert.Contains("get only", def.Description + def.Description);
    }
}

public sealed class Stage16SubDetailTests
{
    private static (StoragePathManager mgr, WorkspaceSessionService svc) NewEnv(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "halo16sub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HALO_BOOTSTRAP_PATH", Path.Combine(root, "bootstrap.json"));
        var mgr = StoragePathManager.Initialize(customBootstrapFilePath: Path.Combine(root, "bootstrap.json"), customDefaultDataRoot: Path.Combine(root, "data"), customFallbackDataRoot: Path.Combine(root, "fallback"));
        var repo = new SessionRepository(mgr);
        var svc = new WorkspaceSessionService(repo);
        return (mgr, svc);
    }

    [Fact]
    public async Task Stage16Sub_MainId_Detail_NOT_A_SUB()
    {
        var (_, svc) = NewEnv(out _);
        var ws = await svc.AddOrUpdateWorkspaceAsync(Path.GetTempPath() + "_" + Guid.NewGuid().ToString("N"));
        var main = await svc.CreateSessionAsync(ws.Id, "main");
        var bridge = MakeBridge(ws.Id, svc);
        var r = await bridge.GetSubSessionDetail(ws.Id, main.Id);
        Assert.False(r.Ok);
        Assert.Equal("NOT_A_SUB", r.Code);
    }

    [Fact]
    public async Task Stage16Sub_Missing_SUB_NOT_FOUND()
    {
        var (_, svc) = NewEnv(out _);
        var ws = await svc.AddOrUpdateWorkspaceAsync(Path.GetTempPath() + "_" + Guid.NewGuid().ToString("N"));
        var main = await svc.CreateSessionAsync(ws.Id, "main");
        var bridge = MakeBridge(ws.Id, svc);
        var r = await bridge.GetSubSessionDetail(ws.Id, "nope");
        Assert.False(r.Ok);
        Assert.Equal("SUB_NOT_FOUND", r.Code);
    }

    [Fact]
    public async Task Stage16Sub_ReadOnly_Guards()
    {
        var (_, svc) = NewEnv(out _);
        var ws = await svc.AddOrUpdateWorkspaceAsync(Path.GetTempPath() + "_" + Guid.NewGuid().ToString("N"));
        var main = await svc.CreateSessionAsync(ws.Id, "main");
        var sub = await svc.CreateSubSessionAsync(ws.Id, null, null, null, main.Id, "sub", "lab", "call1", "task");
        await svc.AppendSessionMessageAsync(ws.Id, sub.Id, UnifiedChatMessage.User("hi"));
        var bridge = MakeBridge(ws.Id, svc);
        var t = await bridge.TruncateSessionHistory(ws.Id, sub.Id, "x", false);
        Assert.False(t.Ok); Assert.Equal("SUB_READONLY", t.Code);
        var b = await bridge.BranchSession(ws.Id, sub.Id, "x", true);
        Assert.False(b.Ok); Assert.Equal("SUB_READONLY", b.Code);
        var d = await bridge.DeleteSessionMessage(ws.Id, sub.Id, "x");
        Assert.False(d.Ok); Assert.Equal("SUB_READONLY", d.Code);
        var msgs = await svc.GetSessionMessagesAsync(ws.Id, sub.Id);
        Assert.Equal(1, msgs.Count);
        // 详情 + 分页
        var det = await bridge.GetSubSessionDetail(ws.Id, sub.Id);
        Assert.True(det.Ok);
        Assert.Equal(sub.Id, det.Data!.SubId);
        var paged = await bridge.GetSubSessionMessages(ws.Id, sub.Id, limit: 1);
        Assert.True(paged.Ok);
        Assert.Single(paged.Data!);
    }

     private static Host.Desktop.Bridge.HostBridgeFacade MakeBridge(string wsId, WorkspaceSessionService svc)
     {
         var mgr = StoragePathManager.Initialize();
         return new Host.Desktop.Bridge.HostBridgeFacade(null, mgr, workspaceSessionService: svc);
     }
 }

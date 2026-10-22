using System.Linq;
using Xunit;
using StartupTool.Core.Templates;

namespace StartupTool.Core.Tests;

/// <summary>
/// D9 脚本模板库：内置集合非空、每份模板字段合法、Id 唯一、心跳模板正确预填心跳开关、
/// CreateDraft 产出「新建」语义（全新 Id、Path 留空）并预填模板字段、Find 找不到返回 null。
/// </summary>
public class ScriptTemplatesTests
{
    [Fact]
    public void All_NotEmpty_AndEachTemplateFieldsValid()
    {
        Assert.NotEmpty(ScriptTemplates.All);

        foreach (var t in ScriptTemplates.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Id));
            Assert.False(string.IsNullOrWhiteSpace(t.NameKey));
            Assert.False(string.IsNullOrWhiteSpace(t.DescriptionKey));
            Assert.StartsWith("Str.Template.", t.NameKey);
            Assert.EndsWith(".Name", t.NameKey);
            Assert.EndsWith(".Desc", t.DescriptionKey);
            // 模板「路径/内容」项：脚本文本必须非空（模板不落盘，ScriptText 即其可复制内容）
            Assert.False(string.IsNullOrWhiteSpace(t.ScriptText));
        }
    }

    [Fact]
    public void Ids_AreUnique()
    {
        var ids = ScriptTemplates.All.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void HeartbeatTemplates_PrefillHeartbeatSwitch()
    {
        var heartbeat = ScriptTemplates.All
            .Where(t => t.Id.StartsWith("Heartbeat", System.StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(heartbeat);
        foreach (var t in heartbeat)
        {
            Assert.True(t.HeartbeatEnabled);
            Assert.True(t.HeartbeatIntervalSeconds > 0);
            Assert.True(t.HeartbeatTimeoutSeconds > t.HeartbeatIntervalSeconds);
        }
    }

    [Fact]
    public void CreateDraft_ProducesFreshNewConfig()
    {
        var template = ScriptTemplates.Find("HeartbeatPython");
        Assert.NotNull(template);

        var draft = template!.CreateDraft("my-heartbeat");
        // 新建分支：不沿用模板 Id，而是生成全新 Id；Path 留空由用户填实际脚本路径
        Assert.NotEqual(template.Id, draft.Id);
        Assert.False(string.IsNullOrWhiteSpace(draft.Id));
        Assert.Equal("my-heartbeat", draft.Name);
        Assert.Equal(string.Empty, draft.Path);
        // 预填字段与模板一致
        Assert.True(draft.HeartbeatEnabled);
        Assert.Equal(template.HeartbeatIntervalSeconds, draft.HeartbeatIntervalSeconds);
        Assert.Equal(template.HeartbeatTimeoutSeconds, draft.HeartbeatTimeoutSeconds);
    }

    [Fact]
    public void CreateDraft_EachCallGetsUniqueId()
    {
        var template = ScriptTemplates.Find("MapNetworkDrive");
        Assert.NotNull(template);

        var a = template!.CreateDraft("a");
        var b = template.CreateDraft("b");
        Assert.NotEqual(a.Id, b.Id);
        Assert.True(a.WaitForNetwork); // 映射网络盘模板预填等待网络
    }

    [Fact]
    public void WindowsServiceTemplate_PrefillsElevation()
    {
        var template = ScriptTemplates.Find("WindowsService");
        Assert.NotNull(template);
        Assert.True(template!.RequireElevation);
    }

    [Fact]
    public void Find_ReturnsNull_ForMissingId()
    {
        Assert.Null(ScriptTemplates.Find("nope"));
        Assert.Null(ScriptTemplates.Find(null));
        Assert.Null(ScriptTemplates.Find(string.Empty));
    }
}

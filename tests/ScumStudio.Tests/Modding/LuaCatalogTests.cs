using MoonSharp.Interpreter;
using ScumStudio.Modding.ServerMods;
using Xunit;

namespace ScumStudio.Tests.Modding;

public sealed class LuaCatalogTests
{
    [Fact]
    public void EntriesAreUniqueAndComplete()
    {
        var ids = LuaApiCatalog.All.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var e in LuaApiCatalog.All)
        {
            Assert.Contains(e.Group, LuaApiCatalog.Groups);
            Assert.False(string.IsNullOrWhiteSpace(e.Name), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.Text), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.TextRu), e.Id);
        }

        foreach (var g in LuaApiCatalog.Groups)
        {
            Assert.Contains(LuaApiCatalog.All, e => e.Group == g);
        }
    }

    [Fact]
    public void EverySnippetParses()
    {
        foreach (var e in LuaApiCatalog.All.Where(e => e.Snippet.Length > 0))
        {
            new Script().LoadString(e.Snippet, null, e.Id);
        }
    }

    [Fact]
    public void ServerTemplatesPassTheCheck()
    {
        foreach (var t in ServerModTemplates.All)
        {
            Assert.DoesNotContain(LuaCheck.Run(t.Code), f => f.Level == LuaFindingLevel.Error);
        }
    }

    [Fact]
    public void CheckFindsSyntaxErrorWithLine()
    {
        var findings = LuaCheck.Run("local a = 1\nif a then\nprint(1)\n");
        var error = Assert.Single(findings, f => f.Code == "Syntax");
        Assert.Equal(LuaFindingLevel.Error, error.Level);
        Assert.True(error.Line >= 3, error.Detail);
    }

    [Fact]
    public void CheckFindsUnknownGameFunction()
    {
        var known = LuaCheck.ReadDump(["Class /Script/SCUM.Item", "Function /Script/SCUM.Item:DropAround"]);
        var findings = LuaCheck.Run("RegisterHook(\"/Script/SCUM.Item:DropAround\", function() end)\nRegisterHook(\"/Script/SCUM.Item:NoSuch\", function() end)", known);
        var unknown = Assert.Single(findings, f => f.Code == "UnknownFunction");
        Assert.Equal(2, unknown.Line);
        Assert.Equal("/Script/SCUM.Item:NoSuch", unknown.Detail);
    }

    [Fact]
    public void CheckWarnsAboutTheMistakesThatHurtTheServer()
    {
        var code = string.Join('\n',
            "print(string.format(\"%d\", 1.5))",
            "deal.TradeableClass = cls",
            "Message:set(\"x\")",
            "-- string.format(\"%d\") in a comment is fine",
            "print(os.date(\"%Y-%m-%d\"))");
        var codes = LuaCheck.Run(code).Select(f => f.Code).ToList();
        Assert.Contains("FormatInteger", codes);
        Assert.Contains("SoftPointer", codes);
        Assert.Contains("HideChat", codes);
        Assert.Single(codes, c => c == "FormatInteger");
    }

    [Fact]
    public void CheckWarnsAboutAPlayerTouchedInsideAHook()
    {
        var bad = "RegisterHook(\"/Script/SCUM.PlayerRpcChannel:Prisoner_Server_Die\", function(self) self:get():GetOuter() end)";
        Assert.Contains(LuaCheck.Run(bad), f => f.Code == "PlayerInHook");
        var good = "RegisterHook(\"/Script/SCUM.PlayerRpcChannel:Prisoner_Server_Die\", function(self) ExecuteWithDelay(800, function() self:get():GetOuter() end) end)";
        Assert.DoesNotContain(LuaCheck.Run(good), f => f.Code == "PlayerInHook");
    }

    /// <summary>The game paths of the catalog exist in the owner's class dump (skipped where there is none).</summary>
    [Fact]
    public void CatalogGamePathsExistInTheServerDump()
    {
        var dump = Environment.GetEnvironmentVariable("SCUM_SERVER_DUMP")
            ?? @"D:\SteamLibrary\steamapps\common\SCUM Server\SCUM\Binaries\Win64\ue4ss\scum_dump.txt";
        if (!File.Exists(dump))
        {
            return;
        }

        var known = LuaCheck.ReadDump(File.ReadLines(dump));
        foreach (var e in LuaApiCatalog.All.Where(e => e.GamePath.Length > 0))
        {
            Assert.True(known.Contains(e.GamePath), e.Id + ": " + e.GamePath);
        }
    }
}

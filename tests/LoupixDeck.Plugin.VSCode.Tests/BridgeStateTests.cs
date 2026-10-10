using System.Text.Json.Nodes;
using LoupixDeck.Plugin.VSCode.Bridge;
using Xunit;

namespace LoupixDeck.Plugin.VSCode.Tests;

public sealed class BridgeStateTests
{
    /// <summary>The State example from docs/PROTOCOL.md.</summary>
    private const string FullState = """
        {
          "window":   {"focused":true,"active":true,"lastFocused":1791650000000},
          "workspace":{"name":"LoupixDeck","folders":["file:///x"],"remote":null},
          "editor":   {"file":"Program.cs","path":"/x/Program.cs","language":"csharp","dirty":false,"line":12,"column":4,"selections":1} ,
          "dirty":    {"count":2},
          "problems": {"errors":3,"warnings":7,"infos":0},
          "git":      {"branch":"main","detached":false,"ahead":1,"behind":0,"changes":4,"staged":1,"conflicts":0,"repos":1,"upstream":true},
          "debug":    {"state":"inactive","session":null,"reason":null},
          "tasks":    {"running":["watch"],"last":{"name":"build","exitCode":0,"at":1791650000000}},
          "terminal": {"count":2,"last":{"command":"npm test","exitCode":1,"at":1791650000000}},
          "settings": {"accessibilitySupport":"auto","wordWrap":"off","minimap":true}
        }
        """;

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Parses_the_protocol_example()
    {
        var s = EditorState.Parse(Obj(FullState));
        Assert.True(s.Window?.Focused);
        Assert.Equal("LoupixDeck", s.Workspace?.Name);
        Assert.Equal(["file:///x"], s.Workspace?.Folders);
        Assert.Equal(new EditorInfo("Program.cs", "/x/Program.cs", "csharp", false, 12, 4, 1), s.Editor);
        Assert.Equal(2, s.DirtyCount);
        Assert.Equal(new ProblemCounts(3, 7, 0), s.Problems);
        Assert.Equal(new GitInfo("main", false, 1, 0, 4, 1, 0, 1, true), s.Git);
        Assert.False(s.Debug.IsActive);
        Assert.Equal(["watch"], s.Tasks.Running);
        Assert.True(s.Tasks.Last?.Succeeded);
        Assert.Equal("npm test", s.Terminal.Last?.Name);
        Assert.True(s.Terminal.Last?.Failed);
        Assert.Equal(new SettingsInfo("auto", "off", true), s.Settings);
    }

    [Fact]
    public void Apply_replaces_only_present_sections()
    {
        var s = EditorState.Parse(Obj(FullState));
        var next = s.Apply(Obj("""{"debug":{"state":"paused","session":"Launch","reason":"breakpoint"},"editor":null,"git":null}"""));

        Assert.True(next.Debug.IsPaused);
        Assert.Equal("breakpoint", next.Debug.Reason);
        Assert.Null(next.Editor);
        Assert.Null(next.Git);
        Assert.Equal(s.Problems, next.Problems);
        Assert.Equal(3, s.Problems.Errors); // the old snapshot is untouched
        Assert.NotNull(s.Editor);
    }

    [Fact]
    public void Apply_tolerates_junk()
    {
        var s = EditorState.Empty.Apply(Obj("""
            {
              "problems": {"errors":"lots","warnings":2.0,"infos":-4,"new":true},
              "editor": {"file":42,"line":"x"},
              "dirty": [1,2],
              "debug": {"state":"exploded"},
              "tasks": {"running":["a",1,null,"b"],"last":"yesterday"},
              "terminal": 7,
              "settings": {"accessibilitySupport":"maybe"},
              "git": {"branch":null,"ahead":1e12},
              "brandNew": {"x":1}
            }
            """));

        Assert.Equal(new ProblemCounts(0, 2, 0), s.Problems);
        Assert.Null(s.Editor?.File);
        Assert.Equal(1, s.Editor?.Line);
        Assert.Equal(0, s.DirtyCount);
        Assert.Equal(DebugInfo.InactiveState, s.Debug.State);
        Assert.Equal(["a", "b"], s.Tasks.Running);
        Assert.Null(s.Tasks.Last);
        Assert.Equal(TerminalInfo.None, s.Terminal);
        Assert.Equal("auto", s.Settings.AccessibilitySupport);
        Assert.Equal(int.MaxValue, s.Git?.Ahead);
    }

    [Fact]
    public void Sections_in_an_event_skip_unknown_names()
    {
        Assert.Equal(["dirty", "problems"], EditorState.SectionsIn(Obj("""{"problems":{},"zzz":{},"dirty":{}}""")));
    }

    [Fact]
    public void Parses_a_registration()
    {
        var r = Registration.Parse("""
            {"v":1,"pid":41234,"socket":"/s/41234.sock","token":"abc","app":"Cursor","uriScheme":"cursor",
             "appRoot":"/Applications/Cursor.app/Contents/Resources/app","version":"1.99.0","remoteName":null,
             "workspaceName":"W","folders":["file:///w"],"extVersion":"0.1.0","startedAt":1791650000000,"future":{"x":1}}
            """);
        Assert.NotNull(r);
        Assert.Equal(41234, r.Pid);
        Assert.Equal("cursor", r.UriScheme);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_791_650_000_000), r.StartedAt);
        Assert.Equal(["file:///w"], r.Folders);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"pid":1,"socket":"/s"}""")]
    [InlineData("""{"pid":"1","socket":"/s","token":"t"}""")]
    public void Rejects_bad_registrations(string json) => Assert.Null(Registration.Parse(json));

    [Fact]
    public void Turns_replies_into_results()
    {
        var ok = BridgeResult.FromReply(Obj("""{"id":3,"ok":true,"result":{"a":1}}"""));
        Assert.True(ok.Ok);
        Assert.Equal(1, ok.Result?["a"]?.GetValue<int>());

        var fail = BridgeResult.FromReply(Obj("""{"id":3,"ok":false,"error":{"code":"denied","message":"policy"}}"""));
        Assert.Equal("denied", fail.ErrorCode);
        Assert.Equal("denied: policy", fail.ToString());

        Assert.Equal(BridgeErrors.Failed, BridgeResult.FromReply(Obj("""{"id":3}""")).ErrorCode);
    }

    [Fact]
    public void Wraps_paths_as_uris()
    {
        var args = Args.Of(Args.Uri("/tmp/a b/c#d"), false, "x", 3, null);
        Assert.Equal("""[{"$uri":"file:///tmp/a%20b/c%23d"},false,"x",3,null]""", args.ToJsonString());
    }

    [Fact]
    public void Registration_directory_honours_the_override()
    {
        var before = Environment.GetEnvironmentVariable(BridgeProtocol.DirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(BridgeProtocol.DirectoryVariable, "/tmp/elsewhere");
            Assert.Equal("/tmp/elsewhere", BridgeProtocol.RegistrationDirectory());
            Environment.SetEnvironmentVariable(BridgeProtocol.DirectoryVariable, null);
            Assert.EndsWith(Path.Combine("LoupixDeck", "vscode"), BridgeProtocol.RegistrationDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(BridgeProtocol.DirectoryVariable, before);
        }
    }

    [Fact]
    public void Client_names_the_plugin_and_version()
    {
        Assert.Matches(@"^LoupixDeck\.Plugin\.VSCode/\d+\.\d+\.\d+", BridgeProtocol.Client);
    }
}

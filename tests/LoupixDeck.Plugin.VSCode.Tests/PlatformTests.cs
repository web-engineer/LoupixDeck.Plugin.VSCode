using LoupixDeck.Plugin.VSCode.Platform;
using LoupixDeck.PluginSdk;
using Xunit;

namespace LoupixDeck.Plugin.VSCode.Tests;

/// <summary>A host that records the key macros it was asked to run; nothing reaches the real keyboard.</summary>
internal sealed class KeySenderHost : IPluginHost
{
    public readonly List<string> Executed = new();

    public IPluginLogger Logger { get; } = new BridgeTestLog();
    public IPluginSettings Settings => throw new NotSupportedException();
    public string CurrentLanguage => "en";
    public string Tr(string english) => english;
    public FolderGridInfo FolderGrid => new(5, 3, 0);
    public DeviceInfo? ActiveDevice => null;
    public void RequestButtonRefresh(string commandName) { }
    public void ExecuteCommand(string command) { lock (Executed) Executed.Add(command); }
    public void OpenFolder(IFolderProvider provider) { }
    public bool OpenBrowser(string url) => false;
    public void OverlayTouchText(int slot, string text, TimeSpan duration) { }
    public int GetTouchSlotForRotary(int rotaryIndex) => -1;
    public bool RequestExclusiveMode(IExclusiveModeProvider provider) => false;
    public void ReleaseExclusiveMode(IExclusiveModeProvider provider) { }
    public bool IsInExclusiveMode => false;
    public IFullDisplayRenderSession? RequestFullDisplayRenderer(IFullDisplayRenderer renderer) => null;
    public IReadOnlyList<string> GetButtonStates(string commandName) => [];
    public string? GetActiveButtonState(string commandName) => null;
    public bool SetActiveButtonState(string commandName, string stateNameOrId) => false;
}

public sealed class PlatformKeySenderTests
{
    [Theory]
    [InlineData("Cmd+S", "System.KeyCombination(Cmd+S)")]
    [InlineData("  Ctrl+Shift+P ", "System.KeyCombination(Ctrl+Shift+P)")]
    [InlineData("Cmd+K Cmd+S", "System.KeySequence(Cmd+K,Cmd+S)")]
    [InlineData("Cmd+K  Z", "System.KeySequence(Cmd+K,Z)")]
    [InlineData("F5", "System.KeyCombination(F5)")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Formats_steps(string? shortcut, string? expected) => Assert.Equal(expected, KeySender.Format(shortcut));

    [Fact]
    public async Task Sends_nothing_without_an_app()
    {
        var host = new KeySenderHost();
        Assert.False(await new KeySender(() => false).SendAsync(host, null, "Cmd+S"));
        Assert.Empty(host.Executed);
    }

    [Fact]
    public async Task Repeats_without_bringing_to_front()
    {
        var host = new KeySenderHost();
        var app = new EditorApp("code", "Visual Studio Code", "com.example.none", null, null, "vscode");
        Assert.True(await new KeySender(() => false).SendAsync(host, app, "Cmd+K Cmd+0", repeat: 3));
        Assert.Equal(3, host.Executed.Count);
        Assert.All(host.Executed, c => Assert.Equal("System.KeySequence(Cmd+K,Cmd+0)", c));
    }
}

public sealed class PlatformEditorAppsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lxv-apps-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A fake .app bundle with a product.json and the given CLI scripts in bin/.</summary>
    private string FakeBundle(string name, string? productJson, params string[] clis)
    {
        var root = Path.Combine(_dir, name + ".app", "Contents", "Resources", "app");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        if (productJson is not null) File.WriteAllText(Path.Combine(root, "product.json"), productJson);
        foreach (var cli in clis) File.WriteAllText(Path.Combine(root, "bin", cli + (OperatingSystem.IsWindows() ? ".cmd" : "")), "#!/bin/sh\n");
        return root;
    }

    [Fact]
    public void Reads_a_known_fork_from_its_product_json()
    {
        var root = FakeBundle("Cursor", """
            {"nameShort":"Cursor","nameLong":"Cursor","applicationName":"cursor","urlProtocol":"cursor",
             "darwinBundleIdentifier":"com.todesktop.230313mzl4w4u92","extra":{"a":1}}
            """, "cursor", "code");

        var app = EditorApps.FromAppRoot(root);
        Assert.NotNull(app);
        Assert.Equal("cursor", app.Key);
        Assert.Equal("Cursor", app.Name);
        Assert.Equal("com.todesktop.230313mzl4w4u92", app.BundleId);
        Assert.Equal("cursor", app.UriScheme);
        Assert.Equal(Path.Combine(_dir, "Cursor.app"), app.AppPath);
        Assert.Equal(Path.Combine(root, "bin", "cursor"), app.CliPath);
        Assert.Equal("Cursor", app.ProcessName);
    }

    [Fact]
    public void Falls_back_to_bin_code()
    {
        var root = FakeBundle("Visual Studio Code - Insiders", """
            {"nameLong":"Visual Studio Code - Insiders","applicationName":"code-insiders","urlProtocol":"vscode-insiders",
             "darwinBundleIdentifier":"com.microsoft.VSCodeInsiders"}
            """, "code");

        var app = EditorApps.FromAppRoot(root + "/");
        Assert.Equal("insiders", app?.Key);
        Assert.Equal(Path.Combine(root, "bin", "code"), app?.CliPath);
    }

    [Fact]
    public void An_unknown_fork_is_named_after_its_cli()
    {
        var root = FakeBundle("Kiro", """{"nameLong":"Kiro","applicationName":"kiro","urlProtocol":"kiro","darwinBundleIdentifier":"dev.kiro.desktop"}""");

        var app = EditorApps.FromAppRoot(root);
        Assert.Equal("kiro", app?.Key);
        Assert.Equal("Kiro", app?.Name);
        Assert.Equal("kiro", app?.UriScheme);
        Assert.Null(app?.CliPath);
    }

    [Fact]
    public void No_product_json_and_no_match_is_null()
    {
        Assert.Null(EditorApps.FromAppRoot(FakeBundle("Nothing", null)));
        Assert.Null(EditorApps.FromAppRoot(""));
    }

    [Theory]
    [InlineData("/Applications/Visual Studio Code.app/Contents/Resources/app", "/Applications/Visual Studio Code.app")]
    [InlineData("/usr/share/code/resources/app", "/usr/share/code")]
    [InlineData("/somewhere/odd", null)]
    public void Finds_the_install_from_the_app_root(string appRoot, string? expected) =>
        Assert.Equal(expected, EditorApps.InstallFromAppRoot(appRoot));

    [Fact]
    public void Parses_product_json_tolerantly()
    {
        Assert.Null(EditorApps.ParseProduct("not json"));
        Assert.Null(EditorApps.ParseProduct("[]"));
        var p = EditorApps.ParseProduct("""{"applicationName":7,"urlProtocol":"vscode"}""");
        Assert.Null(p?.ApplicationName);
        Assert.Equal("vscode", p?.UrlProtocol);
    }

    [Fact]
    public void Detection_is_cached_until_refreshed()
    {
        var first = EditorApps.Detect();
        Assert.Same(first, EditorApps.Detect());
        EditorApps.Refresh();
        Assert.NotSame(first, EditorApps.Detect());
        Assert.All(EditorApps.Detect(), a => Assert.Contains(a.Key, EditorApps.Keys));
    }

    [Fact]
    public void Capture_times_out_a_stalled_child()
    {
        if (OperatingSystem.IsWindows()) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(ProcessRunner.Capture("sleep", ["10"], TimeSpan.FromMilliseconds(300)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Capture_drains_a_full_stderr_pipe()
    {
        if (OperatingSystem.IsWindows()) return;
        // 1 MB on stderr would fill an unread pipe and hang the child.
        var output = ProcessRunner.Capture("sh", ["-c", "head -c 1048576 /dev/zero >&2; echo done"], TimeSpan.FromSeconds(10));
        Assert.Equal("done", output);
    }

    [Fact]
    public void Front_check_needs_an_id_to_match()
    {
        var bare = new EditorApp("x", "X", null, null, null, "x");
        Assert.False(EditorApps.CanCheckFront(bare));
        if (OperatingSystem.IsMacOS()) Assert.True(EditorApps.CanCheckFront(bare with { BundleId = "com.example.x" }));
        if (OperatingSystem.IsWindows()) Assert.True(EditorApps.CanCheckFront(bare with { ProcessName = "X" }));
    }

    [Fact]
    public void Resolve_by_key_only_returns_that_app()
    {
        var app = EditorApps.Resolve("windsurf", null);
        Assert.True(app is null || app.Key == "windsurf");
    }

    [Fact]
    public async Task Cli_without_a_launcher_fails_cleanly()
    {
        var app = new EditorApp("x", "X", null, null, null, "x");
        var (code, output) = await EditorApps.RunCliAsync(app, "--version");
        Assert.Equal(-1, code);
        Assert.Contains("no command-line launcher", output);
    }

    [Fact]
    public async Task Cli_runs_and_captures_output()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = FakeBundle("Echo", null);
        var cli = Path.Combine(root, "bin", "echo-cli");
        File.WriteAllText(cli, "#!/bin/sh\necho \"args: $*\"\necho oops >&2\nexit 3\n");
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (code, output) = await EditorApps.RunCliAsync(new EditorApp("x", "X", null, null, cli, "x"), "--list-extensions", "--show-versions");
        Assert.Equal(3, code);
        Assert.Contains("args: --list-extensions --show-versions", output);
        Assert.Contains("oops", output);
    }
}

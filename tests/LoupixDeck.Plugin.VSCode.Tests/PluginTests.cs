using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.VSCode.Bridge;
using LoupixDeck.Plugin.VSCode.Commands;
using LoupixDeck.Plugin.VSCode.Folders;
using LoupixDeck.Plugin.VSCode.Platform;
using LoupixDeck.Plugin.VSCode.Rendering;
using LoupixDeck.PluginSdk;
using Xunit;

namespace LoupixDeck.Plugin.VSCode.Tests;

/// <summary>A host that records what the plugin asks of it.</summary>
internal sealed class FakeHost : IPluginHost, IPluginLogger, IPluginSettings
{
    public readonly List<string> Executed = new();
    public readonly List<string> Opened = new();
    public readonly List<string> Overlays = new();
    public readonly List<string> Refreshed = new();
    public readonly List<IFolderProvider> Folders = new();
    public readonly Dictionary<string, string> States = new();
    public readonly Dictionary<string, object?> Values = new();

    public FolderGridInfo Grid { get; set; } = new(4, 3, 8);

    public IPluginLogger Logger => this;
    public IPluginSettings Settings => this;
    public string CurrentLanguage => "en";
    public string Tr(string english) => english;
    public FolderGridInfo FolderGrid => Grid;
    public DeviceInfo? ActiveDevice => null;
    public void RequestButtonRefresh(string commandName) { lock (Refreshed) Refreshed.Add(commandName); }
    public void ExecuteCommand(string command) { lock (Executed) Executed.Add(command); }
    public void OpenFolder(IFolderProvider provider) { lock (Folders) Folders.Add(provider); }
    public bool OpenBrowser(string url) { Opened.Add(url); return true; }
    public void OverlayTouchText(int slot, string text, TimeSpan duration) { lock (Overlays) Overlays.Add(text); }
    public int GetTouchSlotForRotary(int rotaryIndex) => -1;
    public bool RequestExclusiveMode(IExclusiveModeProvider provider) => false;
    public void ReleaseExclusiveMode(IExclusiveModeProvider provider) { }
    public bool IsInExclusiveMode => false;
    public IFullDisplayRenderSession? RequestFullDisplayRenderer(IFullDisplayRenderer renderer) => null;
    public IReadOnlyList<string> GetButtonStates(string commandName) => [];
    public string? GetActiveButtonState(string commandName) { lock (States) return States.GetValueOrDefault(commandName); }
    public bool SetActiveButtonState(string commandName, string stateNameOrId) { lock (States) States[commandName] = stateNameOrId; return true; }

    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }

    public T? Get<T>(string key, T? defaultValue = default) => Values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
    public void Set<T>(string key, T value) => Values[key] = value;
    public bool Contains(string key) => Values.ContainsKey(key);
    public void Remove(string key) => Values.Remove(key);
    public IEnumerable<string> Keys => Values.Keys;
    public void Save() { }
}

public sealed class PluginTests : IDisposable
{
    private const int Pid = 900101;

    /// <summary>A private registration directory: the plugin must never see the real editor's windows from a test.</summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lxv-plugin-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _cleanup = new();
    private readonly List<VSCodePlugin> _plugins = new();

    public PluginTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var p in _plugins) p.Shutdown();
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (VSCodePlugin Plugin, FakeHost Host) Load()
    {
        var host = new FakeHost();
        host.Values["bring_to_front"] = false; // never raise the real editor from a test
        var plugin = new VSCodePlugin
        {
            HubFactory = log => new BridgeHub(log, _dir) { RescanInterval = TimeSpan.FromMilliseconds(200), IsAlive = pid => pid == Pid }
        };
        plugin.Initialize(host);
        _plugins.Add(plugin);
        return (plugin, host);
    }

    private async Task<(VSCodePlugin Plugin, FakeHost Host, FakeCompanion Server)> Connected(Action<FakeCompanion>? setup = null)
    {
        var server = new FakeCompanion();
        _cleanup.Add(server);
        setup?.Invoke(server);
        server.Register(_dir, Pid);
        var (plugin, host) = Load();
        await Wait.Until(() => plugin.Hub.Target is not null, what: "the plugin to connect");
        return (plugin, host, server);
    }

    private static CommandContext Ctx(FakeHost host, ButtonTargets target = ButtonTargets.TouchButton, params string[] p) =>
        new() { Parameters = p, Target = target, SourceIndex = 0, Host = host };

    private static IPluginCommand Command(VSCodePlugin plugin, string name) =>
        plugin.GetCommands().Single(c => c.Descriptor.CommandName == name);

    private static HashSet<int> HostIcons()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "mdi-catalog.json")));
        return doc.RootElement.GetProperty("icons").EnumerateArray().Select(i => Convert.ToInt32(i[1].GetString(), 16)).ToHashSet();
    }

    // ---- descriptors ----

    [Fact]
    public void Metadata_names_the_plugin()
    {
        var plugin = new VSCodePlugin();
        Assert.Equal("vscode", plugin.Metadata.Id);
        Assert.Equal("Visual Studio Code", plugin.Metadata.Name);
        Assert.Equal(new Version(0, 1, 0), plugin.Metadata.Version);
        Assert.NotNull(plugin.Metadata.Icon);
    }

    [Fact]
    public void Command_names_are_unique_and_prefixed()
    {
        var (plugin, _) = Load();
        var names = plugin.GetCommands().Select(c => c.Descriptor.CommandName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.StartsWith("VSCode.", n));
        Assert.Contains("VSCode.Open", names);
        Assert.Contains("VSCode.Run", names);
        Assert.Contains("VSCode.ProblemList", names);
    }

    [Fact]
    public void Every_group_has_a_card_with_icon_and_description()
    {
        var (plugin, _) = Load();
        var cards = plugin.GetCommandGroups();
        Assert.Equal(cards.Count, cards.Select(g => g.Group).Distinct().Count());
        Assert.All(cards, g => Assert.False(string.IsNullOrWhiteSpace(g.Description)));
        var names = cards.Select(g => g.Group).ToHashSet();
        Assert.All(plugin.GetCommands(), c => Assert.Contains(c.Descriptor.Group, names));
        // And no card sits empty.
        Assert.All(names, g => Assert.Contains(plugin.GetCommands(), c => c.Descriptor.Group == g));
    }

    [Fact]
    public void Every_glyph_is_an_icon_the_host_has()
    {
        var (plugin, _) = Load();
        var icons = HostIcons();

        void Check(string what, string? glyph)
        {
            Assert.False(string.IsNullOrEmpty(glyph), $"{what}: no glyph");
            Assert.True(glyph!.Length == 2 && char.IsSurrogatePair(glyph, 0), $"{what}: glyph is not one code point");
            Assert.True(icons.Contains(char.ConvertToUtf32(glyph, 0)), $"{what}: U+{char.ConvertToUtf32(glyph, 0):X} is not in mdi-catalog.json");
        }

        foreach (var g in plugin.GetCommandGroups()) Check("group " + g.Group, g.Icon);
        foreach (var c in plugin.GetCommands())
        {
            var d = c.Descriptor;
            Check(d.CommandName, d.Icon);
            var layouts = d.States.Select(s => s.Layout).Append(d.ButtonLayout).OfType<ButtonLayoutDescriptor>();
            foreach (var layer in layouts.SelectMany(l => l.Layers).Where(l => l.Kind == ButtonLayerKind.Symbol))
                Check($"{d.CommandName} layer {layer.Name}", layer.Glyph);
        }
    }

    [Fact]
    public void Keys_are_vs_code_blue_with_a_short_caption()
    {
        var (plugin, _) = Load();
        foreach (var c in plugin.GetCommands().Where(c => c.Descriptor.States.Count == 0 && c.SupportedTargets != ButtonTargets.RotaryEncoder))
        {
            var layout = c.Descriptor.ButtonLayout;
            Assert.NotNull(layout);
            Assert.Equal(CodeKey.BlueHex, layout!.BackgroundColor);
            var caption = layout.Layers.Single(l => l.Name == "Caption");
            Assert.True(caption.Text!.Length <= 10, $"{c.Descriptor.CommandName}: caption '{caption.Text}' is too long");
        }
    }

    [Fact]
    public void Live_keys_bring_a_layout_for_every_state_and_an_offline_one()
    {
        var (plugin, _) = Load();
        var live = plugin.GetCommands().OfType<LiveCommandBase>().ToList();
        Assert.Equal(9, live.Count);
        foreach (var key in live)
        {
            var d = key.Descriptor;
            Assert.Equal(key.Name, d.CommandName);
            Assert.Equal(ButtonLayoutMode.None, d.ButtonLayout?.Mode);
            Assert.True(d.States.Count >= 2, d.CommandName);
            Assert.Equal(d.States.Count, d.States.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains(d.States, s => s.Name == LiveCommandBase.OfflineState);
            Assert.All(d.States, s =>
            {
                Assert.NotNull(s.Layout);
                Assert.False(string.IsNullOrWhiteSpace(s.Description));
                Assert.Contains(s.Layout!.Layers, l => l.TextSource == ButtonTextSource.Value);
            });
            Assert.NotEmpty(key.Sections);
            Assert.All(key.Sections, s => Assert.Contains(s, EditorState.SectionNames));
        }
    }

    [Fact]
    public void Action_keys_mirror_the_catalogue()
    {
        var (plugin, _) = Load();
        var actions = plugin.GetCommands().OfType<ActionCommand>().ToList();
        Assert.Equal(Catalog.CommandCatalog.All.Count, actions.Count);
        Assert.All(actions, a =>
        {
            Assert.EndsWith(".", a.Descriptor.Description);
            // The fallback note names this OS's shortcut, never another OS's.
            Assert.Contains(a.Action.ForThisOs is { } keys ? $"({keys} without the companion)." : "(needs the companion).", a.Descriptor.Description);
            Assert.Equal(a.Action.Glyph, a.Descriptor.ButtonLayout!.Layers[0].Glyph);
        });

        var dials = plugin.GetCommands().OfType<DialCommand>().ToList();
        Assert.Equal(Catalog.DialCatalog.All.Count, dials.Count);
        Assert.All(dials, d => Assert.Equal(ButtonTargets.RotaryEncoder, d.SupportedTargets));
    }

    [Fact]
    public void Parameterised_keys_explain_their_parameters()
    {
        var (plugin, _) = Load();
        var run = Command(plugin, RunCommand.Name).Descriptor;
        Assert.Equal("({Command},{Args})", run.ParameterTemplate);
        Assert.Contains(";", run.Description);
        Assert.Equal("({Task})", Command(plugin, RunTaskCommand.Name).Descriptor.ParameterTemplate);
        Assert.Equal("({Configuration})", Command(plugin, DebugConfigCommand.Name).Descriptor.ParameterTemplate);
        Assert.Equal("({Path})", Command(plugin, OpenFolderCommand.Name).Descriptor.ParameterTemplate);
    }

    // ---- live keys against a fake companion ----

    [Fact]
    public async Task Live_keys_read_the_target_window()
    {
        var (plugin, host, _) = await Connected(s =>
        {
            s.State["editor"] = new JsonObject { ["file"] = "AVeryLongFileName.cs", ["language"] = "csharp", ["dirty"] = true, ["line"] = 12, ["column"] = 4 };
            s.State["dirty"] = new JsonObject { ["count"] = 2 };
            s.State["git"] = new JsonObject { ["branch"] = "feature/state-layouts", ["changes"] = 4, ["ahead"] = 1, ["behind"] = 2, ["upstream"] = true };
            s.State["debug"] = new JsonObject { ["state"] = "paused", ["session"] = "Run app", ["reason"] = "breakpoint" };
        });
        var ctx = Ctx(host);

        AdjustmentValue? Value(string name) => ((IValueDisplayCommand)Command(plugin, name)).GetValue(ctx);

        Assert.Equal("3 errors", Value(ProblemsCommand.CommandName)?.Text);
        Assert.Equal("7 warnings", Value(ProblemsCommand.CommandName)?.Detail);
        Assert.Equal(ProblemsCommand.ErrorsState, host.States[ProblemsCommand.CommandName]);

        var git = Value(GitCommand.CommandName);
        Assert.Equal("feature/st…", git?.Text);
        Assert.Equal("4 changed\n↑1 ↓2", git?.Detail);
        Assert.Equal(GitCommand.ChangesState, host.States[GitCommand.CommandName]);

        Assert.Equal("2 unsaved", Value(UnsavedCommand.CommandName)?.Text);
        Assert.Equal(UnsavedCommand.UnsavedState, host.States[UnsavedCommand.CommandName]);

        var file = Value(ActiveFileCommand.CommandName);
        Assert.Equal("AVeryLongF…", file?.Text);
        Assert.Equal("Ln 12, Col 4\ncsharp", file?.Detail);
        Assert.Equal(ActiveFileCommand.DirtyState, host.States[ActiveFileCommand.CommandName]);

        Assert.Equal("Paused", Value(DebugStateCommand.CommandName)?.Text);
        Assert.Equal("Run app\nbreakpoint", Value(DebugStateCommand.CommandName)?.Detail);

        Assert.Equal("Fake", Value(WindowCommand.CommandName)?.Text);
        Assert.Equal("VS Code", Value(WindowCommand.CommandName)?.Detail);

        Assert.Equal("Auto", Value(ScreenReaderCommand.CommandName)?.Text);
        Assert.Equal("Tasks", Value(TaskStatusCommand.CommandName)?.Text);
        Assert.Equal("Terminal", Value(TerminalStatusCommand.CommandName)?.Text);

        var dial = (IAdjustmentCommand)Command(plugin, "VSCode.ProblemSteps");
        Assert.Equal("3 err", dial.GetValue(Ctx(host, ButtonTargets.RotaryEncoder))?.Text);
    }

    [Fact]
    public async Task A_state_push_repaints_only_the_keys_it_concerns()
    {
        var (plugin, host, server) = await Connected();
        _ = plugin.GetCommands();
        lock (host.Refreshed) host.Refreshed.Clear();

        await server.PushAsync(new JsonObject { ["ev"] = "state", ["data"] = new JsonObject { ["problems"] = new JsonObject { ["errors"] = 0, ["warnings"] = 1 } } });
        await Wait.Until(() => { lock (host.Refreshed) return host.Refreshed.Contains(ProblemsCommand.CommandName); }, what: "the problems key to repaint");

        lock (host.Refreshed)
        {
            Assert.Contains("VSCode.ProblemSteps", host.Refreshed);
            Assert.DoesNotContain(GitCommand.CommandName, host.Refreshed);
        }

        var value = ((IValueDisplayCommand)Command(plugin, ProblemsCommand.CommandName)).GetValue(Ctx(host));
        Assert.Equal("1 warning", value?.Text);
        Assert.Equal(ProblemsCommand.WarningsState, host.States[ProblemsCommand.CommandName]);
    }

    [Fact]
    public void Live_keys_say_why_they_are_empty_without_a_window()
    {
        var (plugin, host) = Load();
        var value = ((IValueDisplayCommand)Command(plugin, GitCommand.CommandName)).GetValue(Ctx(host));
        Assert.Contains(value?.Text, new[] { "No companion", "Not running", "No VS Code" });
        Assert.Equal(LiveCommandBase.OfflineState, host.States[GitCommand.CommandName]);
    }

    // ---- keys running commands ----

    [Fact]
    public async Task Action_keys_run_their_command_in_the_window()
    {
        var (plugin, host, server) = await Connected();
        await Command(plugin, "VSCode.Save").Execute(Ctx(host));

        JsonObject exec;
        lock (server.Requests) exec = server.Requests.Single(r => (string?)r["op"] == "exec");
        Assert.Equal("workbench.action.files.save", (string?)exec["command"]);
        Assert.Null(exec["focus"]); // bring_to_front is off in tests
        lock (host.Executed) Assert.Empty(host.Executed);
        lock (host.Overlays) Assert.Empty(host.Overlays);
    }

    [Fact]
    public async Task Bring_to_front_asks_the_window_to_raise_itself()
    {
        var (plugin, host, server) = await Connected();
        // The fake reports focusWindow, so the plugin leaves raising the window to the companion.
        host.Values["bring_to_front"] = true;
        await Command(plugin, "VSCode.Save").Execute(Ctx(host));

        lock (server.Requests) Assert.True((bool?)server.Requests.Single(r => (string?)r["op"] == "exec")["focus"]);
    }

    [Fact]
    public async Task Catalogue_arguments_go_with_the_command()
    {
        var (plugin, host, server) = await Connected();
        var dial = (IAdjustmentCommand)Command(plugin, "VSCode.Scroll");
        await dial.ApplyAdjustment(Ctx(host, ButtonTargets.RotaryEncoder), 2);

        List<JsonObject> execs;
        lock (server.Requests) execs = server.Requests.Where(r => (string?)r["op"] == "exec").ToList();
        Assert.Equal(2, execs.Count);
        Assert.All(execs, e =>
        {
            Assert.Equal("editorScroll", (string?)e["command"]);
            Assert.Equal("down", (string?)e["args"]![0]!["to"]);
            Assert.Equal(3, (int?)e["args"]![0]!["value"]);
        });
    }

    [Fact]
    public async Task A_fast_spin_is_capped_and_runs_in_order()
    {
        var (plugin, host, server) = await Connected();
        var dial = (IAdjustmentCommand)Command(plugin, "VSCode.EditorTabs");
        await dial.ApplyAdjustment(Ctx(host, ButtonTargets.RotaryEncoder), -9);
        await dial.ApplyReset(Ctx(host, ButtonTargets.RotaryEncoder));

        List<string?> commands;
        lock (server.Requests) commands = server.Requests.Where(r => (string?)r["op"] == "exec").Select(r => (string?)r["command"]).ToList();
        Assert.Equal(Enumerable.Repeat("workbench.action.previousEditor", 5).Append("workbench.action.showAllEditorsByMostRecentlyUsed"), commands);
    }

    [Fact]
    public async Task Run_passes_args_and_reports_a_missing_command()
    {
        var (plugin, host, server) = await Connected();
        var run = Command(plugin, RunCommand.Name);

        // The host split the comma the user typed; a ; inside the string stays a semicolon.
        await run.Execute(Ctx(host, ButtonTargets.TouchButton, "editor.action.insertSnippet", "{\"snippet\":\"a", "b;c\"}"));
        lock (server.Requests)
        {
            var exec = server.Requests.Single(r => (string?)r["op"] == "exec");
            Assert.Equal("editor.action.insertSnippet", (string?)exec["command"]);
            Assert.Equal("a,b;c", (string?)exec["args"]![0]!["snippet"]);
        }

        await run.Execute(Ctx(host, ButtonTargets.TouchButton, "test.unknown"));
        lock (host.Overlays) Assert.Equal("Needs extension", host.Overlays.Last());

        await run.Execute(Ctx(host));
        lock (host.Overlays) Assert.Equal("Set command", host.Overlays.Last());
    }

    [Fact]
    public async Task Run_task_reports_an_unknown_task()
    {
        var (plugin, host, server) = await Connected();
        var task = Command(plugin, RunTaskCommand.Name);
        await task.Execute(Ctx(host, ButtonTargets.TouchButton, "build"));
        lock (host.Overlays) Assert.Empty(host.Overlays);
        await task.Execute(Ctx(host, ButtonTargets.TouchButton, "nope"));
        lock (host.Overlays) Assert.Equal("Not found", host.Overlays.Last());
        lock (server.Requests) Assert.Equal(2, server.Requests.Count(r => (string?)r["op"] == "runTask"));
    }

    [Fact]
    public async Task One_parameter_keys_keep_commas_the_host_split_on()
    {
        var (plugin, host, server) = await Connected();
        await Command(plugin, RunTaskCommand.Name).Execute(Ctx(host, ButtonTargets.TouchButton, "build", " watch"));
        await Command(plugin, DebugConfigCommand.Name).Execute(Ctx(host, ButtonTargets.TouchButton, "Server", " Client"));
        lock (server.Requests)
        {
            Assert.Equal("build, watch", (string?)server.Requests.Single(r => (string?)r["op"] == "runTask")["name"]);
            Assert.Equal("Server, Client", (string?)server.Requests.Single(r => (string?)r["op"] == "startDebug")["name"]);
        }
    }

    [Fact]
    public async Task Open_folder_respects_a_denied_command()
    {
        var (plugin, host, server) = await Connected();
        server.Denied.Add("vscode.openFolder");
        var dir = Directory.CreateTempSubdirectory("lxv-open").FullName;
        try
        {
            await Command(plugin, OpenFolderCommand.Name).Execute(Ctx(host, ButtonTargets.TouchButton, dir));
            lock (host.Overlays) Assert.Equal("Not allowed", Assert.Single(host.Overlays));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public async Task Without_a_window_keys_fall_back_to_the_shortcut()
    {
        var (plugin, host) = Load();
        await Command(plugin, "VSCode.Save").Execute(Ctx(host));

        if (EditorApps.Resolve("auto", null) is null)
        {
            lock (host.Overlays) Assert.Equal("No VS Code", Assert.Single(host.Overlays));
            return;
        }

        var expected = OperatingSystem.IsMacOS() ? "System.KeyCombination(Cmd+S)" : "System.KeyCombination(Ctrl+S)";
        lock (host.Executed) Assert.Equal(expected, Assert.Single(host.Executed));
    }

    [Fact]
    public async Task Without_a_window_keys_with_no_shortcut_say_so()
    {
        var (plugin, host) = Load();
        await Command(plugin, "VSCode.GitSync").Execute(Ctx(host));
        await Command(plugin, RunTaskCommand.Name).Execute(Ctx(host, ButtonTargets.TouchButton, "build"));
        await Command(plugin, "VSCode.Tasks").Execute(Ctx(host));

        lock (host.Overlays) Assert.Equal(["Needs companion", "Needs companion", "Needs companion"], host.Overlays);
        lock (host.Executed) Assert.Empty(host.Executed);
        lock (host.Folders) Assert.Empty(host.Folders);
    }

    // ---- folders ----

    [Fact]
    public async Task Problem_folder_colours_by_severity_and_runs_the_item()
    {
        var (plugin, host, server) = await Connected(s => s.Lists["problems"] = new JsonArray(
            new JsonObject { ["ref"] = "p1", ["label"] = "Program.cs:12", ["detail"] = "CS1002: ; expected", ["severity"] = "error" },
            new JsonObject { ["ref"] = "p2", ["label"] = "Util.cs:3", ["detail"] = "Unused variable", ["severity"] = "warning" }));

        await Command(plugin, "VSCode.ProblemList").Execute(Ctx(host));
        var folder = Assert.Single(host.Folders);
        folder.OnEnter();
        await Wait.Until(() => folder.BuildEntries().Count == 2 && folder.BuildEntries()[0].OnPress is not null, what: "the problems to load");

        var entries = folder.BuildEntries();
        Assert.Equal(CodeKey.Error, entries[0].BackColor);
        Assert.Equal(CodeKey.Warning, entries[1].BackColor);
        Assert.StartsWith("Program.cs:12\n", entries[0].Text);
        Assert.Equal([0, 1], entries.Select(e => e.SlotIndex));

        await entries[1].OnPress!();
        lock (server.Requests)
        {
            var run = server.Requests.Single(r => (string?)r["op"] == "run");
            Assert.Equal("problems", (string?)run["what"]);
            Assert.Equal("p2", (string?)run["ref"]);
        }

        folder.OnExit();
    }

    [Fact]
    public async Task Folder_reloads_when_the_window_says_its_list_changed()
    {
        var (plugin, host, server) = await Connected(s => s.Lists["tasks"] = new JsonArray(new JsonObject { ["ref"] = "t1", ["label"] = "build" }));
        await Command(plugin, "VSCode.Tasks").Execute(Ctx(host));
        var folder = Assert.Single(host.Folders);
        var changed = 0;
        folder.EntriesChanged += () => Interlocked.Increment(ref changed);
        folder.OnEnter();
        await Wait.Until(() => folder.BuildEntries().Count == 1 && folder.BuildEntries()[0].OnPress is not null, what: "the tasks to load");

        server.Lists["tasks"] = new JsonArray(new JsonObject { ["ref"] = "t1", ["label"] = "build" }, new JsonObject { ["ref"] = "t2", ["label"] = "test" });
        await server.PushAsync(new JsonObject { ["ev"] = "lists", ["what"] = new JsonArray("tasks") });
        await Wait.Until(() => folder.BuildEntries().Count == 2, what: "the tasks to reload");
        Assert.True(Volatile.Read(ref changed) >= 2);
        folder.OnExit();
    }

    [Fact]
    public async Task Long_lists_page_with_a_more_key()
    {
        var host = new FakeHost { Grid = new FolderGridInfo(4, 3, 8) };
        using var hub = new BridgeHub(new BridgeTestLog(), _dir);
        IReadOnlyList<FolderItem> items = Enumerable.Range(1, 25)
            .Select(i => new FolderItem($"item {i}", CodeKey.Blue, () => Task.FromResult<string?>(null))).ToList();
        var list = new FolderList(hub, _ => Task.FromResult(items), _ => false, host);
        var first = new ListFolder("Tasks", list, host, "No tasks");

        first.OnEnter();
        await Wait.Until(() => list.Items is not null, what: "the list to load");

        // 11 slots besides Back on a 4x3 grid: 10 items and More on each page but the last.
        var page1 = first.BuildEntries();
        Assert.Equal(11, page1.Count);
        Assert.DoesNotContain(page1, e => e.SlotIndex == 8);
        Assert.Equal("item 1", page1[0].Text);
        var more = page1[^1];
        Assert.StartsWith("More ›", more.Text);
        Assert.Contains("2/3", more.Text);
        Assert.NotNull(more.OpensFolder);
        Assert.True(first.RotaryOverrides.ContainsKey(0));

        var second = more.OpensFolder!;
        second.OnEnter();
        Assert.Equal("item 11", second.BuildEntries()[0].Text);
        var third = second.BuildEntries()[^1].OpensFolder!;
        third.OnEnter();
        var last = third.BuildEntries();
        Assert.Equal(5, last.Count);
        Assert.Equal("item 25", last[^1].Text);
        Assert.Null(last[^1].OpensFolder);
        Assert.Empty(third.RotaryOverrides);

        third.OnExit();
        second.OnExit();
        first.OnExit();
    }

    [Fact]
    public async Task Windows_folder_lists_windows_and_focuses_one()
    {
        var (plugin, host, server) = await Connected();
        await Command(plugin, WindowsFolderCommand.Name).Execute(Ctx(host));
        var folder = Assert.Single(host.Folders);
        folder.OnEnter();
        await Wait.Until(() => folder.BuildEntries().FirstOrDefault()?.OnPress is not null, what: "the windows to list");

        var entry = Assert.Single(folder.BuildEntries());
        Assert.Equal("Fake\nVS Code", entry.Text);
        await entry.OnPress!();
        lock (server.Requests) Assert.Contains(server.Requests, r => (string?)r["op"] == "focus");
        folder.OnExit();
    }

    // ---- settings and requirements ----

    [Fact]
    public async Task Settings_page_shows_the_connected_window()
    {
        var (plugin, _, _) = await Connected();
        var schema = plugin.SettingsSchema;
        Assert.Equal(schema.Count, schema.Select(s => s.Key).Distinct().Count());
        var card = schema.Single(s => s.Key == "companion");
        Assert.Contains("Connected to 1 window", card.Description);
        Assert.Contains("Fake", card.Description);
        Assert.Contains(schema, s => s.Key == "bring_to_front" && s.Kind == PluginSettingKind.Toggle);
        Assert.Contains(schema, s => s.Key == "app" && Equals(s.DefaultValue, "auto"));

        var actions = plugin.SettingsActions.Select(a => a.Label).ToList();
        Assert.Contains("Check connection", actions);
        Assert.Contains("Get support", actions);
        Assert.Contains("Report a bug or request a feature", actions);
        Assert.All(EditorApps.Detect(), app => Assert.Contains($"Install companion in {app.Name}", actions));

        var check = await plugin.SettingsActions.Single(a => a.Label == "Check connection").Invoke();
        Assert.Contains("Fake (Visual Studio Code 1.141.0): connected", check);
    }

    [Fact]
    public async Task Support_links_open_the_right_pages()
    {
        var (plugin, host) = Load();
        await plugin.SettingsActions.Single(a => a.Label == "Get support").Invoke();
        await plugin.SettingsActions.Single(a => a.Label == "Report a bug or request a feature").Invoke();
        Assert.Equal(["https://web-engineer.co.uk/contact", "https://github.com/web-engineer/LoupixDeck.Plugin.VSCode/issues"], host.Opened);
    }

    [Fact]
    public async Task Requirements_report_the_companion_once_connected()
    {
        var (plugin, _, _) = await Connected();
        var requirements = plugin.GetRequirements();
        Assert.Equal(["editor", "companion"], requirements.Select(r => r.Id));
        Assert.True(requirements.Single(r => r.Id == "companion").IsMet);
    }

    // ---- helpers ----

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("16", "[16]")]
    [InlineData("true", "[true]")]
    [InlineData("hello", "[\"hello\"]")]
    [InlineData("[\"editor.fontSize\"; 16]", "[\"editor.fontSize\",16]")]
    [InlineData("{\"to\":\"down\";\"by\":\"page\"}", "[{\"to\":\"down\",\"by\":\"page\"}]")]
    [InlineData("[[1;2]]", "[[1,2]]")]
    [InlineData("null", "[null]")]
    [InlineData("{\"text\":\"cd src; npm test\"}", "[{\"text\":\"cd src; npm test\"}]")]
    [InlineData("{\"text\":\"a\\\";b\";\"n\":1}", "[{\"text\":\"a\\u0022;b\",\"n\":1}]")]
    public void Run_args_turn_semicolons_into_commas(string? raw, string? expected) =>
        Assert.Equal(expected, RunCommand.ParseUserArgs(raw)?.ToJsonString());

    [Fact]
    public void Folder_paths_expand_the_home_folder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "src", "app"), OpenFolderCommand.ExpandPath("~/src/app"));
        Assert.Equal(home, OpenFolderCommand.ExpandPath("~"));
        Assert.Null(OpenFolderCommand.ExpandPath(" "));
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("feature/state-layouts", "feature/st…")]
    [InlineData(null, "")]
    public void Long_text_is_cut_with_an_ellipsis(string? text, string expected) => Assert.Equal(expected, CodeKey.Fit(text));

    [Theory]
    [InlineData("Visual Studio Code", "VS Code")]
    [InlineData("Visual Studio Code - Insiders", "Insiders")]
    [InlineData("Cursor", "Cursor")]
    [InlineData(null, "VS Code")]
    public void Editor_names_fit_a_key(string? app, string expected) => Assert.Equal(expected, WindowsFolderCommand.AppName(app));

    [Fact]
    public void Extension_hints_use_a_readable_name()
    {
        Assert.Equal("Claude Code", CodeCommandBase.ExtensionName("anthropic.claude-code"));
        Assert.Equal("gitlens", CodeCommandBase.ExtensionName("eamodio.gitlens"));
    }
}

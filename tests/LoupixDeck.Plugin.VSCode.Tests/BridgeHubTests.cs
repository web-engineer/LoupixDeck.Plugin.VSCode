using System.Text.Json.Nodes;
using LoupixDeck.Plugin.VSCode.Bridge;
using Xunit;

namespace LoupixDeck.Plugin.VSCode.Tests;

public sealed class BridgeHubTests : IDisposable
{
    /// <summary>Fake pids the hub treats as alive; anything else is a dead process.</summary>
    private readonly HashSet<int> _alive = [900001, 900002];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lxv-hub-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _cleanup = new();

    public BridgeHubTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private BridgeHub Hub(bool manual = false, int rescanMs = 200)
    {
        var hub = new BridgeHub(new BridgeTestLog(), _dir)
        {
            RescanInterval = TimeSpan.FromMilliseconds(rescanMs),
            FirstScan = manual ? Timeout.InfiniteTimeSpan : TimeSpan.Zero,
            IsAlive = pid => _alive.Contains(pid)
        };
        _cleanup.Insert(0, hub);
        return hub;
    }

    private FakeCompanion Server(long lastFocused = 1_791_650_000_000)
    {
        var server = new FakeCompanion();
        server.State["window"] = new JsonObject { ["focused"] = false, ["active"] = false, ["lastFocused"] = lastFocused };
        _cleanup.Add(server);
        return server;
    }

    [Fact]
    public async Task Discovers_a_window_from_its_registration()
    {
        var server = Server();
        server.Register(_dir, 900001);
        var hub = Hub();
        var changes = new List<HubChange>();
        hub.Changed += c => { lock (changes) changes.Add(c); };

        hub.Start();
        await Wait.Until(() => hub.AnyConnected, what: "the hub to connect");

        var window = Assert.Single(hub.Windows);
        Assert.Same(window, hub.Target);
        Assert.Equal(900001, window.Pid);
        lock (changes) Assert.Contains(changes, c => c.WindowsChanged && c.TargetChanged);

        var result = await hub.ExecAsync("workbench.action.files.save");
        Assert.True(result.Ok, result.ToString());
    }

    [Fact]
    public async Task Removes_the_registration_of_a_dead_process()
    {
        var server = Server();
        var path = server.Register(_dir, 123456789);
        var hub = Hub(manual: true);
        hub.Start();
        await hub.ScanAsync();

        Assert.False(File.Exists(path));
        Assert.False(hub.AnyConnected);
    }

    [Fact]
    public async Task Keeps_a_young_refused_registration_once_then_removes_it()
    {
        string socket;
        using (var gone = new FakeCompanion()) socket = gone.SocketPath;
        var path = Path.Combine(_dir, "900001.json");
        File.WriteAllText(path, $$"""{"v":1,"pid":900001,"socket":"{{socket}}","token":"t","app":"Visual Studio Code"}""");
        var hub = Hub(manual: true);
        hub.Start();

        await hub.ScanAsync();
        Assert.True(File.Exists(path), "a fresh registration that failed once must survive");
        await hub.ScanAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Removes_an_old_refused_registration_at_once()
    {
        string socket;
        using (var gone = new FakeCompanion()) socket = gone.SocketPath;
        var path = Path.Combine(_dir, "900001.json");
        File.WriteAllText(path, $$"""{"v":1,"pid":900001,"socket":"{{socket}}","token":"t"}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var hub = Hub(manual: true);
        hub.Start();

        await hub.ScanAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Refuses_a_newer_protocol()
    {
        var server = Server();
        var path = server.Register(_dir, 900001, v: 2);
        var hub = Hub(manual: true);
        hub.Start();
        await hub.ScanAsync();

        Assert.False(hub.AnyConnected);
        Assert.True(hub.NeedsPluginUpdate);
        Assert.True(File.Exists(path)); // not stale, just newer
        Assert.Equal(0, server.Hellos);
    }

    [Fact]
    public async Task Target_follows_focus_then_last_focused()
    {
        var a = Server(lastFocused: 1_791_650_001_000);
        var b = Server(lastFocused: 1_791_650_002_000);
        a.Register(_dir, 900001);
        b.Register(_dir, 900002);
        var hub = Hub();
        hub.Start();
        await Wait.Until(() => hub.Windows.Count == 2, what: "both windows");

        // Nobody focused: the one focused last.
        Assert.Equal(900002, hub.Target?.Pid);

        var targetChanged = new TaskCompletionSource<HubChange>();
        hub.Changed += c => { if (c.TargetChanged) targetChanged.TrySetResult(c); };
        await a.PushAsync("""{"ev":"focus","focused":true,"active":true,"at":1791650003000}""");
        var change = await targetChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(900001, hub.Target?.Pid);
        Assert.Contains("window", change.Sections);
        Assert.True(change.Affects("problems")); // a new target means every key repaints

        // A loses focus to another app: still the most recently focused.
        await a.PushAsync("""{"ev":"focus","focused":false,"active":false,"at":1791650004000}""");
        await Wait.Until(() => hub.Target is { IsFocused: false });
        Assert.Equal(900001, hub.Target?.Pid);

        // B gains focus.
        await b.PushAsync("""{"ev":"focus","focused":true,"active":true,"at":1791650005000}""");
        await Wait.Until(() => hub.Target?.Pid == 900002, what: "B to become the target");
    }

    [Fact]
    public async Task State_changes_name_their_sections()
    {
        var server = Server();
        server.Register(_dir, 900001);
        var hub = Hub();
        hub.Start();
        await Wait.Until(() => hub.AnyConnected);

        var seen = new TaskCompletionSource<HubChange>();
        hub.Changed += c => { if (c.Sections.Contains("git")) seen.TrySetResult(c); };
        await server.PushAsync("""{"ev":"state","data":{"git":{"branch":"feature","changes":0}}}""");
        var change = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(change.ForTarget);
        Assert.True(change.Affects("git"));
        Assert.False(change.Affects("problems"));
        Assert.Equal("feature", hub.Target?.State.Git?.Branch);
    }

    [Fact]
    public async Task Reconnects_a_dropped_window()
    {
        var server = Server();
        server.Register(_dir, 900001);
        var hub = Hub();
        var dropped = new TaskCompletionSource();
        hub.Start();
        await Wait.Until(() => hub.AnyConnected);
        hub.Changed += c => { if (c.WindowsChanged && !hub.AnyConnected) dropped.TrySetResult(); };

        server.DropClients();
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Wait.Until(() => hub.AnyConnected, what: "the reconnect");
        Assert.Equal(2, server.Hellos);
    }

    [Fact]
    public async Task Offline_without_windows()
    {
        var hub = Hub();
        hub.Start();
        Assert.Null(hub.Target);
        Assert.Equal(BridgeErrors.Offline, (await hub.ExecAsync("x")).ErrorCode);
        Assert.Empty(await hub.ListAsync("tasks"));
    }

    [Fact]
    public async Task Start_and_stop_are_idempotent()
    {
        var server = Server();
        server.Register(_dir, 900001);
        var hub = Hub();
        hub.Stop();
        hub.Start();
        hub.Start();
        await Wait.Until(() => hub.AnyConnected);
        hub.Stop();
        hub.Stop();
        Assert.False(hub.AnyConnected);
        Assert.Empty(hub.Windows);

        hub.Start();
        await Wait.Until(() => hub.AnyConnected, what: "a restart");
        Assert.Equal(2, server.Hellos);
    }

    [Fact]
    public async Task Watcher_picks_up_a_new_registration()
    {
        var hub = Hub(rescanMs: 60_000);
        hub.Start();
        await Task.Delay(100);
        var server = Server();
        // Written atomically as the companion does it: a temp file renamed into place.
        var tmp = Path.Combine(_dir, "900001.json.tmp");
        File.Move(server.Register(_dir, 900001), tmp);
        File.Move(tmp, Path.Combine(_dir, "900001.json"));
        await Wait.Until(() => hub.AnyConnected, what: "the watcher to trigger a scan");
    }
}

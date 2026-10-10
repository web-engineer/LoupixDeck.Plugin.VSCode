using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.VSCode.Bridge;
using LoupixDeck.PluginSdk;
using Xunit;

namespace LoupixDeck.Plugin.VSCode.Tests;

/// <summary>A logger that keeps what it was told, for assertions and for reading a failed test.</summary>
internal sealed class BridgeTestLog : IPluginLogger
{
    public readonly List<string> Lines = new();

    public void Info(string message) { lock (Lines) Lines.Add("info: " + message); }
    public void Warn(string message) { lock (Lines) Lines.Add("warn: " + message); }
    public void Error(string message, Exception? exception = null) { lock (Lines) Lines.Add("error: " + message + " " + exception?.Message); }
}

/// <summary>
/// A stand-in for the companion extension: an NDJSON server on a Unix socket under /tmp (macOS
/// limits socket paths to 104 bytes, so never under the long per-user temp folder). It checks the
/// token, answers hello with a snapshot, echoes <c>exec</c>, serves lists and pushes events.
/// </summary>
internal sealed class FakeCompanion : IDisposable
{
    private readonly Socket _listener;
    private readonly List<Socket> _clients = new();
    private readonly CancellationTokenSource _cts = new();

    public FakeCompanion(string token = "secret-token", int protocol = 1)
    {
        Token = token;
        Protocol = protocol;
        SocketPath = $"/tmp/lxv-{Guid.NewGuid().ToString("N")[..10]}.sock";
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen(8);
        _ = AcceptLoopAsync();
    }

    public string Token { get; }
    public int Protocol { get; }
    public string SocketPath { get; }

    /// <summary>The snapshot hello returns.</summary>
    public JsonObject State { get; set; } = new()
    {
        ["window"] = new JsonObject { ["focused"] = false, ["active"] = false, ["lastFocused"] = 1_791_650_000_000 },
        ["workspace"] = new JsonObject { ["name"] = "Fake", ["folders"] = new JsonArray("file:///tmp/fake"), ["remote"] = null },
        ["problems"] = new JsonObject { ["errors"] = 3, ["warnings"] = 7, ["infos"] = 0 },
        ["git"] = new JsonObject { ["branch"] = "main", ["changes"] = 4, ["upstream"] = true }
    };

    /// <summary>Lines sent in the same write as the hello reply, as a window flushing pending events would.</summary>
    public List<JsonObject> AfterHello { get; } = new();

    /// <summary>Command ids that <c>exec</c> refuses with <c>denied</c>, as the companion's allow list would.</summary>
    public HashSet<string> Denied { get; } = new();

    /// <summary>Items per <c>list</c> kind.</summary>
    public Dictionary<string, JsonArray> Lists { get; } = new();

    /// <summary>Every request after hello, in order.</summary>
    public List<JsonObject> Requests { get; } = new();

    public int Hellos;

    /// <summary>A registration file for this server in <paramref name="dir"/>.</summary>
    public string Register(string dir, int pid, long? startedAt = null, int v = 1, string? token = null)
    {
        var json = new JsonObject
        {
            ["v"] = v,
            ["pid"] = pid,
            ["socket"] = SocketPath,
            ["token"] = token ?? Token,
            ["app"] = "Visual Studio Code",
            ["uriScheme"] = "vscode",
            ["appRoot"] = "/nowhere/Contents/Resources/app",
            ["version"] = "1.141.0",
            ["remoteName"] = null,
            ["workspaceName"] = "Fake",
            ["folders"] = new JsonArray("file:///tmp/fake"),
            ["extVersion"] = "0.1.0",
            ["startedAt"] = startedAt ?? 1_791_600_000_000
        };
        var path = Path.Combine(dir, pid + ".json");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    public Registration Registration(int pid = 4242, string? token = null) =>
        Bridge.Registration.Parse(new JsonObject
        {
            ["pid"] = pid, ["socket"] = SocketPath, ["token"] = token ?? Token, ["app"] = "Visual Studio Code", ["startedAt"] = 1_791_600_000_000
        }.ToJsonString())!;

    /// <summary>Sends a raw line to every connected client.</summary>
    public async Task PushAsync(string line)
    {
        Socket[] clients;
        lock (_clients) clients = _clients.ToArray();
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        foreach (var c in clients)
        {
            try { await c.SendAsync(bytes, SocketFlags.None); } catch (SocketException) { } catch (ObjectDisposedException) { }
        }
    }

    public Task PushAsync(JsonObject ev) => PushAsync(ev.ToJsonString());

    /// <summary>Closes every client connection; the listener keeps running so clients can reconnect.</summary>
    public void DropClients()
    {
        lock (_clients)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
    }

    public int ClientCount { get { lock (_clients) return _clients.Count; } }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptAsync(_cts.Token);
                _ = ServeAsync(client);
            }
        }
        catch (Exception)
        {
            // listener closed
        }
    }

    private async Task ServeAsync(Socket client)
    {
        try
        {
            using var stream = new NetworkStream(client, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var helloLine = await reader.ReadLineAsync(_cts.Token);
            var hello = helloLine is null ? null : JsonNode.Parse(helloLine) as JsonObject;
            if (hello?["op"]?.GetValue<string>() != "hello" || hello["token"]?.GetValue<string>() != Token)
            {
                client.Dispose();
                return;
            }

            Interlocked.Increment(ref Hellos);
            lock (_clients) _clients.Add(client);
            var helloReply = new JsonObject
            {
                ["id"] = hello["id"]!.GetValue<int>(), ["ok"] = true, ["result"] = new JsonObject
                {
                    ["protocol"] = Protocol, ["pid"] = 4242, ["app"] = "Visual Studio Code", ["version"] = "1.141.0",
                    ["uriScheme"] = "vscode", ["extVersion"] = "0.1.0", ["focusWindow"] = true, ["state"] = State.DeepClone()
                }
            };
            var burst = string.Concat(new[] { helloReply }.Concat(AfterHello).Select(m => m.ToJsonString() + "\n"));
            await client.SendAsync(Encoding.UTF8.GetBytes(burst), SocketFlags.None);

            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                var request = (JsonObject)JsonNode.Parse(line)!;
                lock (Requests) Requests.Add(request);
                switch (request["op"]!.GetValue<string>())
                {
                    case "exec":
                        var command = request["command"]!.GetValue<string>();
                        if (command == "test.slow") break; // never answers
                        if (Denied.Contains(command))
                            await Fail(client, request, "denied", $"{command} is not allowed");
                        else if (command == "test.unknown")
                            await Fail(client, request, "unknown_command", "command 'test.unknown' not found");
                        else
                            await Reply(client, request, new JsonObject { ["command"] = command, ["args"] = request["args"]?.DeepClone() });
                        break;
                    case "list":
                        var what = request["what"]!.GetValue<string>();
                        await Reply(client, request, new JsonObject { ["items"] = Lists.TryGetValue(what, out var items) ? items.DeepClone() : new JsonArray() });
                        break;
                    case "runTask":
                        if (request["name"]!.GetValue<string>() == "build") await Reply(client, request, null);
                        else await Fail(client, request, "not_found", "no such task");
                        break;
                    case "state":
                        await Reply(client, request, State.DeepClone());
                        break;
                    case "ping":
                        await Reply(client, request, "pong");
                        break;
                    default:
                        await Reply(client, request, null);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // client gone
        }
    }

    private static Task Reply(Socket client, JsonObject request, JsonNode? result) =>
        Send(client, new JsonObject { ["id"] = request["id"]!.GetValue<int>(), ["ok"] = true, ["result"] = result });

    private static Task Fail(Socket client, JsonObject request, string code, string message) =>
        Send(client, new JsonObject { ["id"] = request["id"]!.GetValue<int>(), ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    private static async Task Send(Socket client, JsonObject message)
    {
        try { await client.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"), SocketFlags.None); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        DropClients();
        _listener.Dispose();
        try { File.Delete(SocketPath); } catch { }
    }
}

internal static class Wait
{
    /// <summary>Polls until the condition holds; fails the test after the timeout.</summary>
    public static async Task Until(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting" + (what is null ? string.Empty : " for " + what));
            await Task.Delay(20);
        }
    }
}

public sealed class BridgeWindowTests
{
    private static async Task<(FakeCompanion Server, BridgeWindow Window)> Connected()
    {
        var server = new FakeCompanion();
        var window = new BridgeWindow(server.Registration(), new BridgeTestLog());
        Assert.Equal(ConnectOutcome.Connected, await window.ConnectAsync());
        return (server, window);
    }

    [Fact]
    public async Task Hello_connects_and_takes_the_snapshot()
    {
        var (server, window) = await Connected();
        using (server)
        using (window)
        {
            Assert.True(window.IsConnected);
            Assert.True(window.HasFocusWindow);
            Assert.Equal("Visual Studio Code", window.App);
            Assert.Equal("1.141.0", window.Version);
            Assert.Equal("0.1.0", window.ExtVersion);
            Assert.Equal("Fake", window.WorkspaceName);
            Assert.Equal(3, window.State.Problems.Errors);
            Assert.Equal("main", window.State.Git?.Branch);
            Assert.False(window.IsFocused);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_791_650_000_000), window.LastFocused);
        }
    }

    [Fact]
    public async Task Events_right_after_hello_land_on_top_of_the_snapshot()
    {
        using var server = new FakeCompanion();
        server.AfterHello.Add(new JsonObject
        {
            ["ev"] = "state", ["data"] = new JsonObject { ["problems"] = new JsonObject { ["errors"] = 0, ["warnings"] = 1, ["infos"] = 0 } }
        });
        for (var i = 0; i < 20; i++)
        {
            using var w = new BridgeWindow(server.Registration());
            Assert.Equal(ConnectOutcome.Connected, await w.ConnectAsync());
            await Task.Delay(20);
            Assert.Equal(new ProblemCounts(0, 1, 0), w.State.Problems);
        }
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        using var server = new FakeCompanion();
        using var window = new BridgeWindow(server.Registration(token: "wrong"));
        Assert.Equal(ConnectOutcome.Rejected, await window.ConnectAsync());
        Assert.False(window.IsConnected);
        Assert.Equal(0, server.Hellos);
    }

    [Fact]
    public async Task Missing_socket_is_refused()
    {
        string path;
        using (var server = new FakeCompanion()) path = server.SocketPath;
        var registration = Registration.Parse($$"""{"pid":1,"socket":"{{path}}","token":"t"}""")!;
        using var window = new BridgeWindow(registration);
        Assert.Equal(ConnectOutcome.Refused, await window.ConnectAsync());
    }

    [Fact]
    public async Task Newer_protocol_is_refused()
    {
        using var server = new FakeCompanion(protocol: 2);
        using var window = new BridgeWindow(server.Registration());
        Assert.Equal(ConnectOutcome.ProtocolTooNew, await window.ConnectAsync());
        Assert.False(window.IsConnected);
    }

    [Fact]
    public async Task Exec_round_trips_args_and_errors()
    {
        var (server, window) = await Connected();
        using (server)
        using (window)
        {
            var ok = await window.ExecAsync("vscode.openFolder", Args.Of(Args.Uri("/tmp/some folder"), false), focus: true);
            Assert.True(ok.Ok, ok.ToString());
            Assert.Equal("vscode.openFolder", ok.Result?["command"]?.GetValue<string>());
            Assert.Equal("file:///tmp/some%20folder", ok.Result?["args"]?[0]?["$uri"]?.GetValue<string>());
            Assert.True(server.Requests.Last()["focus"]?.GetValue<bool>());

            var unknown = await window.ExecAsync("test.unknown");
            Assert.False(unknown.Ok);
            Assert.True(unknown.IsUnknownCommand);
            Assert.Contains("not found", unknown.ErrorMessage);

            var missing = await window.RunTaskAsync("nope");
            Assert.Equal(BridgeErrors.NotFound, missing.ErrorCode);
            Assert.True((await window.RunTaskAsync("build")).Ok);
            Assert.Equal("pong", (await window.PingAsync()).Result?.GetValue<string>());
        }
    }

    [Fact]
    public async Task Request_times_out()
    {
        var (server, window) = await Connected();
        using (server)
        using (window)
        {
            var started = DateTime.UtcNow;
            var result = await window.RequestAsync(new JsonObject { ["op"] = "exec", ["command"] = "test.slow" }, TimeSpan.FromMilliseconds(200));
            Assert.Equal(BridgeErrors.Timeout, result.ErrorCode);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
            // The connection survives a slow command.
            Assert.True((await window.PingAsync()).Ok);
        }
    }

    [Fact]
    public async Task Lists_parse_items()
    {
        var (server, window) = await Connected();
        using (server)
        using (window)
        {
            server.Lists["tasks"] = (JsonArray)JsonNode.Parse("""
                [{"ref":"t1","label":"build","detail":"npm · tasks.json","kind":"task","severity":null,"dirty":false,"active":false},
                 {"label":"no ref, skipped"},
                 {"ref":"t2","label":"watch","extra":42}]
                """)!;
            var items = await window.ListAsync("tasks");
            Assert.Equal(["build", "watch"], items.Select(i => i.Label));
            Assert.Equal("npm · tasks.json", items[0].Detail);
            Assert.Empty(await window.ListAsync("launch"));
        }
    }

    [Fact]
    public async Task State_event_replaces_only_its_sections()
    {
        var (server, window) = await Connected();
        using (server)
        using (window)
        {
            var seen = new TaskCompletionSource<IReadOnlyList<string>>();
            window.StateChanged += (_, sections) => seen.TrySetResult(sections);
            await server.PushAsync("""{"ev":"state","data":{"problems":{"errors":1,"warnings":0,"infos":2},"mystery":{"x":1}}}""");

            var sections = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["problems"], sections);
            Assert.Equal(new ProblemCounts(1, 0, 2), window.State.Problems);
            Assert.Equal("main", window.State.Git?.Branch); // untouched
        }
    }

    [Fact]
    public async Task Focus_event_updates_focus()
    {
        var (server, window) = await Connected();
        using (server)
        using (window)
        {
            var focused = new TaskCompletionSource();
            window.FocusChanged += _ => focused.TrySetResult();
            await server.PushAsync("""{"ev":"focus","focused":true,"active":true,"at":1791659999000}""");
            await focused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(window.IsFocused);
            Assert.True(window.State.Window?.Focused);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_791_659_999_000), window.LastFocused);
        }
    }

    [Fact]
    public async Task Lists_and_notify_events_are_raised()
    {
        var (server, window) = await Connected();
        using (server)
        using (window)
        {
            var lists = new TaskCompletionSource<IReadOnlyList<string>>();
            var notify = new TaskCompletionSource<(string, string)>();
            window.ListsChanged += (_, what) => lists.TrySetResult(what);
            window.Notified += (_, kind, text) => notify.TrySetResult((kind, text));
            await server.PushAsync("""{"ev":"lists","what":["tasks","launch"]}""");
            await server.PushAsync("""{"ev":"notify","kind":"taskFailed","text":"build exited with 1"}""");
            Assert.Equal(["tasks", "launch"], await lists.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(("taskFailed", "build exited with 1"), await notify.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Garbage_lines_are_skipped()
    {
        var (server, window) = await Connected();
        using (server)
        using (window)
        {
            await server.PushAsync("not json at all");
            await server.PushAsync("[1,2,3]");
            Assert.True((await window.PingAsync()).Ok);
        }
    }

    [Fact]
    public async Task Server_drop_disconnects_and_fails_pending_requests()
    {
        var (server, window) = await Connected();
        using (server)
        using (window)
        {
            var dropped = new TaskCompletionSource();
            window.Disconnected += _ => dropped.TrySetResult();
            var pending = window.RequestAsync(new JsonObject { ["op"] = "exec", ["command"] = "test.slow" }, TimeSpan.FromSeconds(10));
            await Wait.Until(() => server.Requests.Count > 0);
            server.DropClients();

            await dropped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsConnected);
            Assert.Equal(BridgeErrors.Disconnected, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode);
            Assert.Equal(BridgeErrors.Disconnected, (await window.PingAsync()).ErrorCode);
        }
    }

    [Fact]
    public async Task Overlong_line_closes_the_connection()
    {
        var (server, window) = await Connected();
        using (server)
        using (window)
        {
            var dropped = new TaskCompletionSource();
            window.Disconnected += _ => dropped.TrySetResult();
            await server.PushAsync("{\"ev\":\"notify\",\"text\":\"" + new string('x', BridgeProtocol.MaxLine + 10) + "\"}");
            await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(window.IsConnected);
        }
    }

    [Fact]
    public async Task Dispose_does_not_raise_disconnected()
    {
        var (server, window) = await Connected();
        using (server)
        {
            var raised = false;
            window.Disconnected += _ => raised = true;
            window.Dispose();
            await Task.Delay(100);
            Assert.False(raised);
            Assert.False(window.IsConnected);
        }
    }
}

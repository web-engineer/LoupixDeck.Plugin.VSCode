using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Bridge;

/// <summary>How a connection attempt ended.</summary>
internal enum ConnectOutcome
{
    /// <summary>Connected, and the window accepted the token.</summary>
    Connected,
    /// <summary>Nothing listens there (socket missing or connection refused): the registration is probably stale.</summary>
    Refused,
    /// <summary>Something listens but closed on us or said no to <c>hello</c> (wrong token).</summary>
    Rejected,
    /// <summary>The window speaks a newer protocol than this plugin: the plugin needs an update.</summary>
    ProtocolTooNew,
    /// <summary>Anything else (timeout, I/O error).</summary>
    Failed
}

/// <summary>
/// One VS Code window, reached through the socket (or, on Windows, the named pipe) its companion
/// extension registered. Connects, says <c>hello</c> with the window's token, then reads replies
/// and events on a background loop: replies complete the matching request, <c>state</c> events
/// build a new <see cref="State"/>, the rest are raised as C# events. Single use: once the
/// connection drops, the hub makes a new one.
/// </summary>
internal sealed class BridgeWindow : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly IPluginLogger? _log;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject?>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();
    private Stream? _stream;
    private Task? _readLoop;
    private int _nextId;
    private int _helloId;
    private int _closed;
    private EditorState _state = EditorState.Empty;
    private bool _focused;
    private DateTimeOffset _lastFocused;

    public BridgeWindow(Registration registration, IPluginLogger? log = null)
    {
        Registration = registration;
        _log = log;
        App = registration.App;
        Version = registration.Version;
        UriScheme = registration.UriScheme;
        ExtVersion = registration.ExtVersion;
        _lastFocused = registration.StartedAt ?? DateTimeOffset.MinValue;
    }

    /// <summary>The registration file this connection came from.</summary>
    public Registration Registration { get; }

    /// <summary>The window's extension host process id.</summary>
    public int Pid => Registration.Pid;

    /// <summary>The editor's name, e.g. "Visual Studio Code" or "Cursor".</summary>
    public string App { get; private set; }

    /// <summary>The editor's version, e.g. "1.141.0".</summary>
    public string? Version { get; private set; }

    /// <summary>The editor's URI scheme: <c>vscode</c>, <c>vscode-insiders</c>, <c>cursor</c>…</summary>
    public string UriScheme { get; private set; }

    /// <summary>The editor's <c>appRoot</c> (…/Contents/Resources/app), where its product.json lives.</summary>
    public string? AppRoot => Registration.AppRoot;

    /// <summary>The companion's own version.</summary>
    public string? ExtVersion { get; private set; }

    /// <summary>The workspace name, from the latest state (falling back to the registration).</summary>
    public string? WorkspaceName => State.Workspace?.Name ?? Registration.WorkspaceName;

    /// <summary>The workspace folder URIs, from the latest state (falling back to the registration).</summary>
    public IReadOnlyList<string> Folders => State.Workspace?.Folders ?? Registration.Folders;

    /// <summary>The remote name (<c>ssh-remote</c>, <c>wsl</c>…), null for a local window.</summary>
    public string? Remote => State.Workspace is { } w ? w.Remote : Registration.RemoteName;

    /// <summary>When the window started (from the registration), for ordering windows nobody focused yet.</summary>
    public DateTimeOffset StartedAt => Registration.StartedAt ?? DateTimeOffset.MinValue;

    /// <summary>True when the window has <c>workbench.action.focusWindow</c>, so <c>focus: true</c> raises it.</summary>
    public bool HasFocusWindow { get; private set; }

    /// <summary>True while the window has OS focus.</summary>
    public bool IsFocused { get { lock (_stateLock) return _focused; } }

    /// <summary>When the window last gained or lost focus; <see cref="DateTimeOffset.MinValue"/> when unknown.</summary>
    public DateTimeOffset LastFocused { get { lock (_stateLock) return _lastFocused; } }

    /// <summary>The latest snapshot. Swapped whole on every state event; safe to read from any thread.</summary>
    public EditorState State => Volatile.Read(ref _state);

    public bool IsConnected => _stream is not null && Volatile.Read(ref _closed) == 0;

    /// <summary>A short name for logs and keys: the workspace name, else the app.</summary>
    public string DisplayName => WorkspaceName is { Length: > 0 } name ? name : App;

    /// <summary>Raised with the section names a <c>state</c> event replaced.</summary>
    public event Action<BridgeWindow, IReadOnlyList<string>>? StateChanged;

    /// <summary>Raised when the window gained or lost focus.</summary>
    public event Action<BridgeWindow>? FocusChanged;

    /// <summary>Raised with the list kinds (<c>tasks</c>, <c>problems</c>…) whose results may have changed.</summary>
    public event Action<BridgeWindow, IReadOnlyList<string>>? ListsChanged;

    /// <summary>Raised with a notification's kind (<c>taskFailed</c>…) and short text.</summary>
    public event Action<BridgeWindow, string, string>? Notified;

    /// <summary>Raised once when the connection drops on its own (not on <see cref="Dispose"/>).</summary>
    public event Action<BridgeWindow>? Disconnected;

    /// <summary>
    /// Opens the channel and says <c>hello</c>. On anything but <see cref="ConnectOutcome.Connected"/>
    /// the connection is closed again and this object is spent.
    /// </summary>
    public async Task<ConnectOutcome> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _stream = await OpenAsync(Registration.Socket, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            Close();
            return ConnectOutcome.Refused;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log?.Warn($"VS Code bridge: cannot connect to window {Pid}: {ex.Message}");
            Close();
            return ConnectOutcome.Failed;
        }

        _readLoop = Task.Run(ReadLoopAsync);

        var hello = new JsonObject
        {
            ["op"] = "hello",
            ["token"] = Registration.Token,
            ["client"] = BridgeProtocol.Client,
            ["protocol"] = BridgeProtocol.Protocol
        };
        var reply = await RequestAsync(hello, BridgeProtocol.RequestTimeout, ct).ConfigureAwait(false);
        if (!reply.Ok)
        {
            Close();
            return reply.ErrorCode is BridgeErrors.Timeout or BridgeErrors.Cancelled ? ConnectOutcome.Failed : ConnectOutcome.Rejected;
        }

        var result = reply.Result as JsonObject;
        if ((Json.Int(result, "protocol") ?? 1) > BridgeProtocol.Protocol)
        {
            Close();
            return ConnectOutcome.ProtocolTooNew;
        }

        App = Json.Str(result, "app") ?? App;
        Version = Json.Str(result, "version") ?? Version;
        UriScheme = Json.Str(result, "uriScheme") ?? UriScheme;
        ExtVersion = Json.Str(result, "extVersion") ?? ExtVersion;
        HasFocusWindow = Json.Bool(result, "focusWindow") ?? false;
        // The snapshot in result.state was installed by the read loop as the reply arrived (see
        // Dispatch), so state and focus events that follow it are applied on top, not overwritten.

        return IsConnected ? ConnectOutcome.Connected : ConnectOutcome.Rejected;
    }

    /// <summary>Runs a command by id; <paramref name="focus"/> raises the window first.</summary>
    public Task<BridgeResult> ExecAsync(string command, JsonArray? args = null, bool focus = false, CancellationToken ct = default)
    {
        var request = new JsonObject { ["op"] = "exec", ["command"] = command };
        if (args is not null) request["args"] = args.Parent is null ? args : args.DeepClone();
        if (focus) request["focus"] = true;
        return RequestAsync(request, BridgeProtocol.ExecTimeout, ct);
    }

    /// <summary>A live list (<c>tasks</c>, <c>launch</c>, <c>problems</c>, <c>editors</c>, <c>recent</c>, <c>terminals</c>); empty on any failure.</summary>
    public async Task<IReadOnlyList<BridgeItem>> ListAsync(string what, CancellationToken ct = default)
    {
        var result = await RequestAsync(new JsonObject { ["op"] = "list", ["what"] = what }, null, ct).ConfigureAwait(false);
        return result.Ok ? BridgeItem.ListFrom(result.Result) : [];
    }

    /// <summary>Acts on a list item by its <c>ref</c> (runs the task, opens the problem, focuses the tab…).</summary>
    public Task<BridgeResult> RunAsync(string what, string itemRef, bool focus = false, CancellationToken ct = default) =>
        RequestAsync(new JsonObject { ["op"] = "run", ["what"] = what, ["ref"] = itemRef, ["focus"] = focus }, BridgeProtocol.ExecTimeout, ct);

    /// <summary>Runs a task by label; <c>not_found</c> when there is none.</summary>
    public Task<BridgeResult> RunTaskAsync(string name, bool focus = false, CancellationToken ct = default) =>
        RequestAsync(new JsonObject { ["op"] = "runTask", ["name"] = name, ["focus"] = focus }, BridgeProtocol.ExecTimeout, ct);

    /// <summary>Starts a launch configuration or compound by name; <c>not_found</c> when there is none.</summary>
    public Task<BridgeResult> StartDebugAsync(string name, bool focus = false, CancellationToken ct = default) =>
        RequestAsync(new JsonObject { ["op"] = "startDebug", ["name"] = name, ["focus"] = focus }, BridgeProtocol.ExecTimeout, ct);

    /// <summary>Brings this window to the front (needs <see cref="HasFocusWindow"/> to do anything).</summary>
    public Task<BridgeResult> FocusAsync(CancellationToken ct = default) =>
        RequestAsync(new JsonObject { ["op"] = "focus" }, null, ct);

    public Task<BridgeResult> PingAsync(CancellationToken ct = default) =>
        RequestAsync(new JsonObject { ["op"] = "ping" }, null, ct);

    /// <summary>Asks for a full snapshot and swaps it in.</summary>
    public async Task<BridgeResult> RefreshStateAsync(CancellationToken ct = default)
    {
        var result = await RequestAsync(new JsonObject { ["op"] = "state" }, null, ct).ConfigureAwait(false);
        if (result.Ok && result.Result is JsonObject snapshot)
        {
            lock (_stateLock) SetState(EditorState.Parse(snapshot));
            StateChanged?.Invoke(this, EditorState.SectionNames);
        }

        return result;
    }

    /// <summary>
    /// Sends any request and waits for its reply. The <c>id</c> is set here (overwriting one the
    /// caller set). Never throws: timeouts, cancellation and a dropped connection come back as
    /// <see cref="BridgeResult.Fail"/> with a client-side code.
    /// </summary>
    public async Task<BridgeResult> RequestAsync(JsonObject request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IsConnected) return BridgeResult.Fail(BridgeErrors.Disconnected, "Not connected");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            request["id"] = id;
            if (Json.Str(request, "op") == "hello") Volatile.Write(ref _helloId, id);
            await WriteAsync(request, ct).ConfigureAwait(false);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            var reply = await tcs.Task.WaitAsync(timeout ?? BridgeProtocol.RequestTimeout, linked.Token).ConfigureAwait(false);
            return reply is null ? BridgeResult.Fail(BridgeErrors.Disconnected, "Connection closed") : BridgeResult.FromReply(reply);
        }
        catch (TimeoutException)
        {
            return BridgeResult.Fail(BridgeErrors.Timeout, $"No reply from the window within {(timeout ?? BridgeProtocol.RequestTimeout).TotalSeconds:0} s");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return BridgeResult.Fail(BridgeErrors.Cancelled);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            return BridgeResult.Fail(BridgeErrors.Disconnected, ex.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        // The token source is left to the GC: the read loop may still be unwinding with its token.
        Close();
    }

    // ---- transport ----

    private static async Task<Stream> OpenAsync(string address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        if (OperatingSystem.IsWindows())
        {
            const string prefix = @"\\.\pipe\";
            var name = address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? address[prefix.Length..] : address;
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        if (!File.Exists(address)) throw new FileNotFoundException("Socket not found", address);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), timeout.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Nobody listens at the address: the socket file is gone, or the connection was refused.</summary>
    private static bool IsRefusal(Exception ex) => ex switch
    {
        FileNotFoundException => true,
        SocketException se => se.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported,
        // A named pipe that does not exist times out on Windows; there is no refusal as such.
        TimeoutException => OperatingSystem.IsWindows(),
        OperationCanceledException => OperatingSystem.IsWindows(),
        _ => false
    };

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("Not connected");
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var stream = _stream!;
        var buffer = new byte[64 * 1024];
        var line = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read == 0) break;

                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    if (line.Length > BridgeProtocol.MaxLine) throw new InvalidDataException("Line too long");
                    if (line.Length > 0) Dispatch(line.GetBuffer().AsSpan(0, (int)line.Length));
                    line.SetLength(0);
                }

                line.Write(buffer, start, read - start);
                if (line.Length > BridgeProtocol.MaxLine) throw new InvalidDataException("Line too long");
            }
        }
        catch (OperationCanceledException)
        {
            // closing
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _closed) == 0) _log?.Warn($"VS Code bridge: window {Pid} ({DisplayName}) connection error: {ex.Message}");
        }

        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            CloseCore();
            Disconnected?.Invoke(this);
        }
    }

    private void Dispatch(ReadOnlySpan<byte> utf8)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(utf8) as JsonObject;
        }
        catch (JsonException)
        {
            _log?.Warn($"VS Code bridge: window {Pid} sent a line that is not JSON: {Truncate(Encoding.UTF8.GetString(utf8))}");
            return;
        }

        if (message is null) return;

        if (Json.Int(message, "id") is { } id)
        {
            // Install the hello snapshot here, on the read loop, before the next line is read: the
            // window may push state or focus events straight after the reply, and those must land
            // on top of the snapshot rather than be replaced by it when ConnectAsync resumes.
            if (id == Volatile.Read(ref _helloId) && Json.Bool(message, "ok") == true)
                lock (_stateLock) SetState(EditorState.Parse((message["result"] as JsonObject)?["state"] as JsonObject));
            if (_pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(message);
            return;
        }

        try
        {
            switch (Json.Str(message, "ev"))
            {
                case "state" when message["data"] is JsonObject data:
                    OnState(data);
                    break;
                case "focus":
                    OnFocus(message);
                    break;
                case "lists":
                    ListsChanged?.Invoke(this, Json.Strings(message, "what"));
                    break;
                case "notify":
                    Notified?.Invoke(this, Json.Str(message, "kind") ?? string.Empty, Json.Str(message, "text") ?? string.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            // A handler's bug must not kill the connection.
            _log?.Error("VS Code bridge: event handler failed", ex);
        }
    }

    private void OnState(JsonObject data)
    {
        lock (_stateLock) SetState(_state.Apply(data));
        var sections = EditorState.SectionsIn(data);
        if (sections.Count > 0) StateChanged?.Invoke(this, sections);
    }

    private void OnFocus(JsonObject message)
    {
        lock (_stateLock)
        {
            var focused = Json.Bool(message, "focused") ?? false;
            var at = Json.Time(message, "at") ?? DateTimeOffset.UtcNow;
            var window = new WindowInfo(focused, Json.Bool(message, "active") ?? focused, at);
            SetState(_state with { Window = window });
        }

        FocusChanged?.Invoke(this);
    }

    /// <summary>Swaps the snapshot in and keeps the focus fields in step with its window section. Call under the state lock.</summary>
    private void SetState(EditorState state)
    {
        Volatile.Write(ref _state, state);
        if (state.Window is not { } w) return;
        _focused = w.Focused;
        if (w.LastFocused is { } at) _lastFocused = at;
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) CloseCore();
    }

    private void CloseCore()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _stream?.Dispose(); } catch { /* already gone */ }
        foreach (var tcs in _pending.Values) tcs.TrySetResult(null);
    }

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80] + "…";

    public override string ToString() => $"{App} window {Pid} ({DisplayName})";
}

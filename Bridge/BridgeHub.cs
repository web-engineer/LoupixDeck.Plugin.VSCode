using System.Diagnostics;
using System.Text.Json.Nodes;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Bridge;

/// <summary>
/// What changed in the hub, for the plugin to refresh the right keys: the window it concerns, the
/// state sections it replaced, and flags for the window list, the target window, live lists and
/// notifications.
/// </summary>
internal sealed record HubChange(
    BridgeWindow? Window,
    IReadOnlySet<string> Sections,
    bool WindowsChanged = false,
    bool TargetChanged = false,
    IReadOnlyList<string>? Lists = null,
    string? NotifyKind = null,
    string? NotifyText = null)
{
    private static readonly IReadOnlySet<string> NoSections = new HashSet<string>();

    /// <summary>True when the change is about the target window (or switched the target), so keys showing its state need a repaint.</summary>
    public bool ForTarget { get; init; }

    /// <summary>True when the section changed for the target window, or the target itself changed.</summary>
    public bool Affects(string section) => TargetChanged || ForTarget && Sections.Contains(section);

    /// <summary>True when the list kind may have changed for the target window, or the target itself changed.</summary>
    public bool AffectsList(string what) => TargetChanged || ForTarget && Lists is { } l && l.Contains(what);

    internal static HubChange Of(BridgeWindow? window, IEnumerable<string>? sections = null) =>
        new(window, sections is null ? NoSections : sections.ToHashSet());
}

/// <summary>
/// Finds every VS Code window running the companion and keeps one connection open to each. The
/// companion writes <c>&lt;pid&gt;.json</c> into the registration directory; the hub watches the
/// directory and rescans it every 3 s (file watchers miss events), connects to each live window,
/// deletes registrations a crash left behind and reconnects windows whose connection dropped.
/// Keys talk to the <see cref="Target"/>: the focused window, else the one focused last.
/// <see cref="Start"/> and <see cref="Stop"/> are idempotent so a <c>DemandGate</c> can drive them.
/// </summary>
internal sealed class BridgeHub : IDisposable
{
    /// <summary>A registration younger than this that refused once may simply not be listening yet.</summary>
    private static readonly TimeSpan StaleGrace = TimeSpan.FromSeconds(5);

    /// <summary>Lets a burst of watcher events settle into one scan.</summary>
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>The <see cref="LastError"/> text when a window speaks a newer protocol.</summary>
    public const string UpdatePluginError = "UpdatePlugin";

    private readonly IPluginLogger _log;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, BridgeWindow> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _refusals = new(StringComparer.Ordinal);
    /// <summary>Registrations not to retry until the file changes (bad token, newer protocol), by path, with the file time seen.</summary>
    private readonly Dictionary<string, DateTime> _shelved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private BridgeWindow? _target;
    private bool _running;
    private bool _rescanRequested;

    public BridgeHub(IPluginLogger logger, string? directory = null)
    {
        _log = logger;
        Directory = directory ?? BridgeProtocol.RegistrationDirectory();
    }

    /// <summary>The registration directory this hub watches.</summary>
    public string Directory { get; }

    /// <summary>How often the directory is rescanned. Tests shorten it.</summary>
    internal TimeSpan RescanInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>When the first scan after <see cref="Start"/> runs. Tests set it to infinite and scan by hand.</summary>
    internal TimeSpan FirstScan { get; init; } = TimeSpan.Zero;

    /// <summary>Whether a process is alive. Tests replace it to fake dead or live windows.</summary>
    internal Func<int, bool> IsAlive { get; init; } = DefaultIsAlive;

    /// <summary>Raised on any change: windows connecting or dropping, state, focus, lists, notifications.</summary>
    public event Action<HubChange>? Changed;

    /// <summary>The connected windows, oldest first.</summary>
    public IReadOnlyList<BridgeWindow> Windows
    {
        get
        {
            lock (_lock) return _windows.Values.Where(w => w.IsConnected).OrderBy(w => w.StartedAt).ToList();
        }
    }

    /// <summary>The window keys act on: the focused one, else the one focused last, else the newest. Null when none is connected.</summary>
    public BridgeWindow? Target
    {
        get
        {
            lock (_lock) return PickTarget();
        }
    }

    public bool AnyConnected
    {
        get
        {
            lock (_lock) return _windows.Values.Any(w => w.IsConnected);
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock) return _running;
        }
    }

    /// <summary>
    /// The last problem worth showing, or null. <see cref="UpdatePluginError"/> when a window speaks
    /// a newer protocol than this plugin.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>True when a window was refused because it needs a newer plugin.</summary>
    public bool NeedsPluginUpdate => LastError == UpdatePluginError;

    /// <summary>Starts watching and connecting. Does nothing when already running.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            EnsureDirectory();
            try
            {
                _watcher = new FileSystemWatcher(Directory, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                _watcher.Created += OnWatcher;
                _watcher.Changed += OnWatcher;
                _watcher.Renamed += OnWatcher;
                _watcher.Deleted += OnWatcher;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // The rescan timer still finds windows, just a little later.
                _log.Warn($"VS Code bridge: cannot watch {Directory}: {ex.Message}");
                _watcher?.Dispose();
                _watcher = null;
            }

            _timer = new Timer(_ => _ = ScanAsync(), null, FirstScan, FirstScan == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : RescanInterval);
        }
    }

    /// <summary>Stops watching and closes every connection. Does nothing when already stopped.</summary>
    public void Stop()
    {
        List<BridgeWindow> closed;
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
            _cts = null;
            _watcher?.Dispose();
            _watcher = null;
            _timer?.Dispose();
            _timer = null;
            closed = _windows.Values.ToList();
            _windows.Clear();
            _refusals.Clear();
            _shelved.Clear();
            _target = null;
        }

        foreach (var w in closed) Detach(w);
        if (closed.Count > 0) Raise(new HubChange(null, new HashSet<string>(), WindowsChanged: true, TargetChanged: true));
    }

    public void Dispose() => Stop();

    // ---- convenience: requests to the target window ----

    public Task<BridgeResult> ExecAsync(string command, JsonArray? args = null, bool focus = false, CancellationToken ct = default) =>
        Target is { } w ? w.ExecAsync(command, args, focus, ct) : Offline();

    public Task<IReadOnlyList<BridgeItem>> ListAsync(string what, CancellationToken ct = default) =>
        Target is { } w ? w.ListAsync(what, ct) : Task.FromResult<IReadOnlyList<BridgeItem>>([]);

    public Task<BridgeResult> RunAsync(string what, string itemRef, bool focus = false, CancellationToken ct = default) =>
        Target is { } w ? w.RunAsync(what, itemRef, focus, ct) : Offline();

    public Task<BridgeResult> RunTaskAsync(string name, bool focus = false, CancellationToken ct = default) =>
        Target is { } w ? w.RunTaskAsync(name, focus, ct) : Offline();

    public Task<BridgeResult> StartDebugAsync(string name, bool focus = false, CancellationToken ct = default) =>
        Target is { } w ? w.StartDebugAsync(name, focus, ct) : Offline();

    public Task<BridgeResult> FocusAsync(CancellationToken ct = default) =>
        Target is { } w ? w.FocusAsync(ct) : Offline();

    private static Task<BridgeResult> Offline() => Task.FromResult(BridgeResult.Fail(BridgeErrors.Offline, "No VS Code window with the companion is connected"));

    // ---- discovery ----

    /// <summary>
    /// Reads the directory once: connects to new live windows, deletes stale registrations and
    /// reconnects dropped ones. Concurrent calls fold into one extra pass. Never throws.
    /// </summary>
    internal async Task ScanAsync()
    {
        CancellationToken ct;
        lock (_lock)
        {
            if (!_running || _cts is null) return;
            ct = _cts.Token;
        }

        if (!await _scanGate.WaitAsync(0).ConfigureAwait(false))
        {
            lock (_lock) _rescanRequested = true;
            return;
        }

        try
        {
            do
            {
                lock (_lock) _rescanRequested = false;
                await ScanOnceAsync(ct).ConfigureAwait(false);
            } while (!ct.IsCancellationRequested && RescanRequested());
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error("VS Code bridge: scan failed", ex);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private bool RescanRequested()
    {
        lock (_lock) return _rescanRequested;
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        string[] files;
        try
        {
            files = System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogOnce("dir", $"VS Code bridge: cannot read {Directory}: {ex.Message}");
            return;
        }

        var attempts = new List<Task>();
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_windows.TryGetValue(path, out var existing) && existing.IsConnected) continue;
            }

            DateTime written;
            try
            {
                written = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            lock (_lock)
            {
                if (_shelved.TryGetValue(path, out var seen))
                {
                    if (seen == written) continue;
                    _shelved.Remove(path);
                }
            }

            var registration = Registration.TryRead(path);
            if (registration is null)
            {
                // The companion renames complete files into place, so an unreadable one that has
                // sat there a while is junk. A young one might still be mid-copy by something else.
                if (DateTime.UtcNow - written > StaleGrace) DeleteStale(path, null, "it is not a valid registration");
                continue;
            }

            if (!IsAlive(registration.Pid))
            {
                DeleteStale(path, registration, $"process {registration.Pid} is gone");
                continue;
            }

            if (registration.V > BridgeProtocol.Protocol)
            {
                RefuseNewer(path, written, registration.V);
                continue;
            }

            attempts.Add(ConnectAsync(path, written, registration, ct));
        }

        await Task.WhenAll(attempts).ConfigureAwait(false);
    }

    private async Task ConnectAsync(string path, DateTime written, Registration registration, CancellationToken ct)
    {
        var window = new BridgeWindow(registration, _log);
        ConnectOutcome outcome;
        try
        {
            outcome = await window.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            window.Dispose();
            return;
        }

        switch (outcome)
        {
            case ConnectOutcome.Connected:
                Attach(path, window);
                return;

            case ConnectOutcome.Refused:
                window.Dispose();
                int count;
                lock (_lock) count = _refusals[path] = _refusals.GetValueOrDefault(path) + 1;
                if (count >= 2 || DateTime.UtcNow - written > StaleGrace)
                    DeleteStale(path, registration, "nothing is listening on its socket");
                return;

            case ConnectOutcome.Rejected:
                window.Dispose();
                lock (_lock) _shelved[path] = written;
                LastError = $"Window {registration.Pid} rejected the connection";
                LogOnce(path + ":rejected", $"VS Code bridge: window {registration.Pid} ({registration.WorkspaceName ?? registration.App}) rejected the connection; will retry when its registration changes");
                return;

            case ConnectOutcome.ProtocolTooNew:
                window.Dispose();
                RefuseNewer(path, written, null);
                return;

            default:
                // Timeouts and I/O errors: try again on the next scan.
                window.Dispose();
                return;
        }
    }

    private void Attach(string path, BridgeWindow window)
    {
        BridgeWindow? replaced;
        bool targetChanged;
        lock (_lock)
        {
            if (!_running)
            {
                window.Dispose();
                return;
            }

            _windows.TryGetValue(path, out replaced);
            _windows[path] = window;
            _refusals.Remove(path);
            window.StateChanged += OnStateChanged;
            window.FocusChanged += OnFocusChanged;
            window.ListsChanged += OnListsChanged;
            window.Notified += OnNotified;
            window.Disconnected += OnDisconnected;
            targetChanged = UpdateTarget();
        }

        if (replaced is not null) Detach(replaced);
        if (LastError is not null && LastError != UpdatePluginError) LastError = null;
        _log.Info($"VS Code bridge: connected to {window.App} {window.Version} window {window.Pid} ({window.DisplayName}), companion {window.ExtVersion}");

        // The connection may have dropped before the handlers were attached.
        if (!window.IsConnected)
        {
            OnDisconnected(window);
            return;
        }

        Raise(HubChange.Of(window, EditorState.SectionNames) with { WindowsChanged = true, TargetChanged = targetChanged, ForTarget = IsTarget(window) });
    }

    private static void Detach(BridgeWindow window)
    {
        window.Dispose();
    }

    private void RefuseNewer(string path, DateTime written, int? version)
    {
        lock (_lock) _shelved[path] = written;
        LastError = UpdatePluginError;
        LogOnce(path + ":protocol", $"VS Code bridge: the companion in {Path.GetFileName(path)} speaks protocol {version?.ToString() ?? "newer than " + BridgeProtocol.Protocol}; update the LoupixDeck plugin");
    }

    private void DeleteStale(string path, Registration? registration, string why)
    {
        lock (_lock)
        {
            _refusals.Remove(path);
            _shelved.Remove(path);
        }

        try
        {
            File.Delete(path);
            // A socket in the registration directory (or the /tmp fallback) goes with its file.
            if (registration is { Socket: { Length: > 0 } socket } && !OperatingSystem.IsWindows() && IsOurSocket(socket) && File.Exists(socket))
                File.Delete(socket);
            _log.Info($"VS Code bridge: removed stale registration {Path.GetFileName(path)} ({why})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogOnce(path + ":delete", $"VS Code bridge: cannot remove stale registration {path}: {ex.Message}");
        }
    }

    /// <summary>Only ever delete sockets where the companion puts them, never an arbitrary path from a file.</summary>
    private bool IsOurSocket(string socket)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(socket)) ?? string.Empty;
        return socket.EndsWith(".sock", StringComparison.Ordinal)
               && (string.Equals(dir, Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
                   || Path.GetFileName(dir).StartsWith("loupixdeck-vscode-", StringComparison.Ordinal));
    }

    private void EnsureDirectory()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory)) return;
            // Same permissions the companion uses: only this user may see the tokens.
            if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(Directory);
            else System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogOnce("mkdir", $"VS Code bridge: cannot create {Directory}: {ex.Message}");
        }
    }

    private void OnWatcher(object sender, FileSystemEventArgs e)
    {
        lock (_lock) _timer?.Change(WatchDebounce, RescanInterval);
    }

    private static bool DefaultIsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ---- window events ----

    private void OnStateChanged(BridgeWindow window, IReadOnlyList<string> sections)
    {
        bool targetChanged;
        lock (_lock) targetChanged = sections.Contains("window") && UpdateTarget();
        Raise(HubChange.Of(window, sections) with { TargetChanged = targetChanged, ForTarget = IsTarget(window) });
    }

    private void OnFocusChanged(BridgeWindow window)
    {
        bool targetChanged;
        lock (_lock) targetChanged = UpdateTarget();
        Raise(HubChange.Of(window, ["window"]) with { TargetChanged = targetChanged, ForTarget = IsTarget(window) });
    }

    private void OnListsChanged(BridgeWindow window, IReadOnlyList<string> what) =>
        Raise(HubChange.Of(window) with { Lists = what, ForTarget = IsTarget(window) });

    private void OnNotified(BridgeWindow window, string kind, string text) =>
        Raise(HubChange.Of(window) with { NotifyKind = kind, NotifyText = text, ForTarget = IsTarget(window) });

    private void OnDisconnected(BridgeWindow window)
    {
        bool removed, targetChanged;
        lock (_lock)
        {
            var entry = _windows.FirstOrDefault(kv => ReferenceEquals(kv.Value, window));
            removed = entry.Key is not null && _windows.Remove(entry.Key);
            targetChanged = UpdateTarget();
        }

        window.Dispose();
        if (!removed) return;
        _log.Info($"VS Code bridge: window {window.Pid} ({window.DisplayName}) disconnected");
        Raise(HubChange.Of(window) with { WindowsChanged = true, TargetChanged = targetChanged });

        // A window that reloaded or quit leaves a stale file or a new one; look straight away.
        lock (_lock) _timer?.Change(WatchDebounce, RescanInterval);
    }

    // ---- target ----

    private bool IsTarget(BridgeWindow window)
    {
        lock (_lock) return ReferenceEquals(PickTarget(), window);
    }

    /// <summary>Recomputes the target; true when it changed. Call under the lock.</summary>
    private bool UpdateTarget()
    {
        var next = PickTarget();
        if (ReferenceEquals(next, _target)) return false;
        _target = next;
        return true;
    }

    private BridgeWindow? PickTarget() => _windows.Values
        .Where(w => w.IsConnected)
        .OrderByDescending(w => w.IsFocused)
        .ThenByDescending(w => w.LastFocused)
        .ThenByDescending(w => w.StartedAt)
        .FirstOrDefault();

    private void Raise(HubChange change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception ex)
        {
            _log.Error("VS Code bridge: change handler failed", ex);
        }
    }

    private void LogOnce(string key, string message)
    {
        lock (_lock)
        {
            if (!_logged.Add(key)) return;
        }

        _log.Warn(message);
    }
}

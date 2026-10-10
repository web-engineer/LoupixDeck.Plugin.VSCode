using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.VSCode.Bridge;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Platform;

/// <summary>
/// One installed VS Code-family editor. <c>Key</c> is the settings value (<c>code</c>,
/// <c>insiders</c>, <c>codium</c>, <c>cursor</c>, <c>windsurf</c>, or the CLI name of a fork found
/// through a window). <c>AppPath</c> is the .app bundle on macOS and the install folder elsewhere;
/// <c>CliPath</c> is the command-line launcher, null when there is none.
/// </summary>
internal sealed record EditorApp(string Key, string Name, string? BundleId, string? AppPath, string? CliPath, string UriScheme)
{
    /// <summary>The process name on Windows and Linux (<c>Code</c>, <c>Cursor</c>…), for front and running checks.</summary>
    public string? ProcessName { get; init; }
}

/// <summary>
/// What this machine has of VS Code and its forks, and how to reach them: detection (the known
/// install locations, with each app's own product.json as the source of truth), bringing an app to
/// the front before a fallback shortcut, and running its CLI. The per-OS branches live here so the
/// commands stay platform-neutral.
/// </summary>
internal static class EditorApps
{
    /// <summary>The <c>app</c> setting values besides <c>auto</c>, in the order detection reports them.</summary>
    public static readonly IReadOnlyList<string> Keys = ["code", "insiders", "codium", "cursor", "windsurf"];

    /// <summary>A known editor and where it usually lives on each OS.</summary>
    private sealed record Known(
        string Key,
        string Name,
        string BundleId,
        string MacApp,
        string Cli,
        string Scheme,
        string ProcessName,
        string[] WindowsDirs,
        string[] LinuxDirs,
        string[] LinuxClis);

    private static readonly Known[] KnownApps =
    [
        new("code", "Visual Studio Code", "com.microsoft.VSCode", "Visual Studio Code.app", "code", "vscode", "Code",
            [@"%LOCALAPPDATA%\Programs\Microsoft VS Code", @"%ProgramFiles%\Microsoft VS Code"],
            ["/usr/share/code", "/snap/code/current/usr/share/code", "/var/lib/flatpak/app/com.visualstudio.code/current/active/files/extra/vscode"],
            ["/usr/bin/code", "/snap/bin/code", "/var/lib/flatpak/exports/bin/com.visualstudio.code"]),
        new("insiders", "Visual Studio Code - Insiders", "com.microsoft.VSCodeInsiders", "Visual Studio Code - Insiders.app", "code-insiders", "vscode-insiders", "Code - Insiders",
            [@"%LOCALAPPDATA%\Programs\Microsoft VS Code Insiders", @"%ProgramFiles%\Microsoft VS Code Insiders"],
            ["/usr/share/code-insiders", "/snap/code-insiders/current/usr/share/code-insiders"],
            ["/usr/bin/code-insiders", "/snap/bin/code-insiders"]),
        new("codium", "VSCodium", "com.vscodium", "VSCodium.app", "codium", "vscodium", "VSCodium",
            [@"%LOCALAPPDATA%\Programs\VSCodium", @"%ProgramFiles%\VSCodium"],
            ["/usr/share/codium", "/snap/codium/current/usr/share/codium", "/var/lib/flatpak/app/com.vscodium.codium/current/active/files/share/codium"],
            ["/usr/bin/codium", "/snap/bin/codium", "/var/lib/flatpak/exports/bin/com.vscodium.codium"]),
        new("cursor", "Cursor", "com.todesktop.230313mzl4w4u92", "Cursor.app", "cursor", "cursor", "Cursor",
            [@"%LOCALAPPDATA%\Programs\cursor", @"%ProgramFiles%\Cursor"],
            ["/usr/share/cursor", "/opt/Cursor"],
            ["/usr/bin/cursor", "/usr/local/bin/cursor"]),
        new("windsurf", "Windsurf", "com.exafunction.windsurf", "Windsurf.app", "windsurf", "windsurf", "Windsurf",
            [@"%LOCALAPPDATA%\Programs\Windsurf", @"%ProgramFiles%\Windsurf"],
            ["/usr/share/windsurf", "/opt/Windsurf"],
            ["/usr/bin/windsurf", "/usr/local/bin/windsurf"])
    ];

    /// <summary>How long to wait for the editor to become the frontmost app before a shortcut is sent.</summary>
    private static readonly TimeSpan ActivateTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>A cold start takes longer to show a window; how long to wait when the editor was not running.</summary>
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Linux cannot cheaply tell when the editor has taken focus, so it waits this long after asking.</summary>
    private static readonly TimeSpan BlindSettle = TimeSpan.FromMilliseconds(350);

    /// <summary>Extra pause after the editor came to the front, so its window has keyboard focus before the keys arrive.</summary>
    private static readonly TimeSpan FocusSettle = TimeSpan.FromMilliseconds(120);

    /// <summary>Installing an extension can download dependencies; give the CLI time.</summary>
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long a detection result is trusted. An editor installed or moved while LoupixDeck runs
    /// shows up within this; detection only checks folders and reads product.json, so it is cheap.
    /// </summary>
    private static readonly TimeSpan DetectTtl = TimeSpan.FromSeconds(30);

    private static readonly object CacheLock = new();
    private static IReadOnlyList<EditorApp>? _detected;
    private static DateTime _detectedAt;

    /// <summary>The installed editors, cached for a short while (<see cref="Refresh"/> forgets them at once).</summary>
    public static IReadOnlyList<EditorApp> Detect()
    {
        lock (CacheLock)
        {
            if (_detected is null || DateTime.UtcNow - _detectedAt >= DetectTtl)
            {
                _detected = DetectCore();
                _detectedAt = DateTime.UtcNow;
            }

            return _detected;
        }
    }

    /// <summary>Forgets what <see cref="Detect"/> found, e.g. before the settings page lists apps again.</summary>
    public static void Refresh()
    {
        lock (CacheLock) _detected = null;
    }

    /// <summary>
    /// The editor the <c>app</c> setting picks. A named key gives that app when it is installed.
    /// <c>auto</c> (or anything unknown) gives the target window's app when a window is connected,
    /// else a running editor, else the first one installed. Null when nothing is found.
    /// </summary>
    public static EditorApp? Resolve(string? setting, BridgeWindow? target)
    {
        var key = setting?.Trim().ToLowerInvariant();
        var detected = Detect();
        if (!string.IsNullOrEmpty(key) && key != "auto" && Keys.Contains(key))
            return detected.FirstOrDefault(a => a.Key == key);

        if (target is not null && ForWindow(target) is { } own) return own;
        return detected.FirstOrDefault(IsRunning) ?? detected.FirstOrDefault();
    }

    /// <summary>The app behind a connected window: from its appRoot, else a detected app with the same name or URI scheme.</summary>
    public static EditorApp? ForWindow(BridgeWindow window)
    {
        if (window.AppRoot is { Length: > 0 } root && FromAppRoot(root) is { } app) return app;
        return Detect().FirstOrDefault(a => string.Equals(a.UriScheme, window.UriScheme, StringComparison.OrdinalIgnoreCase))
               ?? Detect().FirstOrDefault(a => string.Equals(a.Name, window.App, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the app from a window's <c>appRoot</c> (…/Contents/Resources/app on macOS,
    /// …\resources\app elsewhere) and the product.json found there. Null when there is no
    /// product.json and no detected app lives at that path.
    /// </summary>
    public static EditorApp? FromAppRoot(string appRoot)
    {
        if (string.IsNullOrWhiteSpace(appRoot)) return null;
        var root = appRoot.TrimEnd('/', '\\');
        var install = InstallFromAppRoot(root);
        var product = ReadProduct(root);
        if (product is null)
        {
            return Detect().FirstOrDefault(a => a.AppPath is { } p && install is not null &&
                                                string.Equals(Path.GetFullPath(p).TrimEnd('/', '\\'), Path.GetFullPath(install).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase));
        }

        var known = Match(product);
        return Build(known, product, root, install);
    }

    /// <summary>
    /// Brings the editor to the front, launching it when it is not running, and returns once it is
    /// the frontmost app. Returns false when the app could not be reached or did not come to the
    /// front in time, so no shortcut lands in another app. Where the front app cannot be checked
    /// (Linux, or an app with no bundle id / process name) it waits a moment and trusts the request.
    /// </summary>
    public static async Task<bool> ActivateAsync(IPluginHost host, EditorApp app)
    {
        if (IsFrontmost(app)) return true;
        var canCheck = CanCheckFront(app);
        var timeout = canCheck && !IsRunning(app) ? LaunchTimeout : ActivateTimeout;

        if (OperatingSystem.IsMacOS())
        {
            var opened = app.BundleId is { Length: > 0 } id && ProcessRunner.Capture("open", "-b", id) is not null
                         || app.AppPath is { Length: > 0 } path && ProcessRunner.Capture("open", "-a", path) is not null;
            if (!opened) return false;
        }
        else
        {
            // The URI scheme with no path focuses the running editor (or starts it) without opening anything.
            var asked = host.OpenBrowser(app.UriScheme + "://");
            if (!asked && app.CliPath is { } cli) asked = ProcessRunner.Launch(cli);
            if (!asked) return false;
        }

        if (!canCheck)
        {
            await Task.Delay(BlindSettle).ConfigureAwait(false);
            return true;
        }

        if (await WaitFrontAsync(app, timeout).ConfigureAwait(false)) return true;
        host.Logger.Warn($"{app.Name} did not come to the front in time; the shortcut was not sent");
        return false;
    }

    /// <summary>True when <see cref="IsFrontmost"/> can give a real answer for this app on this OS.</summary>
    internal static bool CanCheckFront(EditorApp app) =>
        OperatingSystem.IsMacOS() ? !string.IsNullOrEmpty(app.BundleId)
        : OperatingSystem.IsWindows() && !string.IsNullOrEmpty(app.ProcessName);

    /// <summary>True when the editor owns the frontmost window. Linux always answers false.</summary>
    public static bool IsFrontmost(EditorApp app)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (string.IsNullOrEmpty(app.BundleId)) return false;
                // lsappinfo front prints the ASN of the frontmost app; ask for its bundle id.
                var asn = ProcessRunner.Capture("lsappinfo", "front");
                if (string.IsNullOrEmpty(asn)) return false;
                var info = ProcessRunner.Capture("lsappinfo", "info", "-only", "bundleid", asn);
                return info is not null && info.Contains('"' + app.BundleId + '"', StringComparison.OrdinalIgnoreCase);
            }

            if (OperatingSystem.IsWindows()) return WindowsForegroundIs(app.ProcessName);
        }
        catch
        {
            // best effort: an unknown answer only means we activate again
        }

        return false;
    }

    /// <summary>True when the editor is running (macOS: lsappinfo by bundle id; elsewhere: by process name).</summary>
    public static bool IsRunning(EditorApp app)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (string.IsNullOrEmpty(app.BundleId)) return false;
                var info = ProcessRunner.Capture("lsappinfo", "info", "-only", "pid", "-app", app.BundleId);
                return info is not null && info.Contains("pid", StringComparison.OrdinalIgnoreCase);
            }

            if (app.ProcessName is { Length: > 0 } name)
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var p in processes) p.Dispose();
                return processes.Length > 0;
            }
        }
        catch
        {
            // best effort
        }

        return false;
    }

    /// <summary>
    /// Runs the editor's CLI, e.g. <c>--install-extension &lt;vsix&gt; --force</c> or
    /// <c>--list-extensions --show-versions</c>, and returns its exit code and output. Exit code -1
    /// when the app has no CLI, it could not start or it ran past two minutes.
    /// </summary>
    public static Task<(int ExitCode, string Output)> RunCliAsync(EditorApp app, params string[] args)
    {
        if (string.IsNullOrEmpty(app.CliPath) || !File.Exists(app.CliPath))
            return Task.FromResult((-1, $"{app.Name} has no command-line launcher"));
        return ProcessRunner.RunAsync(app.CliPath, args, CliTimeout);
    }

    /// <summary>Opens a folder or .code-workspace in the editor (focusing the window that has it open). False when nothing could be started.</summary>
    public static bool LaunchFolder(EditorApp app, string path)
    {
        if (app.CliPath is { Length: > 0 } cli && File.Exists(cli) && ProcessRunner.Launch(cli, path)) return true;
        if (OperatingSystem.IsMacOS())
        {
            if (app.BundleId is { Length: > 0 } id && ProcessRunner.Launch("open", "-b", id, path)) return true;
            if (app.AppPath is { Length: > 0 } bundle && ProcessRunner.Launch("open", "-a", bundle, path)) return true;
        }

        return false;
    }

    // ---- detection ----

    private static IReadOnlyList<EditorApp> DetectCore()
    {
        var found = new List<EditorApp>();
        try
        {
            foreach (var known in KnownApps)
            {
                if (DetectOne(known) is { } app) found.Add(app);
            }
        }
        catch
        {
            // detection is best effort
        }

        return found;
    }

    private static EditorApp? DetectOne(Known known)
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var parent in new[] { "/Applications", Path.Combine(home, "Applications") })
            {
                var bundle = Path.Combine(parent, known.MacApp);
                if (Directory.Exists(bundle)) return FromBundle(bundle, known);
            }

            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var template in known.WindowsDirs)
            {
                var dir = Environment.ExpandEnvironmentVariables(template);
                if (!Directory.Exists(dir)) continue;
                var root = Path.Combine(dir, "resources", "app");
                return Build(known, ReadProduct(root), root, dir);
            }

            return null;
        }

        if (OperatingSystem.IsLinux())
        {
            var cli = known.LinuxClis.FirstOrDefault(File.Exists);
            foreach (var dir in known.LinuxDirs)
            {
                if (!Directory.Exists(dir)) continue;
                var root = Path.Combine(dir, "resources", "app");
                var app = Build(known, ReadProduct(root), root, dir);
                return cli is not null ? app with { CliPath = cli } : app;
            }

            if (cli is not null) return new EditorApp(known.Key, known.Name, null, null, cli, known.Scheme) { ProcessName = known.Cli };
        }

        return null;
    }

    /// <summary>A macOS .app bundle, read through its product.json.</summary>
    internal static EditorApp FromBundle(string bundle) => FromBundle(bundle, null);

    private static EditorApp FromBundle(string bundle, Known? known)
    {
        var root = Path.Combine(bundle, "Contents", "Resources", "app");
        var product = ReadProduct(root);
        return Build(known ?? (product is null ? null : Match(product)), product, root, bundle);
    }

    /// <summary>The fields of product.json the plugin uses.</summary>
    internal sealed record Product(string? NameLong, string? NameShort, string? ApplicationName, string? UrlProtocol, string? BundleId);

    /// <summary>Reads <c>&lt;appRoot&gt;/product.json</c>; null when missing or unreadable.</summary>
    internal static Product? ReadProduct(string appRoot)
    {
        try
        {
            var path = Path.Combine(appRoot, "product.json");
            if (!File.Exists(path)) return null;
            return ParseProduct(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static Product? ParseProduct(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            return new Product(Json.Str(o, "nameLong"), Json.Str(o, "nameShort"), Json.Str(o, "applicationName"),
                Json.Str(o, "urlProtocol"), Json.Str(o, "darwinBundleIdentifier"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Known? Match(Product product) =>
        KnownApps.FirstOrDefault(k => product.BundleId is { } id && string.Equals(k.BundleId, id, StringComparison.OrdinalIgnoreCase))
        ?? KnownApps.FirstOrDefault(k => product.UrlProtocol is { } scheme && string.Equals(k.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
        ?? KnownApps.FirstOrDefault(k => product.ApplicationName is { } cli && string.Equals(k.Cli, cli, StringComparison.OrdinalIgnoreCase));

    /// <summary>Combines what product.json says with the known defaults; product.json wins.</summary>
    private static EditorApp Build(Known? known, Product? product, string appRoot, string? install)
    {
        var cliName = product?.ApplicationName ?? known?.Cli ?? "code";
        var key = known?.Key ?? cliName;
        var name = product?.NameLong ?? known?.Name ?? product?.NameShort ?? cliName;
        var bundleId = product?.BundleId ?? (OperatingSystem.IsMacOS() ? known?.BundleId : null);
        var scheme = product?.UrlProtocol ?? known?.Scheme ?? cliName;
        return new EditorApp(key, name, bundleId, install, FindCli(appRoot, install, cliName), scheme)
        {
            ProcessName = known?.ProcessName ?? product?.NameShort
        };
    }

    /// <summary>
    /// The CLI: <c>bin/&lt;applicationName&gt;</c> under the app root (macOS, Linux tarballs, Cursor on
    /// Windows) or the install folder (Windows), as <c>.cmd</c> on Windows; several forks also keep
    /// <c>bin/code</c>, so that is the fallback.
    /// </summary>
    private static string? FindCli(string appRoot, string? install, string cliName)
    {
        var ext = OperatingSystem.IsWindows() ? ".cmd" : string.Empty;
        var dirs = new List<string> { Path.Combine(appRoot, "bin") };
        if (install is not null) dirs.Add(Path.Combine(install, "bin"));
        foreach (var name in new[] { cliName, "code" })
        {
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>The install location for an app root: the .app bundle on macOS, the folder above resources/app elsewhere.</summary>
    internal static string? InstallFromAppRoot(string appRoot)
    {
        var root = appRoot.TrimEnd('/', '\\');
        const string mac = "/Contents/Resources/app";
        if (root.EndsWith(mac, StringComparison.Ordinal)) return root[..^mac.Length];
        var resources = Path.GetDirectoryName(root);
        if (resources is not null && string.Equals(Path.GetFileName(root), "app", StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(Path.GetFileName(resources), "resources", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(resources);
        return null;
    }

    private static async Task<bool> WaitFrontAsync(EditorApp app, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval).ConfigureAwait(false);
            if (IsFrontmost(app))
            {
                await Task.Delay(FocusSettle).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    // ---- Windows foreground window ----

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static bool WindowsForegroundIs(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        using var p = Process.GetProcessById((int)pid);
        return string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
    }
}

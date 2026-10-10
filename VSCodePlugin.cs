using LoupixDeck.Plugin.VSCode.Bridge;
using LoupixDeck.Plugin.VSCode.Catalog;
using LoupixDeck.Plugin.VSCode.Commands;
using LoupixDeck.Plugin.VSCode.Platform;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode;

/// <summary>
/// LoupixDeck plugin entry point. Keys run VS Code commands by id through the LoupixDeck Companion
/// extension, which every VS Code window runs; live keys and folders show what the window last
/// pushed. With no companion window connected, keys that have a default shortcut press it in the
/// editor instead.
/// <para>
/// The bridge hub starts with the plugin and keeps running, rather than waiting behind a demand
/// gate like Slack's pollers: it costs one directory watcher, a directory listing every 3 s and an
/// idle socket per window, nothing polls the editor, and an ordinary key press needs the window
/// connected at that moment, not a scan later.
/// </para>
/// </summary>
public sealed class VSCodePlugin : LoupixPlugin, IPluginSettingsPage, IPluginRequirements
{
    private const string BringToFrontKey = "bring_to_front";
    private const string AppKey = "app";
    private const string CompanionId = "web-engineer.loupixdeck-companion";
    private const string CompanionFile = "loupixdeck-companion.vsix";

    private const string SupportUrl = "https://web-engineer.co.uk/contact";
    private const string IssuesUrl = "https://github.com/web-engineer/LoupixDeck.Plugin.VSCode/issues";

    /// <summary>How long the offline text ("No companion", "Not running") is trusted before it is worked out again.</summary>
    private static readonly TimeSpan OfflineTextTtl = TimeSpan.FromSeconds(10);

    private readonly Lazy<IReadOnlyList<IPluginCommand>> _commands;
    private readonly object _offlineLock = new();
    private string _offlineText = "No companion";
    private DateTime _offlineCheckedAt = DateTime.MinValue;
    private bool _offlineChecking;
    private IReadOnlyList<LiveCommandBase> _live = [];

    public VSCodePlugin()
    {
        _commands = new Lazy<IReadOnlyList<IPluginCommand>>(BuildCommands, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "vscode",
        Name = "Visual Studio Code",
        Version = new Version(0, 1, 0),
        SdkVersion = SdkInfo.Version,
        Author = "web-engineer",
        Description = "Visual Studio Code from the deck: commands by ID through a companion extension, live problems, git, debug and task keys, and folders of tasks, launch configurations, problems and open editors.",
        Icon = LoadEmbeddedIcon("LoupixDeck.Plugin.VSCode.icon.png")
    };

    /// <summary>Test hook: builds the hub, so tests can point it at a temporary registration directory.</summary>
    internal Func<IPluginLogger, BridgeHub>? HubFactory { get; set; }

    // ---- state the commands read ----

    internal IPluginHost Host { get; private set; } = null!;
    internal BridgeHub Hub { get; private set; } = null!;
    internal KeySender Keys { get; private set; } = null!;

    internal bool BringToFront => Host.Settings.Get(BringToFrontKey, true);

    /// <summary>The <c>app</c> setting: which editor fallback shortcuts go to (<c>auto</c> by default).</summary>
    internal string AppSetting => Host.Settings.Get(AppKey, "auto") ?? "auto";

    // ---- lifecycle ----

    public override void Initialize(IPluginHost host)
    {
        Host = host;
        Keys = new KeySender(() => BringToFront);
        Hub = HubFactory?.Invoke(host.Logger) ?? new BridgeHub(host.Logger);
        Hub.Changed += OnHubChanged;
        Hub.Start();

        var apps = EditorApps.Detect();
        host.Logger.Info((apps.Count > 0 ? "Editors found: " + string.Join(", ", apps.Select(a => a.Name)) : "No VS Code-family editor found") +
                         $"; watching {Hub.Directory} for companion windows");
    }

    public override void Shutdown()
    {
        if (Hub is null) return;
        Hub.Changed -= OnHubChanged;
        Hub.Dispose();
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands.Value;

    private IReadOnlyList<IPluginCommand> BuildCommands()
    {
        LiveCommandBase[] live =
        [
            new ProblemsCommand(this),
            new GitCommand(this),
            new DebugStateCommand(this),
            new UnsavedCommand(this),
            new ActiveFileCommand(this),
            new TaskStatusCommand(this),
            new TerminalStatusCommand(this),
            new ScreenReaderCommand(this),
            new WindowCommand(this)
        ];
        _live = live;

        return
        [
            new OpenCommand(this),
            new OpenFolderCommand(this),
            new WindowsFolderCommand(this),
            .. live,
            .. ListFolderCommand.All(this),
            new RunCommand(this),
            new RunTaskCommand(this),
            new DebugConfigCommand(this),
            .. ActionCommand.All(this),
            .. DialCommand.All(this)
        ];
    }

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        Group(CommandCatalog.Main, "VS Code: open the editor and folders, pick a window, live problems, git, debugger, task and file keys, folders of tasks and problems, and any command by ID",
            "\U000F0C8C"), // mdi-application-brackets-outline
        Group(CommandCatalog.Files, "VS Code: open, save, close and reveal files and editors", "\U000F1518"), // mdi-file-document-multiple-outline
        Group(CommandCatalog.Navigate, "VS Code: Command Palette, go to file, line, symbol and definition, and move back and forward", "\U000F018C"), // mdi-compass-outline
        Group(CommandCatalog.Edit, "VS Code: undo, format, rename, comment, move lines and other edits", "\U000F0CB6"), // mdi-pencil-outline
        Group(CommandCatalog.Search, "VS Code: find and replace in the file and across the workspace", "\U000F0349"), // mdi-magnify
        Group(CommandCatalog.View, "VS Code: side bar, panel, splits, Zen mode, word wrap and other layout", "\U000F0A1D"), // mdi-view-dashboard-outline
        Group(CommandCatalog.Terminal, "VS Code: show, create, split and clear the integrated terminal", "\U000F018D"), // mdi-console
        Group(CommandCatalog.SourceControl, "VS Code: commit, stage, sync, pull, push and browse changes", "\U000F062C"), // mdi-source-branch
        Group(CommandCatalog.Debug, "VS Code: start, step, pause and stop the debugger, breakpoints and launch configurations", "\U000F0A30"), // mdi-bug-outline
        Group(CommandCatalog.RunTest, "VS Code: run tasks and tests", "\U000F0C5A"), // mdi-clipboard-play-outline
        Group(CommandCatalog.AI, "VS Code: Copilot Chat, inline chat, agent mode and Claude Code", "\U000F167A"), // mdi-robot-outline
        Group(CommandCatalog.Accessibility, "VS Code: screen reader mode, accessible view, signals, help and focus moves", "\U000F05A4"), // mdi-wheelchair-accessibility
        Group(CommandCatalog.Dials, "VS Code: dials for zoom, tabs, problems, changes, scrolling, history, terminals and debugging", "\U000F1B96") // mdi-knob
    ];

    private static CommandGroupDescriptor Group(string name, string description, string icon) =>
        new() { Group = name, Description = description, Icon = icon };

    /// <summary>Repaints the keys a hub change concerns: live keys whose sections changed (or all of them when the target window changed).</summary>
    private void OnHubChanged(HubChange change)
    {
        foreach (var key in _live)
        {
            if (key.Sections.Any(change.Affects) || change.WindowsChanged && key is WindowCommand)
                Host.RequestButtonRefresh(key.Name);
        }

        if (change.Affects("problems"))
            Host.RequestButtonRefresh("VSCode." + DialCommand.ProblemsDialId);
        if (change.WindowsChanged || change.TargetChanged)
            InvalidateOfflineText();
    }

    // ---- offline text for live keys ----

    /// <summary>
    /// What live keys say when no window is connected: "No companion" when an editor is running
    /// (so the companion is missing or off), "Not running" when one is installed but closed, "No
    /// VS Code" when none is installed. Asking whether an editor runs starts a process, so the
    /// answer is cached and refreshed in the background; render paths never wait for it.
    /// </summary>
    internal string OfflineText()
    {
        lock (_offlineLock)
        {
            if (!_offlineChecking && DateTime.UtcNow - _offlineCheckedAt > OfflineTextTtl)
            {
                _offlineChecking = true;
                _ = Task.Run(RefreshOfflineText);
            }

            return Hub.NeedsPluginUpdate ? "Update plugin" : _offlineText;
        }
    }

    private void InvalidateOfflineText()
    {
        lock (_offlineLock) _offlineCheckedAt = DateTime.MinValue;
    }

    private void RefreshOfflineText()
    {
        string text;
        try
        {
            var app = EditorApps.Resolve(AppSetting, null);
            text = app is null ? "No VS Code" : EditorApps.IsRunning(app) ? "No companion" : "Not running";
        }
        catch (Exception ex)
        {
            Host.Logger.Error("VS Code: checking for a running editor failed", ex);
            text = "No companion";
        }

        bool changed;
        lock (_offlineLock)
        {
            changed = text != _offlineText;
            _offlineText = text;
            _offlineCheckedAt = DateTime.UtcNow;
            _offlineChecking = false;
        }

        if (changed && Hub.Target is null)
        {
            foreach (var key in _live) Host.RequestButtonRefresh(key.Name);
        }
    }

    // ---- IPluginRequirements ----

    public IReadOnlyList<PluginRequirement> GetRequirements()
    {
        var apps = SafeDetect();
        var installedIn = apps.Where(CompanionInstalled).Select(a => a.Name).ToList();
        var connected = Hub?.AnyConnected ?? false;
        return
        [
            new PluginRequirement
            {
                Id = "editor",
                Name = "Visual Studio Code or a compatible editor",
                IsMet = apps.Count > 0,
                Message = "No VS Code, VS Code Insiders, VSCodium, Cursor or Windsurf installation was found.",
                InstallHint = "Install Visual Studio Code from https://code.visualstudio.com."
            },
            new PluginRequirement
            {
                Id = "companion",
                Name = "LoupixDeck Companion extension",
                IsMet = connected || installedIn.Count > 0,
                Message = "The companion extension is not installed, so only keys with a default shortcut work, and live keys and folders stay empty.",
                InstallHint = "Open this plugin's settings and press \"Install companion in …\" for your editor."
            }
        ];
    }

    private static IReadOnlyList<EditorApp> SafeDetect()
    {
        try
        {
            return EditorApps.Detect();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The extensions folder each editor installs into, relative to the home folder.</summary>
    private static string? ExtensionsFolder(EditorApp app) => app.Key switch
    {
        "code" => ".vscode",
        "insiders" => ".vscode-insiders",
        "codium" => ".vscode-oss",
        "cursor" => ".cursor",
        "windsurf" => ".windsurf",
        _ => null
    };

    /// <summary>True when the companion sits in the editor's extensions folder. A quick directory check, no CLI.</summary>
    private static bool CompanionInstalled(EditorApp app)
    {
        try
        {
            if (ExtensionsFolder(app) is not { } folder) return false;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder, "extensions");
            return Directory.Exists(dir) && Directory.EnumerateDirectories(dir, CompanionId + "-*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- IPluginSettingsPage ----
    //
    // The host rebuilds the page from SettingsSchema and SettingsActions after every action, so the
    // companion card and the install buttons follow what was installed and connected.

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema =>
    [
        new PluginSettingDescriptor
        {
            Key = "companion",
            Label = "Companion extension",
            Kind = PluginSettingKind.Heading,
            Description = CompanionStatus()
        },
        new PluginSettingDescriptor
        {
            Key = "keys",
            Label = "Keys",
            Kind = PluginSettingKind.Heading,
            Description = "With the companion, keys run VS Code commands by ID, so they work whatever your keyboard layout and keybindings. " +
                          "Without it, keys that have a default shortcut press it in the editor; on macOS that needs the Accessibility permission LoupixDeck already asks for."
        },
        new PluginSettingDescriptor
        {
            Key = BringToFrontKey,
            Label = "Bring the editor to the front first",
            Kind = PluginSettingKind.Toggle,
            DefaultValue = true,
            Description = "Before a key runs its command, raise the window it acts on. Off: commands still run in the window used last, but shortcuts go to whichever app has focus."
        },
        new PluginSettingDescriptor
        {
            Key = AppKey,
            Label = "Editor for shortcuts",
            Kind = PluginSettingKind.Text,
            DefaultValue = "auto",
            Description = AppDescription()
        },
        new PluginSettingDescriptor
        {
            Key = "about",
            Label = "About and support",
            Kind = PluginSettingKind.Heading,
            Description = "Made by web-engineer. Not affiliated with or endorsed by Microsoft. Visual Studio Code is a trademark of Microsoft Corporation."
        }
    ];

    private string CompanionStatus()
    {
        var windows = Hub?.Windows ?? [];
        var apps = SafeDetect();
        var lines = new List<string>();
        if (windows.Count > 0)
        {
            lines.Add(windows.Count == 1 ? "Connected to 1 window:" : $"Connected to {windows.Count} windows:");
            lines.AddRange(windows.Select(w => $"• {w.DisplayName} — {w.App} {w.Version}{(w.Remote is { Length: > 0 } r ? " (" + r + ")" : string.Empty)}, companion {w.ExtVersion}"));
        }
        else if (apps.Where(CompanionInstalled).Select(a => a.Name).ToList() is { Count: > 0 } installed)
        {
            lines.Add($"Installed in {string.Join(", ", installed)}, but no window is connected. Open a folder in the editor (or reload its window), then press \"Check connection\".");
        }
        else if (apps.Count > 0)
        {
            lines.Add("Not installed yet. Keys with a default shortcut work without it; live keys, folders and commands without a shortcut need it. " +
                      "Press \"Install companion in …\" below for your editor.");
        }
        else
        {
            lines.Add("No VS Code, VS Code Insiders, VSCodium, Cursor or Windsurf installation was found. Install an editor, then come back here to add the companion.");
        }

        if (Hub?.NeedsPluginUpdate == true)
            lines.Add("A window runs a newer companion than this plugin understands: update the Visual Studio Code plugin.");
        return string.Join("\n", lines);
    }

    private static string AppDescription()
    {
        var apps = SafeDetect();
        var found = apps.Count > 0 ? string.Join(", ", apps.Select(a => $"{a.Key} ({a.Name})")) : "none";
        return "Which editor gets the shortcuts when no companion window is connected: auto (the one running, else the first installed), " +
               $"code, insiders, codium, cursor or windsurf. Found: {found}.";
    }

    public IReadOnlyList<PluginSettingAction> SettingsActions
    {
        get
        {
            var actions = new List<PluginSettingAction>();
            // The settings page is where a user looks after installing an editor: look again now.
            EditorApps.Refresh();
            foreach (var app in SafeDetect())
                actions.Add(new PluginSettingAction { Label = $"Install companion in {app.Name}", Invoke = () => InstallCompanionAsync(app) });
            actions.Add(new PluginSettingAction { Label = "Check connection", Invoke = CheckConnectionAsync });
            actions.Add(new PluginSettingAction { Label = "Get support", Invoke = () => OpenLink(SupportUrl) });
            actions.Add(new PluginSettingAction { Label = "Report a bug or request a feature", Invoke = () => OpenLink(IssuesUrl) });
            return actions;
        }
    }

    public void OnSettingsSaved()
    {
        EditorApps.Refresh();
        Keys.Invalidate();
        InvalidateOfflineText();
        foreach (var key in _live) Host.RequestButtonRefresh(key.Name);
    }

    private Task<string> OpenLink(string url) =>
        Task.FromResult(Host.OpenBrowser(url) ? $"Opened {url}" : $"Could not open a browser; visit {url}");

    /// <summary>The packed companion shipped next to the plugin assembly.</summary>
    internal static string CompanionPackage() =>
        Path.Combine(Path.GetDirectoryName(typeof(VSCodePlugin).Assembly.Location) ?? ".", CompanionFile);

    private async Task<string> InstallCompanionAsync(EditorApp app)
    {
        var vsix = CompanionPackage();
        if (!File.Exists(vsix)) return $"The companion package is missing from the plugin folder ({vsix}). Reinstall the plugin.";
        if (string.IsNullOrEmpty(app.CliPath)) return $"{app.Name} has no command-line launcher, so the companion cannot be installed from here. In the editor, run Extensions: Install from VSIX… and pick {vsix}.";

        var update = CompanionInstalled(app);
        var (exitCode, output) = await EditorApps.RunCliAsync(app, "--install-extension", vsix, "--force").ConfigureAwait(false);
        var tail = Tail(output);
        if (exitCode != 0)
        {
            Host.Logger.Warn($"VS Code: installing the companion in {app.Name} failed ({exitCode}): {output}");
            return $"Could not install the companion in {app.Name}." + (tail.Length > 0 ? "\n" + tail : string.Empty);
        }

        Host.Logger.Info($"VS Code: installed the companion in {app.Name}");
        _ = Hub.ScanAsync();
        return update
            ? $"Updated the companion in {app.Name}. Reload each open window (Developer: Reload Window) to start the new version."
            : $"Installed the companion in {app.Name}. Open windows connect within a few seconds; if one does not, reload it (Developer: Reload Window).";
    }

    private async Task<string> CheckConnectionAsync()
    {
        EditorApps.Refresh();
        Hub.Start();
        await Hub.ScanAsync().ConfigureAwait(false);
        var windows = Hub.Windows;
        if (windows.Count == 0)
        {
            var why = Hub.NeedsPluginUpdate ? " A window needs a newer plugin." : Hub.LastError is { } error ? $" Last problem: {error}." : string.Empty;
            return "No VS Code window with the companion is connected. Check the companion is installed and enabled, and that a window is open." + why;
        }

        var lines = await Task.WhenAll(windows.Select(async w =>
        {
            var ping = await w.PingAsync().ConfigureAwait(false);
            return $"{w.DisplayName} ({w.App} {w.Version}): " + (ping.Ok ? "connected" : $"not answering ({ping})");
        })).ConfigureAwait(false);
        var target = Hub.Target is { } t ? $"\nKeys act on {t.DisplayName}." : string.Empty;
        return string.Join("\n", lines) + target;
    }

    /// <summary>The last few lines of the CLI's output, for the settings page.</summary>
    private static string Tail(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = string.Join("\n", lines.TakeLast(4));
        return tail.Length <= 600 ? tail : tail[^600..];
    }

    private static byte[]? LoadEmbeddedIcon(string name)
    {
        try
        {
            using var stream = typeof(VSCodePlugin).Assembly.GetManifestResourceStream(name);
            if (stream is null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}

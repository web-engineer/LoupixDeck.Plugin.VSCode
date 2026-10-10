using LoupixDeck.Plugin.VSCode.Bridge;
using LoupixDeck.Plugin.VSCode.Catalog;
using LoupixDeck.Plugin.VSCode.Folders;
using LoupixDeck.Plugin.VSCode.Platform;
using LoupixDeck.Plugin.VSCode.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Commands;

/// <summary>
/// A key that opens a folder of live items from the target window, one item per key: its tasks,
/// launch configurations, problems, open editors, recent folders or terminals. Pressing an item
/// acts on it in the window it came from. Needs the companion.
/// </summary>
internal sealed class ListFolderCommand : CodeCommandBase
{
    /// <summary>The problems list is long; the first ones (errors first) are the ones worth a key.</summary>
    private const int MaxProblems = 40;

    private readonly string _what;
    private readonly string _title;
    private readonly string _empty;

    private ListFolderCommand(VSCodePlugin plugin, string id, string what, string displayName, string title, string caption,
        string glyph, string description, string empty) : base(plugin)
    {
        _what = what;
        _title = title;
        _empty = empty;
        Descriptor = new CommandDescriptor
        {
            CommandName = "VSCode." + id,
            DisplayName = displayName,
            Group = CommandCatalog.Main,
            Icon = glyph,
            Description = description,
            ButtonLayout = CodeKey.IconAndCaption(glyph, caption)
        };
    }

    public override CommandDescriptor Descriptor { get; }

    public override ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public static IEnumerable<ListFolderCommand> All(VSCodePlugin plugin) =>
    [
        new(plugin, "Tasks", "tasks", "Tasks folder", "Tasks", "Tasks", "\U000F0C5A", // mdi-clipboard-play-outline
            "Open a folder with a key for every task in the VS Code window; press one to run it (needs the companion).", "No tasks"),
        new(plugin, "DebugConfigs", "launch", "Launch configurations folder", "Launch", "Launch", "\U000F1AF8", // mdi-bug-play-outline
            "Open a folder with a key for every launch configuration and compound; press one to start debugging (needs the companion).", "No launch configurations"),
        new(plugin, "ProblemList", "problems", "Problems folder", "Problems", "Problems", "\U000F0CE6", // mdi-alert-octagon-outline
            "Open a folder with the first 40 problems, errors first, as file:line and message; press one to go there (needs the companion).", "No problems"),
        new(plugin, "OpenEditors", "editors", "Open editors folder", "Editors", "Editors", "\U000F1518", // mdi-file-document-multiple-outline
            "Open a folder with a key for every open editor tab, ● marking unsaved ones; press one to switch to it (needs the companion).", "No open editors"),
        new(plugin, "Recent", "recent", "Recent folders", "Recent", "Recent", "\U000F02DA", // mdi-history
            "Open a folder of recently opened folders and workspaces; press one to open it in the current window (needs the companion).", "Nothing recent"),
        new(plugin, "TerminalList", "terminals", "Terminals folder", "Terminals", "Terminals", "\U000F018D", // mdi-console
            "Open a folder with a key for every integrated terminal and its last command; press one to show it (needs the companion).", "No terminals")
    ];

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, _title + " folder", () =>
    {
        if (Plugin.Hub.Target is null)
        {
            Hint(ctx, OfflineHint());
            return Task.CompletedTask;
        }

        var list = new FolderList(Plugin.Hub, LoadAsync, c => c.AffectsList(_what), ctx.Host.Logger);
        ctx.Host.OpenFolder(new ListFolder(_title, list, ctx.Host, _empty));
        return Task.CompletedTask;
    });

    /// <summary>Reads the list from the target window; each item acts on that same window, since refs are per window.</summary>
    private async Task<IReadOnlyList<FolderItem>> LoadAsync(BridgeHub hub)
    {
        if (hub.Target is not { } window) return [];
        var items = await window.ListAsync(_what).ConfigureAwait(false);
        if (_what == "problems") items = items.Take(MaxProblems).ToList();
        return items.Select(i => ToFolderItem(window, i)).ToList();
    }

    private FolderItem ToFolderItem(BridgeWindow window, BridgeItem item)
    {
        Func<Task<string?>> press = async () =>
        {
            var focus = Plugin.BringToFront && window.HasFocusWindow;
            if (Plugin.BringToFront && !window.HasFocusWindow && EditorApps.ForWindow(window) is { } app)
                await EditorApps.ActivateAsync(Plugin.Host, app).ConfigureAwait(false);
            var result = await window.RunAsync(_what, item.Ref, focus).ConfigureAwait(false);
            return result.Ok ? null : result.ErrorCode switch
            {
                BridgeErrors.NotFound => "Gone",
                BridgeErrors.Offline or BridgeErrors.Disconnected => "Needs companion",
                _ => "Failed"
            };
        };

        return _what switch
        {
            "problems" => new FolderItem(ItemText(item.Label, item.Detail, 22, 30), item.Severity switch
            {
                "error" => CodeKey.Error,
                "warning" => CodeKey.Warning,
                _ => CodeKey.Blue
            }, press),
            "editors" => new FolderItem(ItemText((item.Dirty ? "● " : string.Empty) + item.Label, item.Detail),
                item.Active ? CodeKey.BrightBlue : CodeKey.Blue, press, item.Active),
            "terminals" => new FolderItem(ItemText(item.Label, item.Detail), item.Active ? CodeKey.BrightBlue : CodeKey.Blue, press, item.Active),
            "launch" => new FolderItem(ItemText(item.Label, item.Kind == "compound" ? Plugin.Host.Tr("compound") : item.Detail), CodeKey.Blue, press),
            // The detail of a recent entry is its full path: too long for a key, the name says enough.
            "recent" => new FolderItem(ItemText(item.Label, item.Kind == "workspace" ? Plugin.Host.Tr("workspace") : null), CodeKey.Blue, press),
            _ => new FolderItem(ItemText(item.Label, item.Detail), CodeKey.Blue, press)
        };
    }

    /// <summary>The label (wrapping over up to three lines) and a one-line detail under it, both cut to fit a key.</summary>
    internal static string ItemText(string label, string? detail, int labelMax = 30, int detailMax = CodeKey.LineChars + 3)
    {
        var text = CodeKey.Fit(label, labelMax);
        return string.IsNullOrWhiteSpace(detail) ? text : text + "\n" + CodeKey.Fit(detail, detailMax);
    }
}

/// <summary>
/// Opens a folder with a key for every connected VS Code window (workspace name and editor), the
/// one keys act on highlighted. Pressing one brings that window to the front, which also makes it
/// the window the other keys act on.
/// </summary>
internal sealed class WindowsFolderCommand(VSCodePlugin plugin) : CodeCommandBase(plugin)
{
    public const string Name = "VSCode.Windows";
    private const string Glyph = "\U000F037A"; // mdi-monitor-multiple

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Windows folder",
        Group = CommandCatalog.Main,
        Icon = Glyph,
        Description = "Open a folder with a key for every VS Code window running the companion; press one to bring it to the front.",
        ButtonLayout = CodeKey.IconAndCaption(Glyph, "Windows")
    };

    public override ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "windows folder", () =>
    {
        if (!Open(Plugin, ctx.Host)) Hint(ctx, OfflineHint());
        return Task.CompletedTask;
    });

    /// <summary>Opens the folder; false when no window is connected.</summary>
    public static bool Open(VSCodePlugin plugin, IPluginHost host)
    {
        if (!plugin.Hub.AnyConnected) return false;
        var list = new FolderList(plugin.Hub, hub => Task.FromResult(Items(plugin, hub)),
            c => c.WindowsChanged || c.TargetChanged || c.Sections.Contains("workspace"), host.Logger);
        host.OpenFolder(new ListFolder("Windows", list, host, "No windows"));
        return true;
    }

    private static IReadOnlyList<FolderItem> Items(VSCodePlugin plugin, BridgeHub hub)
    {
        var target = hub.Target;
        return hub.Windows.Select(w =>
        {
            var isTarget = ReferenceEquals(w, target);
            var name = w.WorkspaceName is { Length: > 0 } ws ? ws : plugin.Host.Tr("No folder");
            var detail = AppName(w.App) + (w.Remote is { Length: > 0 } remote ? " · " + remote : string.Empty);
            return new FolderItem(ListFolderCommand.ItemText(name, detail), isTarget ? CodeKey.BrightBlue : CodeKey.Blue,
                () => FocusAsync(plugin, w), isTarget);
        }).ToList();
    }

    private static async Task<string?> FocusAsync(VSCodePlugin plugin, BridgeWindow window)
    {
        if (!window.IsConnected) return "Gone";
        if (window.HasFocusWindow && (await window.FocusAsync().ConfigureAwait(false)).Ok) return null;
        // An editor before VS Code 1.128 cannot raise one window; bringing its app forward is the best there is.
        if (EditorApps.ForWindow(window) is { } app && await EditorApps.ActivateAsync(plugin.Host, app).ConfigureAwait(false)) return null;
        return "Failed";
    }

    /// <summary>A short name for the editor, to fit a key: "VS Code", "Insiders", "Cursor"…</summary>
    internal static string AppName(string? app) => app switch
    {
        null or "" => "VS Code",
        "Visual Studio Code" or "Code" => "VS Code",
        _ when app.Contains("Insiders", StringComparison.OrdinalIgnoreCase) => "Insiders",
        _ when app.StartsWith("Visual Studio Code", StringComparison.OrdinalIgnoreCase) => "VS Code",
        _ => CodeKey.Fit(app, CodeKey.LineChars + 3)
    };
}

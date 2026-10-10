using System.Text.Json.Nodes;
using LoupixDeck.Plugin.VSCode.Bridge;
using LoupixDeck.Plugin.VSCode.Catalog;
using LoupixDeck.Plugin.VSCode.Platform;
using LoupixDeck.Plugin.VSCode.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Commands;

/// <summary>Brings the editor to the front, starting it when it is not running.</summary>
internal sealed class OpenCommand(VSCodePlugin plugin) : CodeCommandBase(plugin)
{
    public const string Name = "VSCode.Open";
    private const string Glyph = "\U000F0C8C"; // mdi-application-brackets-outline

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Open VS Code",
        Group = CommandCatalog.Main,
        Icon = Glyph,
        Description = "Bring the VS Code window you used last to the front, starting the editor if needed (the app setting picks which editor).",
        ButtonLayout = CodeKey.IconAndCaption(Glyph, "VS Code")
    };

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "open", async () =>
    {
        if (Plugin.Hub.Target is { } target)
        {
            if (target.HasFocusWindow && (await target.FocusAsync().ConfigureAwait(false)).Ok) return;
            if (EditorApps.ForWindow(target) is { } own && await EditorApps.ActivateAsync(ctx.Host, own).ConfigureAwait(false)) return;
        }

        var app = EditorApps.Resolve(Plugin.AppSetting, Plugin.Hub.Target);
        if (app is null || !await EditorApps.ActivateAsync(ctx.Host, app).ConfigureAwait(false)) Hint(ctx, "No VS Code");
    });
}

/// <summary>
/// Opens a folder or a <c>.code-workspace</c> file. Through the companion it always asks for a new
/// window, so a stray press never swaps out the workspace you are in (VS Code focuses the window
/// that already has the folder open instead of opening it twice). Without the companion it hands
/// the path to the editor's command-line launcher, which does the same.
/// </summary>
internal sealed class OpenFolderCommand(VSCodePlugin plugin) : CodeCommandBase(plugin)
{
    public const string Name = "VSCode.OpenFolder";
    private const string Glyph = "\U000F0DCF"; // mdi-folder-open-outline

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Open folder or workspace",
        Group = CommandCatalog.Main,
        Icon = Glyph,
        Description = "Open a folder or .code-workspace file in a new VS Code window, or switch to the window that already has it. ~ stands for your home folder.",
        ParameterTemplate = "({Path})",
        Parameters = [new CommandParameter("Path", typeof(string))],
        ButtonLayout = CodeKey.IconAndCaption(Glyph, "Folder")
    };

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "open folder", async () =>
    {
        if (ExpandPath(JoinedParameter(ctx)) is not { } path)
        {
            Hint(ctx, "Set folder");
            return;
        }

        if (!Directory.Exists(path) && !File.Exists(path))
        {
            Hint(ctx, "Not found");
            ctx.Host.Logger.Warn($"VSCode.OpenFolder: {path} does not exist");
            return;
        }

        if (Plugin.Hub.Target is { } target)
        {
            var focus = await PrepareFocusAsync(ctx, target).ConfigureAwait(false);
            var args = new JsonArray(Args.Uri(path), new JsonObject { ["forceNewWindow"] = true });
            var result = await target.ExecAsync("vscode.openFolder", args, focus).ConfigureAwait(false);
            if (result.Ok) return;
            // The companion's command policy said no: respect it rather than opening the folder another way.
            if (result.ErrorCode is BridgeErrors.Denied)
            {
                Hint(ctx, "Not allowed");
                return;
            }

            if (result.ErrorCode is not (BridgeErrors.Offline or BridgeErrors.Disconnected))
            {
                ctx.Host.Logger.Warn($"VSCode.OpenFolder: {result}; trying the command-line launcher");
            }
        }

        var app = EditorApps.Resolve(Plugin.AppSetting, Plugin.Hub.Target);
        if (app is null || !EditorApps.LaunchFolder(app, path)) Hint(ctx, "No VS Code");
    });

    /// <summary>The path with a leading <c>~</c> expanded and made absolute; null when blank.</summary>
    internal static string? ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"');
        if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 2 ? p[2..] : string.Empty);
        return Path.GetFullPath(p);
    }
}

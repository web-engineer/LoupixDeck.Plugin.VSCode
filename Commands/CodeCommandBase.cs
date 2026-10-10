using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.VSCode.Bridge;
using LoupixDeck.Plugin.VSCode.Catalog;
using LoupixDeck.Plugin.VSCode.Platform;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Commands;

/// <summary>
/// Shared plumbing: plugin access, parameter helpers, error guarding, on-key hints, and the one
/// rule every key follows: run the VS Code command through the companion in the target window,
/// and only when no window is connected fall back to the editor's default shortcut.
/// </summary>
internal abstract class CodeCommandBase(VSCodePlugin plugin) : IPluginCommand
{
    protected VSCodePlugin Plugin { get; } = plugin;

    public abstract CommandDescriptor Descriptor { get; }

    public virtual ButtonTargets SupportedTargets => ButtonTargets.All;

    public abstract Task Execute(CommandContext ctx);

    protected static string? Parameter(CommandContext ctx, int index) =>
        ctx.Parameters.Length > index && !string.IsNullOrWhiteSpace(ctx.Parameters[index]) ? ctx.Parameters[index].Trim() : null;

    /// <summary>
    /// The whole parameter text of a one-parameter command. The host splits parameters on commas
    /// with no escaping, so a task label, launch name or path that contains one arrives in pieces;
    /// this joins them back. Null when blank.
    /// </summary>
    internal static string? JoinedParameter(CommandContext ctx) =>
        string.Join(",", ctx.Parameters) is { } joined && !string.IsNullOrWhiteSpace(joined) ? joined.Trim() : null;

    /// <summary>Runs <paramref name="action"/> and routes any exception to the host logger instead of the caller.</summary>
    protected static async Task GuardAsync(CommandContext ctx, string what, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error($"VS Code {what} failed", ex);
        }
    }

    /// <summary>Short on-key feedback for a touch button; silent for other targets.</summary>
    protected static void Hint(CommandContext ctx, string text)
    {
        if (ctx.Target == ButtonTargets.TouchButton && ctx.SourceIndex is { } slot)
            ctx.Host.OverlayTouchText(slot, ctx.Host.Tr(text), TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Switches every key bound to <paramref name="command"/> to <paramref name="state"/> when the
    /// key being rendered shows another one, so a key that was reset is put right on its next poll.
    /// </summary>
    protected static void Show(CommandContext ctx, string command, string state)
    {
        if (!string.Equals(ctx.StateName, state, StringComparison.OrdinalIgnoreCase))
            ctx.Host.SetActiveButtonState(command, state);
    }

    /// <summary>A catalogue action by id, for live keys that run one of them when tapped.</summary>
    protected static CodeAction CatalogAction(string id) => CommandCatalog.All.Single(a => a.Id == id);

    /// <summary>
    /// Runs a catalogue action: through the companion when a window is connected, else with its
    /// fallback shortcut. Hints on the key when neither works.
    /// </summary>
    protected Task<bool> RunActionAsync(CommandContext ctx, CodeAction action) =>
        ExecOrShortcutAsync(ctx, action.Command, action.ArgsJson, action.ForThisOs, action.RequiresExtension);

    /// <summary>
    /// Runs <paramref name="command"/> <paramref name="repeat"/> times in the target window, one
    /// request after another so the editor sees them in order. With no window connected (or one
    /// that drops part way), the rest go out as <paramref name="shortcut"/> through the host's key
    /// macros instead. Returns false, after a hint, when nothing could run.
    /// </summary>
    protected async Task<bool> ExecOrShortcutAsync(CommandContext ctx, string command, string? argsJson, string? shortcut,
        string? requiresExtension = null, int repeat = 1)
    {
        var left = Math.Max(1, repeat);
        if (Plugin.Hub.Target is { } target)
        {
            var focus = await PrepareFocusAsync(ctx, target).ConfigureAwait(false);
            while (left > 0)
            {
                var result = await target.ExecAsync(command, ParseArgs(argsJson), focus).ConfigureAwait(false);
                // Raising the window once is enough for a burst of dial ticks.
                focus = false;
                if (result.Ok)
                {
                    left--;
                    continue;
                }

                if (result.ErrorCode is BridgeErrors.Offline or BridgeErrors.Disconnected) break;
                ReportFailure(ctx, command, result, requiresExtension);
                return false;
            }

            if (left == 0) return true;
        }

        return await ShortcutAsync(ctx, shortcut, left).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request to the target window (a task, a launch configuration, a list item…) for
    /// commands that have no shortcut to fall back on. Hints "Needs companion" when no window is
    /// connected and the reason when the window says no.
    /// </summary>
    protected async Task<bool> RequestAsync(CommandContext ctx, string what, Func<BridgeWindow, bool, Task<BridgeResult>> request)
    {
        if (Plugin.Hub.Target is not { } target)
        {
            Hint(ctx, OfflineHint());
            return false;
        }

        var focus = await PrepareFocusAsync(ctx, target).ConfigureAwait(false);
        var result = await request(target, focus).ConfigureAwait(false);
        if (result.Ok) return true;
        if (result.ErrorCode is BridgeErrors.Offline or BridgeErrors.Disconnected) Hint(ctx, OfflineHint());
        else ReportFailure(ctx, what, result, null);
        return false;
    }

    /// <summary>
    /// What <c>focus</c> to send with a request. With "bring to the front" on, a window that has
    /// <c>workbench.action.focusWindow</c> raises itself; an older one cannot, so its app is
    /// activated from here first (the window that was last in front comes with it).
    /// </summary>
    protected async Task<bool> PrepareFocusAsync(CommandContext ctx, BridgeWindow target)
    {
        if (!Plugin.BringToFront) return false;
        if (target.HasFocusWindow) return true;
        if (EditorApps.ForWindow(target) is { } app) await EditorApps.ActivateAsync(ctx.Host, app).ConfigureAwait(false);
        return false;
    }

    /// <summary>The fallback: the editor's default shortcut, sent to the app the <c>app</c> setting picks.</summary>
    private async Task<bool> ShortcutAsync(CommandContext ctx, string? shortcut, int repeat)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            Hint(ctx, Plugin.Hub.NeedsPluginUpdate ? "Update plugin" : "Needs companion");
            return false;
        }

        var app = EditorApps.Resolve(Plugin.AppSetting, null);
        if (app is null)
        {
            Hint(ctx, "No VS Code");
            return false;
        }

        if (await Plugin.Keys.SendAsync(ctx.Host, app, shortcut, repeat).ConfigureAwait(false)) return true;
        // The editor could not be brought to the front (or started), so the keys were not sent.
        Hint(ctx, "No VS Code");
        return false;
    }

    /// <summary>The key hint for a request that needs a window when none is connected.</summary>
    protected string OfflineHint() => Plugin.Hub.NeedsPluginUpdate ? "Update plugin" : "Needs companion";

    private static void ReportFailure(CommandContext ctx, string what, BridgeResult result, string? requiresExtension)
    {
        switch (result.ErrorCode)
        {
            case BridgeErrors.UnknownCommand:
                Hint(ctx, requiresExtension is null ? "Needs extension" : "Needs " + ExtensionName(requiresExtension));
                break;
            case BridgeErrors.NotFound:
                Hint(ctx, "Not found");
                break;
            case BridgeErrors.Denied:
                // The user's own allow list in the companion settings said no.
                Hint(ctx, "Not allowed");
                break;
            case BridgeErrors.Busy:
                Hint(ctx, "Busy");
                break;
            case BridgeErrors.Timeout:
                Hint(ctx, "No reply");
                break;
            default:
                Hint(ctx, "Failed");
                break;
        }

        ctx.Host.Logger.Warn($"VS Code {what}: {result}");
    }

    /// <summary>A short name for an extension id, for the "Needs …" hint.</summary>
    internal static string ExtensionName(string extensionId) => extensionId switch
    {
        "anthropic.claude-code" => "Claude Code",
        "ms-vscode.vscode-speech" => "VS Code Speech",
        _ => extensionId.Contains('.') ? extensionId[(extensionId.IndexOf('.') + 1)..] : extensionId
    };

    /// <summary>A catalogue argument list (a JSON array) as a fresh node for one request; null for none or bad JSON.</summary>
    internal static JsonArray? ParseArgs(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            return JsonNode.Parse(argsJson) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

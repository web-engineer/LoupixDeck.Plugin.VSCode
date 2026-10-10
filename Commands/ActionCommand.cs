using System.Text.RegularExpressions;
using LoupixDeck.Plugin.VSCode.Catalog;
using LoupixDeck.Plugin.VSCode.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Commands;

/// <summary>
/// One key per catalogue action: run its VS Code command in the target window through the
/// companion, or press its default shortcut in the editor when no window is connected.
/// </summary>
internal sealed partial class ActionCommand(VSCodePlugin plugin, CodeAction action) : CodeCommandBase(plugin)
{
    public CodeAction Action { get; } = action;

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = action.CommandName,
        DisplayName = action.DisplayName,
        Group = action.Group,
        Icon = action.Glyph,
        Description = Describe(action),
        ButtonLayout = CodeKey.IconAndCaption(action.Glyph, action.Caption)
    };

    public static IEnumerable<ActionCommand> All(VSCodePlugin plugin) =>
        CommandCatalog.All.Select(a => new ActionCommand(plugin, a));

    /// <summary>
    /// The catalogue description with its closing "(… without the companion)." naming this OS's
    /// shortcut, so Windows and Linux users are not told to press Cmd; "(needs the companion)."
    /// when this OS has none.
    /// </summary>
    internal static string Describe(CodeAction action) => FallbackNote().Replace(action.Description,
        action.ForThisOs is { } keys ? $"({keys} without the companion)." : "(needs the companion).", 1);

    [GeneratedRegex(@"\(([^()]+) without the companion\)\.")]
    private static partial Regex FallbackNote();

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, Action.Id, () => RunActionAsync(ctx, Action));
}

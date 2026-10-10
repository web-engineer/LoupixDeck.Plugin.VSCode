using LoupixDeck.Plugin.VSCode.Catalog;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Commands;

/// <summary>
/// A dial from the catalogue: turn left for one command, right for another, press for a third.
/// Each tick is one command, run one after another so the editor sees them in order, and capped
/// so a fast spin cannot flood the editor. Without the companion each tick is the step's shortcut.
/// The problems dial also shows the error or warning count next to the dial.
/// </summary>
internal sealed class DialCommand(VSCodePlugin plugin, DialSpec spec) : CodeCommandBase(plugin), IAdjustmentCommand
{
    private const int MaxTicksPerTurn = 5;

    /// <summary>The dial that shows the problem count, refreshed when the problems change.</summary>
    public const string ProblemsDialId = "ProblemSteps";

    public DialSpec Spec { get; } = spec;

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = spec.CommandName,
        DisplayName = spec.DisplayName,
        Group = CommandCatalog.Dials,
        Icon = spec.Glyph,
        Description = spec.Description
    };

    public override ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;

    public static IEnumerable<DialCommand> All(VSCodePlugin plugin) =>
        DialCatalog.All.Select(d => new DialCommand(plugin, d));

    public Task ApplyAdjustment(CommandContext ctx, int ticks) => GuardAsync(ctx, Spec.Id, async () =>
    {
        if (ticks == 0) return;
        var step = ticks < 0 ? Spec.Left : Spec.Right;
        await ExecOrShortcutAsync(ctx, step.Command, step.ArgsJson, step.ForThisOs, null, Math.Min(Math.Abs(ticks), MaxTicksPerTurn)).ConfigureAwait(false);
    });

    public Task ApplyReset(CommandContext ctx) => Press(ctx);

    public override Task Execute(CommandContext ctx) => Press(ctx);

    /// <summary>Only the problems dial reports a value: "3 err", "5 warn" or "OK", read from the last state the window pushed.</summary>
    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        if (Spec.Id != ProblemsDialId || Plugin.Hub.Target is not { } target) return null;
        var p = target.State.Problems;
        var text = p.Errors > 0 ? $"{p.Errors} err" : p.Warnings > 0 ? $"{p.Warnings} warn" : ctx.Host.Tr("OK");
        return new AdjustmentValue(double.NaN, text);
    }

    private Task Press(CommandContext ctx) => GuardAsync(ctx, Spec.Id, () =>
        ExecOrShortcutAsync(ctx, Spec.Press.Command, Spec.Press.ArgsJson, Spec.Press.ForThisOs));
}

using LoupixDeck.Plugin.VSCode.Catalog;
using LoupixDeck.Plugin.VSCode.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Commands;

/// <summary>Runs one task by its label (from tasks.json or a task provider such as npm). Needs the companion.</summary>
internal sealed class RunTaskCommand(VSCodePlugin plugin) : CodeCommandBase(plugin)
{
    public const string Name = "VSCode.RunTask";
    private const string Glyph = "\U000F0C5A"; // mdi-clipboard-play-outline

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Run task",
        Group = CommandCatalog.RunTest,
        Icon = Glyph,
        Description = "Run one task by its label, as it appears in Run Task, e.g. build (needs the companion).",
        ParameterTemplate = "({Task})",
        Parameters = [new CommandParameter("Task", typeof(string))],
        ButtonLayout = CodeKey.IconAndCaption(Glyph, "Task")
    };

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "run task", async () =>
    {
        if (JoinedParameter(ctx) is not { } name)
        {
            Hint(ctx, "Set task");
            return;
        }

        await RequestAsync(ctx, "task " + name, (w, focus) => w.RunTaskAsync(name, focus)).ConfigureAwait(false);
    });
}

/// <summary>Starts one launch configuration or compound by its name. Needs the companion.</summary>
internal sealed class DebugConfigCommand(VSCodePlugin plugin) : CodeCommandBase(plugin)
{
    public const string Name = "VSCode.Debug";
    private const string Glyph = "\U000F1AF8"; // mdi-bug-play-outline

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Start launch configuration",
        Group = CommandCatalog.Debug,
        Icon = Glyph,
        Description = "Start debugging one launch configuration or compound by its name in launch.json (needs the companion).",
        ParameterTemplate = "({Configuration})",
        Parameters = [new CommandParameter("Configuration", typeof(string))],
        ButtonLayout = CodeKey.IconAndCaption(Glyph, "Launch")
    };

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "start debugging", async () =>
    {
        if (JoinedParameter(ctx) is not { } name)
        {
            Hint(ctx, "Set config");
            return;
        }

        await RequestAsync(ctx, "launch " + name, (w, focus) => w.StartDebugAsync(name, focus)).ConfigureAwait(false);
    });
}

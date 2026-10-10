using LoupixDeck.Plugin.VSCode.Bridge;
using LoupixDeck.Plugin.VSCode.Catalog;
using LoupixDeck.Plugin.VSCode.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Commands;

/// <summary>
/// A key that shows part of the target window's state (problems, git, the debugger…) and acts on
/// it when tapped. <see cref="GetValue"/> only reads the snapshot the window last pushed, so it is
/// free to call on every render; the plugin repaints the key when the hub reports a change in one
/// of its <see cref="Sections"/>. Every state brings its own layout (the background tells the state
/// at a glance), and every key has an <see cref="OfflineState"/> for when no window is connected.
/// </summary>
internal abstract class LiveCommandBase(VSCodePlugin plugin) : CodeCommandBase(plugin), IValueDisplayCommand
{
    public const string OfflineState = "offline";

    /// <summary>The command name, for state switches and refreshes.</summary>
    public abstract string Name { get; }

    /// <summary>The state sections (PROTOCOL.md, "State") this key shows; a change in one repaints it.</summary>
    public abstract IReadOnlyList<string> Sections { get; }

    public override ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    /// <summary>A slow safety net: changes arrive by push from the hub.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        if (Plugin.Hub.Target is not { } window)
        {
            Show(ctx, Name, OfflineState);
            return new AdjustmentValue(double.NaN, ctx.Host.Tr(Plugin.OfflineText()));
        }

        var view = Read(ctx, window, window.State);
        Show(ctx, Name, view.State);
        return new AdjustmentValue(double.NaN, view.Value) { Detail = view.Detail };
    }

    /// <summary>What the key shows for this snapshot: the state name, one line of value and up to two lines of detail.</summary>
    protected abstract LiveView Read(CommandContext ctx, BridgeWindow window, EditorState state);

    protected static ButtonStateDescriptor State(string name, string description, string background, string glyph) => new()
    {
        Name = name,
        Description = description,
        Layout = CodeKey.Live(background, glyph)
    };

    protected static ButtonStateDescriptor Offline(string glyph) =>
        State(OfflineState, "No VS Code window with the companion is connected", CodeKey.DimHex, glyph);

    /// <summary>"1 error", "3 errors".</summary>
    protected static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    protected readonly record struct LiveView(string State, string Value, string? Detail = null);
}

/// <summary>Error and warning counts across the workspace. Tap to jump to the next problem.</summary>
internal sealed class ProblemsCommand(VSCodePlugin plugin) : LiveCommandBase(plugin)
{
    public const string CommandName = "VSCode.Problems";
    public const string CleanState = "clean";
    public const string WarningsState = "warnings";
    public const string ErrorsState = "errors";

    public override string Name => CommandName;
    public override IReadOnlyList<string> Sections { get; } = ["problems"];

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = CommandName,
        DisplayName = "Problems",
        Group = CommandCatalog.Main,
        Icon = "\U000F05D6", // mdi-alert-circle-outline
        Description = "Shows how many errors and warnings the workspace has, red for errors and amber for warnings. Tap to jump to the next problem (needs the companion to show the count).",
        States =
        [
            State(CleanState, "No errors or warnings", CodeKey.OkHex, "\U000F05E1"), // mdi-check-circle-outline
            State(WarningsState, "Warnings but no errors", CodeKey.WarningHex, "\U000F002A"), // mdi-alert-outline
            State(ErrorsState, "At least one error", CodeKey.ErrorHex, "\U000F05D6"), // mdi-alert-circle-outline
            Offline("\U000F05D6") // mdi-alert-circle-outline
        ],
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };

    protected override LiveView Read(CommandContext ctx, BridgeWindow window, EditorState state)
    {
        var p = state.Problems;
        if (p.Errors > 0)
            return new(ErrorsState, Count(p.Errors, "error", "errors"), p.Warnings > 0 ? Count(p.Warnings, "warning", "warnings") : null);
        if (p.Warnings > 0)
            return new(WarningsState, Count(p.Warnings, "warning", "warnings"), p.Infos > 0 ? Count(p.Infos, "info", "infos") : null);
        return new(CleanState, ctx.Host.Tr("No problems"), p.Infos > 0 ? Count(p.Infos, "info", "infos") : null);
    }

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "problems", async () =>
    {
        if (Plugin.Hub.Target is { } window && window.State.Problems is { Errors: 0, Warnings: 0 })
        {
            Hint(ctx, "No problems");
            return;
        }

        await RunActionAsync(ctx, CatalogAction("NextProblem")).ConfigureAwait(false);
    });
}

/// <summary>The branch, changed files and commits to push or pull. Tap to sync when out of step with the remote, else to show Source Control.</summary>
internal sealed class GitCommand(VSCodePlugin plugin) : LiveCommandBase(plugin)
{
    public const string CommandName = "VSCode.Git";
    public const string CleanState = "clean";
    public const string ChangesState = "changes";
    public const string ConflictsState = "conflicts";
    public const string NoneState = "none";

    public override string Name => CommandName;
    public override IReadOnlyList<string> Sections { get; } = ["git"];

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = CommandName,
        DisplayName = "Git status",
        Group = CommandCatalog.Main,
        Icon = "\U000F062C", // mdi-source-branch
        Description = "Shows the current branch, how many files changed and the commits to push (↑) or pull (↓). Tap to sync when there are any, else to show Source Control (needs the companion to show the status).",
        States =
        [
            State(CleanState, "Nothing to commit", CodeKey.OkHex, "\U000F14CF"), // mdi-source-branch-check
            State(ChangesState, "Uncommitted changes", CodeKey.WarningHex, "\U000F062C"), // mdi-source-branch
            State(ConflictsState, "Merge conflicts", CodeKey.ErrorHex, "\U000F062D"), // mdi-source-merge
            State(NoneState, "The window has no repository", CodeKey.DimHex, "\U000F14CC"), // mdi-source-branch-remove
            Offline("\U000F062C") // mdi-source-branch
        ],
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };

    protected override LiveView Read(CommandContext ctx, BridgeWindow window, EditorState state)
    {
        if (state.Git is not { } git) return new(NoneState, ctx.Host.Tr("No repo"));

        var branch = CodeKey.Fit(git.Branch is { Length: > 0 } b ? b : ctx.Host.Tr(git.Detached ? "Detached" : "No branch"));
        var changed = git.Changes + git.Staged;
        var line1 = git.Conflicts > 0 ? Count(git.Conflicts, "conflict", "conflicts")
            : changed > 0 ? $"{changed} {ctx.Host.Tr("changed")}"
            : ctx.Host.Tr("Clean");
        var sync = string.Join(" ", new[] { git.Ahead > 0 ? $"↑{git.Ahead}" : null, git.Behind > 0 ? $"↓{git.Behind}" : null }.Where(s => s is not null));
        if (sync.Length == 0 && !git.Upstream && !git.Detached) sync = ctx.Host.Tr("No upstream");
        var detail = sync.Length > 0 ? line1 + "\n" + sync : line1;

        var stateName = git.Conflicts > 0 ? ConflictsState : changed > 0 ? ChangesState : CleanState;
        return new(stateName, branch, detail);
    }

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "git", async () =>
    {
        var git = Plugin.Hub.Target?.State.Git;
        var id = git is { } g && (g.Ahead > 0 || g.Behind > 0) ? "GitSync" : "ShowSourceControl";
        await RunActionAsync(ctx, CatalogAction(id)).ConfigureAwait(false);
    });
}

/// <summary>The debugger: idle, running or paused (and why). Tap to start, pause or continue.</summary>
internal sealed class DebugStateCommand(VSCodePlugin plugin) : LiveCommandBase(plugin)
{
    public const string CommandName = "VSCode.DebugState";
    public const string InactiveState = "inactive";
    public const string RunningState = "running";
    public const string PausedState = "paused";

    public override string Name => CommandName;
    public override IReadOnlyList<string> Sections { get; } = ["debug"];

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = CommandName,
        DisplayName = "Debugger status",
        Group = CommandCatalog.Main,
        Icon = "\U000F0A30", // mdi-bug-outline
        Description = "Shows whether the debugger is idle, running or paused, with the session and why it stopped. Tap to start debugging, pause, or continue (F5 without the companion).",
        States =
        [
            State(InactiveState, "Not debugging", CodeKey.IdleHex, "\U000F0A30"), // mdi-bug-outline
            State(RunningState, "The program is running", CodeKey.OkHex, "\U000F1AF8"), // mdi-bug-play-outline
            State(PausedState, "Paused at a breakpoint, an exception or a step", CodeKey.AttentionHex, "\U000F1AF6"), // mdi-bug-pause-outline
            Offline("\U000F0A30") // mdi-bug-outline
        ],
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };

    protected override LiveView Read(CommandContext ctx, BridgeWindow window, EditorState state)
    {
        var d = state.Debug;
        var session = d.Session is { Length: > 0 } s ? CodeKey.Fit(s, CodeKey.LineChars + 3) : null;
        if (d.IsPaused)
            return new(PausedState, ctx.Host.Tr("Paused"), string.Join("\n", new[] { session, d.Reason }.Where(x => !string.IsNullOrEmpty(x))));
        if (d.IsRunning) return new(RunningState, ctx.Host.Tr("Running"), session);
        return new(InactiveState, ctx.Host.Tr("Debug"), ctx.Host.Tr("Not running"));
    }

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "debugger", async () =>
    {
        var d = Plugin.Hub.Target?.State.Debug;
        var id = d?.IsPaused == true ? "DebugContinue" : d?.IsRunning == true ? "DebugPause" : "StartDebugging";
        await RunActionAsync(ctx, CatalogAction(id)).ConfigureAwait(false);
    });
}

/// <summary>How many files have unsaved changes. Tap to save them all.</summary>
internal sealed class UnsavedCommand(VSCodePlugin plugin) : LiveCommandBase(plugin)
{
    public const string CommandName = "VSCode.Unsaved";
    public const string SavedState = "saved";
    public const string UnsavedState = "unsaved";

    public override string Name => CommandName;
    public override IReadOnlyList<string> Sections { get; } = ["dirty"];

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = CommandName,
        DisplayName = "Unsaved files",
        Group = CommandCatalog.Main,
        Icon = "\U000F0F43", // mdi-content-save-alert-outline
        Description = "Shows how many files have unsaved changes. Tap to save them all (with the default shortcut when the companion is missing).",
        States =
        [
            State(SavedState, "Everything is saved", CodeKey.OkHex, "\U000F18EB"), // mdi-content-save-check-outline
            State(UnsavedState, "Files with unsaved changes", CodeKey.WarningHex, "\U000F0F43"), // mdi-content-save-alert-outline
            Offline("\U000F0F44") // mdi-content-save-all-outline
        ],
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };

    protected override LiveView Read(CommandContext ctx, BridgeWindow window, EditorState state) =>
        state.DirtyCount > 0
            ? new(UnsavedState, $"{state.DirtyCount} {ctx.Host.Tr("unsaved")}")
            : new(SavedState, ctx.Host.Tr("All saved"));

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "save all", () => RunActionAsync(ctx, CatalogAction("SaveAll")));
}

/// <summary>The active file, cursor position and language. Tap to show the file in the Explorer.</summary>
internal sealed class ActiveFileCommand(VSCodePlugin plugin) : LiveCommandBase(plugin)
{
    public const string CommandName = "VSCode.ActiveFile";
    public const string NormalState = "normal";
    public const string DirtyState = "dirty";

    public override string Name => CommandName;
    public override IReadOnlyList<string> Sections { get; } = ["editor"];

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = CommandName,
        DisplayName = "Active file",
        Group = CommandCatalog.Main,
        Icon = "\U000F09EE", // mdi-file-document-outline
        Description = "Shows the active file's name, the cursor's line and column and the language, amber while it has unsaved changes. Tap to show the file in the Explorer (needs the companion).",
        States =
        [
            State(NormalState, "Saved, or no file open", CodeKey.IdleHex, "\U000F09EE"), // mdi-file-document-outline
            State(DirtyState, "The file has unsaved changes", CodeKey.WarningHex, "\U000F0DC9"), // mdi-file-document-edit-outline
            Offline("\U000F09EE") // mdi-file-document-outline
        ],
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };

    protected override LiveView Read(CommandContext ctx, BridgeWindow window, EditorState state)
    {
        if (state.Editor is not { } e) return new(NormalState, ctx.Host.Tr("No file"));
        var position = $"Ln {e.Line}, Col {e.Column}";
        var detail = e.Language is { Length: > 0 } lang ? position + "\n" + CodeKey.Fit(lang, CodeKey.LineChars + 3) : position;
        return new(e.Dirty ? DirtyState : NormalState, CodeKey.Fit(e.File is { Length: > 0 } f ? f : ctx.Host.Tr("Untitled")), detail);
    }

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "reveal file", () => RunActionAsync(ctx, CatalogAction("RevealInExplorer")));
}

/// <summary>The running task, or the last one and how it ended. Tap to run the last task again.</summary>
internal sealed class TaskStatusCommand(VSCodePlugin plugin) : LiveCommandBase(plugin)
{
    public const string CommandName = "VSCode.TaskStatus";
    public const string IdleState = "idle";
    public const string RunningState = "running";
    public const string OkState = "ok";
    public const string FailedState = "failed";

    public override string Name => CommandName;
    public override IReadOnlyList<string> Sections { get; } = ["tasks"];

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = CommandName,
        DisplayName = "Task status",
        Group = CommandCatalog.Main,
        Icon = "\U000F0756", // mdi-format-list-checks
        Description = "Shows the task that is running, or the last one and whether it succeeded, green or red. Tap to run the last task again (needs the companion).",
        States =
        [
            State(IdleState, "No task has run yet", CodeKey.IdleHex, "\U000F0756"), // mdi-format-list-checks
            State(RunningState, "A task is running", CodeKey.IdleHex, "\U000F0996"), // mdi-progress-clock
            State(OkState, "The last task succeeded", CodeKey.OkHex, "\U000F05E1"), // mdi-check-circle-outline
            State(FailedState, "The last task failed", CodeKey.ErrorHex, "\U000F015A"), // mdi-close-circle-outline
            Offline("\U000F0756") // mdi-format-list-checks
        ],
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };

    protected override LiveView Read(CommandContext ctx, BridgeWindow window, EditorState state)
    {
        var t = state.Tasks;
        if (t.Running.Count > 0)
        {
            var more = t.Running.Count > 1 ? $"\n+{t.Running.Count - 1} {ctx.Host.Tr("more")}" : string.Empty;
            return new(RunningState, CodeKey.Fit(t.Running[0]), ctx.Host.Tr("Running") + more);
        }

        if (t.Last is not { } last) return new(IdleState, ctx.Host.Tr("Tasks"), ctx.Host.Tr("None run yet"));
        var name = CodeKey.Fit(last.Name.Length > 0 ? last.Name : ctx.Host.Tr("Task"));
        if (last.Failed) return new(FailedState, name, $"{ctx.Host.Tr("Exit")} {last.ExitCode}");
        return new(OkState, name, ctx.Host.Tr(last.Succeeded ? "Succeeded" : "Finished"));
    }

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "rerun task", () => RunActionAsync(ctx, CatalogAction("RerunTask")));
}

/// <summary>The last command the integrated terminal ran and its exit code. Tap to show or hide the terminal.</summary>
internal sealed class TerminalStatusCommand(VSCodePlugin plugin) : LiveCommandBase(plugin)
{
    public const string CommandName = "VSCode.TerminalStatus";
    public const string IdleState = "idle";
    public const string OkState = "ok";
    public const string FailedState = "failed";

    public override string Name => CommandName;
    public override IReadOnlyList<string> Sections { get; } = ["terminal"];

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = CommandName,
        DisplayName = "Terminal status",
        Group = CommandCatalog.Main,
        Icon = "\U000F018D", // mdi-console
        Description = "Shows the last command the integrated terminal ran and its exit code, green or red (needs shell integration). Tap to show or hide the terminal (Ctrl+Grave without the companion).",
        States =
        [
            State(IdleState, "No command has finished yet", CodeKey.IdleHex, "\U000F018D"), // mdi-console
            State(OkState, "The last command succeeded", CodeKey.OkHex, "\U000F018D"), // mdi-console
            State(FailedState, "The last command failed", CodeKey.ErrorHex, "\U000F018D"), // mdi-console
            Offline("\U000F018D") // mdi-console
        ],
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };

    protected override LiveView Read(CommandContext ctx, BridgeWindow window, EditorState state)
    {
        var t = state.Terminal;
        if (t.Last is not { } last)
            return new(IdleState, ctx.Host.Tr("Terminal"), t.Count > 0 ? $"{t.Count} {ctx.Host.Tr("open")}" : ctx.Host.Tr("None open"));
        var command = CodeKey.Fit(last.Name.Length > 0 ? last.Name : ctx.Host.Tr("Command"));
        if (last.Failed) return new(FailedState, command, $"{ctx.Host.Tr("Exit")} {last.ExitCode}");
        return new(OkState, command, last.Succeeded ? $"{ctx.Host.Tr("Exit")} 0" : ctx.Host.Tr("Finished"));
    }

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "toggle terminal", () => RunActionAsync(ctx, CatalogAction("ToggleTerminal")));
}

/// <summary>VS Code's screen reader optimised mode (editor.accessibilitySupport). Tap to toggle it.</summary>
internal sealed class ScreenReaderCommand(VSCodePlugin plugin) : LiveCommandBase(plugin)
{
    public const string CommandName = "VSCode.ScreenReader";
    public const string OnState = "on";
    public const string OffState = "off";

    public override string Name => CommandName;
    public override IReadOnlyList<string> Sections { get; } = ["settings"];

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = CommandName,
        DisplayName = "Screen reader mode",
        Group = CommandCatalog.Accessibility,
        Icon = "\U000F05CB", // mdi-account-voice
        Description = "Shows whether VS Code's screen reader optimised mode is on, off or automatic. Tap to toggle it (with the default shortcut when the companion is missing).",
        States =
        [
            State(OnState, "Screen reader mode is on", CodeKey.OkHex, "\U000F05CB"), // mdi-account-voice
            State(OffState, "Off, or left to VS Code to detect", CodeKey.IdleHex, "\U000F05CB"), // mdi-account-voice
            Offline("\U000F05CB") // mdi-account-voice
        ],
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };

    protected override LiveView Read(CommandContext ctx, BridgeWindow window, EditorState state)
    {
        var detail = ctx.Host.Tr("Screen reader");
        return state.Settings.AccessibilitySupport switch
        {
            "on" => new(OnState, ctx.Host.Tr("On"), detail),
            "off" => new(OffState, ctx.Host.Tr("Off"), detail),
            _ => new(OffState, ctx.Host.Tr("Auto"), detail)
        };
    }

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "screen reader mode", () => RunActionAsync(ctx, CatalogAction("ScreenReaderMode")));
}

/// <summary>The window keys act on: its workspace and editor. Tap to open the Windows folder and pick another.</summary>
internal sealed class WindowCommand(VSCodePlugin plugin) : LiveCommandBase(plugin)
{
    public const string CommandName = "VSCode.Window";
    public const string ConnectedState = "connected";

    public override string Name => CommandName;
    public override IReadOnlyList<string> Sections { get; } = ["workspace", "window"];

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = CommandName,
        DisplayName = "Target window",
        Group = CommandCatalog.Main,
        Icon = "\U000F0C8C", // mdi-application-brackets-outline
        Description = "Shows the VS Code window keys act on (the one in front, else the one used last): its workspace and editor. Tap to pick another window.",
        States =
        [
            State(ConnectedState, "A window is connected", CodeKey.IdleHex, "\U000F0C8C"), // mdi-application-brackets-outline
            Offline("\U000F0C8C") // mdi-application-brackets-outline
        ],
        ButtonLayout = new ButtonLayoutDescriptor { Mode = ButtonLayoutMode.None }
    };

    protected override LiveView Read(CommandContext ctx, BridgeWindow window, EditorState state)
    {
        var name = window.WorkspaceName is { Length: > 0 } ws ? CodeKey.Fit(ws) : ctx.Host.Tr("No folder");
        var count = Plugin.Hub.Windows.Count;
        var detail = WindowsFolderCommand.AppName(window.App) + (count > 1 ? $"\n{count} {ctx.Host.Tr("windows")}" : string.Empty);
        return new(ConnectedState, name, detail);
    }

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "windows folder", () =>
    {
        if (!WindowsFolderCommand.Open(Plugin, ctx.Host)) Hint(ctx, OfflineHint());
        return Task.CompletedTask;
    });
}

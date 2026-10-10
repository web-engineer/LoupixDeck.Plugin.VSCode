using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.VSCode.Bridge;

/// <summary>
/// What one window last told us about itself (PROTOCOL.md, "State"). Immutable: a <c>state</c>
/// event builds a new snapshot with <see cref="Apply"/> and the connection swaps it in, so keys
/// read it without locks and never see half an update.
/// </summary>
internal sealed record EditorState(
    WindowInfo? Window,
    WorkspaceInfo? Workspace,
    EditorInfo? Editor,
    int DirtyCount,
    ProblemCounts Problems,
    GitInfo? Git,
    DebugInfo Debug,
    TaskInfo Tasks,
    TerminalInfo Terminal,
    SettingsInfo Settings)
{
    /// <summary>The section names a <c>state</c> event can carry, as they appear in the JSON.</summary>
    public static readonly IReadOnlyList<string> SectionNames =
        ["window", "workspace", "editor", "dirty", "problems", "git", "debug", "tasks", "terminal", "settings"];

    /// <summary>Nothing known yet: no editor, no repository, no problems, debugger idle.</summary>
    public static readonly EditorState Empty = new(
        null, null, null, 0, ProblemCounts.None, null, DebugInfo.Inactive, TaskInfo.None, TerminalInfo.None, SettingsInfo.Default);

    /// <summary>A full snapshot (the <c>hello</c> reply or the <c>state</c> op).</summary>
    public static EditorState Parse(JsonObject? snapshot) => snapshot is null ? Empty : Empty.Apply(snapshot);

    /// <summary>
    /// A new snapshot with every section present in <paramref name="sections"/> replaced whole (no
    /// deep merge, as the protocol says). Unknown sections and fields are ignored, and a section of
    /// the wrong shape resets to its empty value instead of throwing.
    /// </summary>
    public EditorState Apply(JsonObject sections)
    {
        var s = this;
        foreach (var (key, node) in sections)
        {
            var o = node as JsonObject;
            s = key switch
            {
                "window" => s with { Window = WindowInfo.From(o) },
                "workspace" => s with { Workspace = WorkspaceInfo.From(o) },
                "editor" => s with { Editor = EditorInfo.From(o) },
                "dirty" => s with { DirtyCount = Math.Max(0, Json.Int(o, "count") ?? 0) },
                "problems" => s with { Problems = ProblemCounts.From(o) },
                "git" => s with { Git = GitInfo.From(o) },
                "debug" => s with { Debug = DebugInfo.From(o) },
                "tasks" => s with { Tasks = TaskInfo.From(o) },
                "terminal" => s with { Terminal = TerminalInfo.From(o) },
                "settings" => s with { Settings = SettingsInfo.From(o) },
                _ => s
            };
        }

        return s;
    }

    /// <summary>The known section names present in a <c>state</c> event, in protocol order.</summary>
    public static IReadOnlyList<string> SectionsIn(JsonObject sections) =>
        SectionNames.Where(sections.ContainsKey).ToList();
}

/// <summary>The window's focus: <c>Focused</c> is OS focus, <c>Active</c> is VS Code's own activity flag.</summary>
internal sealed record WindowInfo(bool Focused, bool Active, DateTimeOffset? LastFocused)
{
    public static WindowInfo? From(JsonObject? o) => o is null
        ? null
        : new WindowInfo(Json.Bool(o, "focused") ?? false, Json.Bool(o, "active") ?? false, Json.Time(o, "lastFocused"));
}

/// <summary>The workspace: its name, folder URIs and remote name (<c>ssh-remote</c>, <c>wsl</c>…; null when local).</summary>
internal sealed record WorkspaceInfo(string? Name, IReadOnlyList<string> Folders, string? Remote)
{
    public static WorkspaceInfo? From(JsonObject? o) => o is null
        ? null
        : new WorkspaceInfo(Json.Str(o, "name"), Json.Strings(o, "folders"), Json.Str(o, "remote"));
}

/// <summary>The active text editor; <c>Line</c> and <c>Column</c> are 1-based.</summary>
internal sealed record EditorInfo(string? File, string? Path, string? Language, bool Dirty, int Line, int Column, int Selections)
{
    public static EditorInfo? From(JsonObject? o) => o is null
        ? null
        : new EditorInfo(
            Json.Str(o, "file"),
            Json.Str(o, "path"),
            Json.Str(o, "language"),
            Json.Bool(o, "dirty") ?? false,
            Math.Max(1, Json.Int(o, "line") ?? 1),
            Math.Max(1, Json.Int(o, "column") ?? 1),
            Math.Max(1, Json.Int(o, "selections") ?? 1));
}

/// <summary>Diagnostic counts across the workspace.</summary>
internal sealed record ProblemCounts(int Errors, int Warnings, int Infos)
{
    public static readonly ProblemCounts None = new(0, 0, 0);

    public int Total => Errors + Warnings + Infos;

    public static ProblemCounts From(JsonObject? o) => o is null
        ? None
        : new ProblemCounts(Math.Max(0, Json.Int(o, "errors") ?? 0), Math.Max(0, Json.Int(o, "warnings") ?? 0), Math.Max(0, Json.Int(o, "infos") ?? 0));
}

/// <summary>
/// The selected repository. <c>Changes</c> counts working-tree, untracked and merge changes,
/// <c>Staged</c> the index; <c>Upstream</c> is false when the branch tracks nothing.
/// </summary>
internal sealed record GitInfo(string? Branch, bool Detached, int Ahead, int Behind, int Changes, int Staged, int Conflicts, int Repos, bool Upstream)
{
    public static GitInfo? From(JsonObject? o) => o is null
        ? null
        : new GitInfo(
            Json.Str(o, "branch"),
            Json.Bool(o, "detached") ?? false,
            Math.Max(0, Json.Int(o, "ahead") ?? 0),
            Math.Max(0, Json.Int(o, "behind") ?? 0),
            Math.Max(0, Json.Int(o, "changes") ?? 0),
            Math.Max(0, Json.Int(o, "staged") ?? 0),
            Math.Max(0, Json.Int(o, "conflicts") ?? 0),
            Math.Max(1, Json.Int(o, "repos") ?? 1),
            Json.Bool(o, "upstream") ?? false);
}

/// <summary>The debugger: <c>State</c> is <c>inactive</c>, <c>running</c> or <c>paused</c>; <c>Reason</c> is why it stopped.</summary>
internal sealed record DebugInfo(string State, string? Session, string? Reason)
{
    public const string InactiveState = "inactive";
    public const string RunningState = "running";
    public const string PausedState = "paused";

    public static readonly DebugInfo Inactive = new(InactiveState, null, null);

    public bool IsRunning => State == RunningState;
    public bool IsPaused => State == PausedState;
    public bool IsActive => IsRunning || IsPaused;

    public static DebugInfo From(JsonObject? o)
    {
        if (o is null) return Inactive;
        var state = Json.Str(o, "state") switch
        {
            RunningState => RunningState,
            PausedState => PausedState,
            _ => InactiveState
        };
        return new DebugInfo(state, Json.Str(o, "session"), Json.Str(o, "reason"));
    }
}

/// <summary>A task or terminal command that finished; <c>ExitCode</c> is null when unknown.</summary>
internal sealed record CompletedRun(string Name, int? ExitCode, DateTimeOffset? At)
{
    public bool Failed => ExitCode is { } code && code != 0;
    public bool Succeeded => ExitCode == 0;

    public static CompletedRun? From(JsonObject? o, string nameKey)
    {
        if (o is null) return null;
        return new CompletedRun(Json.Str(o, nameKey) ?? string.Empty, Json.Int(o, "exitCode"), Json.Time(o, "at"));
    }
}

/// <summary>Running task labels, and the most recent task that ended.</summary>
internal sealed record TaskInfo(IReadOnlyList<string> Running, CompletedRun? Last)
{
    public static readonly TaskInfo None = new([], null);

    public static TaskInfo From(JsonObject? o) => o is null
        ? None
        : new TaskInfo(Json.Strings(o, "running"), CompletedRun.From(o["last"] as JsonObject, "name"));
}

/// <summary>Terminal count, and the last command shell integration saw finish (<c>Name</c> is the command line).</summary>
internal sealed record TerminalInfo(int Count, CompletedRun? Last)
{
    public static readonly TerminalInfo None = new(0, null);

    public static TerminalInfo From(JsonObject? o) => o is null
        ? None
        : new TerminalInfo(Math.Max(0, Json.Int(o, "count") ?? 0), CompletedRun.From(o["last"] as JsonObject, "command"));
}

/// <summary>A few editor settings the keys show: <c>AccessibilitySupport</c> is <c>on</c>, <c>off</c> or <c>auto</c>.</summary>
internal sealed record SettingsInfo(string AccessibilitySupport, string? WordWrap, bool? Minimap)
{
    public static readonly SettingsInfo Default = new("auto", null, null);

    public static SettingsInfo From(JsonObject? o)
    {
        if (o is null) return Default;
        var support = Json.Str(o, "accessibilitySupport") switch
        {
            "on" => "on",
            "off" => "off",
            _ => "auto"
        };
        return new SettingsInfo(support, Json.Str(o, "wordWrap"), Json.Bool(o, "minimap"));
    }
}

using System.Text.Json;
using LoupixDeck.Plugin.VSCode.Catalog;
using Xunit;

namespace LoupixDeck.Plugin.VSCode.Tests;

public sealed class CatalogTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static IEnumerable<string> FixtureLines(string name) =>
        File.ReadLines(Fixture(name)).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

    /// <summary>Every command id VS Code 1.141.0 had registered (Developer: dump, all built-in extensions active).</summary>
    private static readonly HashSet<string> VSCodeCommands = new(FixtureLines("vscode-1.141-commands.txt"), StringComparer.Ordinal);

    /// <summary>
    /// Key names the host's macro engine knows on macOS (LoupixDeck Utils/KeyNames.cs, with aliases).
    /// A typo here would press nothing, or type the character without its modifiers, silently.
    /// </summary>
    private static readonly HashSet<string> HostKeys = new(FixtureLines("host-key-names.txt"), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Built-in commands that a window registers only once their extension activates, so a dump can
    /// miss them. Each entry needs a reason. Empty for now: the 1.141 dump was taken with every
    /// built-in extension active (git.* included), so every catalogue command is in it.
    /// </summary>
    private static readonly HashSet<string> LazyBuiltIns = new(StringComparer.Ordinal);

    /// <summary>Names of the plugin's own commands outside the catalogues (live keys, folders, generic keys).</summary>
    private static readonly string[] Reserved =
    [
        "Problems", "Git", "DebugState", "Unsaved", "ActiveFile", "TaskStatus", "TerminalStatus", "ScreenReader", "Window",
        "Windows", "Tasks", "DebugConfigs", "ProblemList", "OpenEditors", "Recent",
        "Open", "OpenFolder", "Run", "RunTask", "Debug"
    ];

    private static IEnumerable<DialStep> Steps(DialSpec d) => [d.Left, d.Right, d.Press];

    /// <summary>Every fallback shortcut in both catalogues, labelled for failure messages.</summary>
    private static IEnumerable<(string Label, string Os, string Keys)> Shortcuts()
    {
        foreach (var a in CommandCatalog.All)
        foreach (var (os, keys) in new[] { ("mac", a.Mac), ("windows", a.Windows), ("linux", a.Linux) })
            if (keys is not null)
                yield return (a.Id, os, keys);

        foreach (var d in DialCatalog.All)
        foreach (var (side, s) in new[] { ("left", d.Left), ("right", d.Right), ("press", d.Press) })
        foreach (var (os, keys) in new[] { ("mac", s.Mac), ("windows", s.Windows), ("linux", s.Linux) })
            if (keys is not null)
                yield return ($"{d.Id}.{side}", os, keys);
    }

    [Fact]
    public void Ids_and_command_names_are_unique()
    {
        var ids = CommandCatalog.All.Select(a => a.Id).Concat(DialCatalog.All.Select(d => d.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var names = CommandCatalog.All.Select(a => a.CommandName).Concat(DialCatalog.All.Select(d => d.CommandName)).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.StartsWith("VSCode.", n));
    }

    [Fact]
    public void No_id_takes_a_name_the_plugin_uses_elsewhere()
    {
        var reserved = new HashSet<string>(Reserved, StringComparer.OrdinalIgnoreCase);
        Assert.All(CommandCatalog.All, a => Assert.DoesNotContain(a.Id, reserved));
        Assert.All(DialCatalog.All, d => Assert.DoesNotContain(d.Id, reserved));
    }

    [Fact]
    public void Every_built_in_command_exists_in_vscode()
    {
        foreach (var a in CommandCatalog.All.Where(a => a.RequiresExtension is null))
            Assert.True(VSCodeCommands.Contains(a.Command) || LazyBuiltIns.Contains(a.Command), $"{a.Id}: unknown command '{a.Command}'");

        foreach (var d in DialCatalog.All)
        foreach (var s in Steps(d))
            Assert.True(VSCodeCommands.Contains(s.Command) || LazyBuiltIns.Contains(s.Command), $"{d.Id}: unknown command '{s.Command}'");
    }

    [Fact]
    public void Extension_commands_are_marked_as_such()
    {
        // A command missing from the built-in dump must say which extension brings it, so the key
        // can tell the user what to install instead of failing silently.
        foreach (var a in CommandCatalog.All.Where(a => !VSCodeCommands.Contains(a.Command) && !LazyBuiltIns.Contains(a.Command)))
            Assert.False(string.IsNullOrEmpty(a.RequiresExtension), $"{a.Id}: '{a.Command}' is not built in");
    }

    [Fact]
    public void Every_key_name_is_one_the_host_knows()
    {
        foreach (var (label, os, keys) in Shortcuts())
        {
            Assert.DoesNotContain("  ", keys);
            foreach (var chord in keys.Split(' '))
            foreach (var key in chord.Split('+'))
                Assert.True(HostKeys.Contains(key), $"{label} ({os}): unknown key '{key}' in '{keys}'");
        }
    }

    [Fact]
    public void No_shortcut_contains_a_comma()
    {
        // The host splits command parameters on commas, so "Cmd+," would arrive as "Cmd+".
        Assert.All(Shortcuts(), s => Assert.DoesNotContain(',', s.Keys));
    }

    [Fact]
    public void Windows_and_linux_shortcuts_never_use_cmd()
    {
        // Cmd maps to the Windows (Super) key there, which opens the Start menu or the launcher.
        foreach (var (label, os, keys) in Shortcuts().Where(s => s.Os != "mac"))
            Assert.False(keys.Split(' ', '+').Any(k => k is "Cmd" or "Win" or "Meta" or "Super"), $"{label} ({os}): '{keys}'");
    }

    [Fact]
    public void Captions_fit_on_a_key()
    {
        Assert.All(CommandCatalog.All, a => Assert.InRange(a.Caption.Length, 1, 10));
    }

    [Fact]
    public void Descriptions_end_with_the_fallback()
    {
        foreach (var a in CommandCatalog.All)
        {
            Assert.True(a.Description.EndsWith(')') || a.Description.EndsWith('.'), a.Id);
            var hasFallback = a.Mac is not null || a.Windows is not null || a.Linux is not null;
            Assert.True(hasFallback ? a.Description.Contains(" without the companion)") : a.Description.Contains("(needs the companion)"), a.Id);
            if (a.RequiresExtension is not null)
                Assert.Contains("Needs the ", a.Description);
        }

        Assert.All(DialCatalog.All, d => Assert.EndsWith(".", d.Description));
    }

    [Fact]
    public void Arguments_are_json_arrays()
    {
        var args = CommandCatalog.All.Select(a => (a.Id, a.ArgsJson))
            .Concat(DialCatalog.All.SelectMany(d => Steps(d).Select(s => (d.Id, s.ArgsJson))))
            .Where(x => x.ArgsJson is not null);
        foreach (var (id, json) in args)
        {
            using var doc = JsonDocument.Parse(json!);
            Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, $"{id}: arguments are not an array");
        }
    }

    [Fact]
    public void Groups_are_the_published_groups()
    {
        string[] groups =
        [
            CommandCatalog.Files, CommandCatalog.Navigate, CommandCatalog.Edit, CommandCatalog.Search, CommandCatalog.View,
            CommandCatalog.Terminal, CommandCatalog.SourceControl, CommandCatalog.Debug, CommandCatalog.RunTest,
            CommandCatalog.AI, CommandCatalog.Accessibility
        ];
        Assert.All(CommandCatalog.All, a => Assert.Contains(a.Group, groups));
        Assert.All(groups, g => Assert.Contains(CommandCatalog.All, a => a.Group == g));
    }

    [Fact]
    public void Glyphs_are_mdi_icons_the_host_has()
    {
        // mdi-catalog.json is the host's own list (Assets/Fonts), so a code point outside it renders as a box.
        using var doc = JsonDocument.Parse(File.ReadAllText(Fixture("mdi-catalog.json")));
        var codePoints = doc.RootElement.GetProperty("icons").EnumerateArray()
            .Select(i => Convert.ToInt32(i[1].GetString(), 16)).ToHashSet();

        foreach (var (id, glyph) in CommandCatalog.All.Select(a => (a.Id, a.Glyph)).Concat(DialCatalog.All.Select(d => (d.Id, d.Glyph))))
        {
            Assert.True(glyph.Length == 2 && char.IsSurrogatePair(glyph, 0), $"{id}: glyph is not one code point");
            Assert.True(codePoints.Contains(char.ConvertToUtf32(glyph, 0)), $"{id}: glyph U+{char.ConvertToUtf32(glyph, 0):X} is not in mdi-catalog.json");
        }
    }

    [Fact]
    public void For_this_os_picks_the_platform_shortcut()
    {
        var a = CommandCatalog.All.Single(x => x.Id == "CopyFilePath");
        var expected = OperatingSystem.IsMacOS() ? a.Mac : OperatingSystem.IsWindows() ? a.Windows : a.Linux;
        Assert.Equal(expected, a.ForThisOs);
        var step = DialCatalog.All.Single(d => d.Id == "WindowZoom").Right;
        Assert.Equal(OperatingSystem.IsMacOS() ? step.Mac : OperatingSystem.IsWindows() ? step.Windows : step.Linux, step.ForThisOs);
    }

    [Theory]
    // Cmd+I is shared with inline chat on macOS; Ctrl+Space is the one that always suggests.
    [InlineData("TriggerSuggest", "Ctrl+Space", "Ctrl+Space", "Ctrl+Space")]
    // Cmd+0 focuses the side bar on macOS, so reset is the keypad key.
    [InlineData("ZoomReset", "Cmd+Num0", "Ctrl+Num0", "Ctrl+Num0")]
    [InlineData("KeyboardShortcuts", "Cmd+K Cmd+S", "Ctrl+K Ctrl+S", "Ctrl+K Ctrl+S")]
    [InlineData("SaveAll", "Alt+Cmd+S", "Ctrl+K S", null)]
    [InlineData("NavigateBack", "Ctrl+Minus", "Alt+Left", "Ctrl+Alt+Minus")]
    [InlineData("AgentMode", "Shift+Cmd+I", "Ctrl+Shift+I", "Ctrl+Shift+Alt+I")]
    [InlineData("ToggleTerminal", "Ctrl+Grave", "Ctrl+Grave", "Ctrl+Grave")]
    [InlineData("AttachContext", null, null, null)]
    [InlineData("SplitEditorDown", null, null, null)]
    public void Spot_checks_against_vscode_defaults(string id, string? mac, string? windows, string? linux)
    {
        var a = CommandCatalog.All.Single(x => x.Id == id);
        Assert.Equal(mac, a.Mac);
        Assert.Equal(windows, a.Windows);
        Assert.Equal(linux, a.Linux);
    }

    [Fact]
    public void Zoom_dial_uses_the_main_keyboard_keys()
    {
        var zoom = DialCatalog.All.Single(d => d.Id == "WindowZoom");
        Assert.Equal("Cmd+Minus", zoom.Left.Mac);
        Assert.Equal("Cmd+Equals", zoom.Right.Mac);
        Assert.Equal("Ctrl+Equals", zoom.Right.Windows);
    }

    [Fact]
    public void Scroll_dial_scrolls_without_moving_the_cursor()
    {
        var scroll = DialCatalog.All.Single(d => d.Id == "Scroll");
        Assert.Equal("editorScroll", scroll.Left.Command);
        using var doc = JsonDocument.Parse(scroll.Left.ArgsJson!);
        var arg = doc.RootElement[0];
        Assert.Equal("up", arg.GetProperty("to").GetString());
        Assert.Equal("line", arg.GetProperty("by").GetString());
        Assert.Equal(3, arg.GetProperty("value").GetInt32());
        Assert.False(arg.GetProperty("revealCursor").GetBoolean());
    }
}

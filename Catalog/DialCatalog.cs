namespace LoupixDeck.Plugin.VSCode.Catalog;

/// <summary>
/// One step of a dial: the VS Code command a tick or a press runs through the companion, and the
/// default shortcut sent instead when no companion window is connected (host notation, as
/// <see cref="CodeAction"/>).
/// </summary>
internal sealed record DialStep(string Command, string? ArgsJson, string? Mac, string? Windows, string? Linux)
{
    public string? ForThisOs => OperatingSystem.IsMacOS() ? Mac : OperatingSystem.IsWindows() ? Windows : Linux;
}

/// <summary>A dial: turning left, turning right and pressing each run a step.</summary>
/// <param name="Id">Command id suffix (<c>VSCode.&lt;Id&gt;</c>). Permanent once shipped.</param>
internal sealed record DialSpec(string Id, string DisplayName, string Glyph, string Description, DialStep Left, DialStep Right, DialStep Press)
{
    public string CommandName => "VSCode." + Id;
}

/// <summary>
/// Every dial the plugin offers, checked like <see cref="CommandCatalog"/>. A fallback is left out
/// where the default key would type a character or insert a tab outside the context it is meant
/// for (Alt+[ on macOS, Tab), or where the binding only applies with the terminal focused and the
/// same key does something else in the editor (Cmd+Up moves the cursor, Shift+Cmd+[ switches tabs).
/// </summary>
internal static class DialCatalog
{
    public static readonly IReadOnlyList<DialSpec> All =
    [
        new("WindowZoom", "Window zoom", "\U000F0349", // mdi-magnify
            "Turn to zoom the whole window in or out; press to reset the zoom.",
            Left: new("workbench.action.zoomOut", null, "Cmd+Minus", "Ctrl+Minus", "Ctrl+Minus"),
            Right: new("workbench.action.zoomIn", null, "Cmd+Equals", "Ctrl+Equals", "Ctrl+Equals"),
            Press: new("workbench.action.zoomReset", null, "Cmd+Num0", "Ctrl+Num0", "Ctrl+Num0")),
        new("EditorFontZoom", "Editor font size", "\U000F027F", // mdi-format-size
            "Turn to change only the editor font size; press to reset it.",
            Left: new("editor.action.fontZoomOut", null, null, null, null),
            Right: new("editor.action.fontZoomIn", null, null, null, null),
            Press: new("editor.action.fontZoomReset", null, null, null, null)),
        new("EditorTabs", "Editor tabs", "\U000F04E9", // mdi-tab
            "Turn to move between editor tabs; press to pick from the open editors by most recent use.",
            Left: new("workbench.action.previousEditor", null, "Shift+Cmd+LeftBracket", "Ctrl+PageUp", "Ctrl+PageUp"),
            Right: new("workbench.action.nextEditor", null, "Shift+Cmd+RightBracket", "Ctrl+PageDown", "Ctrl+PageDown"),
            Press: new("workbench.action.showAllEditorsByMostRecentlyUsed", null, null, null, null)),
        new("EditorGroups", "Editor groups", "\U000F0BCC", // mdi-view-split-vertical
            "Turn to move focus between split editor groups; press to maximise the active group.",
            Left: new("workbench.action.focusPreviousGroup", null, null, null, null),
            Right: new("workbench.action.focusNextGroup", null, null, null, null),
            Press: new("workbench.action.toggleMaximizeEditorGroup", null, "Cmd+K Cmd+M", "Ctrl+K Ctrl+M", "Ctrl+K Ctrl+M")),
        new("ProblemSteps", "Problems", "\U000F05D6", // mdi-alert-circle-outline
            "Turn to step through errors and warnings; press to show the Problems panel.",
            Left: new("editor.action.marker.prevInFiles", null, "Shift+F8", "Shift+F8", "Shift+F8"),
            Right: new("editor.action.marker.nextInFiles", null, "F8", "F8", "F8"),
            Press: new("workbench.actions.view.problems", null, "Shift+Cmd+M", "Ctrl+Shift+M", "Ctrl+Shift+M")),
        new("GitChanges", "Diffs", "\U000F062C", // mdi-source-branch
            "Turn to step through the diffs (changed blocks) in the active file; press to peek at the change inline.",
            Left: new("workbench.action.editor.previousChange", null, "Shift+Alt+F5", "Shift+Alt+F5", "Shift+Alt+F5"),
            Right: new("workbench.action.editor.nextChange", null, "Alt+F5", "Alt+F5", "Alt+F5"),
            Press: new("editor.action.dirtydiff.next", null, "Alt+F3", "Alt+F3", "Alt+F3")),
        new("Scroll", "Editor scroll", "\U000F04E2", // mdi-swap-vertical
            "Turn to scroll the editor three lines at a time without moving the cursor; press to focus the editor.",
            Left: new("editorScroll", "[{\"to\":\"up\",\"by\":\"line\",\"value\":3,\"revealCursor\":false}]", "Ctrl+PageUp", "Ctrl+Up", "Ctrl+Up"),
            Right: new("editorScroll", "[{\"to\":\"down\",\"by\":\"line\",\"value\":3,\"revealCursor\":false}]", "Ctrl+PageDown", "Ctrl+Down", "Ctrl+Down"),
            Press: new("workbench.action.focusActiveEditorGroup", null, null, null, null)),
        new("MoveLine", "Move line", "\U000F0BCE", // mdi-swap-vertical-bold
            "Turn to move the current line or selection up or down; press to duplicate it.",
            Left: new("editor.action.moveLinesUpAction", null, "Alt+Up", "Alt+Up", "Alt+Up"),
            Right: new("editor.action.moveLinesDownAction", null, "Alt+Down", "Alt+Down", "Alt+Down"),
            Press: new("editor.action.copyLinesDownAction", null, "Shift+Alt+Down", "Shift+Alt+Down", "Ctrl+Shift+Alt+Down")),
        new("CursorHistory", "Cursor undo", "\U000F01BE", // mdi-cursor-move
            "Turn to undo or redo cursor moves; press to jump to the last edit.",
            Left: new("cursorUndo", null, "Cmd+U", "Ctrl+U", "Ctrl+U"),
            Right: new("cursorRedo", null, null, null, null),
            Press: new("workbench.action.navigateToLastEditLocation", null, "Cmd+K Cmd+Q", "Ctrl+K Ctrl+Q", "Ctrl+K Ctrl+Q")),
        new("NavigationHistory", "Navigation history", "\U000F02DA", // mdi-history
            "Turn to go back or forward through cursor locations; press to jump to the last edit.",
            Left: new("workbench.action.navigateBack", null, "Ctrl+Minus", "Alt+Left", "Ctrl+Alt+Minus"),
            Right: new("workbench.action.navigateForward", null, "Ctrl+Shift+Minus", "Alt+Right", "Ctrl+Shift+Minus"),
            Press: new("workbench.action.navigateToLastEditLocation", null, "Cmd+K Cmd+Q", "Ctrl+K Ctrl+Q", "Ctrl+K Ctrl+Q")),
        new("Folding", "Folding", "\U000F054F", // mdi-unfold-more-horizontal
            "Turn left to fold the region at the cursor and right to unfold it; press to toggle the fold.",
            Left: new("editor.fold", null, "Alt+Cmd+LeftBracket", "Ctrl+Shift+LeftBracket", "Ctrl+Shift+LeftBracket"),
            Right: new("editor.unfold", null, "Alt+Cmd+RightBracket", "Ctrl+Shift+RightBracket", "Ctrl+Shift+RightBracket"),
            Press: new("editor.toggleFold", null, "Cmd+K Cmd+L", "Ctrl+K Ctrl+L", "Ctrl+K Ctrl+L")),
        new("SmartSelect", "Smart select", "\U000F0A6D", // mdi-selection-drag
            "Turn to shrink or grow the selection by syntax; press to copy it.",
            Left: new("editor.action.smartSelect.shrink", null, "Ctrl+Shift+Left", "Shift+Alt+Left", "Shift+Alt+Left"),
            Right: new("editor.action.smartSelect.expand", null, "Ctrl+Shift+Right", "Shift+Alt+Right", "Shift+Alt+Right"),
            Press: new("editor.action.clipboardCopyAction", null, "Cmd+C", "Ctrl+C", "Ctrl+C")),
        new("SearchResults", "Search results", "\U000F13B8", // mdi-text-search
            "Turn to step through workspace search results; press to search in files.",
            Left: new("search.action.focusPreviousSearchResult", null, "Shift+F4", "Shift+F4", "Shift+F4"),
            Right: new("search.action.focusNextSearchResult", null, "F4", "F4", "F4"),
            Press: new("workbench.action.findInFiles", null, "Shift+Cmd+F", "Ctrl+Shift+F", "Ctrl+Shift+F")),
        new("TerminalScroll", "Terminal scroll", "\U000F018D", // mdi-console
            "Turn to scroll the terminal; press to jump to the bottom.",
            Left: new("workbench.action.terminal.scrollUp", null, "Alt+Cmd+PageUp", "Ctrl+Alt+PageUp", "Ctrl+Shift+Up"),
            Right: new("workbench.action.terminal.scrollDown", null, "Alt+Cmd+PageDown", "Ctrl+Alt+PageDown", "Ctrl+Shift+Down"),
            Press: new("workbench.action.terminal.scrollToBottom", null, "Cmd+End", "Ctrl+End", "Shift+End")),
        new("TerminalCommands", "Terminal commands", "\U000F07B7", // mdi-console-line
            "Turn to jump between commands in the terminal output; press to run a recent command again.",
            Left: new("workbench.action.terminal.scrollToPreviousCommand", null, null, null, null),
            Right: new("workbench.action.terminal.scrollToNextCommand", null, null, null, null),
            Press: new("workbench.action.terminal.runRecentCommand", null, null, null, null)),
        new("Terminals", "Terminal switcher", "\U000F0C60", // mdi-console-network-outline
            "Turn to switch between terminals; press to show or hide the terminal.",
            Left: new("workbench.action.terminal.focusPrevious", null, null, null, null),
            Right: new("workbench.action.terminal.focusNext", null, null, null, null),
            Press: new("workbench.action.terminal.toggleTerminal", null, "Ctrl+Grave", "Ctrl+Grave", "Ctrl+Grave")),
        new("DebugStep", "Debug step", "\U000F01B7", // mdi-debug-step-over
            "Turn right to step over and left to step out; press to continue.",
            Left: new("workbench.action.debug.stepOut", null, "Shift+F11", "Shift+F11", "Shift+F11"),
            Right: new("workbench.action.debug.stepOver", null, "F10", "F10", "F10"),
            Press: new("workbench.action.debug.continue", null, "F5", "F5", "F5")),
        new("CallStack", "Call stack", "\U000F09FE", // mdi-layers-outline
            "Turn to move up or down the call stack while paused; press to step into.",
            Left: new("workbench.action.debug.callStackUp", null, null, null, null),
            Right: new("workbench.action.debug.callStackDown", null, null, null, null),
            Press: new("workbench.action.debug.stepInto", null, "F11", "F11", "F11")),
        new("SideBarViews", "Side bar views", "\U000F10AA", // mdi-dock-left
            "Turn to cycle Explorer, Search, Source Control and the other side bar views; press to show or hide the side bar.",
            Left: new("workbench.action.previousSideBarView", null, null, null, null),
            Right: new("workbench.action.nextSideBarView", null, null, null, null),
            Press: new("workbench.action.toggleSidebarVisibility", null, "Cmd+B", "Ctrl+B", "Ctrl+B")),
        new("PanelViews", "Panel views", "\U000F10A9", // mdi-dock-bottom
            "Turn to cycle Terminal, Problems, Output and Debug Console; press to show or hide the panel.",
            Left: new("workbench.action.previousPanelView", null, null, null, null),
            Right: new("workbench.action.nextPanelView", null, null, null, null),
            Press: new("workbench.action.togglePanel", null, "Cmd+J", "Ctrl+J", "Ctrl+J")),
        new("AccessibleDiff", "Accessible diff", "\U000F08AA", // mdi-file-compare
            "Turn to move through the changes in the accessible diff viewer; press to open the Accessible View.",
            Left: new("editor.action.accessibleDiffViewer.prev", null, "Shift+F7", "Shift+F7", "Shift+F7"),
            Right: new("editor.action.accessibleDiffViewer.next", null, "F7", "F7", "F7"),
            Press: new("editor.action.accessibleView", null, "Alt+F2", "Alt+F2", "Shift+Alt+F2")),
        new("AccessibleViewItems", "Accessible View items", "\U000F1900", // mdi-eye-arrow-right-outline
            "With the Accessible View open, turn to move to the previous or next item; press to open the Accessible View.",
            Left: new("editor.action.accessibleViewPrevious", null, null, null, null),
            Right: new("editor.action.accessibleViewNext", null, null, null, null),
            Press: new("editor.action.accessibleView", null, "Alt+F2", "Alt+F2", "Shift+Alt+F2")),
        new("InlineSuggestions", "Inline suggestions", "\U000F0674", // mdi-creation
            "Turn to cycle inline AI suggestions; press to accept the one showing.",
            Left: new("editor.action.inlineSuggest.showPrevious", null, null, null, null),
            Right: new("editor.action.inlineSuggest.showNext", null, null, null, null),
            Press: new("editor.action.inlineSuggest.commit", null, null, null, null))
    ];
}

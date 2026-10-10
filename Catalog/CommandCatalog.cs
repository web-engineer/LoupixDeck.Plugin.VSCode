namespace LoupixDeck.Plugin.VSCode.Catalog;

/// <summary>
/// One VS Code command on a key. The key runs <see cref="Command"/> through the companion
/// extension; with no companion window connected it falls back to the editor's default shortcut,
/// spelt as the host's macro engine spells it (<c>Cmd</c> is Command on macOS, <c>Alt</c> is
/// Option, chords are separated by one space: <c>"Cmd+K Cmd+S"</c>).
/// </summary>
/// <param name="Id">Command id suffix (<c>VSCode.&lt;Id&gt;</c>). Permanent once shipped.</param>
/// <param name="Command">VS Code command id.</param>
/// <param name="ArgsJson">Arguments as a JSON array, or null for none.</param>
/// <param name="Mac">Default shortcut on macOS, or null when there is none.</param>
/// <param name="Windows">Default shortcut on Windows, or null.</param>
/// <param name="Linux">Default shortcut on Linux, or null.</param>
/// <param name="Caption">Text on the key, at most 10 characters.</param>
/// <param name="Glyph">Material Design Icons code point for the key and the picker.</param>
/// <param name="RequiresExtension">Extension id the command belongs to, when it is not built in.</param>
internal sealed record CodeAction(
    string Id,
    string Group,
    string DisplayName,
    string Caption,
    string Command,
    string? ArgsJson,
    string? Mac,
    string? Windows,
    string? Linux,
    string Glyph,
    string Description,
    string? RequiresExtension = null)
{
    public string CommandName => "VSCode." + Id;

    public string? ForThisOs => OperatingSystem.IsMacOS() ? Mac : OperatingSystem.IsWindows() ? Windows : Linux;
}

/// <summary>
/// Every command key the plugin offers. Generated from the research catalogue and curated by hand:
/// command ids checked against the command list of VS Code 1.141.0, fallback shortcuts against its
/// default keybindings, choosing the binding that works with focus in an ordinary editor and leaving
/// the fallback out where the only binding needs focus somewhere narrower (the chat box, a panel).
/// </summary>
internal static class CommandCatalog
{
    public const string Main = "VS Code";
    public const string Files = "VS Code Files";
    public const string Navigate = "VS Code Navigate";
    public const string Edit = "VS Code Edit";
    public const string Search = "VS Code Search";
    public const string View = "VS Code View";
    public const string Terminal = "VS Code Terminal";
    public const string SourceControl = "VS Code Source Control";
    public const string Debug = "VS Code Debug";
    public const string RunTest = "VS Code Run & Test";
    public const string AI = "VS Code AI";
    public const string Accessibility = "VS Code Accessibility";
    public const string Dials = "VS Code Dials";

    public static readonly IReadOnlyList<CodeAction> All =
    [
        // ---- files and editors ----
        new("QuickOpen", Files, "Go to file", "Go to File", "workbench.action.quickOpen", null,
            "Cmd+P", "Ctrl+P", "Ctrl+P", "\U000F0C7D", // mdi-file-search-outline
            "Open Quick Open to jump to any file in the workspace by name (Cmd+P without the companion)."),
        new("NewFile", Files, "New file", "New File", "workbench.action.files.newUntitledFile", null,
            null, null, null, "\U000F0EED", // mdi-file-plus-outline
            "Open a new untitled file (needs the companion)."),
        new("NewExplorerFile", Files, "New file in Explorer", "Add File", "explorer.newFile", null,
            null, null, null, "\U000F1A9E", // mdi-file-document-plus-outline
            "Create a file in the Explorer, next to the selected item (needs the companion)."),
        new("NewExplorerFolder", Files, "New folder in Explorer", "Add Folder", "explorer.newFolder", null,
            null, null, null, "\U000F0B9D", // mdi-folder-plus-outline
            "Create a folder in the Explorer, next to the selected item (needs the companion)."),
        new("OpenRecent", Files, "Open recent", "Recent", "workbench.action.openRecent", null,
            "Ctrl+R", "Ctrl+R", "Ctrl+R", "\U000F02DA", // mdi-history
            "Pick a recently opened folder, workspace or file (Ctrl+R without the companion)."),
        new("Save", Files, "Save", "Save", "workbench.action.files.save", null,
            "Cmd+S", "Ctrl+S", "Ctrl+S", "\U000F0818", // mdi-content-save-outline
            "Save the active file (Cmd+S without the companion)."),
        new("SaveAll", Files, "Save all", "Save All", "workbench.action.files.saveAll", null,
            "Alt+Cmd+S", "Ctrl+K S", null, "\U000F0F44", // mdi-content-save-all-outline
            "Save every file with unsaved changes (Alt+Cmd+S without the companion)."),
        new("CloseEditor", Files, "Close editor", "Close", "workbench.action.closeActiveEditor", null,
            "Cmd+W", "Ctrl+F4", "Ctrl+W", "\U000F0156", // mdi-close
            "Close the active editor tab (Cmd+W without the companion)."),
        new("CloseOtherEditors", Files, "Close other editors", "Close Rest", "workbench.action.closeOtherEditors", null,
            "Alt+Cmd+T", null, null, "\U000F0B4C", // mdi-tab-remove
            "Close every other tab in the active editor group (Alt+Cmd+T without the companion)."),
        new("CloseAllEditors", Files, "Close all editors", "Close All", "workbench.action.closeAllEditors", null,
            "Cmd+K Cmd+W", "Ctrl+K Ctrl+W", "Ctrl+K Ctrl+W", "\U000F0C5E", // mdi-close-box-multiple-outline
            "Close every open editor in every group (Cmd+K Cmd+W without the companion)."),
        new("ReopenClosedEditor", Files, "Reopen closed editor", "Reopen", "workbench.action.reopenClosedEditor", null,
            "Shift+Cmd+T", "Ctrl+Shift+T", "Ctrl+Shift+T", "\U000F075C", // mdi-tab-plus
            "Reopen the editor tab you closed last (Shift+Cmd+T without the companion)."),
        new("RevealInExplorer", Files, "Reveal in Explorer", "Reveal", "workbench.files.action.showActiveFileInExplorer", null,
            null, null, null, "\U000F0645", // mdi-file-tree
            "Show the active file in the Explorer side bar (needs the companion)."),
        new("RevealInOS", Files, "Reveal in Finder or File Explorer", "Show in OS", "workbench.action.files.revealActiveFileInWindows", null,
            "Cmd+K R", "Ctrl+K R", "Ctrl+K R", "\U000F0DCF", // mdi-folder-open-outline
            "Show the active file in Finder, File Explorer or the file manager (Cmd+K R without the companion)."),
        new("CopyFilePath", Files, "Copy path of active file", "Copy Path", "copyFilePath", null,
            "Cmd+K Alt+Cmd+C", "Shift+Alt+C", "Ctrl+K Ctrl+Alt+C", "\U000F018F", // mdi-content-copy
            "Copy the full path of the active file to the clipboard (Cmd+K Alt+Cmd+C without the companion)."),
        new("CompareWithSaved", Files, "Compare with saved", "Diff Saved", "workbench.files.action.compareWithSaved", null,
            "Cmd+K D", "Ctrl+K D", "Ctrl+K D", "\U000F08AA", // mdi-file-compare
            "Compare the active file with its saved copy on disk (Cmd+K D without the companion)."),
        new("ChangeLanguageMode", Files, "Change language mode", "Language", "workbench.action.editor.changeLanguageMode", null,
            "Cmd+K M", "Ctrl+K M", "Ctrl+K M", "\U000F0174", // mdi-code-tags
            "Pick the language mode of the active file (Cmd+K M without the companion)."),

        // ---- getting around ----
        new("CommandPalette", Navigate, "Command Palette", "Commands", "workbench.action.showCommands", null,
            "Shift+Cmd+P", "Ctrl+Shift+P", "Ctrl+Shift+P", "\U000F018D", // mdi-console
            "Open the Command Palette to run any command by name (Shift+Cmd+P without the companion)."),
        new("GoToLine", Navigate, "Go to line", "Go to Line", "workbench.action.gotoLine", null,
            "Ctrl+G", "Ctrl+G", "Ctrl+G", "\U000F027B", // mdi-format-list-numbered
            "Jump to a line number in the active file (Ctrl+G without the companion)."),
        new("GoToSymbol", Navigate, "Go to symbol in editor", "Symbol", "workbench.action.gotoSymbol", null,
            "Shift+Cmd+O", "Ctrl+Shift+O", "Ctrl+Shift+O", "\U000F0169", // mdi-code-braces
            "Jump to a function, class or other symbol in the active file (Shift+Cmd+O without the companion)."),
        new("GoToWorkspaceSymbol", Navigate, "Go to symbol in workspace", "Symbols", "workbench.action.showAllSymbols", null,
            "Cmd+T", "Ctrl+T", "Ctrl+T", "\U000F10D6", // mdi-code-braces-box
            "Search for a symbol across the whole workspace (Cmd+T without the companion)."),
        new("GoToDefinition", Navigate, "Go to definition", "Definition", "editor.action.revealDefinition", null,
            "F12", "F12", "F12", "\U000F0736", // mdi-arrow-right-bold-box-outline
            "Jump to the definition of the symbol at the cursor (F12 without the companion)."),
        new("PeekDefinition", Navigate, "Peek definition", "Peek", "editor.action.peekDefinition", null,
            "Alt+F12", "Alt+F12", "Ctrl+Shift+F10", "\U000F06D0", // mdi-eye-outline
            "Show the definition of the symbol at the cursor inline, without leaving the file (Alt+F12 without the companion)."),
        new("GoToReferences", Navigate, "Go to references", "References", "editor.action.goToReferences", null,
            "Shift+F12", "Shift+F12", "Shift+F12", "\U000F0339", // mdi-link-variant
            "List every reference to the symbol at the cursor (Shift+F12 without the companion)."),
        new("NavigateBack", Navigate, "Go back", "Back", "workbench.action.navigateBack", null,
            "Ctrl+Minus", "Alt+Left", "Ctrl+Alt+Minus", "\U000F004D", // mdi-arrow-left
            "Go back to the previous cursor location (Ctrl+Minus without the companion)."),
        new("NavigateForward", Navigate, "Go forward", "Forward", "workbench.action.navigateForward", null,
            "Ctrl+Shift+Minus", "Alt+Right", "Ctrl+Shift+Minus", "\U000F0054", // mdi-arrow-right
            "Go forward to the next cursor location (Ctrl+Shift+Minus without the companion)."),
        new("NextProblem", Navigate, "Next problem", "Next Issue", "editor.action.marker.nextInFiles", null,
            "F8", "F8", "F8", "\U000F05D6", // mdi-alert-circle-outline
            "Jump to the next error or warning in any file (F8 without the companion)."),
        new("PreviousProblem", Navigate, "Previous problem", "Prev Issue", "editor.action.marker.prevInFiles", null,
            "Shift+F8", "Shift+F8", "Shift+F8", "\U000F05D6", // mdi-alert-circle-outline
            "Jump to the previous error or warning in any file (Shift+F8 without the companion)."),
        new("FocusBreadcrumbs", Navigate, "Focus breadcrumbs", "Breadcrumb", "breadcrumbs.focusAndSelect", null,
            "Shift+Cmd+Period", "Ctrl+Shift+Period", "Ctrl+Shift+Period", "\U000F01D8", // mdi-dots-horizontal
            "Move focus to the breadcrumbs bar above the editor (Shift+Cmd+Period without the companion)."),

        // ---- editing ----
        new("Undo", Edit, "Undo", "Undo", "undo", null,
            "Cmd+Z", "Ctrl+Z", "Ctrl+Z", "\U000F054C", // mdi-undo
            "Undo the last edit (Cmd+Z without the companion)."),
        new("Redo", Edit, "Redo", "Redo", "redo", null,
            "Shift+Cmd+Z", "Ctrl+Y", "Ctrl+Y", "\U000F044E", // mdi-redo
            "Redo the last undone edit (Shift+Cmd+Z without the companion)."),
        new("FormatDocument", Edit, "Format document", "Format", "editor.action.formatDocument", null,
            "Shift+Alt+F", "Shift+Alt+F", "Ctrl+Shift+I", "\U000F0262", // mdi-format-align-left
            "Format the whole active file with its formatter (Shift+Alt+F without the companion)."),
        new("OrganizeImports", Edit, "Organise imports", "Imports", "editor.action.organizeImports", null,
            "Shift+Alt+O", "Shift+Alt+O", "Shift+Alt+O", "\U000F05BD", // mdi-sort-alphabetical-ascending
            "Sort and remove unused imports in the active file (Shift+Alt+O without the companion)."),
        new("QuickFix", Edit, "Quick fix", "Quick Fix", "editor.action.quickFix", null,
            "Cmd+Period", "Ctrl+Period", "Ctrl+Period", "\U000F0336", // mdi-lightbulb-outline
            "Show quick fixes and refactorings for the code at the cursor (Cmd+Period without the companion)."),
        new("RenameSymbol", Edit, "Rename symbol", "Rename", "editor.action.rename", null,
            "F2", "F2", "F2", "\U000F0455", // mdi-rename-box
            "Rename the symbol at the cursor everywhere it is used (F2 without the companion)."),
        new("ToggleLineComment", Edit, "Toggle line comment", "Comment", "editor.action.commentLine", null,
            "Cmd+Slash", "Ctrl+Slash", "Ctrl+Slash", "\U000F0182", // mdi-comment-outline
            "Comment or uncomment the current line or selection (Cmd+Slash without the companion)."),
        new("DuplicateLine", Edit, "Duplicate line", "Duplicate", "editor.action.copyLinesDownAction", null,
            "Shift+Alt+Down", "Shift+Alt+Down", "Ctrl+Shift+Alt+Down", "\U000F0191", // mdi-content-duplicate
            "Copy the current line or selection below itself (Shift+Alt+Down without the companion)."),
        new("DeleteLine", Edit, "Delete line", "Del Line", "editor.action.deleteLines", null,
            "Shift+Cmd+K", "Ctrl+Shift+K", "Ctrl+Shift+K", "\U000F0C62", // mdi-delete-sweep-outline
            "Delete the current line (Shift+Cmd+K without the companion)."),
        new("AddCursorBelow", Edit, "Add cursor below", "Add Cursor", "editor.action.insertCursorBelow", null,
            "Alt+Cmd+Down", "Ctrl+Alt+Down", "Shift+Alt+Down", "\U000F05E7", // mdi-cursor-text
            "Add another cursor on the line below (Alt+Cmd+Down without the companion)."),
        new("SelectNextMatch", Edit, "Select next match", "Next Match", "editor.action.addSelectionToNextFindMatch", null,
            "Cmd+D", "Ctrl+D", "Ctrl+D", "\U000F1285", // mdi-selection-multiple
            "Add the next occurrence of the selection to the selection (Cmd+D without the companion)."),
        new("TriggerSuggest", Edit, "Trigger suggestions", "Suggest", "editor.action.triggerSuggest", null,
            "Ctrl+Space", "Ctrl+Space", "Ctrl+Space", "\U000F0DD0", // mdi-format-list-bulleted-square
            "Show completion suggestions at the cursor (Ctrl+Space without the companion)."),
        new("FoldAll", Edit, "Fold all", "Fold All", "editor.foldAll", null,
            "Cmd+K Cmd+0", "Ctrl+K Ctrl+0", "Ctrl+K Ctrl+0", "\U000F084D", // mdi-arrow-collapse-vertical
            "Fold every region in the active file (Cmd+K Cmd+0 without the companion)."),
        new("UnfoldAll", Edit, "Unfold all", "Unfold All", "editor.unfoldAll", null,
            "Cmd+K Cmd+J", "Ctrl+K Ctrl+J", "Ctrl+K Ctrl+J", "\U000F084F", // mdi-arrow-expand-vertical
            "Unfold every region in the active file (Cmd+K Cmd+J without the companion)."),

        // ---- search ----
        new("Find", Search, "Find", "Find", "actions.find", null,
            "Cmd+F", "Ctrl+F", "Ctrl+F", "\U000F0349", // mdi-magnify
            "Find text in the active file (Cmd+F without the companion)."),
        new("Replace", Search, "Replace", "Replace", "editor.action.startFindReplaceAction", null,
            "Alt+Cmd+F", "Ctrl+H", "Ctrl+H", "\U000F06D4", // mdi-find-replace
            "Find and replace text in the active file (Alt+Cmd+F without the companion)."),
        new("FindInFiles", Search, "Search in files", "Search All", "workbench.action.findInFiles", null,
            "Shift+Cmd+F", "Ctrl+Shift+F", "Ctrl+Shift+F", "\U000F13B8", // mdi-text-search
            "Search for text across the whole workspace (Shift+Cmd+F without the companion)."),
        new("ReplaceInFiles", Search, "Replace in files", "Replace…", "workbench.action.replaceInFiles", null,
            "Shift+Cmd+H", "Ctrl+Shift+H", "Ctrl+Shift+H", "\U000F0B33", // mdi-file-replace-outline
            "Find and replace text across the whole workspace (Shift+Cmd+H without the companion)."),

        // ---- view and layout ----
        new("ToggleSidebar", View, "Toggle primary side bar", "Sidebar", "workbench.action.toggleSidebarVisibility", null,
            "Cmd+B", "Ctrl+B", "Ctrl+B", "\U000F10AA", // mdi-dock-left
            "Show or hide the primary side bar (Cmd+B without the companion)."),
        new("TogglePanel", View, "Toggle panel", "Panel", "workbench.action.togglePanel", null,
            "Cmd+J", "Ctrl+J", "Ctrl+J", "\U000F10A9", // mdi-dock-bottom
            "Show or hide the bottom panel (Cmd+J without the companion)."),
        new("ToggleSecondarySidebar", View, "Toggle secondary side bar", "Sidebar 2", "workbench.action.toggleAuxiliaryBar", null,
            "Alt+Cmd+B", "Ctrl+Alt+B", "Ctrl+Alt+B", "\U000F10AB", // mdi-dock-right
            "Show or hide the secondary side bar (Alt+Cmd+B without the companion)."),
        new("MaximizeEditorGroup", View, "Maximise editor group", "Maximise", "workbench.action.toggleMaximizeEditorGroup", null,
            "Cmd+K Cmd+M", "Ctrl+K Ctrl+M", "Ctrl+K Ctrl+M", "\U000F004C", // mdi-arrow-expand-all
            "Maximise the active editor group, or restore the layout (Cmd+K Cmd+M without the companion)."),
        new("SplitEditorRight", View, "Split editor right", "Split Side", "workbench.action.splitEditorRight", null,
            null, null, null, "\U000F0BCC", // mdi-view-split-vertical
            "Split the active editor into a new group on the right (needs the companion)."),
        new("SplitEditorDown", View, "Split editor down", "Split Down", "workbench.action.splitEditorDown", null,
            null, null, null, "\U000F0BCB", // mdi-view-split-horizontal
            "Split the active editor into a new group below (needs the companion)."),
        new("ToggleZenMode", View, "Toggle Zen mode", "Zen", "workbench.action.toggleZenMode", null,
            "Cmd+K Z", "Ctrl+K Z", "Ctrl+K Z", "\U000F117B", // mdi-meditation
            "Enter or leave Zen mode (Cmd+K Z without the companion)."),
        new("ToggleCenteredLayout", View, "Toggle centred layout", "Centred", "workbench.action.toggleCenteredLayout", null,
            null, null, null, "\U000F0260", // mdi-format-align-center
            "Centre the editor in the window, or restore the full width (needs the companion)."),
        new("ToggleFullScreen", View, "Toggle full screen", "Fullscreen", "workbench.action.toggleFullScreen", null,
            "Ctrl+Cmd+F", "F11", "F11", "\U000F0293", // mdi-fullscreen
            "Enter or leave full screen (Ctrl+Cmd+F without the companion)."),
        new("ToggleWordWrap", View, "Toggle word wrap", "Word Wrap", "editor.action.toggleWordWrap", null,
            "Alt+Z", "Alt+Z", "Alt+Z", "\U000F05B6", // mdi-wrap
            "Turn word wrap on or off in the active editor (Alt+Z without the companion)."),
        new("ToggleStickyScroll", View, "Toggle sticky scroll", "Sticky", "editor.action.toggleStickyScroll", null,
            null, null, null, "\U000F0403", // mdi-pin
            "Turn sticky scroll on or off (needs the companion)."),
        new("ShowExplorer", View, "Show Explorer", "Explorer", "workbench.view.explorer", null,
            "Shift+Cmd+E", "Ctrl+Shift+E", "Ctrl+Shift+E", "\U000F13D2", // mdi-file-tree-outline
            "Show the Explorer in the side bar (Shift+Cmd+E without the companion)."),
        new("ShowProblems", View, "Show Problems panel", "Problems", "workbench.actions.view.problems", null,
            "Shift+Cmd+M", "Ctrl+Shift+M", "Ctrl+Shift+M", "\U000F0CE6", // mdi-alert-octagon-outline
            "Show the Problems panel (Shift+Cmd+M without the companion)."),
        new("ToggleOutput", View, "Toggle Output panel", "Output", "workbench.action.output.toggleOutput", null,
            "Shift+Cmd+U", "Ctrl+Shift+U", "Ctrl+K Ctrl+H", "\U000F09ED", // mdi-text-box-outline
            "Show or hide the Output panel (Shift+Cmd+U without the companion)."),
        new("OpenSettings", View, "Open settings", "Settings", "workbench.action.openSettings", null,
            "Cmd+Comma", "Ctrl+Comma", "Ctrl+Comma", "\U000F08BB", // mdi-cog-outline
            "Open the settings editor (Cmd+Comma without the companion)."),
        new("KeyboardShortcuts", View, "Open keyboard shortcuts", "Shortcuts", "workbench.action.openGlobalKeybindings", null,
            "Cmd+K Cmd+S", "Ctrl+K Ctrl+S", "Ctrl+K Ctrl+S", "\U000F097B", // mdi-keyboard-outline
            "Open the keyboard shortcuts editor (Cmd+K Cmd+S without the companion)."),
        new("ToggleNotifications", View, "Toggle notifications", "Notices", "notifications.toggleList", null,
            null, null, null, "\U000F009C", // mdi-bell-outline
            "Show or hide the notifications list (needs the companion)."),
        new("DoNotDisturb", View, "Toggle Do Not Disturb", "Quiet", "notifications.toggleDoNotDisturbMode", null,
            null, null, null, "\U000F0A91", // mdi-bell-off-outline
            "Turn Do Not Disturb mode for notifications on or off (needs the companion)."),

        // ---- terminal ----
        new("ToggleTerminal", Terminal, "Toggle terminal", "Terminal", "workbench.action.terminal.toggleTerminal", null,
            "Ctrl+Grave", "Ctrl+Grave", "Ctrl+Grave", "\U000F018D", // mdi-console
            "Show or hide the integrated terminal (Ctrl+Grave without the companion)."),
        new("NewTerminal", Terminal, "New terminal", "New Term", "workbench.action.terminal.new", null,
            "Ctrl+Shift+Grave", "Ctrl+Shift+Grave", "Ctrl+Shift+Grave", "\U000F07B7", // mdi-console-line
            "Open a new integrated terminal (Ctrl+Shift+Grave without the companion)."),
        new("SplitTerminal", Terminal, "Split terminal", "Split Term", "workbench.action.terminal.split", null,
            null, null, null, "\U000F0BCC", // mdi-view-split-vertical
            "Split the active terminal (needs the companion)."),
        new("KillTerminal", Terminal, "Kill terminal", "Kill Term", "workbench.action.terminal.kill", null,
            null, null, null, "\U000F015D", // mdi-close-octagon-outline
            "Close the active terminal and end its process (needs the companion)."),
        new("ClearTerminal", Terminal, "Clear terminal", "Clear", "workbench.action.terminal.clear", null,
            null, null, null, "\U000F01FE", // mdi-eraser
            "Clear the active terminal (needs the companion)."),
        new("RunRecentCommand", Terminal, "Run recent command", "Recent Cmd", "workbench.action.terminal.runRecentCommand", null,
            null, null, null, "\U000F02DA", // mdi-history
            "Pick a recent command to run again in the terminal (needs the companion)."),
        new("RunSelectedText", Terminal, "Run selected text", "Run Select", "workbench.action.terminal.runSelectedText", null,
            null, null, null, "\U000F040B", // mdi-play-box-outline
            "Run the selected text, or the current line, in the active terminal (needs the companion)."),
        new("InterruptTerminal", Terminal, "Interrupt terminal", "Interrupt", "workbench.action.terminal.sendSequence", "[{\"text\":\"\\u0003\"}]",
            "Ctrl+C", "Ctrl+C", "Ctrl+C", "\U000F182C", // mdi-hand-back-left-outline
            "Stop the command running in the active terminal, as Ctrl+C does with the terminal focused (Ctrl+C without the companion)."),

        // ---- source control ----
        new("ShowSourceControl", SourceControl, "Show Source Control", "Source", "workbench.view.scm", null,
            "Ctrl+Shift+G", "Ctrl+Shift+G", "Ctrl+Shift+G", "\U000F062C", // mdi-source-branch
            "Show the Source Control view (Ctrl+Shift+G without the companion)."),
        new("GitCommit", SourceControl, "Git: commit", "Commit", "git.commit", null,
            null, null, null, "\U000F0718", // mdi-source-commit
            "Commit the staged changes, or all changes when nothing is staged (needs the companion)."),
        new("GitStageAll", SourceControl, "Git: stage all changes", "Stage All", "git.stageAll", null,
            null, null, null, "\U000F1143", // mdi-plus-box-multiple-outline
            "Stage every change in the repository (needs the companion)."),
        new("GitSync", SourceControl, "Git: sync", "Sync", "git.sync", null,
            null, null, null, "\U000F04E6", // mdi-sync
            "Pull and then push the current branch (needs the companion)."),
        new("GitPull", SourceControl, "Git: pull", "Pull", "git.pull", null,
            null, null, null, "\U000F0048", // mdi-arrow-down-bold-circle-outline
            "Pull from the remote (needs the companion)."),
        new("GitPush", SourceControl, "Git: push", "Push", "git.push", null,
            null, null, null, "\U000F0060", // mdi-arrow-up-bold-circle-outline
            "Push the current branch to the remote (needs the companion)."),
        new("GitCheckout", SourceControl, "Git: check out branch", "Branch", "git.checkout", null,
            null, null, null, "\U000F14CF", // mdi-source-branch-check
            "Pick a branch or tag to check out (needs the companion)."),
        new("GitUndoCommit", SourceControl, "Git: undo last commit", "Uncommit", "git.undoCommit", null,
            null, null, null, "\U000F054D", // mdi-undo-variant
            "Undo the last commit and keep its changes (needs the companion)."),
        new("GitOpenChanges", SourceControl, "Git: open changes", "Open Diff", "git.openChange", null,
            null, null, null, "\U000F08AA", // mdi-file-compare
            "Open a diff of the active file against the last commit (needs the companion)."),
        new("PeekChange", SourceControl, "Peek next change", "Peek Diff", "editor.action.dirtydiff.next", null,
            "Alt+F3", "Alt+F3", "Alt+F3", "\U000F0D05", // mdi-eye-check-outline
            "Show the next changed block inline in the editor (Alt+F3 without the companion)."),
        new("ShowGraph", SourceControl, "Show source control graph", "Graph", "workbench.scm.history.focus", null,
            null, null, null, "\U000F104A", // mdi-graph-outline
            "Show the source control graph of commits (needs the companion)."),

        // ---- debugging ----
        new("StartDebugging", Debug, "Start debugging", "Debug", "workbench.action.debug.start", null,
            "F5", "F5", "F5", "\U000F1AF8", // mdi-bug-play-outline
            "Start debugging with the selected launch configuration (F5 without the companion)."),
        new("RunWithoutDebugging", Debug, "Run without debugging", "Run", "workbench.action.debug.run", null,
            "Ctrl+F5", "Ctrl+F5", "Ctrl+F5", "\U000F040A", // mdi-play
            "Run the selected launch configuration without the debugger (Ctrl+F5 without the companion)."),
        new("SelectAndStartDebugging", Debug, "Select and start debugging", "Pick Debug", "workbench.action.debug.selectandstart", null,
            null, null, null, "\U000F0A2F", // mdi-bug-check-outline
            "Pick a launch configuration and start debugging (needs the companion)."),
        new("DebugContinue", Debug, "Continue", "Continue", "workbench.action.debug.continue", null,
            "F5", "F5", "F5", "\U000F040D", // mdi-play-circle-outline
            "Continue running the paused program (F5 without the companion)."),
        new("DebugPause", Debug, "Pause", "Pause", "workbench.action.debug.pause", null,
            "F6", "F6", "F6", "\U000F03E4", // mdi-pause
            "Pause the running program (F6 without the companion)."),
        new("DebugStepOver", Debug, "Step over", "Step Over", "workbench.action.debug.stepOver", null,
            "F10", "F10", "F10", "\U000F01B7", // mdi-debug-step-over
            "Run the current line and stop at the next one (F10 without the companion)."),
        new("DebugStepInto", Debug, "Step into", "Step Into", "workbench.action.debug.stepInto", null,
            "F11", "F11", "F11", "\U000F01B9", // mdi-debug-step-into
            "Step into the function called on the current line (F11 without the companion)."),
        new("DebugStepOut", Debug, "Step out", "Step Out", "workbench.action.debug.stepOut", null,
            "Shift+F11", "Shift+F11", "Shift+F11", "\U000F01B8", // mdi-debug-step-out
            "Run to the end of the current function and stop in its caller (Shift+F11 without the companion)."),
        new("DebugRestart", Debug, "Restart debugging", "Restart", "workbench.action.debug.restart", null,
            "Shift+Cmd+F5", "Ctrl+Shift+F5", "Ctrl+Shift+F5", "\U000F0709", // mdi-restart
            "Restart the debug session (Shift+Cmd+F5 without the companion)."),
        new("DebugStop", Debug, "Stop debugging", "Stop", "workbench.action.debug.stop", null,
            "Shift+F5", "Shift+F5", "Shift+F5", "\U000F04DB", // mdi-stop
            "Stop the debug session (Shift+F5 without the companion)."),
        new("ToggleBreakpoint", Debug, "Toggle breakpoint", "Breakpoint", "editor.debug.action.toggleBreakpoint", null,
            "F9", "F9", "F9", "\U000F012F", // mdi-checkbox-blank-circle
            "Set or remove a breakpoint on the current line (F9 without the companion)."),
        new("RunToCursor", Debug, "Run to cursor", "To Cursor", "editor.debug.action.runToCursor", null,
            null, null, null, "\U000F0CFE", // mdi-cursor-default-click-outline
            "Continue running until the line with the cursor (needs the companion)."),
        new("ToggleDebugConsole", Debug, "Toggle Debug Console", "Console", "workbench.debug.action.toggleRepl", null,
            null, null, null, "\U000F0C60", // mdi-console-network-outline
            "Show or hide the Debug Console (needs the companion)."),

        // ---- tasks and tests ----
        new("PickTask", RunTest, "Run task…", "Run Task", "workbench.action.tasks.runTask", null,
            null, null, null, "\U000F0C5A", // mdi-clipboard-play-outline
            "Pick a task to run (needs the companion)."),
        new("RunBuildTask", RunTest, "Run build task", "Build", "workbench.action.tasks.build", null,
            "Shift+Cmd+B", "Ctrl+Shift+B", "Ctrl+Shift+B", "\U000F08EA", // mdi-hammer
            "Run the default build task (Shift+Cmd+B without the companion)."),
        new("RerunTask", RunTest, "Rerun last task", "Rerun Task", "workbench.action.tasks.reRunTask", null,
            null, null, null, "\U000F0459", // mdi-replay
            "Run the last task again (needs the companion)."),
        new("TerminateTask", RunTest, "Terminate task", "Stop Task", "workbench.action.tasks.terminate", null,
            null, null, null, "\U000F0667", // mdi-stop-circle-outline
            "Stop a running task (needs the companion)."),
        new("RunAllTests", RunTest, "Run all tests", "All Tests", "testing.runAll", null,
            "Cmd+Semicolon A", "Ctrl+Semicolon A", "Ctrl+Semicolon A", "\U000F0668", // mdi-test-tube
            "Run every test in the workspace (Cmd+Semicolon A without the companion)."),
        new("RunTestAtCursor", RunTest, "Run test at cursor", "Test Here", "testing.runAtCursor", null,
            "Cmd+Semicolon C", "Ctrl+Semicolon C", "Ctrl+Semicolon C", "\U000F0096", // mdi-flask-outline
            "Run the test at the cursor (Cmd+Semicolon C without the companion)."),
        new("DebugTestAtCursor", RunTest, "Debug test at cursor", "Debug Test", "testing.debugAtCursor", null,
            "Cmd+Semicolon Cmd+C", "Ctrl+Semicolon Ctrl+C", "Ctrl+Semicolon Ctrl+C", "\U000F0A30", // mdi-bug-outline
            "Debug the test at the cursor (Cmd+Semicolon Cmd+C without the companion)."),
        new("RunTestsInFile", RunTest, "Run tests in file", "Test File", "testing.runCurrentFile", null,
            "Cmd+Semicolon F", "Ctrl+Semicolon F", "Ctrl+Semicolon F", "\U000F0E29", // mdi-file-check-outline
            "Run every test in the active file (Cmd+Semicolon F without the companion)."),
        new("RerunLastTestRun", RunTest, "Rerun last test run", "Rerun Test", "testing.reRunLastRun", null,
            "Cmd+Semicolon L", "Ctrl+Semicolon L", "Ctrl+Semicolon L", "\U000F124E", // mdi-flask-round-bottom-outline
            "Run the last test run again (Cmd+Semicolon L without the companion)."),
        new("ShowTestOutput", RunTest, "Show test output", "Test Log", "testing.showMostRecentOutput", null,
            "Cmd+Semicolon Cmd+O", "Ctrl+Semicolon Ctrl+O", "Ctrl+Semicolon Ctrl+O", "\U000F0EA7", // mdi-text-box-check-outline
            "Show the output of the most recent test run (Cmd+Semicolon Cmd+O without the companion)."),

        // ---- AI and chat ----
        new("OpenChat", AI, "Open chat", "Chat", "workbench.action.chat.open", null,
            "Ctrl+Cmd+I", "Ctrl+Alt+I", "Ctrl+Alt+I", "\U000F0EDE", // mdi-chat-outline
            "Open the chat view (Ctrl+Cmd+I without the companion)."),
        new("NewChat", AI, "New chat", "New Chat", "workbench.action.chat.newChat", null,
            null, null, null, "\U000F1412", // mdi-chat-plus-outline
            "Start a new chat session (needs the companion)."),
        new("AgentMode", AI, "Open chat in agent mode", "Agent", "workbench.action.chat.openagent", null,
            "Shift+Cmd+I", "Ctrl+Shift+I", "Ctrl+Shift+Alt+I", "\U000F167A", // mdi-robot-outline
            "Open the chat view in agent mode (Shift+Cmd+I without the companion)."),
        new("InlineChat", AI, "Inline chat", "Inline AI", "inlineChat.start", null,
            "Cmd+I", "Ctrl+I", "Ctrl+I", "\U000F0674", // mdi-creation
            "Start an inline chat at the cursor in the editor (Cmd+I without the companion)."),
        new("QuickChat", AI, "Quick chat", "Quick Chat", "workbench.action.quickchat.toggle", null,
            "Shift+Alt+Cmd+L", "Ctrl+Shift+Alt+L", "Ctrl+Shift+Alt+L", "\U000F15AA", // mdi-message-flash-outline
            "Show or hide the quick chat box (Shift+Alt+Cmd+L without the companion)."),
        new("AttachContext", AI, "Attach context to chat", "Attach", "workbench.action.chat.attachContext", null,
            null, null, null, "\U000F03E2", // mdi-paperclip
            "Pick files, symbols or other context to add to the chat (needs the companion)."),
        new("ChatModelPicker", AI, "Pick chat model", "Model", "workbench.action.chat.openModelPicker", null,
            null, null, null, "\U000F09D1", // mdi-brain
            "Pick the language model the chat uses (needs the companion)."),
        new("TriggerInlineSuggestion", AI, "Trigger inline suggestion", "AI Suggest", "editor.action.inlineSuggest.trigger", null,
            null, null, null, "\U000F1801", // mdi-lightbulb-auto-outline
            "Ask for an inline AI suggestion at the cursor (needs the companion)."),
        new("VoiceChat", AI, "Start voice chat", "Voice", "workbench.action.chat.startVoiceChat", null,
            null, null, null, "\U000F036E", // mdi-microphone-outline
            "Start a chat by voice (needs the companion). Needs the VS Code Speech extension.", "ms-vscode.vscode-speech"),
        new("EditorDictation", AI, "Start dictation in editor", "Dictate", "workbench.action.editorDictation.start", null,
            "Alt+Cmd+V", "Ctrl+Alt+V", "Ctrl+Alt+V", "\U000F050A", // mdi-microphone-message
            "Dictate text into the editor at the cursor (Alt+Cmd+V without the companion). Needs the VS Code Speech extension.", "ms-vscode.vscode-speech"),
        new("ClaudeOpen", AI, "Claude Code: open", "Claude", "claude-vscode.editor.openLast", null,
            null, null, null, "\U000F171A", // mdi-robot-happy-outline
            "Open the last Claude Code session in an editor tab (needs the companion). Needs the Claude Code extension.", "anthropic.claude-code"),
        new("ClaudeNewConversation", AI, "Claude Code: new conversation", "New Claude", "claude-vscode.newConversation", null,
            null, null, null, "\U000F140F", // mdi-chat-plus
            "Start a new Claude Code conversation (needs the companion). Needs the Claude Code extension.", "anthropic.claude-code"),
        new("ClaudeFocusInput", AI, "Claude Code: focus input", "Ask Claude", "claude-vscode.focus", null,
            "Cmd+Esc", "Ctrl+Esc", "Ctrl+Esc", "\U000F060E", // mdi-form-textbox
            "Move focus to the Claude Code prompt box (Cmd+Esc without the companion). Needs the Claude Code extension.", "anthropic.claude-code"),
        new("ClaudeInsertMention", AI, "Claude Code: insert @-mention", "@ Mention", "claude-vscode.insertAtMention", null,
            null, null, null, "\U000F0065", // mdi-at
            "Insert an @-mention of the active file and selection into the Claude Code prompt (needs the companion). Needs the Claude Code extension.", "anthropic.claude-code"),
        new("ClaudeAcceptDiff", AI, "Claude Code: accept proposed change", "Accept", "claude-vscode.acceptProposedDiff", null,
            null, null, null, "\U000F05E1", // mdi-check-circle-outline
            "Accept the change Claude Code proposed in the diff (needs the companion). Needs the Claude Code extension.", "anthropic.claude-code"),
        new("ClaudeRejectDiff", AI, "Claude Code: reject proposed change", "Reject", "claude-vscode.rejectProposedDiff", null,
            null, null, null, "\U000F015A", // mdi-close-circle-outline
            "Reject the change Claude Code proposed in the diff (needs the companion). Needs the Claude Code extension.", "anthropic.claude-code"),

        // ---- accessibility ----
        new("AccessibleView", Accessibility, "Open Accessible View", "Accessible", "editor.action.accessibleView", null,
            "Alt+F2", "Alt+F2", "Shift+Alt+F2", "\U000F1900", // mdi-eye-arrow-right-outline
            "Open the Accessible View of the focused item, for reading with a screen reader (Alt+F2 without the companion)."),
        new("AccessibilityHelp", Accessibility, "Accessibility help", "Acc. Help", "editor.action.accessibilityHelp", null,
            "Alt+F1", "Alt+F1", "Shift+Alt+F1", "\U000F0625", // mdi-help-circle-outline
            "Open accessibility help for the focused part of the window (Alt+F1 without the companion)."),
        new("ScreenReaderMode", Accessibility, "Toggle screen reader mode", "Reader", "editor.action.toggleScreenReaderAccessibilityMode", null,
            "Shift+Alt+F1", "Shift+Alt+F1", "Shift+Alt+F4", "\U000F05CB", // mdi-account-voice
            "Turn screen reader optimised mode on or off (Shift+Alt+F1 without the companion)."),
        new("AccessibilitySignals", Accessibility, "Accessibility signal sounds", "Sounds", "signals.sounds.help", null,
            null, null, null, "\U000F057E", // mdi-volume-high
            "Choose which accessibility signals play a sound (needs the companion)."),
        new("AccessibilityAnnouncements", Accessibility, "Accessibility announcements", "Announce", "accessibility.announcement.help", null,
            null, null, null, "\U000F0B23", // mdi-bullhorn-outline
            "Choose which accessibility signals a screen reader announces (needs the companion)."),
        new("ShowHover", Accessibility, "Show hover", "Hover", "editor.action.showHover", null,
            "Cmd+K Cmd+I", "Ctrl+K Ctrl+I", "Ctrl+K Ctrl+I", "\U000F0BD7", // mdi-tooltip-text-outline
            "Show the hover information for the symbol at the cursor (Cmd+K Cmd+I without the companion)."),
        new("AccessibleDiffNext", Accessibility, "Accessible diff: next change", "Diff Next", "editor.action.accessibleDiffViewer.next", null,
            "F7", "F7", "F7", "\U000F08AA", // mdi-file-compare
            "Open the accessible diff viewer at the next change of the diff editor (F7 without the companion)."),
        new("TerminalAccessibleBuffer", Accessibility, "Focus terminal accessible buffer", "Term Text", "workbench.action.terminal.focusAccessibleBuffer", null,
            null, null, null, "\U000F07B7", // mdi-console-line
            "Move focus to the accessible buffer of the terminal, for reading its output with a screen reader (needs the companion)."),
        new("FocusEditor", Accessibility, "Focus active editor", "To Editor", "workbench.action.focusActiveEditorGroup", null,
            null, null, null, "\U000F0DC9", // mdi-file-document-edit-outline
            "Move keyboard focus to the active editor (needs the companion)."),
        new("FocusSideBar", Accessibility, "Focus side bar", "To Sidebar", "workbench.action.focusSideBar", null,
            "Cmd+0", "Ctrl+0", "Ctrl+0", "\U000F10AA", // mdi-dock-left
            "Move keyboard focus to the primary side bar (Cmd+0 without the companion)."),
        new("FocusPanel", Accessibility, "Focus panel", "To Panel", "workbench.action.focusPanel", null,
            null, null, null, "\U000F10A9", // mdi-dock-bottom
            "Move keyboard focus to the bottom panel (needs the companion)."),
        new("FocusStatusBar", Accessibility, "Focus status bar", "To Status", "workbench.action.focusStatusBar", null,
            null, null, null, "\U000F10AC", // mdi-dock-window
            "Move keyboard focus to the status bar (needs the companion)."),
        new("FocusNotifications", Accessibility, "Focus notifications", "To Notices", "notifications.focusToasts", null,
            null, null, null, "\U000F0A04", // mdi-message-alert-outline
            "Move keyboard focus to the notification toasts (needs the companion)."),
        new("ZoomReset", Accessibility, "Reset zoom", "Zoom 100%", "workbench.action.zoomReset", null,
            "Cmd+Num0", "Ctrl+Num0", "Ctrl+Num0", "\U000F0980", // mdi-magnify-close
            "Reset the window zoom to 100 per cent (Cmd+Num0 without the companion)."),
        new("KeyboardReference", Accessibility, "Keyboard shortcuts reference", "Key Ref", "workbench.action.keybindingsReference", null,
            null, null, null, "\U000F09FA", // mdi-keyboard-settings-outline
            "Open the printable keyboard shortcuts reference (needs the companion)."),
        new("AccessibilitySettings", Accessibility, "Accessibility settings", "Acc. Prefs", "workbench.action.openAccessibilitySettings", null,
            null, null, null, "\U000F02E6", // mdi-human
            "Open the settings editor at the accessibility settings (needs the companion)."),
        new("AccessibilityWalkthrough", Accessibility, "Accessibility walkthrough", "Acc. Tour", "workbench.action.getStartedWithAccessibilityFeatures", null,
            null, null, null, "\U000F1180", // mdi-school-outline
            "Open the built-in walkthrough of the accessibility features (needs the companion).")
    ];
}

# The LoupixDeck Companion extension

The **LoupixDeck Companion** (`web-engineer.loupixdeck-companion`) is the VS Code side of the
Visual Studio Code plugin. Every VS Code window runs its own copy. It runs the commands the deck
sends, by ID, pushes the window's state (problems, git, debugger, tasks, terminal, active file,
unsaved files, screen reader mode) and answers the deck's folders. Without it, only keys that
have a default shortcut work.

The source is in [`extension/`](../extension/), and the protocol between the two sides is in
[PROTOCOL.md](PROTOCOL.md).

## Installing

The plugin ships the extension as `loupixdeck-companion.vsix` in its folder
(`~/.config/LoupixDeck/plugins/vscode/` on macOS and Linux, `%APPDATA%\LoupixDeck\plugins\vscode\`
on Windows).

**From the plugin's settings page.** Open LoupixDeck → Plugins → Visual Studio Code → Settings
and press **Install companion in *editor***. There is one button for each editor the plugin
finds: Visual Studio Code, VS Code Insiders, VSCodium, Cursor and Windsurf. The button runs the
editor's command-line launcher with `--install-extension … --force` and reports what it said.

**From a terminal.**

```bash
code --install-extension ~/.config/LoupixDeck/plugins/vscode/loupixdeck-companion.vsix
```

Use `code-insiders`, `codium`, `cursor` or `windsurf` for the other editors. On macOS, if
`code` is not on your PATH, run **Shell Command: Install 'code' command in PATH** in VS Code
first, or use the launcher inside the app:
`"/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code"`.

**From the editor.** Run **Extensions: Install from VSIX…** and pick the file.

Open windows start the companion within a few seconds. After an **update**, reload each open
window (**Developer: Reload Window**) so it runs the new version.

### Profiles

`--install-extension` (and so the settings page button) installs into VS Code's **default
profile**. If you work in another profile, the companion is not active there: switch to that
profile and install it with **Extensions: Install from VSIX…**, or use
`code --profile "<name>" --install-extension …`. You can also apply the extension to all
profiles from its entry in the Extensions view.

## Checking that it works

- **Status bar.** Each window shows **Deck** at the bottom: with a plug icon while the deck is
  connected, with a crossed-out plug icon while it is not. Click it for the connection status.
- **LoupixDeck: Show Connection Status** (Command Palette) shows the window's socket, the
  connected decks and the last command the deck ran.
- **LoupixDeck: Show Log** opens the **LoupixDeck** output channel, which lists every command
  the deck ran and every connection.
- In LoupixDeck, the plugin's settings page lists the connected windows, and **Check connection**
  looks for them again.
- The `VSCode.Window` key shows the window keys act on.

## Settings

| Setting | Default | What it does |
|---|---|---|
| `loupixdeck.enabled` | `true` | Runs the local server. Turn it off to disconnect this editor from the deck |
| `loupixdeck.commandPolicy` | `any` | `allowList` refuses every command not in `loupixdeck.allowedCommands`. Running tasks and launch configurations from the deck's folders stays allowed. User settings only |
| `loupixdeck.allowedCommands` | `[]` | Command IDs the deck may run under `allowList`, for example `workbench.action.files.save`. User settings only |
| `loupixdeck.pushDiagnostics` | `true` | Pushes problem counts as they change. Turn it off in very large workspaces with constant diagnostics churn; the counts are then read only when the deck connects |

Commands:

- **LoupixDeck: Show Connection Status**
- **LoupixDeck: Show Log**
- **LoupixDeck: Restart Server**: starts the server again with a new token; the deck reconnects
  by itself.

The context key `loupixdeck.connected` is true while a deck is connected, for your own
keybindings and `when` clauses.

## How the deck finds a window

Each window writes a registration file, `<pid>.json`, into the registration directory and
listens on a socket next to it:

| OS | Registration directory | Socket |
|---|---|---|
| macOS, Linux | `~/.config/LoupixDeck/vscode/` (mode `0700`) | `<pid>.sock` in the same directory, or `/tmp/loupixdeck-vscode-<uid>/<pid>.sock` when that path is too long for a socket |
| Windows | `%APPDATA%\LoupixDeck\vscode\` | a named pipe, `\\.\pipe\loupixdeck-vscode-<pid>-…` |

The file holds the socket's path, the editor, the workspace and a random token, and only you can
read it (mode `0600`). The plugin watches the directory, checks it again every 3 seconds, and
keeps one connection open to every window. A window removes its file when it closes; a file left
behind by a crash is removed by the plugin or by the next window that starts.

Nothing listens on a network port. Every connection has to present the window's token first.

## Troubleshooting

**The status bar says Deck with a crossed-out plug.** The window runs the companion but the
deck is not connected. Check that LoupixDeck is running with the Visual Studio Code plugin
enabled, then press **Check connection** on the plugin's settings page.

**There is no Deck item in the status bar.** The companion is not running in this window. Check
that it is installed and enabled in the Extensions view (in this profile, see
[Profiles](#profiles)), that `loupixdeck.enabled` is on, and reload the window.

**Live keys say `No companion`.** An editor is running but no window is connected: install the
companion, or reload the window. `Not running` means no editor is open; `Update plugin` means a
window runs a newer companion than the plugin understands.

**A key says `Needs companion`.** The command has no default shortcut to fall back on, so it
needs a connected window.

**A key says `Needs extension` or `Needs Claude Code`.** The command belongs to an extension
that is not installed or not active in that window.

**A key says `Not allowed`.** `loupixdeck.commandPolicy` is `allowList` and the command is not in
`loupixdeck.allowedCommands`.

**Keys act on the wrong window.** Keys act on the window in front, else on the one you used
last. The `VSCode.Window` key shows which one that is, and the `VSCode.Windows` folder lets you
pick another.

**Nothing happens in a remote window.** The companion runs on your computer, not on the remote
side, so it should connect. If it does not, check that it is installed locally (the Extensions
view lists it under *Local*), not only on the remote host.

**Still stuck.** Run **LoupixDeck: Restart Server**, look at **LoupixDeck: Show Log**, and check
the registration directory above for a `<pid>.json` matching the window. Then
[open an issue](https://github.com/web-engineer/LoupixDeck.Plugin.VSCode/issues) with what you
found.

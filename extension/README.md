# LoupixDeck Companion

The editor side of the [Visual Studio Code plugin](https://github.com/web-engineer/LoupixDeck.Plugin.VSCode)
for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck), the open-source control software for
Loupedeck-style decks. With the companion installed, the deck:

- runs any VS Code command by its ID, so keys keep working when you rebind a shortcut, use another
  keyboard layout or switch to a fork such as Cursor or VSCodium;
- shows live state on its keys: errors and warnings, the git branch and changes, whether the
  debugger is running or paused, unsaved files, the current file and cursor position, the last
  task's result, the last terminal command's exit code and the screen reader mode;
- opens folders of live content: tasks, launch configurations, problems, open editors, recent
  folders and terminals, one per key, and runs or opens the one you press;
- works with every open window, and acts on the one you used last.

The extension does nothing on its own. Install the LoupixDeck plugin, then install the companion
from the plugin's settings page (or from the marketplace). The status bar shows **Deck** with a
plug icon while the deck is connected.

Made by [**web-engineer**](https://www.web-engineer.co.uk/)
([@web-engineer](https://github.com/web-engineer)). This is an independent project and is not
affiliated with or endorsed by Microsoft or Loupedeck.

## Security

The deck can run any command, and some commands run code (tasks, terminals), so the companion
only accepts connections from your own user account:

- Each window listens on a **Unix domain socket** in a folder only you can open
  (`~/.config/LoupixDeck/vscode/`, mode `0700`) or, on Windows, on a **named pipe**. Nothing
  listens on a network port, so web pages and other computers cannot reach it.
- Every connection must start with a **random 32-byte token**, new each time the window starts.
  The token is in a registration file only you can read (mode `0600`). The companion checks it
  in constant time and closes the connection on a wrong one.
- Any program running as you can read that file, just as it could already run anything else as
  you. If you want to limit what the deck may do, set `loupixdeck.commandPolicy` to `allowList`
  and list the command IDs in `loupixdeck.allowedCommands`. Both are read from your user settings
  only, so a folder's `.vscode/settings.json` cannot loosen them.
- Every command the deck runs is written to the **LoupixDeck** output channel
  (*LoupixDeck: Show Log*). The token is never logged.

The companion runs in Restricted Mode too. It never runs workspace code by itself. Commands the
deck sends still follow VS Code's workspace trust rules, so for example tasks stay blocked in an
untrusted folder.

## Privacy

Nothing leaves your computer. The companion has no telemetry and makes no network requests. It
talks only to the LoupixDeck plugin over the local socket, and sends it the state listed above:
file names and paths, cursor position, problem counts and messages, the git branch and change
counts, task, launch configuration and terminal names, and recent folder paths.

## Settings

| Setting | Default | What it does |
|---|---|---|
| `loupixdeck.enabled` | `true` | Runs the local server. Turn it off to disconnect this editor from the deck |
| `loupixdeck.commandPolicy` | `any` | `allowList` refuses every command not in `loupixdeck.allowedCommands`. Running tasks and launch configurations from the deck's folders stays allowed. User settings only |
| `loupixdeck.allowedCommands` | `[]` | Command IDs the deck may run under `allowList`, for example `workbench.action.files.save`. User settings only |
| `loupixdeck.pushDiagnostics` | `true` | Pushes problem counts as they change. Turn it off in very large workspaces with constant diagnostics churn |

## Commands

- **LoupixDeck: Show Connection Status**: the server's socket, the connected decks and the last
  command run. Also opens from the **Deck** status bar item.
- **LoupixDeck: Show Log**: the output channel.
- **LoupixDeck: Restart Server**: starts the server again with a new token; the deck reconnects
  by itself.

The context key `loupixdeck.connected` is true while a deck is connected, for your own
keybindings and `when` clauses.

## Remote windows

The companion is a UI extension: it runs on your computer even in SSH, container and WSL windows,
so the deck can reach it. In those windows the git state comes from the git extension's commands,
polled every 2 seconds while the window has focus and every 10 seconds otherwise.

## Requirements

VS Code 1.93 or later, or a fork based on it. Bringing a window to the front from the deck needs
VS Code 1.128 or later; older versions bring back the app's last window instead.

## Licence

MIT. See [LICENSE](LICENSE).

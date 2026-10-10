# Changelog

## 0.1.0

First release.

- One local server per window (Unix socket or named pipe) with a token handshake, registered in
  `~/.config/LoupixDeck/vscode/` (`%APPDATA%\LoupixDeck\vscode\` on Windows).
- Runs any command by ID for the LoupixDeck plugin, with an optional allow-list.
- Pushes live state: window focus, workspace, active editor and cursor, unsaved files, problem
  counts, git, debug, tasks, terminal exit codes and accessibility settings.
- Lists tasks, launch configurations, problems, open editors, recent folders and terminals, and
  runs the one the deck picks.
- Status bar item, connection status quick pick, log channel and the `loupixdeck.connected`
  context key.

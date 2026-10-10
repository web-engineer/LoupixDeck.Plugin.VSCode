# Companion bridge protocol (version 1)

The LoupixDeck plugin talks to the **LoupixDeck Companion** VS Code extension over a local,
per-user channel. This page is the contract between the two sides: `extension/extension.js`
implements the server, `Bridge/` in the plugin implements the client.

## Discovery

Every VS Code window runs its own extension host, so every window runs its own server and
registers itself with a file in the **registration directory**:

| OS | Directory |
|---|---|
| macOS, Linux | `~/.config/LoupixDeck/vscode/` |
| Windows | `%APPDATA%\LoupixDeck\vscode\` |

The environment variable `LOUPIXDECK_VSCODE_DIR` overrides the directory on both sides (tests,
isolated instances). The extension creates the directory with mode `0700` (and `chmod`s it, since
umask applies) and writes `<pid>.json` with mode `0600`, atomically (write `<pid>.json.tmp`, then
rename). `pid` is the extension host's `process.pid`.

```json
{
  "v": 1,
  "pid": 41234,
  "socket": "/Users/me/.config/LoupixDeck/vscode/41234.sock",
  "token": "base64url, 32 random bytes",
  "app": "Visual Studio Code",
  "uriScheme": "vscode",
  "appRoot": "/Applications/Visual Studio Code.app/Contents/Resources/app",
  "version": "1.141.0",
  "remoteName": null,
  "workspaceName": "LoupixDeck",
  "folders": ["file:///Users/me/Documents/LoupixDeck"],
  "extVersion": "0.1.0",
  "startedAt": 1791650000000
}
```

- `socket` is a Unix domain socket path on macOS and Linux, and a named pipe
  (`\\.\pipe\loupixdeck-vscode-<pid>-<8 hex>`) on Windows. On Unix the socket lives in the
  registration directory as `<pid>.sock`. When that path exceeds 100 bytes the extension uses
  `/tmp/loupixdeck-vscode-<uid>/<pid>.sock` instead (directory mode `0700`).
- The extension deletes its file and socket in `deactivate()`. A crash leaves them behind, so
  the client treats an entry as stale when its pid is not alive or the connection is refused, and
  deletes the file. The extension also removes stale entries (dead pid) when it starts.
- The client watches the directory and rescans it every 3 s, because file watchers miss events.
  It keeps one connection open to every live window.

## Framing

UTF-8 JSON, one object per line (`\n`), in both directions. Lines longer than 1 MiB are an
error and close the connection.

## Handshake

The first line from the client must be `hello`. Anything else, or a wrong token, closes the
connection. The extension compares tokens with `crypto.timingSafeEqual`.

```json
{"id":1,"op":"hello","token":"…","client":"LoupixDeck.Plugin.VSCode/0.1.0","protocol":1}
```

The reply carries the window's identity and a full state snapshot:

```json
{"id":1,"ok":true,"result":{"protocol":1,"pid":41234,"app":"Visual Studio Code","version":"1.141.0",
 "uriScheme":"vscode","extVersion":"0.1.0","focusWindow":true,"state":{ …full State… }}}
```

`focusWindow` says whether the window has the `workbench.action.focusWindow` command
(VS Code 1.128 and later).

## Requests

Every request has a client-chosen integer `id` and an `op`. Every reply echoes the `id`:
either `{"id":n,"ok":true,"result":…}` or `{"id":n,"ok":false,"error":{"code":"…","message":"…"}}`.

Error codes: `bad_request`, `unknown_op`, `unknown_command` (the command id is not registered,
for example an extension that is not installed), `not_found` (task, configuration or item),
`failed` (the command threw), `denied` (the user's command policy refused it), `busy`.

| op | Fields | Result |
|---|---|---|
| `hello` | `token`, `client`, `protocol` | see above |
| `exec` | `command` (string), `args` (array, optional), `focus` (bool, optional) | the command's return value, made JSON-safe (Uri → `{"$uri":"…"}`, functions and cycles dropped, `null` when it cannot be serialised) |
| `list` | `what`: `tasks` \| `launch` \| `problems` \| `editors` \| `recent` \| `terminals` | `{"items":[Item…]}` |
| `run` | `what` (as `list`), `ref` (an item's `ref`), `focus` (bool, optional) | `null` |
| `runTask` | `name` (task label), `focus` | `null`, or `not_found` |
| `startDebug` | `name` (configuration or compound name), `focus` | `null`, or `not_found` |
| `focus` | – | `null`: brings this window to the front |
| `state` | – | a full State snapshot |
| `ping` | – | `"pong"` |

**Arguments.** `args` items are plain JSON. An object of the form `{"$uri":"file:///…"}` is
turned into a `vscode.Uri` before the call, at any depth.

**`focus: true`** means: bring this window to the front first. The extension runs
`workbench.action.focusWindow` when the window has it. Otherwise it does nothing, and the client
activates the app itself.

**Command policy.** The extension setting `loupixdeck.commandPolicy` is `any` (default) or
`allowList`. With `allowList`, `exec` refuses (`denied`) any command not in
`loupixdeck.allowedCommands`. `list`, `run`, `runTask`, `startDebug` and `focus` are always allowed.
Both settings are application-scoped and read from user settings only; a workspace or folder value
is ignored, so an opened repository cannot loosen the policy. A client must treat `denied` as
final and not reach the command another way (for example through the editor's CLI).

## Items (`list` / `run`)

```json
{"ref":"opaque string","label":"build","detail":"npm · tasks.json","kind":"task","severity":null,"dirty":false,"active":false}
```

| what | label | detail | extra | `run` does |
|---|---|---|---|---|
| `tasks` | task label | source (`npm`, `Workspace`…) and the folder name in multi-root workspaces | – | `tasks.executeTask` |
| `launch` | configuration or compound name | `launch.json` (plus the folder name in multi-root workspaces), `workspace file` or `user settings` | `kind`: `config` / `compound` | folder `launch.json`: `debug.startDebugging(folder, name)`; `.code-workspace` file or user settings: `debug.startDebugging(folder, configuration)` with the configuration object (a compound starts each of its configurations; one that names a folder's configuration is not listed). Each entry is listed once |
| `problems` | `file.ts:12` | message, first 120 characters | `severity`: `error` / `warning` / `info`. Sorted errors first, then by file. At most 100 items | opens the file at the range |
| `editors` | tab label | parent folder name | `dirty`, `active`, `group` (1-based) | focuses the tab's group and opens the tab |
| `recent` | folder or workspace name | full path | `kind`: `folder` / `workspace` | `vscode.openFolder` in the same window. Uses the internal `_workbench.getRecentlyOpened`; returns an empty list if that fails |
| `terminals` | terminal name | last command, when known | `active` | `terminal.show()` |

`ref` is only valid for the same window until its list changes. A stale ref gives `not_found`.

## Events (extension → client, no `id`)

```json
{"ev":"state","data":{"problems":{…},"editor":{…}}}
{"ev":"focus","focused":true,"active":true,"at":1791650000000}
{"ev":"lists","what":["tasks","launch"]}
{"ev":"notify","kind":"taskFailed","text":"build exited with 1"}
```

- `state` carries only the sections that changed. Each section is replaced whole (no deep merge).
  The extension debounces state pushes to 150 ms. Cursor moves are throttled to one push per
  250 ms.
- `focus` is pushed on every `window.onDidChangeWindowState`. `at` is the time the window last
  gained focus (epoch ms), or the time it lost focus when `focused` is false.
- `lists` says which `list` results may have changed (tasks.json or launch.json edited,
  diagnostics changed, tabs changed, terminals opened or closed). The client refreshes an open
  folder when it sees this.
- `notify` kinds: `taskFailed`, `taskSucceeded`, `debugStopped` (paused at a breakpoint or
  exception), `terminalFailed` (a shell command exited non-zero). The `text` field is short and
  ready to show on a key.

## State

```json
{
  "window":   {"focused":true,"active":true,"lastFocused":1791650000000},
  "workspace":{"name":"LoupixDeck","folders":["file:///…"],"remote":null},
  "editor":   {"file":"Program.cs","path":"/…/Program.cs","language":"csharp","dirty":false,"line":12,"column":4,"selections":1} ,
  "dirty":    {"count":2},
  "problems": {"errors":3,"warnings":7,"infos":0},
  "git":      {"branch":"main","detached":false,"ahead":1,"behind":0,"changes":4,"staged":1,"conflicts":0,"repos":1,"upstream":true},
  "debug":    {"state":"inactive","session":null,"reason":null},
  "tasks":    {"running":["watch"],"last":{"name":"build","exitCode":0,"at":1791650000000}},
  "terminal": {"count":2,"last":{"command":"npm test","exitCode":1,"at":1791650000000}},
  "settings": {"accessibilitySupport":"auto","wordWrap":"off","minimap":true}
}
```

- `editor` is `null` when no text editor is active. `line` and `column` are 1-based.
- `git` is `null` when the window has no repository. With several repositories, it describes the
  repository selected in the Source Control view, or else the first one. `repos` is the count.
  `changes` counts working-tree, untracked and merge changes. `staged` counts index changes.
  `upstream` is false when the branch has no upstream. In local windows the data comes from the
  `vscode.git` extension API and its events. In remote windows, where that API is out of reach,
  it comes from the `git.api.getRepositories` and `git.api.getRepositoryState` commands, polled
  every 2 s while the window is focused and every 10 s otherwise.
- `debug.state` is `inactive`, `running` or `paused`. `reason` is the stop reason when paused
  (`breakpoint`, `exception`, `step`, `pause`…), when it is known. The extension follows the
  debug adapter's `stopped` events, and counts the session running again as soon as VS Code sends
  a `continue`, step, `goto`, `restartFrame` or `restart` request (adapters do not send `continued`
  for those). Without adapter data, only a selected stack frame counts as paused.
- `tasks.last` is the most recent task that ended, or `null`. `exitCode` can be `null`.
- `terminal.last` comes from shell integration and is `null` until a command has finished in
  this window. `exitCode` can be `null`.
- `settings.accessibilitySupport` is `on`, `off` or `auto`, from `editor.accessibilitySupport`.

## Versioning

`protocol` is 1. A future incompatible change raises it. The client refuses a server whose
protocol is higher than its own and shows "Update plugin". The extension accepts lower client
versions for as long as it can.

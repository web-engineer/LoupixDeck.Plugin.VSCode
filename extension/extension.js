// @ts-check
'use strict';

// LoupixDeck Companion: runs one local server per VS Code window so the LoupixDeck plugin can run
// commands by ID and show the window's live state on the deck. docs/PROTOCOL.md in the plugin's
// repository is the contract; the pieces live in lib/.

const path = require('path');
const vscode = require('vscode');
const { PROTOCOL, errorMessage, newToken } = require('./lib/protocol');
const registration = require('./lib/registration');
const { BridgeServer } = require('./lib/server');
const { StateTracker } = require('./lib/state');
const { GitTracker } = require('./lib/git');
const { Lists } = require('./lib/lists');
const { createHandler, FOCUS_WINDOW } = require('./lib/handlers');

/** @type {Companion | undefined} */
let companion;

/** @param {vscode.ExtensionContext} context */
function activate(context) {
    companion = new Companion(context);
    context.subscriptions.push(companion);
    companion.start();
}

function deactivate() {
    companion?.dispose();
    companion = undefined;
}

class Companion {
    /** @param {vscode.ExtensionContext} context */
    constructor(context) {
        this._context = context;
        this._extVersion = String(context.extension?.packageJSON?.version ?? '0.0.0');
        this._output = vscode.window.createOutputChannel('LoupixDeck', { log: true });
        this._status = vscode.window.createStatusBarItem('loupixdeck.status', vscode.StatusBarAlignment.Right, 100);
        this._status.name = 'LoupixDeck';
        this._status.command = 'loupixdeck.showStatus';
        /** @type {vscode.Disposable[]} */
        this._disposables = [this._output, this._status];
        /** @type {BridgeServer | undefined} */
        this._server = undefined;
        /** @type {StateTracker | undefined} */
        this._tracker = undefined;
        /** @type {GitTracker | undefined} */
        this._git = undefined;
        /** @type {vscode.Disposable[]} listeners that live as long as one server */
        this._serverDisposables = [];
        /** @type {string | undefined} */
        this._dir = undefined;
        /** @type {string | undefined} */
        this._token = undefined;
        /** @type {number | undefined} */
        this._startedAt = undefined;
        /** @type {{command: string, at: number} | undefined} */
        this._lastCommand = undefined;
        /** @type {string | undefined} why the server is not running, when it failed */
        this._failure = undefined;
        this._hasFocusWindow = false;
        this._starting = Promise.resolve();
        /** bumped by every stop, so a start-up still awaiting sees it was cancelled */
        this._generation = 0;
        this._disposed = false;
        this._connected = false;
        /** @type {Promise<void>} resolves once the command list was checked for focusWindow */
        this._commandsChecked = Promise.resolve();
    }

    start() {
        const { commands, workspace } = vscode;
        this._disposables.push(
            commands.registerCommand('loupixdeck.showStatus', () => this._showStatus()),
            commands.registerCommand('loupixdeck.showLog', () => this._output.show(true)),
            commands.registerCommand('loupixdeck.restartServer', () => this._restart()),
            workspace.onDidChangeConfiguration(e => {
                if (e.affectsConfiguration('loupixdeck.enabled')) this._guard('enabled', () => this._applyEnabled());
            }),
            workspace.onDidChangeWorkspaceFolders(() => this._guard('folders', () => this._register())),
        );
        this._commandsChecked = Promise.resolve(commands.getCommands(true)).then(
            list => { this._hasFocusWindow = list.includes(FOCUS_WINDOW); },
            error => this._log('warn', `Could not list commands: ${errorMessage(error)}`),
        );
        this._applyEnabled();
    }

    dispose() {
        // A start-up queued before this runs after it; it must not start anything.
        this._disposed = true;
        this._stopServer();
        for (const d of this._disposables) {
            try {
                d.dispose();
            } catch {
                // Nothing to do.
            }
        }
        this._disposables = [];
    }

    _enabled() {
        return vscode.workspace.getConfiguration('loupixdeck').get('enabled') !== false;
    }

    _applyEnabled() {
        if (this._enabled()) {
            if (!this._server) this._queue(() => this._startServer());
        } else {
            this._stopServer();
            this._log('info', 'Disabled by loupixdeck.enabled');
        }
        this._updateStatus();
    }

    async _restart() {
        this._stopServer();
        if (!this._enabled()) {
            vscode.window.showInformationMessage('LoupixDeck: the companion is turned off (loupixdeck.enabled).');
            return;
        }
        this._queue(() => this._startServer());
        await this._starting;
    }

    /**
     * Runs start-ups one after another, so a quick restart cannot start two servers.
     * @param {() => Promise<void>} action
     */
    _queue(action) {
        this._starting = this._starting.then(action).catch(error => this._log('error', errorMessage(error)));
    }

    async _startServer() {
        if (this._server || this._disposed || !this._enabled()) return;
        const generation = this._generation;
        // A stop (Restart Server, loupixdeck.enabled off, deactivate) can land while this awaits.
        const cancelled = () => generation !== this._generation || this._disposed || !this._enabled();
        /** @type {Array<{dispose(): unknown}>} what this start-up made, undone if it is cancelled or fails */
        const made = [];
        this._failure = undefined;
        const pid = process.pid;
        const token = newToken();
        let socketPath;
        try {
            const dir = registration.registrationDir();
            registration.ensurePrivateDir(dir);
            this._dir = dir;
            const removed = registration.removeStale(dir, pid);
            if (removed.length > 0) this._log('info', `Removed stale registrations: ${removed.join(', ')}`);
            socketPath = registration.socketPathFor(dir, pid);
            if (!registration.isPipe(socketPath)) {
                const socketDir = path.dirname(socketPath);
                if (socketDir !== dir) registration.ensurePrivateDir(socketDir);
            }

            await this._commandsChecked;
            if (cancelled()) return;
            /** @type {StateTracker | undefined} */
            let tracker;
            const git = new GitTracker(vscode, {
                log: (level, text) => this._log(level, text),
                onChange: () => tracker?.touch('git'),
                isFocused: () => !!vscode.window.state?.focused,
            });
            made.push(git);
            tracker = new StateTracker(vscode, {
                log: (level, text) => this._log(level, text),
                onState: sections => this._server?.broadcast({ ev: 'state', data: sections }),
                onEvent: event => this._onTrackerEvent(event),
                git: () => git.current,
                pushDiagnostics: () => vscode.workspace.getConfiguration('loupixdeck').get('pushDiagnostics') !== false,
            });
            const state = tracker;
            made.push(state);
            const lists = new Lists(vscode, { terminalCommand: terminal => state.terminalCommand(terminal) });
            const handle = createHandler(vscode, {
                lists,
                tracker: state,
                policy: () => {
                    // User settings only: a workspace's .vscode/settings.json must not loosen the policy.
                    const config = vscode.workspace.getConfiguration('loupixdeck');
                    const allowed = config.inspect('allowedCommands')?.globalValue;
                    return {
                        policy: String(config.inspect('commandPolicy')?.globalValue ?? 'any'),
                        allowed: Array.isArray(allowed) ? allowed.filter(a => typeof a === 'string') : [],
                    };
                },
                hasFocusWindow: () => this._hasFocusWindow,
                log: (level, text) => this._log(level, text),
                onCommand: command => {
                    this._lastCommand = { command, at: Date.now() };
                },
            });
            const server = new BridgeServer({
                token,
                hello: async () => ({
                    protocol: PROTOCOL,
                    pid,
                    app: vscode.env.appName,
                    version: vscode.version,
                    uriScheme: vscode.env.uriScheme,
                    extVersion: this._extVersion,
                    focusWindow: this._hasFocusWindow,
                    state: state.snapshot(),
                }),
                handle: (op, message) => handle(op, message),
                log: (level, text) => this._log(level, text),
                onClientsChanged: () => this._updateStatus(),
            });

            made.push({ dispose: () => server.close() });
            state.start();
            const listeners = [
                vscode.window.onDidChangeWindowState(() => this._guard('gitFocus', () => {
                    git.focusChanged();
                    if (vscode.window.state.focused) git.poke();
                })),
                vscode.workspace.onDidSaveTextDocument(() => this._guard('gitSave', () => git.poke())),
            ];
            made.push(...listeners);
            git.start().catch(error => this._log('error', `Git: ${errorMessage(error)}`));

            await server.listen(socketPath);
            if (cancelled()) {
                this._log('info', 'Start-up cancelled by a stop');
                return;
            }
            made.length = 0;
            this._tracker = state;
            this._git = git;
            this._serverDisposables.push(...listeners);
            this._server = server;
            this._token = token;
            this._register();
            this._log('info', `Listening on ${socketPath} (${vscode.env.appName} ${vscode.version}, focusWindow ${this._hasFocusWindow ? 'available' : 'missing'})`);
        } catch (error) {
            this._failure = errorMessage(error);
            this._log('error', `Could not start the server: ${this._failure}`);
            // Past the hand-over the fields own everything; undo it the usual way.
            if (made.length === 0) this._stopServer();
        } finally {
            for (const d of made.reverse()) {
                try {
                    d.dispose();
                } catch {
                    // Nothing to do.
                }
            }
        }
        this._updateStatus();
    }

    /** Writes (or rewrites, when the folders change) this window's registration file. */
    _register() {
        if (!this._server || !this._dir || !this._server.socketPath || !this._token) return;
        registration.writeEntry(this._dir, {
            v: 1,
            pid: process.pid,
            socket: this._server.socketPath,
            token: this._token,
            app: vscode.env.appName,
            uriScheme: vscode.env.uriScheme,
            appRoot: vscode.env.appRoot,
            version: vscode.version,
            remoteName: vscode.env.remoteName ?? null,
            workspaceName: vscode.workspace.name ?? null,
            folders: (vscode.workspace.workspaceFolders ?? []).map(f => f.uri.toString()),
            extVersion: this._extVersion,
            startedAt: this._startedAt ??= Date.now(),
        });
    }

    _stopServer() {
        this._generation++;
        const socketPath = this._server?.socketPath;
        this._server?.close();
        this._server = undefined;
        this._tracker?.dispose();
        this._tracker = undefined;
        this._git?.dispose();
        this._git = undefined;
        for (const d of this._serverDisposables) d.dispose();
        this._serverDisposables = [];
        if (this._dir) registration.removeEntry(this._dir, process.pid, socketPath);
        this._token = undefined;
        this._startedAt = undefined;
        this._updateStatus();
    }

    /** @param {Record<string, unknown>} event */
    _onTrackerEvent(event) {
        if (event.ev === 'notify') this._log('info', `notify ${event.kind}: ${event.text}`);
        this._server?.broadcast(event);
    }

    _updateStatus() {
        const clients = this._server?.readyClients.length ?? 0;
        const connected = clients > 0;
        if (connected !== this._connected) {
            this._connected = connected;
            Promise.resolve(vscode.commands.executeCommand('setContext', 'loupixdeck.connected', connected))
                .catch(() => undefined);
        }
        if (!this._enabled()) {
            this._status.hide();
            return;
        }
        if (connected) {
            this._status.text = '$(plug) Deck';
            this._status.tooltip = `LoupixDeck: ${clients} ${clients === 1 ? 'client' : 'clients'} connected`;
        } else {
            this._status.text = '$(debug-disconnect) Deck';
            this._status.tooltip = this._failure
                ? `LoupixDeck: the server could not start (${this._failure})`
                : 'LoupixDeck: no deck connected';
        }
        this._status.show();
    }

    async _showStatus() {
        /** @typedef {vscode.QuickPickItem & {action?: () => unknown}} Pick */
        /** @type {Pick[]} */
        const items = [];
        const server = this._server;
        const separator = vscode.QuickPickItemKind.Separator;
        items.push({ label: 'Server', kind: separator });
        if (server?.socketPath) {
            items.push({ label: '$(radio-tower) Listening', detail: server.socketPath });
        } else {
            items.push({
                label: '$(circle-slash) Not running',
                detail: this._failure ?? (this._enabled() ? 'Starting…' : 'Turned off by loupixdeck.enabled'),
            });
        }
        items.push({ label: 'Clients', kind: separator });
        const clients = server?.readyClients ?? [];
        if (clients.length === 0) items.push({ label: '$(debug-disconnect) No deck connected' });
        for (const client of clients) {
            items.push({ label: `$(plug) ${client.name}`, description: `connected ${new Date(client.connectedAt).toLocaleTimeString()}` });
        }
        if (this._lastCommand) {
            items.push({ label: 'Last command', kind: separator });
            items.push({ label: `$(terminal) ${this._lastCommand.command}`, description: new Date(this._lastCommand.at).toLocaleTimeString() });
        }
        items.push({ label: 'Actions', kind: separator });
        items.push({ label: '$(output) Show Log', action: () => this._output.show(true) });
        items.push({ label: '$(debug-restart) Restart Server', action: () => this._restart() });
        const picked = await vscode.window.showQuickPick(items, { title: 'LoupixDeck Companion', placeHolder: 'Connection status' });
        await picked?.action?.();
    }

    /**
     * @param {'info' | 'warn' | 'error'} level
     * @param {string} text
     */
    _log(level, text) {
        try {
            if (level === 'error') this._output.error(text);
            else if (level === 'warn') this._output.warn(text);
            else this._output.info(text);
        } catch {
            // The channel is gone during shutdown.
        }
    }

    /**
     * @param {string} name
     * @param {() => unknown} action
     */
    _guard(name, action) {
        try {
            const result = action();
            if (result instanceof Promise) result.catch(error => this._log('error', `${name}: ${errorMessage(error)}`));
        } catch (error) {
            this._log('error', `${name}: ${errorMessage(error)}`);
        }
    }
}

module.exports = { activate, deactivate };

// @ts-check
'use strict';

// A small stand-in for the `vscode` module: just the parts the companion touches, with events
// the tests can fire and state they can set. createVscode() returns a fresh copy per test.

const path = require('path');

class Disposable {
    /** @param {() => void} [onDispose] */
    constructor(onDispose) {
        this._onDispose = onDispose;
    }

    dispose() {
        this._onDispose?.();
    }

    /** @param {...{dispose(): unknown}} items */
    static from(...items) {
        return new Disposable(() => items.forEach(i => i.dispose()));
    }
}

class EventEmitter {
    constructor() {
        /** @type {Set<(e: any) => void>} */
        this._listeners = new Set();
        /** @param {(e: any) => void} listener */
        this.event = listener => {
            this._listeners.add(listener);
            return new Disposable(() => this._listeners.delete(listener));
        };
    }

    /** @param {any} [e] */
    fire(e) {
        for (const listener of [...this._listeners]) listener(e);
    }

    get count() {
        return this._listeners.size;
    }
}

class Uri {
    /** @param {string} scheme @param {string} authority @param {string} p @param {string} query @param {string} fragment */
    constructor(scheme, authority, p, query, fragment) {
        this.scheme = scheme;
        this.authority = authority;
        this.path = p;
        this.query = query;
        this.fragment = fragment;
    }

    get fsPath() {
        return this.path;
    }

    /** @param {Partial<Uri>} change */
    with(change) {
        return new Uri(change.scheme ?? this.scheme, change.authority ?? this.authority, change.path ?? this.path, change.query ?? this.query, change.fragment ?? this.fragment);
    }

    toString() {
        return `${this.scheme}://${this.authority}${this.path}${this.query ? '?' + this.query : ''}`;
    }

    toJSON() {
        return { $mid: 1, scheme: this.scheme, path: this.path };
    }

    /** @param {string} value */
    static parse(value) {
        const match = /^([a-z][\w+.-]*):\/\/([^/]*)([^?#]*)(?:\?([^#]*))?/i.exec(value);
        if (!match) throw new Error(`Bad URI ${value}`);
        return new Uri(match[1], match[2], match[3] || '/', match[4] ?? '', '');
    }

    /** @param {string} p */
    static file(p) {
        return new Uri('file', '', p, '', '');
    }

    /** @param {{scheme: string, authority?: string, path?: string, query?: string}} parts */
    static from(parts) {
        return new Uri(parts.scheme, parts.authority ?? '', parts.path ?? '/', parts.query ?? '', '');
    }
}

class Position {
    /** @param {number} line @param {number} character */
    constructor(line, character) {
        this.line = line;
        this.character = character;
    }
}

class Range {
    /** @param {number} sl @param {number} sc @param {number} el @param {number} ec */
    constructor(sl, sc, el, ec) {
        this.start = new Position(sl, sc);
        this.end = new Position(el, ec);
    }
}

/**
 * @param {string} file
 * @param {number} line
 * @param {number} severity
 * @param {string} message
 */
function diagnostic(file, line, severity, message) {
    return { uri: Uri.file(file), diagnostic: { range: new Range(line, 0, line, 5), severity, message } };
}

function createVscode() {
    const events = {
        windowState: new EventEmitter(),
        activeEditor: new EventEmitter(),
        selection: new EventEmitter(),
        textDocument: new EventEmitter(),
        save: new EventEmitter(),
        closeDocument: new EventEmitter(),
        folders: new EventEmitter(),
        configuration: new EventEmitter(),
        tabs: new EventEmitter(),
        tabGroups: new EventEmitter(),
        diagnostics: new EventEmitter(),
        debugStart: new EventEmitter(),
        debugEnd: new EventEmitter(),
        debugActive: new EventEmitter(),
        stackItem: new EventEmitter(),
        taskStart: new EventEmitter(),
        taskEnd: new EventEmitter(),
        taskProcessEnd: new EventEmitter(),
        terminalOpen: new EventEmitter(),
        terminalClose: new EventEmitter(),
        terminalActive: new EventEmitter(),
        shellEnd: new EventEmitter(),
    };

    /** @type {Record<string, any>} */
    const settings = {
        'editor.accessibilitySupport': 'auto',
        'editor.wordWrap': 'off',
        'editor.minimap.enabled': true,
        'loupixdeck.enabled': true,
        'loupixdeck.commandPolicy': 'any',
        'loupixdeck.allowedCommands': [],
        'loupixdeck.pushDiagnostics': true,
    };

    /** @type {Record<string, any>} values from a workspace's .vscode/settings.json; they win in get() */
    const workspaceSettings = {};
    /** @type {Record<string, Record<string, any>>} per folder uri: values from that folder's .vscode files */
    const folderSettings = {};

    /** @type {Map<string, (...args: any[]) => any>} */
    const commandHandlers = new Map();
    /** @type {{command: string, args: any[]}[]} */
    const executed = [];
    /** @type {[Uri, any[]][]} */
    let diagnostics = [];
    /** @type {any[]} */
    let fetchTasksResult = [];
    /** @type {any[]} */
    const executedTasks = [];
    /** @type {any[]} */
    const startedDebugs = [];
    /** @type {any[]} */
    const shownDocuments = [];
    /** @type {any[]} */
    const quickPicks = [];
    /** @type {any[]} */
    const statusItems = [];
    /** @type {string[]} */
    const logLines = [];

    const vscode = {
        version: '1.141.0',
        Uri,
        Position,
        Range,
        Disposable,
        EventEmitter,
        StatusBarAlignment: { Left: 1, Right: 2 },
        QuickPickItemKind: { Separator: -1, Default: 0 },
        DiagnosticSeverity: { Error: 0, Warning: 1, Information: 2, Hint: 3 },
        env: {
            appName: 'Visual Studio Code',
            uriScheme: 'vscode',
            appRoot: '/Applications/Visual Studio Code.app/Contents/Resources/app',
            /** @type {string | undefined} */
            remoteName: undefined,
        },
        window: {
            state: { focused: true, active: true },
            /** @type {any} */
            activeTextEditor: undefined,
            /** @type {any[]} */
            terminals: [],
            /** @type {any} */
            activeTerminal: undefined,
            tabGroups: {
                /** @type {any[]} */
                all: [],
                onDidChangeTabs: events.tabs.event,
                onDidChangeTabGroups: events.tabGroups.event,
            },
            onDidChangeWindowState: events.windowState.event,
            onDidChangeActiveTextEditor: events.activeEditor.event,
            onDidChangeTextEditorSelection: events.selection.event,
            onDidOpenTerminal: events.terminalOpen.event,
            onDidCloseTerminal: events.terminalClose.event,
            onDidChangeActiveTerminal: events.terminalActive.event,
            onDidEndTerminalShellExecution: events.shellEnd.event,
            /** @param {string} name */
            createOutputChannel(name) {
                const line = /** @param {string} level */ level => /** @param {string} text */ text => logLines.push(`${level} ${text}`);
                return { name, info: line('info'), warn: line('warn'), error: line('error'), show() {}, dispose() {} };
            },
            createStatusBarItem() {
                const item = { text: '', tooltip: '', name: '', command: '', visible: false, show() { item.visible = true; }, hide() { item.visible = false; }, dispose() {} };
                statusItems.push(item);
                return item;
            },
            /** @param {any} uri @param {any} options */
            async showTextDocument(uri, options) {
                shownDocuments.push({ uri, options });
            },
            /** @param {any[]} items */
            async showQuickPick(items) {
                quickPicks.push(items);
                return undefined;
            },
            async showInformationMessage() {
                return undefined;
            },
        },
        workspace: {
            /** @type {string | undefined} */
            name: 'demo',
            /** @type {any[] | undefined} */
            workspaceFolders: [{ uri: Uri.file('/work/demo'), name: 'demo', index: 0 }],
            /** @type {any[]} */
            textDocuments: [],
            /** @param {string} section */
            /** @param {string} section @param {any} [resource] */
            getConfiguration(section, resource) {
                const folder = resource ? folderSettings[resource.toString()] ?? {} : {};
                return {
                    /** @param {string} key @param {any} [fallback] */
                    get(key, fallback) {
                        const full = `${section}.${key}`;
                        return full in folder ? folder[full] : full in workspaceSettings ? workspaceSettings[full] : full in settings ? settings[full] : fallback;
                    },
                    /** @param {string} key */
                    inspect(key) {
                        const full = `${section}.${key}`;
                        return {
                            key: full,
                            globalValue: settings[full],
                            workspaceValue: workspaceSettings[full],
                            workspaceFolderValue: resource ? folder[full] : undefined,
                        };
                    },
                };
            },
            /** @param {any} uri */
            asRelativePath(uri) {
                const p = typeof uri === 'string' ? uri : uri.path;
                return p.startsWith('/work/demo/') ? p.slice('/work/demo/'.length) : p;
            },
            onDidChangeWorkspaceFolders: events.folders.event,
            onDidChangeTextDocument: events.textDocument.event,
            onDidSaveTextDocument: events.save.event,
            onDidCloseTextDocument: events.closeDocument.event,
            onDidChangeConfiguration: events.configuration.event,
        },
        languages: {
            getDiagnostics: () => diagnostics,
            onDidChangeDiagnostics: events.diagnostics.event,
        },
        debug: {
            /** @type {any} */
            activeDebugSession: undefined,
            /** @type {any} */
            activeStackItem: undefined,
            onDidStartDebugSession: events.debugStart.event,
            onDidTerminateDebugSession: events.debugEnd.event,
            onDidChangeActiveDebugSession: events.debugActive.event,
            onDidChangeActiveStackItem: events.stackItem.event,
            /** @type {any} */
            trackerFactory: undefined,
            /** @param {string} type @param {any} factory */
            registerDebugAdapterTrackerFactory(type, factory) {
                vscode.debug.trackerFactory = factory;
                return new Disposable();
            },
            /** @param {any} folder @param {string} name */
            async startDebugging(folder, name) {
                startedDebugs.push({ folder, name });
                return true;
            },
        },
        tasks: {
            /** @type {any[]} */
            taskExecutions: [],
            fetchTasks: async () => fetchTasksResult,
            /** @param {any} task */
            async executeTask(task) {
                executedTasks.push(task);
                return { task };
            },
            onDidStartTask: events.taskStart.event,
            onDidEndTask: events.taskEnd.event,
            onDidEndTaskProcess: events.taskProcessEnd.event,
        },
        extensions: {
            getExtension: () => undefined,
        },
        commands: {
            /** @param {string} id @param {(...args: any[]) => any} handler */
            registerCommand(id, handler) {
                commandHandlers.set(id, handler);
                return new Disposable(() => commandHandlers.delete(id));
            },
            /** @param {string} id @param {...any} args */
            async executeCommand(id, ...args) {
                executed.push({ command: id, args });
                const handler = commandHandlers.get(id);
                if (handler) return handler(...args);
                if (id === 'setContext') return undefined;
                throw new Error(`command '${id}' not found`);
            },
            async getCommands() {
                return [...commandHandlers.keys(), 'setContext'];
            },
        },
    };

    const control = {
        vscode,
        events,
        settings,
        workspaceSettings,
        folderSettings,
        executed,
        executedTasks,
        startedDebugs,
        shownDocuments,
        quickPicks,
        statusItems,
        logLines,
        commandHandlers,
        /** @param {[Uri, any[]][]} value */
        setDiagnostics(value) {
            diagnostics = value;
        },
        /** @param {any[]} value */
        setTasks(value) {
            fetchTasksResult = value;
        },
        /** Fires onDidChangeConfiguration for the given keys. */
        configChanged(/** @type {string[]} */ ...keys) {
            events.configuration.fire({ affectsConfiguration: /** @param {string} k */ k => keys.some(key => key === k || key.startsWith(k + '.')) });
        },
        /**
         * @param {string} file
         * @param {{line?: number, character?: number, dirty?: boolean, language?: string}} [options]
         */
        openEditor(file, options = {}) {
            const document = { uri: Uri.file(file), fileName: file, languageId: options.language ?? 'javascript', isDirty: !!options.dirty };
            const active = new Position(options.line ?? 0, options.character ?? 0);
            vscode.window.activeTextEditor = { document, selection: { active }, selections: [{ active }] };
            if (!vscode.workspace.textDocuments.includes(document)) vscode.workspace.textDocuments.push(document);
            return vscode.window.activeTextEditor;
        },
        /** @param {string} file */
        basename(file) {
            return path.basename(file);
        },
    };

    return { vscode, control };
}

module.exports = { createVscode, diagnostic, Uri, Range, Position, EventEmitter, Disposable };

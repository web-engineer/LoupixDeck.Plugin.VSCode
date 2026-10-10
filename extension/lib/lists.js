// @ts-check
'use strict';

// The `list` and `run` ops: tasks, launch configurations, problems, open editors, recent folders
// and terminals as items for the deck's folders. A ref is a short hash of what the item points
// at, so it survives a refresh of the same list; a ref whose item has gone gives `not_found`.
// Nothing is cached: every list and run reads the editor's current state.

const crypto = require('crypto');
const path = require('path');
const { BridgeError, Codes } = require('./protocol');
const { sortProblems, tabKey } = require('./state');

const LISTS = Object.freeze(['tasks', 'launch', 'problems', 'editors', 'recent', 'terminals']);
const MAX_PROBLEMS = 100;
const MAX_RECENT = 50;
const GROUP_FOCUS = ['First', 'Second', 'Third', 'Fourth', 'Fifth', 'Sixth', 'Seventh', 'Eighth'];

/**
 * @typedef {{ref: string, label: string, detail: string, kind: string, severity: string | null,
 *   dirty: boolean, active: boolean, group?: number}} Item
 * @typedef {{item: Item, run: () => Promise<unknown>}} Entry
 */

/** @param {string} key */
function refFor(key) {
    return crypto.createHash('sha1').update(key).digest('base64url').slice(0, 16);
}

/**
 * @param {Partial<Item> & {label: string, kind: string}} fields
 * @param {string} key what the item points at, hashed into its ref
 * @param {Set<string>} used refs already handed out in this list, to keep them unique
 * @returns {Item}
 */
function makeItem(fields, key, used) {
    let ref = refFor(key);
    for (let n = 2; used.has(ref); n++) ref = refFor(`${key}#${n}`);
    used.add(ref);
    return {
        ref,
        label: fields.label,
        detail: fields.detail ?? '',
        kind: fields.kind,
        severity: fields.severity ?? null,
        dirty: fields.dirty ?? false,
        active: fields.active ?? false,
        ...(fields.group !== undefined ? { group: fields.group } : {}),
    };
}

/** @param {number} severity a DiagnosticSeverity */
function severityName(severity) {
    return severity === 0 ? 'error' : severity === 1 ? 'warning' : 'info';
}

class Lists {
    /**
     * @param {typeof import('vscode')} vscode
     * @param {{terminalCommand: (terminal: object) => string | null}} options
     */
    constructor(vscode, options) {
        this._vscode = vscode;
        this._options = options;
    }

    /**
     * @param {unknown} what
     * @returns {Promise<{items: Item[]}>}
     */
    async list(what) {
        const entries = await this._build(what);
        return { items: entries.map(e => e.item) };
    }

    /**
     * Runs an item from a list. The list is built afresh first, so a ref whose item has gone
     * since the client listed it gives `not_found` instead of running something stale.
     * @param {unknown} what
     * @param {unknown} ref
     */
    async run(what, ref) {
        if (typeof ref !== 'string' || ref.length === 0) throw new BridgeError(Codes.badRequest, 'Missing ref');
        const entry = (await this._build(what)).find(e => e.item.ref === ref);
        if (!entry) throw new BridgeError(Codes.notFound, 'That item is no longer in the list');
        await entry.run();
        return null;
    }

    /** @param {unknown} name */
    async runTask(name) {
        if (typeof name !== 'string' || name.length === 0) throw new BridgeError(Codes.badRequest, 'Missing name');
        const all = await this._vscode.tasks.fetchTasks();
        // Tasks from tasks.json win over detected ones with the same label.
        const task = all.find(t => t.name === name && t.source === 'Workspace')
            ?? all.find(t => t.name === name)
            ?? all.find(t => `${t.source}: ${t.name}` === name);
        if (!task) throw new BridgeError(Codes.notFound, `No task called ${name}`);
        await this._vscode.tasks.executeTask(task);
        return null;
    }

    /** @param {unknown} name */
    async startDebug(name) {
        if (typeof name !== 'string' || name.length === 0) throw new BridgeError(Codes.badRequest, 'Missing name');
        const entries = this._launch();
        const entry = entries.find(e => e.item.label === name);
        if (!entry) throw new BridgeError(Codes.notFound, `No launch configuration called ${name}`);
        await entry.run();
        return null;
    }

    /**
     * @param {unknown} what
     * @returns {Promise<Entry[]>}
     */
    async _build(what) {
        let entries;
        switch (what) {
            case 'tasks': entries = await this._tasks(); break;
            case 'launch': entries = this._launch(); break;
            case 'problems': entries = this._problems(); break;
            case 'editors': entries = this._editors(); break;
            case 'recent': entries = await this._recent(); break;
            case 'terminals': entries = this._terminals(); break;
            default: throw new BridgeError(Codes.badRequest, `Unknown list ${String(what)}; expected one of ${LISTS.join(', ')}`);
        }
        return entries;
    }

    _multiRoot() {
        return (this._vscode.workspace.workspaceFolders?.length ?? 0) > 1;
    }

    /** @returns {Promise<Entry[]>} */
    async _tasks() {
        const vscode = this._vscode;
        const all = await vscode.tasks.fetchTasks();
        const multi = this._multiRoot();
        /** @type {Set<string>} */
        const used = new Set();
        return all.map(task => {
            const scope = /** @type {any} */ (task.scope);
            const folder = scope && typeof scope === 'object' && 'uri' in scope ? scope : undefined;
            const detail = [task.source, multi && folder ? folder.name : null].filter(Boolean).join(' · ');
            const key = `task|${task.source}|${folder ? folder.uri.toString() : String(scope)}|${task.name}`;
            return {
                item: makeItem({ label: task.name, detail, kind: 'task' }, key, used),
                run: async () => { await vscode.tasks.executeTask(task); },
            };
        });
    }

    /**
     * Launch configurations and compounds from every place VS Code reads them: each folder's
     * launch.json, the .code-workspace file (multi-root) and user settings. Folder entries start
     * by name in their folder. A workspace-file or user entry starts from its configuration object,
     * because VS Code resolves a name only against the folder's own launch.json. Each is listed
     * once, not once per folder.
     * @returns {Entry[]}
     */
    _launch() {
        const vscode = this._vscode;
        const folders = vscode.workspace.workspaceFolders ?? [];
        const multi = folders.length > 1;
        // A single-folder window reports its launch.json as the workspace value too.
        const workspaceFile = multi || !!vscode.workspace.workspaceFile;
        /** @type {Set<string>} */
        const used = new Set();
        /** @type {Entry[]} */
        const entries = [];

        /**
         * @param {any} info the result of inspect()
         * @param {'workspaceFolderValue' | 'workspaceValue' | 'globalValue'} layer
         */
        const read = (info, layer) => (info && typeof info === 'object' ? info[layer] : undefined);

        /**
         * @param {unknown} configs
         * @param {unknown} compounds
         * @param {string} scope a key part that tells the sources apart
         * @param {string} detail
         * @param {import('vscode').WorkspaceFolder | undefined} folder
         * @param {boolean} byName start by name (folder launch.json) or from the configuration object
         */
        const add = (configs, compounds, scope, detail, folder, byName) => {
            /** @type {Map<string, any>} */
            const named = new Map();
            for (const config of Array.isArray(configs) ? configs : []) {
                const name = config && typeof config.name === 'string' ? config.name : null;
                if (!name || named.has(name)) continue;
                named.set(name, config);
                entries.push({
                    item: makeItem({ label: name, detail, kind: 'config' }, `launch|config|${scope}|${name}`, used),
                    run: async () => {
                        const started = await vscode.debug.startDebugging(folder, byName ? name : config);
                        if (!started) throw new BridgeError(Codes.failed, `Debugging ${name} did not start`);
                    },
                });
            }
            for (const compound of Array.isArray(compounds) ? compounds : []) {
                const name = compound && typeof compound.name === 'string' ? compound.name : null;
                if (!name) continue;
                /** @type {any[]} */
                let parts = [];
                if (!byName) {
                    // Started part by part from this scope's own configurations; one that points
                    // elsewhere (a folder's config) cannot be started this way, so it is left out.
                    const refs = Array.isArray(compound.configurations) ? compound.configurations : [];
                    parts = refs.map((/** @type {any} */ r) => named.get(typeof r === 'string' ? r : (r && typeof r === 'object' && !r.folder ? r.name : undefined)));
                    if (parts.length === 0 || parts.some(p => !p)) continue;
                }
                entries.push({
                    item: makeItem({ label: name, detail, kind: 'compound' }, `launch|compound|${scope}|${name}`, used),
                    run: async () => {
                        if (byName) {
                            const started = await vscode.debug.startDebugging(folder, name);
                            if (!started) throw new BridgeError(Codes.failed, `Debugging ${name} did not start`);
                            return;
                        }
                        for (const part of parts) {
                            const started = await vscode.debug.startDebugging(folder, part);
                            if (!started) throw new BridgeError(Codes.failed, `Debugging ${name} did not start (${part.name})`);
                        }
                    },
                });
            }
        };

        for (const folder of folders) {
            const config = vscode.workspace.getConfiguration('launch', folder.uri);
            const layer = workspaceFile ? 'workspaceFolderValue' : 'workspaceValue';
            const value = (/** @type {string} */ key) => read(config.inspect(key), layer) ?? read(config.inspect(key), 'workspaceFolderValue');
            add(value('configurations'), value('compounds'), folder.uri.toString(), multi ? `launch.json · ${folder.name}` : 'launch.json', folder, true);
        }

        const shared = vscode.workspace.getConfiguration('launch');
        // Outside any folder there is no folder to resolve ${workspaceFolder} against; with one folder, use it.
        const sharedFolder = folders.length === 1 ? folders[0] : undefined;
        if (workspaceFile) {
            add(read(shared.inspect('configurations'), 'workspaceValue'), read(shared.inspect('compounds'), 'workspaceValue'),
                'workspace', 'workspace file', sharedFolder, false);
        }
        add(read(shared.inspect('configurations'), 'globalValue'), read(shared.inspect('compounds'), 'globalValue'),
            'user', 'user settings', sharedFolder, false);
        return entries;
    }

    /** @returns {Entry[]} */
    _problems() {
        const vscode = this._vscode;
        /** @type {{severity: number, file: string, line: number, column: number, uri: import('vscode').Uri, diagnostic: import('vscode').Diagnostic}[]} */
        const all = [];
        for (const [uri, diagnostics] of vscode.languages.getDiagnostics()) {
            const file = vscode.workspace.asRelativePath(uri, false);
            for (const d of diagnostics) {
                if (d.severity > 2) continue;
                all.push({ severity: d.severity, file, line: d.range.start.line, column: d.range.start.character, uri, diagnostic: d });
            }
        }
        /** @type {Set<string>} */
        const used = new Set();
        return sortProblems(all).slice(0, MAX_PROBLEMS).map(p => {
            const message = p.diagnostic.message.replace(/\s+/g, ' ').trim().slice(0, 120);
            const label = `${path.posix.basename(p.uri.path)}:${p.line + 1}`;
            const key = `problem|${p.uri.toString()}|${p.line}|${p.column}|${p.diagnostic.message}`;
            const range = p.diagnostic.range;
            return {
                item: makeItem({ label, detail: message, kind: 'problem', severity: severityName(p.severity) }, key, used),
                run: async () => {
                    await vscode.window.showTextDocument(p.uri, { selection: range, preview: false });
                },
            };
        });
    }

    /** @returns {Entry[]} */
    _editors() {
        const vscode = this._vscode;
        /** @type {Set<string>} */
        const used = new Set();
        /** @type {Entry[]} */
        const entries = [];
        vscode.window.tabGroups.all.forEach((group, g) => {
            group.tabs.forEach((tab, index) => {
                const input = /** @type {any} */ (tab.input);
                const uri = input?.uri ?? input?.modified;
                const dir = uri && typeof uri.path === 'string' ? path.posix.dirname(uri.path) : '.';
                const parent = dir === '.' || dir === '/' ? '' : path.posix.basename(dir);
                const key = `editor|${g}|${uri ? uri.toString() : tab.label}|${index}`;
                entries.push({
                    item: makeItem({
                        label: tab.label, detail: parent, kind: 'editor',
                        dirty: tab.isDirty, active: tab.isActive && group.isActive, group: g + 1,
                    }, key, used),
                    run: async () => {
                        const focus = GROUP_FOCUS[g];
                        if (focus) await vscode.commands.executeCommand(`workbench.action.focus${focus}EditorGroup`);
                        // The tab may have moved since the list was built; find it again.
                        const tabs = vscode.window.tabGroups.all[g]?.tabs ?? [];
                        let now = tabs.indexOf(tab);
                        if (now < 0) now = tabs.findIndex(t => t.label === tab.label && tabKey(t) === tabKey(tab));
                        if (now < 0) throw new BridgeError(Codes.notFound, 'That tab has been closed');
                        await vscode.commands.executeCommand('workbench.action.openEditorAtIndex', now);
                    },
                });
            });
        });
        return entries;
    }

    /** @returns {Promise<Entry[]>} */
    async _recent() {
        const vscode = this._vscode;
        let recent;
        try {
            recent = await vscode.commands.executeCommand('_workbench.getRecentlyOpened');
        } catch {
            return [];
        }
        const workspaces = Array.isArray(/** @type {any} */ (recent)?.workspaces) ? /** @type {any} */ (recent).workspaces : [];
        /** @type {Set<string>} */
        const used = new Set();
        /** @type {Entry[]} */
        const entries = [];
        for (const entry of workspaces) {
            if (entries.length >= MAX_RECENT) break;
            const isWorkspace = !!entry?.workspace;
            const uri = toUri(vscode, isWorkspace ? entry.workspace.configPath : entry?.folderUri);
            if (!uri) continue;
            const base = path.posix.basename(uri.path);
            const label = typeof entry.label === 'string' && entry.label.length > 0 && !entry.label.includes('/')
                ? entry.label
                : isWorkspace ? base.replace(/\.code-workspace$/, '') : base || uri.toString();
            const detail = uri.scheme === 'file' ? uri.fsPath : uri.toString();
            entries.push({
                item: makeItem({ label, detail, kind: isWorkspace ? 'workspace' : 'folder' }, `recent|${uri.toString()}`, used),
                run: async () => {
                    await vscode.commands.executeCommand('vscode.openFolder', uri, { forceNewWindow: false });
                },
            });
        }
        return entries;
    }

    /** @returns {Entry[]} */
    _terminals() {
        const vscode = this._vscode;
        /** @type {Set<string>} */
        const used = new Set();
        return vscode.window.terminals.map((terminal, index) => ({
            item: makeItem({
                label: terminal.name,
                detail: this._options.terminalCommand(terminal) ?? '',
                kind: 'terminal',
                active: vscode.window.activeTerminal === terminal,
            }, `terminal|${index}|${terminal.name}`, used),
            run: async () => {
                if (!vscode.window.terminals.includes(terminal)) throw new BridgeError(Codes.notFound, 'That terminal has been closed');
                terminal.show();
            },
        }));
    }
}

/**
 * Accepts a Uri, the plain `{scheme, path…}` shape internal commands return, or a string.
 * @param {typeof import('vscode')} vscode
 * @param {any} value
 * @returns {import('vscode').Uri | undefined}
 */
function toUri(vscode, value) {
    try {
        if (!value) return undefined;
        if (value instanceof vscode.Uri) return value;
        if (typeof value === 'string') return vscode.Uri.parse(value);
        if (typeof value.scheme === 'string') return vscode.Uri.from(value);
    } catch {
        // Not a usable URI.
    }
    return undefined;
}

module.exports = { Lists, LISTS, MAX_PROBLEMS, refFor, makeItem, severityName, toUri };

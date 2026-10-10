// @ts-check
'use strict';

// The window's live state (the State object in docs/PROTOCOL.md). Editor events mark sections
// dirty; a flush 150 ms later rebuilds those sections and pushes the ones whose JSON changed.
// Cursor moves are throttled to one rebuild per 250 ms on top of that. The tracker also raises
// the `focus`, `lists` and `notify` events.

const path = require('path');
const { errorMessage } = require('./protocol');

const DEBOUNCE_MS = 150;
const CURSOR_THROTTLE_MS = 250;
const LISTS_DEBOUNCE_MS = 300;
/** A burst of changes is pushed after this long at the latest, however busy the editor is. */
const MAX_WAIT_MS = 1_000;
/**
 * A task runs in a terminal, so its end also ends a shell execution. A failed shell command
 * within this long of a task ending with the same code is the task, already reported.
 */
const TASK_ECHO_MS = 1_000;

/** DAP requests that set a paused program running again; adapters answer them without a `continued` event. */
const RESUME_REQUESTS = new Set(['continue', 'next', 'stepIn', 'stepOut', 'stepBack', 'reverseContinue', 'goto', 'restartFrame', 'restart']);
const SECTIONS = Object.freeze(['window', 'workspace', 'editor', 'dirty', 'problems', 'git', 'debug', 'tasks', 'terminal', 'settings']);

/**
 * @typedef {object} TrackerOptions
 * @property {(level: 'info' | 'warn' | 'error', text: string) => void} log
 * @property {(sections: Record<string, unknown>) => void} onState changed sections, each whole
 * @property {(event: Record<string, unknown>) => void} onEvent focus, lists and notify events
 * @property {() => any} git the git section, or null
 * @property {() => boolean} pushDiagnostics whether diagnostics changes are pushed as they happen
 * @property {() => number} [now]
 */

/**
 * Debounce with a ceiling: each call restarts the wait, but the action runs at most `maxWait`
 * after the first call of a burst.
 */
class Debouncer {
    /**
     * @param {() => void} action
     * @param {number} wait
     * @param {number} maxWait
     */
    constructor(action, wait, maxWait) {
        this._action = action;
        this._wait = wait;
        this._maxWait = maxWait;
        /** @type {NodeJS.Timeout | undefined} */
        this._timer = undefined;
        this._firstAt = 0;
    }

    trigger() {
        const now = Date.now();
        if (this._timer) clearTimeout(this._timer);
        else this._firstAt = now;
        const delay = Math.max(0, Math.min(this._wait, this._firstAt + this._maxWait - now));
        this._timer = setTimeout(() => {
            this._timer = undefined;
            this._action();
        }, delay);
        this._timer.unref?.();
    }

    cancel() {
        if (this._timer) clearTimeout(this._timer);
        this._timer = undefined;
    }
}

/**
 * Sorts diagnostics for the problems list: errors, then warnings, then infos; within one
 * severity by file, then line, then column.
 * @template {{severity: number, file: string, line: number, column?: number}} T
 * @param {T[]} problems
 * @returns {T[]} a new array
 */
function sortProblems(problems) {
    return [...problems].sort((a, b) =>
        a.severity - b.severity
        || (a.file < b.file ? -1 : a.file > b.file ? 1 : 0)
        || a.line - b.line
        || (a.column ?? 0) - (b.column ?? 0));
}

/**
 * Counts diagnostics by severity (Error 0, Warning 1, Information 2; hints are not counted).
 * @param {[unknown, {severity: number}[]][]} all the result of `languages.getDiagnostics()`
 */
function countProblems(all) {
    let errors = 0;
    let warnings = 0;
    let infos = 0;
    for (const [, diagnostics] of all) {
        for (const d of diagnostics) {
            if (d.severity === 0) errors++;
            else if (d.severity === 1) warnings++;
            else if (d.severity === 2) infos++;
        }
    }
    return { errors, warnings, infos };
}

/**
 * Picks the sections whose JSON differs from what was last sent, and remembers the new JSON.
 * @param {Record<string, unknown>} built freshly built sections
 * @param {Map<string, string>} lastSent section name → JSON last pushed
 * @returns {Record<string, unknown> | null} the changed sections, or null when nothing changed
 */
function diffSections(built, lastSent) {
    /** @type {Record<string, unknown>} */
    const changed = {};
    let any = false;
    for (const [name, value] of Object.entries(built)) {
        const json = JSON.stringify(value ?? null);
        if (lastSent.get(name) === json) continue;
        lastSent.set(name, json);
        changed[name] = value ?? null;
        any = true;
    }
    return any ? changed : null;
}

/** @param {string} text @param {number} max */
function shorten(text, max) {
    const flat = text.replace(/\s+/g, ' ').trim();
    return flat.length <= max ? flat : flat.slice(0, max - 1) + '…';
}

class StateTracker {
    /**
     * @param {typeof import('vscode')} vscode
     * @param {TrackerOptions} options
     */
    constructor(vscode, options) {
        this._vscode = vscode;
        this._options = options;
        this._now = options.now ?? Date.now;
        /** @type {Set<string>} */
        this._dirty = new Set();
        /** @type {Map<string, string>} */
        this._lastSent = new Map();
        /** @type {Set<string>} */
        this._dirtyLists = new Set();
        this._flusher = new Debouncer(() => this._guard('flush', () => this.flush()), DEBOUNCE_MS, MAX_WAIT_MS);
        this._listFlusher = new Debouncer(() => this._guard('lists', () => this._flushLists()), LISTS_DEBOUNCE_MS, MAX_WAIT_MS);
        this._lastCursorAt = 0;
        /** @type {NodeJS.Timeout | undefined} */
        this._cursorTimer = undefined;
        /** @type {import('vscode').Disposable[]} */
        this._disposables = [];

        const focused = !!vscode.window.state?.focused;
        this._focused = focused;
        this._lastFocused = focused ? this._now() : 0;

        /** @type {{name: string, exitCode: number | null, at: number} | null} */
        this._lastTask = null;
        /** @type {{exitCode: number | null, at: number} | null} the last task process end, to spot echoes */
        this._lastTaskProcess = null;
        /** @type {WeakSet<object>} task executions whose process end was seen */
        this._processEnded = new WeakSet();
        /** @type {{command: string, exitCode: number | null, at: number} | null} */
        this._lastTerminal = null;
        /** @type {WeakMap<object, string>} terminal → last command line */
        this._terminalCommands = new WeakMap();
        /** @type {Map<string, {stopped: boolean, reason: string | null}>} debug session id → stop state */
        this._debugStops = new Map();
        this._wasPaused = false;
    }

    /** The last command line run in a terminal, for the terminals list. */
    terminalCommand(/** @type {object} */ terminal) {
        return this._terminalCommands.get(terminal) ?? null;
    }

    /** Subscribes to the editor's events. */
    start() {
        const vscode = this._vscode;
        const w = vscode.window;
        /**
         * @template T
         * @param {((listener: (e: T) => any) => import('vscode').Disposable) | undefined} event
         * @param {string} name
         * @param {(e: T) => void} listener
         */
        const on = (event, name, listener) => {
            if (typeof event !== 'function') return;
            this._disposables.push(event(e => this._guard(name, () => listener(e))));
        };

        on(w.onDidChangeWindowState, 'window', () => this._onWindowState());
        on(vscode.workspace.onDidChangeWorkspaceFolders, 'folders', () => {
            this.touch('workspace');
            this.touchLists('tasks', 'launch');
        });
        on(w.onDidChangeActiveTextEditor, 'activeEditor', () => this.touch('editor', 'dirty'));
        on(w.onDidChangeTextEditorSelection, 'selection', e => {
            if (e.textEditor === w.activeTextEditor) this.touchCursor();
        });
        on(vscode.workspace.onDidChangeTextDocument, 'document', e => {
            if (e.document === w.activeTextEditor?.document) this.touch('editor');
            this.touch('dirty');
        });
        on(vscode.workspace.onDidSaveTextDocument, 'save', () => this.touch('editor', 'dirty'));
        on(vscode.workspace.onDidCloseTextDocument, 'close', () => this.touch('dirty'));
        on(w.tabGroups?.onDidChangeTabs, 'tabs', () => {
            this.touch('dirty');
            this.touchLists('editors');
        });
        on(w.tabGroups?.onDidChangeTabGroups, 'tabGroups', () => this.touchLists('editors'));
        on(vscode.languages.onDidChangeDiagnostics, 'diagnostics', () => {
            if (!this._options.pushDiagnostics()) return;
            this.touch('problems');
            this.touchLists('problems');
        });

        on(vscode.debug.onDidStartDebugSession, 'debugStart', () => this._debugChanged());
        on(vscode.debug.onDidTerminateDebugSession, 'debugEnd', s => {
            this._debugStops.delete(s.id);
            this._debugChanged();
        });
        on(vscode.debug.onDidChangeActiveDebugSession, 'debugActive', () => this._debugChanged());
        on(vscode.debug.onDidChangeActiveStackItem, 'stackItem', () => this._debugChanged());
        if (typeof vscode.debug.registerDebugAdapterTrackerFactory === 'function') {
            try {
                this._disposables.push(vscode.debug.registerDebugAdapterTrackerFactory('*', {
                    createDebugAdapterTracker: session => ({
                        onDidSendMessage: message => this._guard('dapTracker', () => this._onAdapterMessage(session.id, message)),
                        onWillReceiveMessage: message => this._guard('dapTracker', () => this._onClientMessage(session.id, message)),
                    }),
                }));
            } catch (error) {
                this._options.log('warn', `Debug adapter tracker unavailable: ${errorMessage(error)}`);
            }
        }

        on(vscode.tasks.onDidStartTask, 'taskStart', () => {
            this.touch('tasks');
        });
        on(vscode.tasks.onDidEndTaskProcess, 'taskProcessEnd', e => this._onTaskProcessEnd(e));
        on(vscode.tasks.onDidEndTask, 'taskEnd', e => {
            if (!this._processEnded.has(e.execution)) {
                this._lastTask = { name: e.execution.task.name, exitCode: null, at: this._now() };
            }
            this.touch('tasks');
        });

        on(w.onDidOpenTerminal, 'terminalOpen', () => {
            this.touch('terminal');
            this.touchLists('terminals');
        });
        on(w.onDidCloseTerminal, 'terminalClose', () => {
            this.touch('terminal');
            this.touchLists('terminals');
        });
        on(w.onDidChangeActiveTerminal, 'terminalActive', () => this.touchLists('terminals'));
        on(w.onDidEndTerminalShellExecution, 'shellEnd', e => this._onShellEnd(e));

        on(vscode.workspace.onDidChangeConfiguration, 'configuration', e => {
            if (e.affectsConfiguration('editor.accessibilitySupport')
                || e.affectsConfiguration('editor.wordWrap')
                || e.affectsConfiguration('editor.minimap.enabled')) this.touch('settings');
            if (e.affectsConfiguration('launch')) this.touchLists('launch');
            if (e.affectsConfiguration('tasks')) this.touchLists('tasks');
        });
    }

    dispose() {
        this._flusher.cancel();
        this._listFlusher.cancel();
        if (this._cursorTimer) clearTimeout(this._cursorTimer);
        for (const d of this._disposables) {
            try {
                d.dispose();
            } catch {
                // Nothing to do.
            }
        }
        this._disposables = [];
    }

    /**
     * Marks sections as changed; they are rebuilt and pushed after the debounce.
     * @param {...string} sections
     */
    touch(...sections) {
        for (const s of sections) this._dirty.add(s);
        this._flusher.trigger();
    }

    /** A cursor move: rebuild the editor section at most once per 250 ms. */
    touchCursor() {
        if (this._cursorTimer) return;
        const wait = this._lastCursorAt + CURSOR_THROTTLE_MS - this._now();
        if (wait <= 0) {
            this._lastCursorAt = this._now();
            this.touch('editor');
            return;
        }
        this._cursorTimer = setTimeout(() => {
            this._cursorTimer = undefined;
            this._lastCursorAt = this._now();
            this.touch('editor');
        }, wait);
        this._cursorTimer.unref?.();
    }

    /** @param {...string} lists */
    touchLists(...lists) {
        for (const l of lists) this._dirtyLists.add(l);
        this._listFlusher.trigger();
    }

    /** Rebuilds the dirty sections now and pushes the ones that changed. */
    flush() {
        if (this._dirty.size === 0) return;
        const names = [...this._dirty];
        this._dirty.clear();
        /** @type {Record<string, unknown>} */
        const built = {};
        for (const name of names) built[name] = this.build(name);
        const changed = diffSections(built, this._lastSent);
        if (changed) this._options.onState(changed);
    }

    _flushLists() {
        if (this._dirtyLists.size === 0) return;
        const what = [...this._dirtyLists].sort();
        this._dirtyLists.clear();
        this._options.onEvent({ ev: 'lists', what });
    }

    /** A full State snapshot (for hello and the `state` op). Does not change what counts as sent. */
    snapshot() {
        /** @type {Record<string, unknown>} */
        const state = {};
        for (const name of SECTIONS) state[name] = this.build(name);
        return state;
    }

    /**
     * Builds one section. A builder that throws gives null rather than breaking the push.
     * @param {string} name
     */
    build(name) {
        try {
            switch (name) {
                case 'window': return this._window();
                case 'workspace': return this._workspace();
                case 'editor': return this._editor();
                case 'dirty': return this._dirtyCount();
                case 'problems': return countProblems(this._vscode.languages.getDiagnostics());
                case 'git': return this._options.git() ?? null;
                case 'debug': return this._debug();
                case 'tasks': return this._tasks();
                case 'terminal': return this._terminal();
                case 'settings': return this._settings();
                default: return null;
            }
        } catch (error) {
            this._options.log('error', `State section ${name}: ${errorMessage(error)}`);
            return null;
        }
    }

    _window() {
        const state = this._vscode.window.state;
        const focused = !!state?.focused;
        return { focused, active: typeof state?.active === 'boolean' ? state.active : focused, lastFocused: this._lastFocused || null };
    }

    _workspace() {
        const ws = this._vscode.workspace;
        return {
            name: ws.name ?? null,
            folders: (ws.workspaceFolders ?? []).map(f => f.uri.toString()),
            remote: this._vscode.env.remoteName ?? null,
        };
    }

    _editor() {
        const editor = this._vscode.window.activeTextEditor;
        if (!editor) return null;
        const doc = editor.document;
        const uri = doc.uri;
        const active = editor.selection?.active;
        return {
            file: path.posix.basename(uri.path) || doc.fileName,
            path: uri.scheme === 'file' ? uri.fsPath : uri.toString(),
            language: doc.languageId,
            dirty: doc.isDirty,
            line: active ? active.line + 1 : 1,
            column: active ? active.character + 1 : 1,
            selections: editor.selections?.length ?? 1,
        };
    }

    /** Dirty editors, counted once per resource: text documents plus tabs such as notebooks. */
    _dirtyCount() {
        /** @type {Set<string>} */
        const seen = new Set();
        const groups = this._vscode.window.tabGroups?.all ?? [];
        for (const group of groups) {
            for (const tab of group.tabs) {
                if (!tab.isDirty) continue;
                seen.add(tabKey(tab));
            }
        }
        for (const doc of this._vscode.workspace.textDocuments) {
            if (doc.isDirty && doc.uri.scheme !== 'output') seen.add(doc.uri.toString());
        }
        return { count: seen.size };
    }

    _debug() {
        const debug = this._vscode.debug;
        const session = debug.activeDebugSession;
        if (!session) return { state: 'inactive', session: null, reason: null };
        const stop = this._debugStops.get(session.id);
        // The adapter tracker knows best. Without it, only a selected stack frame proves a pause:
        // activeStackItem can stay a thread while the program runs.
        const paused = stop ? stop.stopped : this._isStackFrame(debug.activeStackItem);
        return { state: paused ? 'paused' : 'running', session: session.name, reason: paused ? stop?.reason ?? null : null };
    }

    _tasks() {
        /** @type {string[]} */
        const running = [];
        for (const execution of this._vscode.tasks.taskExecutions ?? []) {
            const name = execution.task.name;
            if (!running.includes(name)) running.push(name);
        }
        return { running, last: this._lastTask };
    }

    _terminal() {
        return { count: this._vscode.window.terminals.length, last: this._lastTerminal };
    }

    _settings() {
        const editor = this._vscode.workspace.getConfiguration('editor');
        const support = editor.get('accessibilitySupport');
        return {
            accessibilitySupport: support === 'on' || support === 'off' ? support : 'auto',
            wordWrap: String(editor.get('wordWrap') ?? 'off'),
            minimap: editor.get('minimap.enabled') !== false,
        };
    }

    _onWindowState() {
        const state = this._vscode.window.state;
        const now = this._now();
        // `at` is when the window last gained focus, so only a blur-to-focus change moves it.
        if (state.focused && !this._focused) this._lastFocused = now;
        this._focused = !!state.focused;
        this._options.onEvent({
            ev: 'focus',
            focused: !!state.focused,
            active: typeof state.active === 'boolean' ? state.active : !!state.focused,
            at: state.focused ? this._lastFocused : now,
        });
        this.touch('window');
    }

    /**
     * @param {string} sessionId
     * @param {any} message a Debug Adapter Protocol message
     */
    _onAdapterMessage(sessionId, message) {
        if (!message || message.type !== 'event') return;
        if (message.event === 'stopped') {
            this._debugStops.set(sessionId, { stopped: true, reason: typeof message.body?.reason === 'string' ? message.body.reason : null });
        } else if (message.event === 'continued') {
            this._debugStops.set(sessionId, { stopped: false, reason: null });
        } else if (message.event === 'terminated' || message.event === 'exited') {
            this._debugStops.delete(sessionId);
        } else {
            return;
        }
        this._debugChanged();
    }

    /** @param {unknown} item */
    _isStackFrame(item) {
        if (!item) return false;
        const frameClass = this._vscode.DebugStackFrame;
        if (typeof frameClass === 'function') return item instanceof frameClass;
        return typeof (/** @type {any} */ (item).frameId) === 'number';
    }

    /**
     * Requests VS Code sends to the adapter. Adapters do not send `continued` for a continue or
     * step request they were asked for (the DAP spec says so), so the session counts as running
     * from the moment one goes out; the next `stopped` event marks it paused again.
     * @param {string} sessionId
     * @param {any} message a Debug Adapter Protocol message
     */
    _onClientMessage(sessionId, message) {
        if (!message || message.type !== 'request' || !RESUME_REQUESTS.has(message.command)) return;
        const was = this._debugStops.get(sessionId);
        this._debugStops.set(sessionId, { stopped: false, reason: null });
        if (was?.stopped !== false) this._debugChanged();
    }

    /** Pushes the debug section and raises `debugStopped` when the session has just paused. */
    _debugChanged() {
        this.touch('debug');
        const debug = /** @type {{state: string, session: string | null, reason: string | null}} */ (this._debug());
        const paused = debug.state === 'paused';
        if (paused && !this._wasPaused) {
            const where = debug.reason === 'exception' ? 'on exception'
                : debug.reason === 'breakpoint' ? 'at breakpoint'
                : debug.reason ? `(${debug.reason})` : '';
            this._options.onEvent({ ev: 'notify', kind: 'debugStopped', text: shorten(`Paused ${where}`, 60) });
        }
        this._wasPaused = paused;
    }

    /** @param {import('vscode').TaskProcessEndEvent} e */
    _onTaskProcessEnd(e) {
        this._processEnded.add(e.execution);
        const name = e.execution.task.name;
        const exitCode = typeof e.exitCode === 'number' ? e.exitCode : null;
        this._lastTask = { name, exitCode, at: this._now() };
        this._lastTaskProcess = { exitCode, at: this._now() };
        this.touch('tasks');
        if (exitCode === 0) {
            this._options.onEvent({ ev: 'notify', kind: 'taskSucceeded', text: shorten(`${name} succeeded`, 60) });
        } else if (exitCode !== null) {
            this._options.onEvent({ ev: 'notify', kind: 'taskFailed', text: shorten(`${name} exited with ${exitCode}`, 60) });
        }
    }

    /** @param {import('vscode').TerminalShellExecutionEndEvent} e */
    _onShellEnd(e) {
        const command = e.execution?.commandLine?.value ?? '';
        const exitCode = typeof e.exitCode === 'number' ? e.exitCode : null;
        this._lastTerminal = { command, exitCode, at: this._now() };
        if (e.terminal) this._terminalCommands.set(e.terminal, command);
        this.touch('terminal');
        this.touchLists('terminals');
        if (exitCode === null || exitCode === 0) return;
        // Wait briefly: the task's own end event can arrive just before or just after this one.
        const endedAt = this._now();
        const timer = setTimeout(() => this._guard('terminalFailed', () => {
            const task = this._lastTaskProcess;
            if (task && task.exitCode === exitCode && Math.abs(task.at - endedAt) <= TASK_ECHO_MS) return;
            this._options.onEvent({ ev: 'notify', kind: 'terminalFailed', text: `${shorten(command, 40)} exited with ${exitCode}` });
        }), 300);
        timer.unref?.();
    }


    /**
     * @param {string} name
     * @param {() => void} action
     */
    _guard(name, action) {
        try {
            action();
        } catch (error) {
            this._options.log('error', `Event ${name}: ${errorMessage(error)}`);
        }
    }
}

/**
 * A key for a tab's resource, so the same file in two groups counts once.
 * @param {any} tab
 */
function tabKey(tab) {
    const input = tab.input;
    const uri = input?.uri ?? input?.modified;
    if (uri && typeof uri.toString === 'function') return uri.toString();
    return `tab:${tab.label}`;
}

module.exports = {
    StateTracker,
    Debouncer,
    SECTIONS,
    DEBOUNCE_MS,
    CURSOR_THROTTLE_MS,
    sortProblems,
    countProblems,
    diffSections,
    shorten,
    tabKey,
};

// @ts-check
'use strict';

// The `git` state section. In a local window it comes from the built-in git extension's API and
// its change events. In a remote window that API lives in the other extension host, so the
// tracker polls the git extension's plain-data `git.api.*` commands instead; it does the same
// when the API cannot be had locally (for example the git extension is disabled).

const { errorMessage } = require('./protocol');

const POLL_FOCUSED_MS = 2_000;
const POLL_UNFOCUSED_MS = 10_000;

/**
 * @typedef {{branch: string | null, detached: boolean, ahead: number, behind: number,
 *   changes: number, staged: number, conflicts: number, repos: number, upstream: boolean}} GitState
 */

/**
 * Builds the section from one repository's state. Takes both the API's objects (numeric
 * `HEAD.type`, `untrackedChanges`) and the `git.api.getRepositoryState` command's plain data
 * (string `HEAD.type`, no untracked list).
 * @param {any} state
 * @param {number} repos
 * @returns {GitState}
 */
function summarise(state, repos) {
    const head = state?.HEAD;
    const type = head?.type;
    const isBranch = type === undefined || type === 0 || type === 'Head';
    const name = typeof head?.name === 'string' && head.name.length > 0 ? head.name : null;
    const commit = typeof head?.commit === 'string' ? head.commit : null;
    const detached = !name || !isBranch;
    const count = /** @param {unknown} list */ list => (Array.isArray(list) ? list.length : 0);
    return {
        branch: name ?? (commit ? commit.slice(0, 7) : null),
        detached,
        ahead: Number.isFinite(head?.ahead) ? head.ahead : 0,
        behind: Number.isFinite(head?.behind) ? head.behind : 0,
        changes: count(state?.workingTreeChanges) + count(state?.untrackedChanges) + count(state?.mergeChanges),
        staged: count(state?.indexChanges),
        conflicts: count(state?.mergeChanges),
        repos,
        upstream: !!head?.upstream,
    };
}

class GitTracker {
    /**
     * @param {typeof import('vscode')} vscode
     * @param {{log: (level: 'info' | 'warn' | 'error', text: string) => void, onChange: () => void,
     *   isFocused: () => boolean}} options
     */
    constructor(vscode, options) {
        this._vscode = vscode;
        this._options = options;
        /** @type {GitState | null} */
        this.current = null;
        /** @type {any} the git extension's API, when it is usable here */
        this._api = undefined;
        /** @type {import('vscode').Disposable[]} */
        this._disposables = [];
        /** @type {Map<any, import('vscode').Disposable[]>} */
        this._repoListeners = new Map();
        /** @type {NodeJS.Timeout | undefined} */
        this._timer = undefined;
        this._polling = false;
        this._busy = false;
        this._disposed = false;
        /** bumped by dispose and restart, so work that was awaiting can tell it is stale */
        this._generation = 0;
    }

    /** 'api', 'poll' or 'off', for the log and the status quick pick. */
    get mode() {
        if (this._api) return 'api';
        return this._polling ? 'poll' : 'off';
    }

    async start() {
        const generation = this._generation;
        if (!this._vscode.env.remoteName && await this._tryApi()) return;
        // Disposed or restarted while the git extension activated: the newer start owns the mode.
        if (this._disposed || generation !== this._generation) return;
        this._startPolling();
    }

    /** Asks for a fresh read soon, for example after a save or when the window gains focus. */
    poke() {
        if (this._polling) this._schedule(0);
    }

    /** Re-times the poll after a focus change. */
    focusChanged() {
        if (this._polling) this._schedule(this._options.isFocused() ? 0 : POLL_UNFOCUSED_MS);
    }

    dispose() {
        this._disposed = true;
        this._generation++;
        if (this._timer) clearTimeout(this._timer);
        for (const listeners of this._repoListeners.values()) listeners.forEach(d => safeDispose(d));
        this._repoListeners.clear();
        this._disposables.forEach(d => safeDispose(d));
        this._disposables = [];
    }

    /** @returns {Promise<boolean>} true when the local API is in use */
    async _tryApi() {
        const generation = this._generation;
        try {
            const extension = this._vscode.extensions.getExtension('vscode.git');
            if (!extension) return false;
            const exports = extension.isActive ? extension.exports : await extension.activate();
            // Disposed (or restarted) while the git extension activated: subscribe to nothing.
            if (this._disposed || generation !== this._generation) return false;
            if (!exports || typeof exports.getAPI !== 'function') return false;
            if (typeof exports.onDidChangeEnablement === 'function') {
                this._disposables.push(exports.onDidChangeEnablement(() => this._guard(() => this._restart())));
            }
            if (exports.enabled === false) return false;
            const api = exports.getAPI(1);
            this._api = api;
            this._disposables.push(
                api.onDidOpenRepository(/** @param {any} repo */ repo => this._guard(() => this._watchRepo(repo))),
                api.onDidCloseRepository(/** @param {any} repo */ repo => this._guard(() => this._unwatchRepo(repo))),
            );
            if (typeof api.onDidChangeState === 'function') {
                this._disposables.push(api.onDidChangeState(() => this._guard(() => this._readApi())));
            }
            for (const repo of api.repositories) this._watchRepo(repo);
            this._readApi();
            this._options.log('info', 'Git: using the git extension API');
            return true;
        } catch (error) {
            this._api = undefined;
            this._options.log('warn', `Git: extension API unavailable (${errorMessage(error)}); polling instead`);
            return false;
        }
    }

    /** The git extension was enabled or disabled: start over. */
    async _restart() {
        if (this._disposed) return;
        this.dispose();
        this._disposed = false;
        this._api = undefined;
        this._polling = false;
        // A poll still awaiting from before belongs to the old generation; it must not hold the
        // busy flag or reschedule itself.
        this._busy = false;
        await this.start();
        this._options.onChange();
    }

    /** @param {any} repo */
    _watchRepo(repo) {
        if (this._repoListeners.has(repo)) return;
        const listeners = [repo.state.onDidChange(() => this._guard(() => this._readApi()))];
        if (repo.ui && typeof repo.ui.onDidChange === 'function') {
            listeners.push(repo.ui.onDidChange(() => this._guard(() => this._readApi())));
        }
        this._repoListeners.set(repo, listeners);
        this._readApi();
    }

    /** @param {any} repo */
    _unwatchRepo(repo) {
        this._repoListeners.get(repo)?.forEach(d => safeDispose(d));
        this._repoListeners.delete(repo);
        this._readApi();
    }

    _readApi() {
        const repos = this._api ? this._api.repositories : [];
        if (!repos || repos.length === 0) {
            this._set(null);
            return;
        }
        const repo = repos.find(/** @param {any} r */ r => r.ui?.selected) ?? repos[0];
        this._set(summarise(repo.state, repos.length));
    }

    _startPolling() {
        this._polling = true;
        this._options.log('info', 'Git: polling the git.api commands');
        this._schedule(0);
    }

    /** @param {number} delay */
    _schedule(delay) {
        if (this._disposed || !this._polling) return;
        if (this._timer) clearTimeout(this._timer);
        this._timer = setTimeout(() => this._guard(() => this._poll()), delay);
        this._timer.unref?.();
    }

    async _poll() {
        if (this._busy || this._disposed || !this._polling) return;
        const generation = this._generation;
        this._busy = true;
        try {
            const commands = this._vscode.commands;
            const roots = await commands.executeCommand('git.api.getRepositories');
            if (generation !== this._generation) return;
            if (!Array.isArray(roots) || roots.length === 0) {
                this._set(null);
            } else {
                const state = await commands.executeCommand('git.api.getRepositoryState', String(roots[0]));
                if (generation !== this._generation) return;
                this._set(state ? summarise(state, roots.length) : null);
            }
        } catch {
            // The git extension is not there or not ready yet.
            if (generation === this._generation) this._set(null);
        } finally {
            if (generation === this._generation) {
                this._busy = false;
                this._schedule(this._options.isFocused() ? POLL_FOCUSED_MS : POLL_UNFOCUSED_MS);
            }
        }
    }

    /** @param {GitState | null} next */
    _set(next) {
        if (JSON.stringify(next) === JSON.stringify(this.current)) return;
        this.current = next;
        this._options.onChange();
    }

    /** @param {() => unknown} action */
    _guard(action) {
        try {
            const result = action();
            if (result instanceof Promise) {
                result.catch(error => this._options.log('error', `Git: ${errorMessage(error)}`));
            }
        } catch (error) {
            this._options.log('error', `Git: ${errorMessage(error)}`);
        }
    }
}

/** @param {{dispose(): unknown}} disposable */
function safeDispose(disposable) {
    try {
        disposable.dispose();
    } catch {
        // Nothing to do.
    }
}

module.exports = { GitTracker, summarise, POLL_FOCUSED_MS, POLL_UNFOCUSED_MS };

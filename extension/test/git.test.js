// @ts-check
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { GitTracker } = require('../lib/git');

/** A promise with its resolve function on the side. */
function deferred() {
    /** @type {(value?: any) => void} */
    let resolve = () => {};
    /** @type {Promise<any>} */
    const promise = new Promise(r => { resolve = r; });
    return { promise, resolve };
}

const tick = () => new Promise(r => setImmediate(r));

/**
 * A minimal vscode with a git extension whose activation and git.api commands the test controls.
 * @param {{enabled: boolean, active?: boolean}} options
 */
function fakeVscode(options) {
    let subscriptions = 0;
    /** @type {(() => void)[]} */
    const enablementListeners = [];
    const activation = deferred();
    /** @type {ReturnType<typeof deferred>[]} */
    const pendingCommands = [];
    const sub = () => { subscriptions++; return { dispose() { subscriptions--; } }; };
    const api = {
        repositories: [],
        onDidOpenRepository: sub,
        onDidCloseRepository: sub,
        onDidChangeState: sub,
    };
    const exports = {
        enabled: options.enabled,
        getAPI: () => api,
        /** @param {() => void} listener */
        onDidChangeEnablement(listener) {
            enablementListeners.push(listener);
            return sub();
        },
    };
    const vscode = {
        env: { remoteName: undefined },
        extensions: {
            getExtension: () => ({
                isActive: !!options.active,
                exports,
                activate: () => activation.promise.then(() => exports),
            }),
        },
        commands: {
            executeCommand() {
                const d = deferred();
                pendingCommands.push(d);
                return d.promise;
            },
        },
    };
    return {
        vscode: /** @type {any} */ (vscode),
        exports,
        activation,
        pendingCommands,
        fireEnablement: () => enablementListeners.forEach(l => l()),
        get subscriptions() { return subscriptions; },
    };
}

const options = { log() {}, onChange() {}, isFocused: () => true };

test('a tracker disposed while the git extension activates subscribes to nothing and does not poll', async () => {
    const fake = fakeVscode({ enabled: true });
    const git = new GitTracker(fake.vscode, options);
    const started = git.start();
    git.dispose();
    fake.activation.resolve();
    await started;
    assert.equal(fake.subscriptions, 0);
    assert.equal(git.mode, 'off');
    assert.equal(fake.pendingCommands.length, 0);
});

test('turning git back on during a poll leaves the API alone, with no poll loop beside it', async () => {
    const fake = fakeVscode({ enabled: false, active: true });
    const git = new GitTracker(fake.vscode, options);
    await git.start();
    assert.equal(git.mode, 'poll');
    await new Promise(r => setTimeout(r, 5));
    assert.equal(fake.pendingCommands.length, 1, 'a poll is awaiting git.api.getRepositories');

    fake.exports.enabled = true;
    fake.fireEnablement();
    await tick();
    assert.equal(git.mode, 'api');

    let scheduled = 0;
    const schedule = git._schedule.bind(git);
    git._schedule = (/** @type {number} */ delay) => { scheduled++; schedule(delay); };
    fake.pendingCommands[0].resolve(['/work/demo']);
    await tick();
    await tick();
    assert.equal(scheduled, 0, 'the stale poll does not reschedule');
    assert.equal(fake.pendingCommands.length, 1, 'and does not go on to read the repository');
    git.dispose();
});

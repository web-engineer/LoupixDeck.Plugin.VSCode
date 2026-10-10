// @ts-check
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createHandler } = require('../lib/handlers');
const { Lists } = require('../lib/lists');
const { createVscode, Uri } = require('./support/vscode-stub');

/**
 * @param {{policy?: string, allowed?: string[], focusWindow?: boolean}} [options]
 */
function setup(options = {}) {
    const { vscode, control } = createVscode();
    /** @type {string[]} */
    const commands = [];
    /** @type {string[]} */
    const logs = [];
    const lists = new Lists(/** @type {any} */ (vscode), { terminalCommand: () => null });
    const handle = createHandler(/** @type {any} */ (vscode), {
        lists,
        tracker: { snapshot: () => ({ dirty: { count: 1 } }) },
        policy: () => ({ policy: options.policy ?? 'any', allowed: options.allowed ?? [] }),
        hasFocusWindow: () => options.focusWindow ?? false,
        log: (level, text) => logs.push(`${level} ${text}`),
        onCommand: command => commands.push(command),
    });
    return { vscode, control, handle, commands, logs };
}

/**
 * @param {Promise<unknown>} promise
 * @param {string} code
 */
async function rejectsWith(promise, code) {
    await assert.rejects(promise, (/** @type {any} */ e) => {
        assert.equal(e.code, code);
        return true;
    });
}

test('exec runs the command with revived $uri arguments and returns a JSON-safe result', async () => {
    const { control, handle, commands, logs } = setup();
    /** @type {any[]} */
    let received = [];
    control.commandHandlers.set('demo.open', (...args) => {
        received = args;
        return { opened: args[0], fn: () => 1 };
    });
    const result = await handle('exec', { id: 1, op: 'exec', command: 'demo.open', args: [{ $uri: 'file:///a/b.ts' }, { line: 3 }] });
    assert.ok(received[0] instanceof Uri);
    assert.equal(received[0].path, '/a/b.ts');
    assert.deepEqual(received[1], { line: 3 });
    assert.deepEqual(result, { opened: { $uri: 'file:///a/b.ts' } });
    assert.deepEqual(commands, ['demo.open']);
    assert.ok(logs.includes('info exec demo.open'));
});

test('exec without args calls the command with none', async () => {
    const { control, handle } = setup();
    let count = -1;
    control.commandHandlers.set('demo.none', (...args) => { count = args.length; });
    assert.equal(await handle('exec', { command: 'demo.none' }), null);
    assert.equal(count, 0);
});

test('exec maps failures to error codes', async () => {
    const { control, handle } = setup();
    control.commandHandlers.set('demo.throws', () => { throw new Error('boom'); });
    await rejectsWith(handle('exec', { command: 'not.there' }), 'unknown_command');
    await rejectsWith(handle('exec', { command: 'demo.throws' }), 'failed');
    await rejectsWith(handle('exec', { command: '' }), 'bad_request');
    await rejectsWith(handle('exec', { command: 7 }), 'bad_request');
    await rejectsWith(handle('exec', { command: 'demo.throws', args: 'x' }), 'bad_request');
    await rejectsWith(handle('wat', {}), 'unknown_op');
});

test('allowList refuses commands that are not listed but keeps the other ops', async () => {
    const { control, handle } = setup({ policy: 'allowList', allowed: ['demo.ok'] });
    control.commandHandlers.set('demo.ok', () => 'fine');
    control.commandHandlers.set('demo.no', () => 'ran');
    assert.equal(await handle('exec', { command: 'demo.ok' }), 'fine');
    await rejectsWith(handle('exec', { command: 'demo.no' }), 'denied');
    assert.ok(!control.executed.some(e => e.command === 'demo.no'));
    assert.equal(await handle('ping', {}), 'pong');
    assert.deepEqual(await handle('state', {}), { dirty: { count: 1 } });
    control.setTasks([{ name: 'build', source: 'Workspace', scope: 1 }]);
    assert.equal(await handle('runTask', { name: 'build' }), null);
});

test('focus: true runs workbench.action.focusWindow first when the window has it', async () => {
    const withFocus = setup({ focusWindow: true });
    withFocus.control.commandHandlers.set('workbench.action.focusWindow', () => undefined);
    withFocus.control.commandHandlers.set('demo.x', () => undefined);
    await withFocus.handle('exec', { command: 'demo.x', focus: true });
    assert.deepEqual(withFocus.control.executed.map(e => e.command), ['workbench.action.focusWindow', 'demo.x']);
    await withFocus.handle('focus', {});
    assert.equal(withFocus.control.executed.at(-1)?.command, 'workbench.action.focusWindow');

    const without = setup({ focusWindow: false });
    without.control.commandHandlers.set('demo.x', () => undefined);
    await without.handle('exec', { command: 'demo.x', focus: true });
    assert.deepEqual(without.control.executed.map(e => e.command), ['demo.x']);
    assert.equal(await without.handle('focus', {}), null);
});

test('runTask and startDebug report not_found', async () => {
    const { control, handle } = setup();
    control.setTasks([]);
    await rejectsWith(handle('runTask', { name: 'missing' }), 'not_found');
    await rejectsWith(handle('startDebug', { name: 'missing' }), 'not_found');
    await rejectsWith(handle('run', { what: 'tasks', ref: 'stale' }), 'not_found');
    await rejectsWith(handle('list', { what: 'bogus' }), 'bad_request');
});

// @ts-check
'use strict';

// Loads extension.js against the stub `vscode` module, starts the real server in a temporary
// registration directory and talks to it over the socket like the plugin does.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const Module = require('module');
const { createVscode } = require('./support/vscode-stub');
const { connect } = require('./support/client');

const unix = { skip: process.platform === 'win32' };

/** @param {number} ms */
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

/**
 * Loads a fresh copy of extension.js with `require('vscode')` answered by the stub.
 * @param {any} vscode
 */
function loadExtension(vscode) {
    const moduleAny = /** @type {any} */ (Module);
    const original = moduleAny._load;
    moduleAny._load = function (/** @type {string} */ request, /** @type {any[]} */ ...rest) {
        if (request === 'vscode') return vscode;
        return original.call(this, request, ...rest);
    };
    try {
        const file = require.resolve('../extension.js');
        delete require.cache[file];
        return require(file);
    } finally {
        moduleAny._load = original;
    }
}

/** @param {string} dir @param {number} [timeout] */
async function waitForEntry(dir, timeout = 3000) {
    const file = path.join(dir, `${process.pid}.json`);
    const started = Date.now();
    while (!fs.existsSync(file)) {
        if (Date.now() - started > timeout) throw new Error('no registration file');
        await sleep(20);
    }
    return { file, entry: JSON.parse(fs.readFileSync(file, 'utf8')) };
}

test('activate registers the window, serves hello/exec/state, and deactivate cleans up', unix, async t => {
    const dir = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ldk-')), 'reg');
    const previous = process.env.LOUPIXDECK_VSCODE_DIR;
    process.env.LOUPIXDECK_VSCODE_DIR = dir;
    t.after(() => {
        if (previous === undefined) delete process.env.LOUPIXDECK_VSCODE_DIR;
        else process.env.LOUPIXDECK_VSCODE_DIR = previous;
    });

    // A stale entry from a dead window is cleared at start-up.
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, '4000000.json'), JSON.stringify({ pid: 4000000, socket: path.join(dir, '4000000.sock') }));

    const { vscode, control } = createVscode();
    control.commandHandlers.set('workbench.action.focusWindow', () => undefined);
    control.commandHandlers.set('demo.add', (/** @type {number} */ a, /** @type {number} */ b) => a + b);
    control.openEditor('/work/demo/index.js', { line: 4, character: 1 });
    const extension = loadExtension(vscode);
    /** @type {any[]} */
    const subscriptions = [];
    extension.activate({ subscriptions, extension: { packageJSON: { version: '0.1.0' } } });

    const { file, entry } = await waitForEntry(dir);
    assert.equal(fs.statSync(dir).mode & 0o777, 0o700);
    assert.equal(fs.statSync(file).mode & 0o777, 0o600);
    assert.equal(fs.existsSync(path.join(dir, '4000000.json')), false);
    assert.equal(entry.v, 1);
    assert.equal(entry.pid, process.pid);
    assert.equal(entry.socket, path.join(dir, `${process.pid}.sock`));
    assert.equal(entry.app, 'Visual Studio Code');
    assert.equal(entry.workspaceName, 'demo');
    assert.deepEqual(entry.folders, ['file:///work/demo']);
    assert.equal(entry.extVersion, '0.1.0');
    assert.equal(typeof entry.token, 'string');

    const status = control.statusItems[0];
    assert.equal(status.text, '$(debug-disconnect) Deck');
    assert.equal(status.visible, true);

    const bad = await connect(entry.socket);
    bad.send({ id: 1, op: 'hello', token: 'nope', client: 'test', protocol: 1 });
    await bad.waitClosed();

    const client = await connect(entry.socket);
    const hello = await client.request({ id: 1, op: 'hello', token: entry.token, client: 'test/1', protocol: 1 });
    assert.equal(hello.ok, true);
    assert.equal(hello.result.protocol, 1);
    assert.equal(hello.result.pid, process.pid);
    assert.equal(hello.result.focusWindow, true);
    assert.equal(hello.result.extVersion, '0.1.0');
    assert.equal(hello.result.state.editor.line, 5);
    assert.equal(status.text, '$(plug) Deck');
    assert.ok(control.executed.some(e => e.command === 'setContext' && e.args[0] === 'loupixdeck.connected' && e.args[1] === true));

    const sum = await client.request({ id: 2, op: 'exec', command: 'demo.add', args: [2, 3] });
    assert.deepEqual(sum, { id: 2, ok: true, result: 5 });
    assert.ok(control.logLines.includes('info exec demo.add'));
    assert.ok(!control.logLines.some(line => line.includes(entry.token)), 'the token is never logged');

    control.settings['loupixdeck.commandPolicy'] = 'allowList';
    const denied = await client.request({ id: 3, op: 'exec', command: 'demo.add', args: [1, 1] });
    assert.equal(denied.error.code, 'denied');
    // A workspace's .vscode/settings.json cannot loosen the user's policy.
    control.workspaceSettings['loupixdeck.commandPolicy'] = 'any';
    control.workspaceSettings['loupixdeck.allowedCommands'] = ['demo.add'];
    const stillDenied = await client.request({ id: 31, op: 'exec', command: 'demo.add', args: [1, 1] });
    assert.equal(stillDenied.error.code, 'denied');
    delete control.workspaceSettings['loupixdeck.commandPolicy'];
    delete control.workspaceSettings['loupixdeck.allowedCommands'];
    control.settings['loupixdeck.commandPolicy'] = 'any';

    const doc = vscode.window.activeTextEditor.document;
    doc.isDirty = true;
    control.events.textDocument.fire({ document: doc });
    const event = await client.waitFor(m => m.ev === 'state' && m.data.dirty);
    assert.deepEqual(event.data.dirty, { count: 1 });

    assert.equal((await client.request({ id: 4, op: 'ping' })).result, 'pong');

    // The status quick pick lists the client.
    await control.commandHandlers.get('loupixdeck.showStatus')?.();
    assert.ok(control.quickPicks[0].some((/** @type {any} */ item) => item.label.includes('test/1')));

    extension.deactivate();
    await client.waitClosed();
    assert.equal(fs.existsSync(file), false);
    assert.equal(fs.existsSync(entry.socket), false);
});

test('loupixdeck.enabled false stops the server and removes the entry', unix, async t => {
    const dir = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ldk-')), 'reg');
    const previous = process.env.LOUPIXDECK_VSCODE_DIR;
    process.env.LOUPIXDECK_VSCODE_DIR = dir;
    t.after(() => {
        if (previous === undefined) delete process.env.LOUPIXDECK_VSCODE_DIR;
        else process.env.LOUPIXDECK_VSCODE_DIR = previous;
    });
    const { vscode, control } = createVscode();
    const extension = loadExtension(vscode);
    extension.activate({ subscriptions: [], extension: { packageJSON: { version: '0.1.0' } } });
    const { file, entry } = await waitForEntry(dir);
    control.settings['loupixdeck.enabled'] = false;
    control.configChanged('loupixdeck.enabled');
    assert.equal(fs.existsSync(file), false);
    assert.equal(control.statusItems[0].visible, false);
    control.settings['loupixdeck.enabled'] = true;
    control.configChanged('loupixdeck.enabled');
    const again = await waitForEntry(dir);
    assert.notEqual(again.entry.token, entry.token, 'a restart issues a new token');
    extension.deactivate();
    assert.equal(fs.existsSync(again.file), false);
});

test('a stop during start-up leaves no server and no registration behind', unix, async t => {
    const dir = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ldk-')), 'reg');
    const previous = process.env.LOUPIXDECK_VSCODE_DIR;
    process.env.LOUPIXDECK_VSCODE_DIR = dir;
    t.after(() => {
        if (previous === undefined) delete process.env.LOUPIXDECK_VSCODE_DIR;
        else process.env.LOUPIXDECK_VSCODE_DIR = previous;
    });
    const file = path.join(dir, `${process.pid}.json`);
    const socket = path.join(dir, `${process.pid}.sock`);

    // Turned off while the start-up awaits the command list.
    {
        const { vscode, control } = createVscode();
        const extension = loadExtension(vscode);
        extension.activate({ subscriptions: [], extension: { packageJSON: { version: '0.1.0' } } });
        control.settings['loupixdeck.enabled'] = false;
        control.configChanged('loupixdeck.enabled');
        await sleep(300);
        assert.equal(fs.existsSync(file), false, 'no registration');
        assert.equal(fs.existsSync(socket), false, 'no socket');
        extension.deactivate();
    }

    // Deactivated while the start-up awaits.
    {
        const { vscode } = createVscode();
        const extension = loadExtension(vscode);
        extension.activate({ subscriptions: [], extension: { packageJSON: { version: '0.1.0' } } });
        extension.deactivate();
        await sleep(300);
        assert.equal(fs.existsSync(file), false, 'no registration after deactivate');
        assert.equal(fs.existsSync(socket), false, 'no socket after deactivate');
    }

    // Restarted during start-up: the second start-up wins and its server is fully live.
    {
        const { vscode, control } = createVscode();
        const extension = loadExtension(vscode);
        extension.activate({ subscriptions: [], extension: { packageJSON: { version: '0.1.0' } } });
        const restarted = control.commandHandlers.get('loupixdeck.restartServer')?.();
        await restarted;
        const { entry } = await waitForEntry(dir);
        const client = await connect(entry.socket);
        const hello = await client.request({ id: 1, op: 'hello', token: entry.token, client: 'test/1', protocol: 1 });
        assert.equal(hello.ok, true);
        const editor = control.openEditor('/work/demo/late.js', { dirty: true });
        control.events.textDocument.fire({ document: editor.document });
        const event = await client.waitFor(m => m.ev === 'state' && m.data.dirty);
        assert.ok(event, 'the live server still pushes state');
        client.close();
        extension.deactivate();
    }
});

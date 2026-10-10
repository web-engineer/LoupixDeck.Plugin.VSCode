// @ts-check
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { BridgeServer } = require('../lib/server');
const { BridgeError, newToken } = require('../lib/protocol');
const { connect } = require('./support/client');

/** @param {Partial<import('../lib/server').ServerOptions>} [overrides] */
async function startServer(overrides = {}) {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ldk-'));
    const socketPath = process.platform === 'win32'
        ? `\\\\.\\pipe\\loupixdeck-test-${process.pid}-${Math.random().toString(16).slice(2)}`
        : path.join(dir, 's.sock');
    const token = newToken();
    /** @type {string[]} */
    const logs = [];
    const server = new BridgeServer({
        token,
        hello: async () => ({ protocol: 1, state: { dirty: { count: 0 } } }),
        handle: async (op, message) => {
            if (op === 'ping') return 'pong';
            if (op === 'echo') return message.value;
            if (op === 'slow') return new Promise(resolve => setTimeout(() => resolve('late'), 50));
            if (op === 'cycle') {
                /** @type {any} */
                const loop = {};
                loop.loop = loop;
                return loop;
            }
            throw new BridgeError('unknown_op', `Unknown op ${op}`);
        },
        log: (level, text) => logs.push(`${level} ${text}`),
        ...overrides,
    });
    await server.listen(socketPath);
    return { server, socketPath, token, logs, dir };
}

test('a wrong token closes the connection', async t => {
    const { server, socketPath } = await startServer();
    t.after(() => server.close());
    const client = await connect(socketPath);
    client.send({ id: 1, op: 'hello', token: 'wrong', client: 'test', protocol: 1 });
    await client.waitClosed();
    assert.equal(client.received.length, 0);
});

test('anything before hello closes the connection', async t => {
    const { server, socketPath, token } = await startServer();
    t.after(() => server.close());
    const a = await connect(socketPath);
    a.send({ id: 1, op: 'ping', token });
    await a.waitClosed();
    const b = await connect(socketPath);
    b.send('not json');
    await b.waitClosed();
});

test('hello with the token gets the result, then requests work', async t => {
    const { server, socketPath, token } = await startServer();
    t.after(() => server.close());
    const client = await connect(socketPath);
    const hello = await client.request({ id: 1, op: 'hello', token, client: 'LoupixDeck.Plugin.VSCode/0.1.0', protocol: 1 });
    assert.deepEqual(hello, { id: 1, ok: true, result: { protocol: 1, state: { dirty: { count: 0 } } } });
    assert.equal(server.readyClients.length, 1);
    assert.equal(server.readyClients[0].name, 'LoupixDeck.Plugin.VSCode/0.1.0');
    assert.deepEqual(await client.request({ id: 2, op: 'ping' }), { id: 2, ok: true, result: 'pong' });
    assert.deepEqual(await client.request({ id: 3, op: 'nope' }), { id: 3, ok: false, error: { code: 'unknown_op', message: 'Unknown op nope' } });
    assert.deepEqual(await client.request({ id: 4, op: 'echo' }), { id: 4, ok: true, result: null }, 'undefined becomes null');
    const second = await client.request({ id: 5, op: 'hello', token });
    assert.equal(second.error.code, 'bad_request');
    const cycle = await client.request({ id: 6, op: 'cycle' });
    assert.equal(cycle.ok, false);
    assert.equal(cycle.error.code, 'failed');
});

test('a malformed request after hello gets bad_request with a null id and the connection stays', async t => {
    const { server, socketPath, token } = await startServer();
    t.after(() => server.close());
    const client = await connect(socketPath);
    await client.request({ id: 1, op: 'hello', token });
    client.send('{oops');
    const reply = await client.waitFor(m => m.id === null);
    assert.equal(reply.error.code, 'bad_request');
    assert.equal((await client.request({ id: 2, op: 'ping' })).result, 'pong');
});

test('replies keep their ids when they finish out of order', async t => {
    const { server, socketPath, token } = await startServer();
    t.after(() => server.close());
    const client = await connect(socketPath);
    await client.request({ id: 1, op: 'hello', token });
    client.send({ id: 10, op: 'slow' });
    client.send({ id: 11, op: 'ping' });
    const fast = await client.waitFor(m => m.id === 11);
    const slow = await client.waitFor(m => m.id === 10);
    assert.equal(fast.result, 'pong');
    assert.equal(slow.result, 'late');
    assert.ok(client.received.indexOf(fast) < client.received.indexOf(slow));
});

test('an over-long line closes only that client', async t => {
    const { server, socketPath, token } = await startServer();
    t.after(() => server.close());
    const good = await connect(socketPath);
    await good.request({ id: 1, op: 'hello', token });
    const bad = await connect(socketPath);
    await bad.request({ id: 1, op: 'hello', token });
    bad.socket.write('x'.repeat(1024 * 1024 + 10));
    await bad.waitClosed();
    assert.equal((await good.request({ id: 2, op: 'ping' })).result, 'pong');
});

test('events go only to clients that have said hello', async t => {
    const { server, socketPath, token } = await startServer();
    t.after(() => server.close());
    const ready = await connect(socketPath);
    await ready.request({ id: 1, op: 'hello', token });
    const waiting = await connect(socketPath);
    await new Promise(resolve => setTimeout(resolve, 20));
    server.broadcast({ ev: 'state', data: { dirty: { count: 2 } } });
    const event = await ready.waitFor(m => m.ev === 'state');
    assert.deepEqual(event.data, { dirty: { count: 2 } });
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(waiting.received.length, 0);
    waiting.close();
});

test('onClientsChanged fires on connect and disconnect', async t => {
    let changes = 0;
    const { server, socketPath, token } = await startServer({ onClientsChanged: () => changes++ });
    t.after(() => server.close());
    const client = await connect(socketPath);
    await client.request({ id: 1, op: 'hello', token });
    assert.equal(changes, 1);
    client.close();
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(changes, 2);
    assert.equal(server.readyClients.length, 0);
});

test('listen replaces a stale socket file and close removes it', { skip: process.platform === 'win32' }, async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ldk-'));
    const socketPath = path.join(dir, 'stale.sock');
    fs.writeFileSync(socketPath, 'left over');
    const server = new BridgeServer({ token: newToken(), hello: async () => ({}), handle: async () => null, log: () => undefined });
    await server.listen(socketPath);
    assert.equal(fs.statSync(socketPath).isSocket(), true);
    assert.equal(fs.statSync(socketPath).mode & 0o777, 0o600);
    server.close();
    assert.equal(fs.existsSync(socketPath), false);
});

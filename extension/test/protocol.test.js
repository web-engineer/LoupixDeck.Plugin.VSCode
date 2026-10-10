// @ts-check
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const protocol = require('../lib/protocol');
const { Uri } = require('./support/vscode-stub');

/** @param {number} [max] */
function reader(max) {
    /** @type {string[]} */
    const lines = [];
    const r = new protocol.LineReader(line => lines.push(line), max);
    return { r, lines };
}

test('LineReader splits lines and joins chunks', () => {
    const { r, lines } = reader();
    r.push('{"a":1}\n{"b"');
    r.push(':2}\n\n  \n{"c":3}');
    assert.deepEqual(lines, ['{"a":1}', '{"b":2}']);
    r.push('\r\n');
    assert.deepEqual(lines, ['{"a":1}', '{"b":2}', '{"c":3}']);
});

test('LineReader keeps a multi-byte character split across chunks', () => {
    const { r, lines } = reader();
    const bytes = Buffer.from('"café ✓"\n', 'utf8');
    const cut = bytes.indexOf(0xc3) + 1;
    r.push(bytes.subarray(0, cut));
    r.push(bytes.subarray(cut));
    assert.deepEqual(lines, ['"café ✓"']);
});

test('LineReader rejects a line over the cap, with or without a newline', () => {
    const a = reader(10);
    assert.throws(() => a.r.push('x'.repeat(11)), /longer than/);
    const b = reader(10);
    b.r.push('x'.repeat(6));
    assert.throws(() => b.r.push('y'.repeat(6) + '\n'), (/** @type {any} */ e) => e.code === 'bad_request');
    const c = reader(10);
    c.r.push('x'.repeat(10) + '\n');
    assert.equal(c.lines.length, 1);
});

test('the default cap is 1 MiB', () => {
    assert.equal(protocol.MAX_LINE, 1024 * 1024);
});

test('tokenEquals accepts only the exact token', () => {
    const token = protocol.newToken();
    assert.equal(Buffer.from(token, 'base64url').length, 32);
    assert.equal(protocol.tokenEquals(token, token), true);
    assert.equal(protocol.tokenEquals(token, token.slice(0, -1) + (token.endsWith('A') ? 'B' : 'A')), false);
    assert.equal(protocol.tokenEquals(token, token + 'x'), false);
    assert.equal(protocol.tokenEquals(token, ''), false);
    assert.equal(protocol.tokenEquals(token, undefined), false);
    assert.equal(protocol.tokenEquals(token, 42), false);
});

test('parseRequest needs an object with an integer id and an op', () => {
    assert.deepEqual(protocol.parseRequest('{"id":3,"op":"ping"}'), { id: 3, op: 'ping' });
    for (const bad of ['nope', '[1]', '{"op":"ping"}', '{"id":"1","op":"ping"}', '{"id":1.5,"op":"ping"}', '{"id":1}', '{"id":1,"op":""}', 'null']) {
        assert.throws(() => protocol.parseRequest(bad), (/** @type {any} */ e) => e.code === 'bad_request', bad);
    }
});

test('reviveArgs turns $uri objects into Uris at any depth', () => {
    const args = protocol.reviveArgs([
        { $uri: 'file:///a/b.ts' },
        'text',
        { nested: [{ $uri: 'vscode-remote://ssh/x' }], keep: { $uri: 'file:///c', other: 1 } },
        null,
        3,
    ], Uri);
    assert.ok(args[0] instanceof Uri);
    assert.equal(args[0].path, '/a/b.ts');
    assert.equal(args[1], 'text');
    assert.ok(args[2].nested[0] instanceof Uri);
    assert.equal(args[2].nested[0].scheme, 'vscode-remote');
    assert.deepEqual(args[2].keep, { $uri: 'file:///c', other: 1 }, 'only an object with $uri alone is a Uri');
    assert.equal(args[3], null);
    assert.equal(args[4], 3);
    assert.throws(() => protocol.reviveArgs([{ $uri: 5 }], Uri), (/** @type {any} */ e) => e.code === 'bad_request');
    assert.throws(() => protocol.reviveArgs([{ $uri: 'not a uri' }], Uri), (/** @type {any} */ e) => e.code === 'bad_request');
});

test('toJsonSafe makes command results serialisable', () => {
    /** @type {any} */
    const cyclic = { name: 'loop' };
    cyclic.self = cyclic;
    const value = {
        uri: Uri.file('/x/y.js'),
        list: [Uri.parse('file:///z'), () => 1, undefined],
        fn: () => 1,
        nan: Number.NaN,
        inf: Infinity,
        big: 10n,
        date: new Date(0),
        map: new Map([['k', 1]]),
        set: new Set([1, 2]),
        cyclic,
        shared: [cyclic, cyclic],
        bytes: new Uint8Array([1]),
    };
    assert.deepEqual(protocol.toJsonSafe(value), {
        uri: { $uri: 'file:///x/y.js' },
        list: [{ $uri: 'file:///z' }, null, null],
        nan: null,
        inf: null,
        big: '10',
        date: '1970-01-01T00:00:00.000Z',
        map: { k: 1 },
        set: [1, 2],
        cyclic: { name: 'loop' },
        shared: [{ name: 'loop' }, { name: 'loop' }],
        bytes: null,
    });
    assert.equal(protocol.toJsonSafe(undefined), null);
    assert.equal(protocol.toJsonSafe(() => 1), null);
    assert.equal(protocol.toJsonSafe('s'), 's');
    const throwing = { get bad() { throw new Error('no'); }, ok: 1 };
    assert.deepEqual(protocol.toJsonSafe(throwing), { ok: 1 });
    // The result always survives JSON.stringify.
    assert.doesNotThrow(() => JSON.stringify(protocol.toJsonSafe(value)));
});

test('isAllowed applies the command policy', () => {
    assert.equal(protocol.isAllowed('any', [], 'x'), true);
    assert.equal(protocol.isAllowed(undefined, undefined, 'x'), true);
    assert.equal(protocol.isAllowed('allowList', ['a', 'x'], 'x'), true);
    assert.equal(protocol.isAllowed('allowList', ['a'], 'x'), false);
    assert.equal(protocol.isAllowed('allowList', undefined, 'x'), false);
});

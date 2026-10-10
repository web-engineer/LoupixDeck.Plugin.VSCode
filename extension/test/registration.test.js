// @ts-check
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const registration = require('../lib/registration');

const unix = { skip: process.platform === 'win32' };

test('the directory follows the OS and LOUPIXDECK_VSCODE_DIR', () => {
    assert.equal(registration.registrationDir({ LOUPIXDECK_VSCODE_DIR: '/tmp/x' }, 'darwin'), path.resolve('/tmp/x'));
    assert.equal(registration.registrationDir({}, 'darwin'), path.join(os.homedir(), '.config', 'LoupixDeck', 'vscode'));
    assert.equal(registration.registrationDir({ APPDATA: 'C:\\Users\\me\\AppData\\Roaming' }, 'win32'),
        path.join('C:\\Users\\me\\AppData\\Roaming', 'LoupixDeck', 'vscode'));
});

test('socket paths: in the directory, in /tmp when too long, a pipe on Windows', () => {
    assert.equal(registration.socketPathFor('/Users/me/.config/LoupixDeck/vscode', 41234, 'darwin'), '/Users/me/.config/LoupixDeck/vscode/41234.sock');
    const long = '/Users/me/' + 'x'.repeat(100);
    const moved = registration.socketPathFor(long, 41234, 'darwin');
    assert.match(moved, /^\/tmp\/loupixdeck-vscode-\d+\/41234\.sock$/);
    const pipe = registration.socketPathFor('C:\\x', 41234, 'win32');
    assert.match(pipe, /^\\\\\.\\pipe\\loupixdeck-vscode-41234-[0-9a-f]{8}$/);
    assert.equal(registration.isPipe(pipe), true);
    assert.equal(registration.isPipe(moved), false);
});

test('entries are written 0600 in a 0700 directory and removed again', unix, () => {
    const dir = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ldk-')), 'nested', 'vscode');
    const old = process.umask(0o022);
    try {
        registration.ensurePrivateDir(dir);
    } finally {
        process.umask(old);
    }
    assert.equal(fs.statSync(dir).mode & 0o777, 0o700);
    const file = registration.writeEntry(dir, { pid: 4242, socket: path.join(dir, '4242.sock'), token: 't' });
    assert.equal(fs.statSync(file).mode & 0o777, 0o600);
    assert.equal(JSON.parse(fs.readFileSync(file, 'utf8')).token, 't');
    assert.equal(fs.existsSync(`${file}.tmp`), false);
    fs.writeFileSync(path.join(dir, '4242.sock'), '');
    registration.removeEntry(dir, 4242, path.join(dir, '4242.sock'));
    assert.deepEqual(fs.readdirSync(dir), []);
});

test('removeStale deletes dead windows only, and only sockets named after the pid', unix, () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ldk-'));
    const victim = path.join(dir, 'precious.txt');
    fs.writeFileSync(victim, 'keep me');
    fs.writeFileSync(path.join(dir, '111.json'), JSON.stringify({ pid: 111, socket: path.join(dir, '111.sock') }));
    fs.writeFileSync(path.join(dir, '111.sock'), '');
    fs.writeFileSync(path.join(dir, '222.json'), JSON.stringify({ pid: 222, socket: victim }));
    fs.writeFileSync(path.join(dir, '333.json'), JSON.stringify({ pid: 333 }));
    fs.writeFileSync(path.join(dir, '444.json.tmp'), '{');
    fs.writeFileSync(path.join(dir, 'notes.json'), '{}');
    const removed = registration.removeStale(dir, 999, pid => pid === 333);
    assert.deepEqual(removed.sort(), [111, 222, 444]);
    assert.deepEqual(fs.readdirSync(dir).sort(), ['333.json', 'notes.json', 'precious.txt']);
});

test('pidAlive knows this process', () => {
    assert.equal(registration.pidAlive(process.pid), true);
    assert.equal(registration.pidAlive(2 ** 22 + 12345), false);
});

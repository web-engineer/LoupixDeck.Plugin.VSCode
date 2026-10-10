// @ts-check
'use strict';

// The registration directory: one `<pid>.json` per window, so the plugin can find every window's
// socket and token. See "Discovery" in docs/PROTOCOL.md.

const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');

/** Unix socket paths longer than this move to /tmp (macOS allows 104 bytes in sun_path). */
const MAX_SOCKET_PATH = 100;

/**
 * @param {NodeJS.ProcessEnv} [env]
 * @param {NodeJS.Platform} [platform]
 */
function registrationDir(env = process.env, platform = process.platform) {
    const override = env.LOUPIXDECK_VSCODE_DIR;
    if (override && override.trim().length > 0) return path.resolve(override);
    if (platform === 'win32') {
        const appData = env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming');
        return path.join(appData, 'LoupixDeck', 'vscode');
    }
    return path.join(os.homedir(), '.config', 'LoupixDeck', 'vscode');
}

/**
 * Creates a user-only directory. `chmod` runs as well because umask applies to `mkdir`.
 * @param {string} dir
 */
function ensurePrivateDir(dir) {
    fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
    if (process.platform === 'win32') return;
    const stat = fs.lstatSync(dir);
    if (!stat.isDirectory()) throw new Error(`${dir} is not a directory`);
    if (typeof process.getuid === 'function' && stat.uid !== process.getuid()) {
        throw new Error(`${dir} belongs to another user`);
    }
    fs.chmodSync(dir, 0o700);
}

/**
 * Where this window listens: a named pipe on Windows, otherwise `<dir>/<pid>.sock`, or
 * `/tmp/loupixdeck-vscode-<uid>/<pid>.sock` when that path is too long for a Unix socket.
 * @param {string} dir
 * @param {number} pid
 * @param {NodeJS.Platform} [platform]
 */
function socketPathFor(dir, pid, platform = process.platform) {
    if (platform === 'win32') {
        return `\\\\.\\pipe\\loupixdeck-vscode-${pid}-${crypto.randomBytes(4).toString('hex')}`;
    }
    const inDir = path.join(dir, `${pid}.sock`);
    if (Buffer.byteLength(inDir, 'utf8') <= MAX_SOCKET_PATH) return inDir;
    const uid = typeof process.getuid === 'function' ? process.getuid() : 0;
    return path.join('/tmp', `loupixdeck-vscode-${uid}`, `${pid}.sock`);
}

/** @param {string} socketPath */
function isPipe(socketPath) {
    return socketPath.startsWith('\\\\.\\pipe\\');
}

/**
 * Writes `<pid>.json` atomically with mode 0600: a temporary file first, then a rename.
 * @param {string} dir
 * @param {Record<string, unknown> & {pid: number}} entry
 * @returns {string} the file's path
 */
function writeEntry(dir, entry) {
    const file = path.join(dir, `${entry.pid}.json`);
    const temp = `${file}.tmp`;
    fs.writeFileSync(temp, JSON.stringify(entry, null, 2) + '\n', { mode: 0o600 });
    if (process.platform !== 'win32') fs.chmodSync(temp, 0o600);
    fs.renameSync(temp, file);
    return file;
}

/**
 * Removes this window's file and socket. Never throws.
 * @param {string} dir
 * @param {number} pid
 * @param {string | undefined} socketPath
 */
function removeEntry(dir, pid, socketPath) {
    tryUnlink(path.join(dir, `${pid}.json`));
    tryUnlink(path.join(dir, `${pid}.json.tmp`));
    if (socketPath && !isPipe(socketPath)) tryUnlink(socketPath);
}

/**
 * Deletes entries whose window has gone (the pid is dead), with their sockets. A socket is only
 * deleted when it is named `<pid>.sock`, so a doctored file cannot point us at anything else.
 * @param {string} dir
 * @param {number} ownPid
 * @param {(pid: number) => boolean} [isAlive]
 * @returns {number[]} the pids removed
 */
function removeStale(dir, ownPid, isAlive = pidAlive) {
    /** @type {number[]} */
    const removed = [];
    let names;
    try {
        names = fs.readdirSync(dir);
    } catch {
        return removed;
    }
    for (const name of names) {
        const match = /^(\d+)\.json(\.tmp)?$/.exec(name);
        if (!match) continue;
        const pid = Number(match[1]);
        if (pid === ownPid || isAlive(pid)) continue;
        const file = path.join(dir, name);
        let socketPath;
        try {
            const entry = JSON.parse(fs.readFileSync(file, 'utf8'));
            if (typeof entry.socket === 'string' && path.basename(entry.socket) === `${pid}.sock`) {
                socketPath = entry.socket;
            }
        } catch {
            // An unreadable or half-written file is removed all the same.
        }
        tryUnlink(file);
        if (socketPath) tryUnlink(socketPath);
        tryUnlink(path.join(dir, `${pid}.sock`));
        removed.push(pid);
    }
    return removed;
}

/**
 * @param {number} pid
 * @returns {boolean} true when a process with that id exists (EPERM means it does, but is not ours)
 */
function pidAlive(pid) {
    try {
        process.kill(pid, 0);
        return true;
    } catch (error) {
        return /** @type {NodeJS.ErrnoException} */ (error).code === 'EPERM';
    }
}

/** @param {string} file */
function tryUnlink(file) {
    try {
        fs.unlinkSync(file);
    } catch {
        // Already gone.
    }
}

module.exports = {
    MAX_SOCKET_PATH,
    registrationDir,
    ensurePrivateDir,
    socketPathFor,
    isPipe,
    writeEntry,
    removeEntry,
    removeStale,
    pidAlive,
    tryUnlink,
};

#!/usr/bin/env node
// Smoke client for the companion bridge (docs/PROTOCOL.md). Finds every registered VS Code
// window, checks the registration file, says hello and prints the window's state. Optional
// actions run against each window (or the one picked with --pid):
//
//   node tools/bridge-smoke.mjs                         hello + state for every window
//   node tools/bridge-smoke.mjs exec workbench.action.files.newUntitledFile
//   node tools/bridge-smoke.mjs exec vscode.open '[{"$uri":"file:///tmp/a.txt"}]'
//   node tools/bridge-smoke.mjs list tasks              tasks | launch | problems | editors | recent | terminals
//   node tools/bridge-smoke.mjs run tasks <ref>
//   node tools/bridge-smoke.mjs runTask build
//   node tools/bridge-smoke.mjs startDebug "Run app"
//   node tools/bridge-smoke.mjs state | focus | ping
//
// Options: --dir <path> (default LOUPIXDECK_VSCODE_DIR, else the per-OS directory),
// --pid <n> (one window only), --watch <seconds> (print events for that long afterwards),
// --bad-token (send a wrong token and expect the connection to close), --quiet (no state dump).

import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';

const OPS = new Set(['exec', 'list', 'run', 'runTask', 'startDebug', 'state', 'focus', 'ping']);

function usage(message) {
    if (message) console.error(message);
    console.error('usage: bridge-smoke.mjs [--dir D] [--pid N] [--watch S] [--bad-token] [--quiet] [op args…]');
    process.exit(2);
}

function parseArgs(argv) {
    const options = { dir: undefined, pid: undefined, watch: 0, badToken: false, quiet: false, op: undefined, rest: [] };
    for (let i = 0; i < argv.length; i++) {
        const arg = argv[i];
        if (arg === '--dir') options.dir = argv[++i];
        else if (arg === '--pid') options.pid = Number(argv[++i]);
        else if (arg === '--watch') options.watch = Number(argv[++i]);
        else if (arg === '--bad-token') options.badToken = true;
        else if (arg === '--quiet') options.quiet = true;
        else if (arg === '--help' || arg === '-h') usage();
        else if (!options.op) {
            if (!OPS.has(arg)) usage(`unknown op ${arg}`);
            options.op = arg;
        } else options.rest.push(arg);
    }
    return options;
}

function registrationDir(override) {
    if (override) return path.resolve(override);
    if (process.env.LOUPIXDECK_VSCODE_DIR) return path.resolve(process.env.LOUPIXDECK_VSCODE_DIR);
    if (process.platform === 'win32') {
        return path.join(process.env.APPDATA ?? path.join(os.homedir(), 'AppData', 'Roaming'), 'LoupixDeck', 'vscode');
    }
    return path.join(os.homedir(), '.config', 'LoupixDeck', 'vscode');
}

/** Builds the request for the op given on the command line. */
function buildRequest(op, rest) {
    switch (op) {
        case 'exec': {
            if (!rest[0]) usage('exec needs a command id');
            const request = { op, command: rest[0], focus: false };
            if (rest[1]) {
                const args = JSON.parse(rest[1]);
                request.args = Array.isArray(args) ? args : [args];
            }
            return request;
        }
        case 'list':
            if (!rest[0]) usage('list needs tasks | launch | problems | editors | recent | terminals');
            return { op, what: rest[0] };
        case 'run':
            if (!rest[0] || !rest[1]) usage('run needs <what> <ref>');
            return { op, what: rest[0], ref: rest[1] };
        case 'runTask':
        case 'startDebug':
            if (!rest[0]) usage(`${op} needs a name`);
            return { op, name: rest.join(' ') };
        default:
            return { op };
    }
}

/** One NDJSON connection with request/response matching and an event log. */
function open(socketPath) {
    return new Promise((resolve, reject) => {
        const socket = net.createConnection(socketPath);
        const pending = new Map();
        const listeners = [];
        let buffer = '';
        let nextId = 1;
        const connection = {
            closed: false,
            request(message) {
                const id = nextId++;
                socket.write(JSON.stringify({ id, ...message }) + '\n');
                return new Promise((res, rej) => {
                    const timer = setTimeout(() => {
                        pending.delete(id);
                        rej(new Error(`no reply to ${message.op} within 15 s`));
                    }, 15_000);
                    pending.set(id, reply => {
                        clearTimeout(timer);
                        if (reply instanceof Error) rej(reply);
                        else res(reply);
                    });
                });
            },
            onEvent(listener) {
                listeners.push(listener);
            },
            closedPromise: new Promise(res => socket.on('close', () => {
                connection.closed = true;
                // Requests still waiting will never get a reply (the window quit or reloaded).
                for (const settle of pending.values()) settle(new Error('connection closed before the reply'));
                pending.clear();
                res();
            })),
            close() {
                socket.end();
            },
        };
        socket.setEncoding('utf8');
        socket.on('data', chunk => {
            buffer += chunk;
            let newline;
            while ((newline = buffer.indexOf('\n')) !== -1) {
                const message = JSON.parse(buffer.slice(0, newline));
                buffer = buffer.slice(newline + 1);
                if (message.ev) listeners.forEach(l => l(message));
                else pending.get(message.id)?.(message);
                pending.delete(message.id);
            }
        });
        socket.once('connect', () => resolve(connection));
        socket.once('error', reject);
    });
}

function checkPermissions(dir, file) {
    if (process.platform === 'win32') return;
    const dirMode = fs.statSync(dir).mode & 0o777;
    const fileMode = fs.statSync(file).mode & 0o777;
    const ok = dirMode === 0o700 && fileMode === 0o600;
    console.log(`  permissions: dir ${dirMode.toString(8)}, file ${fileMode.toString(8)} ${ok ? 'OK' : 'WRONG (want 700 / 600)'}`);
    if (!ok) process.exitCode = 1;
}

function summarise(state) {
    const s = state ?? {};
    const lines = [];
    if (s.workspace) lines.push(`workspace: ${s.workspace.name ?? '(none)'} ${s.workspace.remote ? `[${s.workspace.remote}]` : ''}`);
    if (s.editor) lines.push(`editor: ${s.editor.file} (${s.editor.language}) Ln ${s.editor.line}, Col ${s.editor.column}${s.editor.dirty ? ' dirty' : ''}`);
    else lines.push('editor: none');
    lines.push(`dirty: ${s.dirty?.count ?? '?'}`);
    if (s.problems) lines.push(`problems: ${s.problems.errors} errors, ${s.problems.warnings} warnings, ${s.problems.infos} infos`);
    lines.push(s.git ? `git: ${s.git.branch}${s.git.detached ? ' (detached)' : ''}, ${s.git.changes} changed, ${s.git.staged} staged, ↑${s.git.ahead} ↓${s.git.behind}` : 'git: none');
    if (s.debug) lines.push(`debug: ${s.debug.state}${s.debug.session ? ` (${s.debug.session})` : ''}`);
    if (s.tasks) lines.push(`tasks: running [${s.tasks.running.join(', ')}], last ${s.tasks.last ? `${s.tasks.last.name} → ${s.tasks.last.exitCode}` : 'none'}`);
    if (s.terminal) lines.push(`terminal: ${s.terminal.count} open, last ${s.terminal.last ? `${s.terminal.last.command} → ${s.terminal.last.exitCode}` : 'none'}`);
    return lines.map(l => `  ${l}`).join('\n');
}

async function smoke(dir, file, options) {
    const entry = JSON.parse(fs.readFileSync(file, 'utf8'));
    console.log(`\n== ${entry.app} ${entry.version} — ${entry.workspaceName ?? '(no folder)'} (pid ${entry.pid})`);
    console.log(`  socket: ${entry.socket}`);
    checkPermissions(dir, file);

    let connection;
    try {
        connection = await open(entry.socket);
    } catch (error) {
        console.log(`  connect failed: ${error.message} (stale entry?)`);
        process.exitCode = 1;
        return;
    }

    if (options.badToken) {
        const reply = connection.request({ op: 'hello', token: 'not-the-token', client: 'bridge-smoke', protocol: 1 });
        const outcome = await Promise.race([
            connection.closedPromise.then(() => 'closed'),
            reply.then(r => `replied ${JSON.stringify(r)}`),
        ]);
        console.log(`  bad token: ${outcome === 'closed' ? 'connection closed (OK)' : outcome + ' (WRONG)'}`);
        if (outcome !== 'closed') process.exitCode = 1;
        connection.close();
        return;
    }

    const started = Date.now();
    const hello = await connection.request({ op: 'hello', token: entry.token, client: 'bridge-smoke/1', protocol: 1 });
    if (!hello.ok) {
        console.log(`  hello failed: ${JSON.stringify(hello.error)}`);
        process.exitCode = 1;
        connection.close();
        return;
    }
    const r = hello.result;
    console.log(`  hello OK in ${Date.now() - started} ms: protocol ${r.protocol}, ext ${r.extVersion}, focusWindow ${r.focusWindow}`);
    console.log(summarise(r.state));
    if (!options.quiet) console.log(`  state: ${JSON.stringify(r.state)}`);

    connection.onEvent(event => console.log(`  event +${Date.now() - started} ms: ${JSON.stringify(event)}`));

    if (options.op) {
        const request = buildRequest(options.op, options.rest);
        let reply;
        try {
            reply = await connection.request(request);
        } catch (error) {
            console.log(`  ${options.op}: ${error.message}`);
            return;
        }
        if (reply.ok && options.op === 'list') {
            console.log(`  ${options.rest[0]}: ${reply.result.items.length} items`);
            for (const item of reply.result.items) console.log(`    ${JSON.stringify(item)}`);
        } else {
            console.log(`  ${options.op}: ${JSON.stringify(reply)}`);
        }
        if (!reply.ok) process.exitCode = 1;
    }

    if (options.watch > 0) await new Promise(resolve => setTimeout(resolve, options.watch * 1000));
    connection.close();
}

const options = parseArgs(process.argv.slice(2));
const dir = registrationDir(options.dir);
let names;
try {
    names = fs.readdirSync(dir).filter(n => /^\d+\.json$/.test(n));
} catch {
    console.error(`No registration directory at ${dir}`);
    process.exit(1);
}
if (options.pid) names = names.filter(n => n === `${options.pid}.json`);
console.log(`registration dir: ${dir} (${names.length} ${names.length === 1 ? 'window' : 'windows'})`);
if (names.length === 0) process.exit(1);
for (const name of names) await smoke(dir, path.join(dir, name), options);

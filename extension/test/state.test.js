// @ts-check
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { StateTracker, Debouncer, diffSections, sortProblems, countProblems, SECTIONS } = require('../lib/state');
const { summarise } = require('../lib/git');
const { createVscode, Uri, Range } = require('./support/vscode-stub');

/** @param {number} ms */
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function setup() {
    const { vscode, control } = createVscode();
    /** @type {Record<string, unknown>[]} */
    const pushes = [];
    /** @type {Record<string, unknown>[]} */
    const events = [];
    /** @type {any} */
    let git = null;
    let pushDiagnostics = true;
    const tracker = new StateTracker(/** @type {any} */ (vscode), {
        log: () => undefined,
        onState: sections => pushes.push(sections),
        onEvent: event => events.push(event),
        git: () => git,
        pushDiagnostics: () => pushDiagnostics,
    });
    tracker.start();
    return {
        vscode, control, tracker, pushes, events,
        /** @param {any} value */
        setGit(value) { git = value; },
        /** @param {boolean} value */
        setPushDiagnostics(value) { pushDiagnostics = value; },
    };
}

test('diffSections sends only sections whose JSON changed', () => {
    const last = new Map();
    assert.deepEqual(diffSections({ a: { x: 1 }, b: null }, last), { a: { x: 1 }, b: null });
    assert.equal(diffSections({ a: { x: 1 }, b: null }, last), null);
    assert.deepEqual(diffSections({ a: { x: 2 }, b: null }, last), { a: { x: 2 } });
    assert.deepEqual(diffSections({ c: undefined }, last), { c: null });
});

test('sortProblems puts errors first, then by file, line and column', () => {
    const sorted = sortProblems([
        { severity: 1, file: 'a.ts', line: 1 },
        { severity: 0, file: 'b.ts', line: 9 },
        { severity: 2, file: 'a.ts', line: 0 },
        { severity: 0, file: 'a.ts', line: 5, column: 3 },
        { severity: 0, file: 'a.ts', line: 5, column: 1 },
    ]);
    assert.deepEqual(sorted.map(p => `${p.severity}:${p.file}:${p.line}:${p.column ?? ''}`),
        ['0:a.ts:5:1', '0:a.ts:5:3', '0:b.ts:9:', '1:a.ts:1:', '2:a.ts:0:']);
});

test('countProblems counts errors, warnings and infos but not hints', () => {
    const d = /** @param {number} severity */ severity => ({ severity });
    assert.deepEqual(countProblems([[Uri.file('/a'), [d(0), d(0), d(1), d(3)]], [Uri.file('/b'), [d(2), d(1)]]]),
        { errors: 2, warnings: 2, infos: 1 });
});

test('Debouncer runs once after a burst, and no later than the ceiling', async () => {
    let runs = 0;
    const d = new Debouncer(() => runs++, 30, 80);
    d.trigger();
    d.trigger();
    await sleep(10);
    d.trigger();
    assert.equal(runs, 0);
    await sleep(60);
    assert.equal(runs, 1);
    // A steady stream still flushes by the ceiling.
    runs = 0;
    const started = Date.now();
    while (Date.now() - started < 150) {
        d.trigger();
        await sleep(10);
    }
    assert.ok(runs >= 1, 'flushed during the stream');
    d.cancel();
});

test('snapshot has every section', () => {
    const { tracker, control } = setup();
    control.openEditor('/work/demo/src/app.js', { line: 11, character: 3 });
    const state = /** @type {any} */ (tracker.snapshot());
    assert.deepEqual(Object.keys(state), [...SECTIONS]);
    assert.deepEqual(state.editor, { file: 'app.js', path: '/work/demo/src/app.js', language: 'javascript', dirty: false, line: 12, column: 4, selections: 1 });
    assert.deepEqual(state.workspace, { name: 'demo', folders: ['file:///work/demo'], remote: null });
    assert.deepEqual(state.debug, { state: 'inactive', session: null, reason: null });
    assert.deepEqual(state.tasks, { running: [], last: null });
    assert.deepEqual(state.terminal, { count: 0, last: null });
    assert.deepEqual(state.settings, { accessibilitySupport: 'auto', wordWrap: 'off', minimap: true });
    assert.equal(state.git, null);
    assert.equal(state.window.focused, true);
    tracker.dispose();
});

test('changes are debounced into one push with only the changed sections', async () => {
    const { tracker, control, pushes } = setup();
    const doc = control.openEditor('/work/demo/a.js').document;
    control.events.activeEditor.fire(undefined);
    doc.isDirty = true;
    control.events.textDocument.fire({ document: doc });
    control.events.textDocument.fire({ document: doc });
    assert.equal(pushes.length, 0, 'nothing before the debounce');
    await sleep(200);
    assert.equal(pushes.length, 1);
    assert.deepEqual(Object.keys(pushes[0]).sort(), ['dirty', 'editor']);
    assert.deepEqual(pushes[0].dirty, { count: 1 });
    // The same state again pushes nothing.
    control.events.textDocument.fire({ document: doc });
    await sleep(200);
    assert.equal(pushes.length, 1);
    tracker.dispose();
});

test('cursor moves are throttled to one rebuild per 250 ms', async () => {
    const { tracker, control, pushes } = setup();
    const editor = control.openEditor('/work/demo/a.js');
    let line = 0;
    const move = () => {
        line++;
        editor.selection = { active: { line, character: 0 } };
        control.events.selection.fire({ textEditor: editor });
    };
    const started = Date.now();
    while (Date.now() - started < 600) {
        move();
        await sleep(15);
    }
    await sleep(450);
    const editorPushes = pushes.filter(p => 'editor' in p);
    assert.ok(editorPushes.length >= 2 && editorPushes.length <= 4, `got ${editorPushes.length} editor pushes`);
    assert.equal(/** @type {any} */ (editorPushes.at(-1)).editor.line, line + 1, 'the last position arrives');
    tracker.dispose();
});

test('diagnostics push problems and a lists event, unless pushDiagnostics is off', async () => {
    const { tracker, control, pushes, events, setPushDiagnostics } = setup();
    control.setDiagnostics([[Uri.file('/work/demo/a.js'), [{ severity: 0, range: new Range(0, 0, 0, 1), message: 'x' }]]]);
    control.events.diagnostics.fire({});
    await sleep(400);
    assert.deepEqual(pushes.at(-1), { problems: { errors: 1, warnings: 0, infos: 0 } });
    assert.deepEqual(events.at(-1), { ev: 'lists', what: ['problems'] });
    setPushDiagnostics(false);
    control.setDiagnostics([]);
    const before = pushes.length;
    control.events.diagnostics.fire({});
    await sleep(400);
    assert.equal(pushes.length, before);
    assert.deepEqual(/** @type {any} */ (tracker.snapshot()).problems, { errors: 0, warnings: 0, infos: 0 }, 'still read on demand');
    tracker.dispose();
});

test('task ends set tasks.last and raise notify events', async () => {
    const { tracker, control, pushes, events } = setup();
    const execution = { task: { name: 'build' } };
    control.vscode.tasks.taskExecutions = [execution];
    control.events.taskStart.fire({ execution });
    await sleep(200);
    assert.deepEqual(/** @type {any} */ (pushes.at(-1)).tasks.running, ['build']);
    control.vscode.tasks.taskExecutions = [];
    control.events.taskProcessEnd.fire({ execution, exitCode: 2 });
    control.events.taskEnd.fire({ execution });
    await sleep(200);
    const tasks = /** @type {any} */ (pushes.at(-1)).tasks;
    assert.deepEqual(tasks.running, []);
    assert.equal(tasks.last.name, 'build');
    assert.equal(tasks.last.exitCode, 2);
    assert.deepEqual(events.find(e => e.ev === 'notify'), { ev: 'notify', kind: 'taskFailed', text: 'build exited with 2' });
    // A task without a process ends with a null exit code.
    const custom = { task: { name: 'custom' } };
    control.events.taskEnd.fire({ execution: custom });
    await sleep(200);
    assert.deepEqual(/** @type {any} */ (pushes.at(-1)).tasks.last.exitCode, null);
    tracker.dispose();
});

test('shell executions set terminal.last and flag failures', async () => {
    const { tracker, control, pushes, events } = setup();
    const terminal = { name: 'zsh' };
    control.vscode.window.terminals = [terminal];
    control.events.shellEnd.fire({ terminal, exitCode: 1, execution: { commandLine: { value: 'npm test' } } });
    await sleep(400);
    const section = /** @type {any} */ (pushes.at(-1)).terminal;
    assert.equal(section.count, 1);
    assert.equal(section.last.command, 'npm test');
    assert.equal(section.last.exitCode, 1);
    assert.deepEqual(events.find(e => e.ev === 'notify'), { ev: 'notify', kind: 'terminalFailed', text: 'npm test exited with 1' });
    assert.equal(tracker.terminalCommand(terminal), 'npm test');
    tracker.dispose();
});

test('a failing task does not also raise terminalFailed for its own terminal', async () => {
    const { tracker, control, events } = setup();
    const execution = { task: { name: 'lint' } };
    control.events.shellEnd.fire({ terminal: {}, exitCode: 4, execution: { commandLine: { value: 'eslint .' } } });
    control.events.taskProcessEnd.fire({ execution, exitCode: 4 });
    await sleep(400);
    assert.deepEqual(events.filter(e => e.ev === 'notify').map(e => e.kind), ['taskFailed']);
    tracker.dispose();
});

test('debug state follows the session, the stack item and the adapter tracker', async () => {
    const { tracker, control, pushes, events } = setup();
    const session = { id: 's1', name: 'Launch app' };
    control.vscode.debug.activeDebugSession = session;
    control.events.debugStart.fire(session);
    await sleep(200);
    assert.deepEqual(/** @type {any} */ (pushes.at(-1)).debug, { state: 'running', session: 'Launch app', reason: null });

    const adapter = control.vscode.debug.trackerFactory.createDebugAdapterTracker(session);
    adapter.onDidSendMessage({ type: 'event', event: 'stopped', body: { reason: 'breakpoint' } });
    control.vscode.debug.activeStackItem = { frameId: 1 };
    control.events.stackItem.fire(undefined);
    await sleep(200);
    assert.deepEqual(/** @type {any} */ (pushes.at(-1)).debug, { state: 'paused', session: 'Launch app', reason: 'breakpoint' });
    assert.deepEqual(events.filter(e => e.kind === 'debugStopped'), [{ ev: 'notify', kind: 'debugStopped', text: 'Paused at breakpoint' }]);

    adapter.onDidSendMessage({ type: 'event', event: 'continued', body: {} });
    control.vscode.debug.activeStackItem = undefined;
    control.events.stackItem.fire(undefined);
    await sleep(200);
    assert.equal(/** @type {any} */ (pushes.at(-1)).debug.state, 'running');

    // Adapters send no `continued` for a continue request: the request itself resumes the session.
    adapter.onDidSendMessage({ type: 'event', event: 'stopped', body: { reason: 'breakpoint' } });
    control.vscode.debug.activeStackItem = { frameId: 1 };
    await sleep(200);
    assert.equal(/** @type {any} */ (pushes.at(-1)).debug.state, 'paused');
    adapter.onWillReceiveMessage({ type: 'request', command: 'continue', arguments: { threadId: 1 } });
    control.vscode.debug.activeStackItem = { threadId: 1 }; // a thread stays selected while running
    await sleep(200);
    assert.equal(/** @type {any} */ (pushes.at(-1)).debug.state, 'running');
    adapter.onDidSendMessage({ type: 'event', event: 'stopped', body: { reason: 'breakpoint' } });
    await sleep(200);
    assert.equal(events.filter(e => e.kind === 'debugStopped').length, 3, 'every new pause notifies, including one after a continue request');

    control.vscode.debug.activeDebugSession = undefined;
    control.events.debugEnd.fire(session);
    await sleep(200);
    assert.equal(/** @type {any} */ (pushes.at(-1)).debug.state, 'inactive');
    tracker.dispose();
});

test('without adapter data only a stack frame counts as paused', async () => {
    const { tracker, control, pushes } = setup();
    const session = { id: 's2', name: 'Attach' };
    control.vscode.debug.activeDebugSession = session;
    control.vscode.debug.activeStackItem = { threadId: 3 };
    control.events.debugStart.fire(session);
    await sleep(200);
    assert.equal(/** @type {any} */ (pushes.at(-1)).debug.state, 'running');
    control.vscode.debug.activeStackItem = { threadId: 3, frameId: 9 };
    control.events.stackItem.fire(undefined);
    await sleep(200);
    assert.equal(/** @type {any} */ (pushes.at(-1)).debug.state, 'paused');
    tracker.dispose();
});

test('focus changes push a focus event at once and the window section later', async () => {
    const { tracker, control, pushes, events } = setup();
    control.vscode.window.state = { focused: false, active: false };
    control.events.windowState.fire(control.vscode.window.state);
    assert.equal(events[0].ev, 'focus');
    assert.equal(events[0].focused, false);
    assert.equal(typeof events[0].at, 'number');
    await sleep(200);
    assert.equal(/** @type {any} */ (pushes.at(-1)).window.focused, false);

    control.vscode.window.state = { focused: true, active: true };
    control.events.windowState.fire(control.vscode.window.state);
    const gained = events.at(-1)?.at;
    await sleep(5);
    control.vscode.window.state = { focused: true, active: false };
    control.events.windowState.fire(control.vscode.window.state);
    assert.equal(events.at(-1)?.at, gained, 'an activity change while focused keeps the focus time');
    tracker.dispose();
});

test('settings and lists follow configuration changes', async () => {
    const { tracker, control, pushes, events } = setup();
    control.settings['editor.accessibilitySupport'] = 'on';
    control.configChanged('editor.accessibilitySupport', 'launch');
    await sleep(400);
    assert.equal(/** @type {any} */ (pushes.at(-1)).settings.accessibilitySupport, 'on');
    assert.deepEqual(events.at(-1), { ev: 'lists', what: ['launch'] });
    tracker.dispose();
});

test('git summary reads the API and the command shapes', () => {
    assert.deepEqual(summarise({
        HEAD: { type: 0, name: 'main', commit: 'abcdef1234', upstream: { remote: 'origin', name: 'main' }, ahead: 1, behind: 2 },
        workingTreeChanges: [1, 2], untrackedChanges: [3], mergeChanges: [], indexChanges: [4],
    }, 1), { branch: 'main', detached: false, ahead: 1, behind: 2, changes: 3, staged: 1, conflicts: 0, repos: 1, upstream: true });
    assert.deepEqual(summarise({
        HEAD: { type: 'Head', commit: 'abcdef1234' }, workingTreeChanges: [], mergeChanges: [1], indexChanges: [],
    }, 2), { branch: 'abcdef1', detached: true, ahead: 0, behind: 0, changes: 1, staged: 0, conflicts: 1, repos: 2, upstream: false });
});

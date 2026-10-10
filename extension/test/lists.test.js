// @ts-check
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { Lists, MAX_PROBLEMS } = require('../lib/lists');
const { createVscode, Uri, Range } = require('./support/vscode-stub');

function setup() {
    const { vscode, control } = createVscode();
    const lists = new Lists(/** @type {any} */ (vscode), { terminalCommand: t => (/** @type {any} */ (t).name === 'zsh' ? 'npm test' : null) });
    return { vscode, control, lists };
}

/**
 * @param {string} file
 * @param {number} line
 * @param {number} severity
 * @param {string} message
 */
const diag = (file, line, severity, message) => ({ severity, message, range: new Range(line, 2, line, 6) });

test('problems are sorted errors first, then by file, and capped', async () => {
    const { control, lists } = setup();
    control.setDiagnostics([
        [Uri.file('/work/demo/src/b.ts'), [diag('b', 4, 1, 'Unused\nvariable'), diag('b', 9, 0, 'Type error')]],
        [Uri.file('/work/demo/src/a.ts'), [diag('a', 20, 0, 'x'.repeat(300)), diag('a', 1, 3, 'hint, skipped'), diag('a', 2, 2, 'Info')]],
    ]);
    const { items } = await lists.list('problems');
    assert.deepEqual(items.map(i => `${i.severity} ${i.label}`), ['error a.ts:21', 'error b.ts:10', 'warning b.ts:5', 'info a.ts:3']);
    assert.equal(items[0].detail.length, 120);
    assert.equal(items[2].detail, 'Unused variable');
    assert.equal(items[0].kind, 'problem');

    await lists.run('problems', items[1].ref);
    assert.equal(control.shownDocuments[0].uri.path, '/work/demo/src/b.ts');
    assert.equal(control.shownDocuments[0].options.selection.start.line, 9);

    control.setDiagnostics([[Uri.file('/work/demo/many.ts'), Array.from({ length: 150 }, (_, i) => diag('m', i, 0, `e${i}`))]]);
    assert.equal((await lists.list('problems')).items.length, MAX_PROBLEMS);
});

test('refs are stable across refreshes and a gone item is not_found', async () => {
    const { control, lists } = setup();
    const build = { name: 'build', source: 'Workspace', scope: { uri: Uri.file('/work/demo'), name: 'demo' } };
    const test1 = { name: 'test', source: 'npm', scope: { uri: Uri.file('/work/demo'), name: 'demo' } };
    control.setTasks([build, test1]);
    const first = (await lists.list('tasks')).items;
    assert.deepEqual(first.map(i => [i.label, i.detail, i.kind]), [['build', 'Workspace', 'task'], ['test', 'npm', 'task']]);
    const fresh = new Lists(control.vscode, { terminalCommand: () => null });
    await fresh.run('tasks', first[1].ref);
    assert.equal(control.executedTasks[0], test1, 'a ref works without a cached list');
    control.setTasks([build]);
    await assert.rejects(lists.run('tasks', first[1].ref), (/** @type {any} */ e) => e.code === 'not_found');
});

test('tasks show the folder name in multi-root workspaces; runTask prefers tasks.json', async () => {
    const { control, lists } = setup();
    control.vscode.workspace.workspaceFolders = [{ uri: Uri.file('/w/a'), name: 'a', index: 0 }, { uri: Uri.file('/w/b'), name: 'b', index: 1 }];
    const detected = { name: 'build', source: 'npm', scope: { uri: Uri.file('/w/a'), name: 'a' } };
    const configured = { name: 'build', source: 'Workspace', scope: { uri: Uri.file('/w/b'), name: 'b' } };
    control.setTasks([detected, configured]);
    const { items } = await lists.list('tasks');
    assert.deepEqual(items.map(i => i.detail), ['npm · a', 'Workspace · b']);
    assert.notEqual(items[0].ref, items[1].ref);
    await lists.runTask('build');
    assert.equal(control.executedTasks[0], configured);
    await lists.runTask('npm: build');
    assert.equal(control.executedTasks[1], detected, '"source: label" picks a detected task');
});

test('launch lists configurations and compounds and starts them', async () => {
    const { control, lists } = setup();
    control.folderSettings[Uri.file('/work/demo').toString()] = {
        'launch.configurations': [{ name: 'Run app', type: 'node' }, { type: 'nameless' }],
        'launch.compounds': [{ name: 'All', configurations: ['Run app'] }],
    };
    const { items } = await lists.list('launch');
    assert.deepEqual(items.map(i => [i.label, i.kind, i.detail]), [['Run app', 'config', 'launch.json'], ['All', 'compound', 'launch.json']]);
    await lists.run('launch', items[1].ref);
    await lists.startDebug('Run app');
    assert.deepEqual(control.startedDebugs.map(d => d.name), ['All', 'Run app']);
    assert.equal(control.startedDebugs[0].folder.name, 'demo');
});

test('workspace-file and user configurations are listed once and start from their object', async () => {
    const { control, lists } = setup();
    const a = { uri: Uri.file('/w/a'), name: 'a', index: 0 };
    const b = { uri: Uri.file('/w/b'), name: 'b', index: 1 };
    control.vscode.workspace.workspaceFolders = [a, b];
    control.folderSettings[a.uri.toString()] = { 'launch.configurations': [{ name: 'Serve a', type: 'node' }] };
    const server = { name: 'Server', type: 'node', program: '${workspaceFolder:a}/s.js' };
    const client = { name: 'Client', type: 'chrome' };
    control.workspaceSettings['launch.configurations'] = [server, client];
    control.workspaceSettings['launch.compounds'] = [
        { name: 'Full stack', configurations: ['Server', 'Client'] },
        { name: 'Mixed', configurations: ['Server', { name: 'Serve a', folder: 'a' }] },
    ];
    control.settings['launch.configurations'] = [{ name: 'Attach', type: 'node', request: 'attach' }];

    const { items } = await lists.list('launch');
    assert.deepEqual(items.map(i => [i.label, i.detail]), [
        ['Serve a', 'launch.json · a'],
        ['Server', 'workspace file'], ['Client', 'workspace file'], ['Full stack', 'workspace file'],
        ['Attach', 'user settings'],
    ]);

    await lists.startDebug('Server');
    assert.deepEqual(control.startedDebugs.at(-1), { folder: undefined, name: server });
    await lists.startDebug('Serve a');
    assert.deepEqual(control.startedDebugs.at(-1), { folder: a, name: 'Serve a' });
    await lists.run('launch', items[3].ref);
    assert.deepEqual(control.startedDebugs.slice(-2).map(d => d.name), [server, client]);
});

test('editors list every tab with group, dirty and active flags, and run focuses the tab', async () => {
    const { control, lists } = setup();
    const tab = /** @param {string} label @param {string} file @param {boolean} active @param {boolean} dirty */ (label, file, active, dirty) =>
        ({ label, input: { uri: Uri.file(file) }, isActive: active, isDirty: dirty });
    control.vscode.window.tabGroups.all = [
        { isActive: false, tabs: [tab('a.ts', '/work/demo/src/a.ts', true, false)] },
        { isActive: true, tabs: [tab('b.ts', '/work/demo/lib/b.ts', false, true), tab('Settings', '', true, false)] },
    ];
    control.vscode.window.tabGroups.all[1].tabs[1].input = {};
    const { items } = await lists.list('editors');
    assert.deepEqual(items.map(i => [i.label, i.detail, i.group, i.dirty, i.active]), [
        ['a.ts', 'src', 1, false, false],
        ['b.ts', 'lib', 2, true, false],
        ['Settings', '', 2, false, true],
    ]);
    control.commandHandlers.set('workbench.action.focusSecondEditorGroup', () => undefined);
    control.commandHandlers.set('workbench.action.openEditorAtIndex', () => undefined);
    await lists.run('editors', items[2].ref);
    assert.deepEqual(control.executed.map(e => [e.command, ...e.args]), [
        ['workbench.action.focusSecondEditorGroup'],
        ['workbench.action.openEditorAtIndex', 1],
    ]);
});

test('recent uses the internal command and copes with it failing', async () => {
    const { control, lists } = setup();
    assert.deepEqual((await lists.list('recent')).items, [], 'command missing gives an empty list');
    control.commandHandlers.set('_workbench.getRecentlyOpened', () => ({
        workspaces: [
            { folderUri: { scheme: 'file', path: '/Users/me/proj' } },
            { workspace: { id: 'x', configPath: Uri.file('/Users/me/all.code-workspace') } },
            { folderUri: 'file:///Users/me/other', label: 'Other' },
            { nothing: true },
        ],
        files: [],
    }));
    const { items } = await lists.list('recent');
    assert.deepEqual(items.map(i => [i.label, i.detail, i.kind]), [
        ['proj', '/Users/me/proj', 'folder'],
        ['all', '/Users/me/all.code-workspace', 'workspace'],
        ['Other', '/Users/me/other', 'folder'],
    ]);
    control.commandHandlers.set('vscode.openFolder', () => undefined);
    await lists.run('recent', items[0].ref);
    const call = control.executed.at(-1);
    assert.equal(call?.command, 'vscode.openFolder');
    assert.equal(call?.args[0].path, '/Users/me/proj');
    assert.deepEqual(call?.args[1], { forceNewWindow: false });
});

test('terminals show the last command and run shows the terminal', async () => {
    const { control, lists } = setup();
    let shown = 0;
    const zsh = { name: 'zsh', show: () => shown++ };
    const node = { name: 'node', show: () => undefined };
    control.vscode.window.terminals = [zsh, node];
    control.vscode.window.activeTerminal = node;
    const { items } = await lists.list('terminals');
    assert.deepEqual(items.map(i => [i.label, i.detail, i.active]), [['zsh', 'npm test', false], ['node', '', true]]);
    await lists.run('terminals', items[0].ref);
    assert.equal(shown, 1);
    control.vscode.window.terminals = [node];
    await assert.rejects(lists.run('terminals', items[0].ref), (/** @type {any} */ e) => e.code === 'not_found');
});

// @ts-check
'use strict';

// What each request op does once the client has said hello (see "Requests" in docs/PROTOCOL.md).

const { BridgeError, Codes, errorMessage, isAllowed, reviveArgs, toJsonSafe } = require('./protocol');

const FOCUS_WINDOW = 'workbench.action.focusWindow';

/**
 * @typedef {object} HandlerDeps
 * @property {import('./lists').Lists} lists
 * @property {{snapshot(): Record<string, unknown>}} tracker
 * @property {() => {policy: string, allowed: string[]}} policy the user's command policy, read per call
 * @property {() => boolean} hasFocusWindow whether this window has workbench.action.focusWindow
 * @property {(level: 'info' | 'warn' | 'error', text: string) => void} log
 * @property {(command: string) => void} [onCommand] called for every command run through exec
 */

/**
 * @param {typeof import('vscode')} vscode
 * @param {HandlerDeps} deps
 * @returns {(op: string, message: Record<string, any>) => Promise<unknown>}
 */
function createHandler(vscode, deps) {
    /** Brings this window to the front when the client asked and the command exists. */
    async function focusFirst(/** @type {unknown} */ wanted) {
        if (wanted !== true || !deps.hasFocusWindow()) return;
        try {
            await vscode.commands.executeCommand(FOCUS_WINDOW);
        } catch (error) {
            deps.log('warn', `focusWindow failed: ${errorMessage(error)}`);
        }
    }

    /** @param {Record<string, any>} message */
    async function exec(message) {
        const command = message.command;
        if (typeof command !== 'string' || command.length === 0 || command.length > 256) {
            throw new BridgeError(Codes.badRequest, 'command must be a non-empty string');
        }
        if (message.args !== undefined && message.args !== null && !Array.isArray(message.args)) {
            throw new BridgeError(Codes.badRequest, 'args must be an array');
        }
        const { policy, allowed } = deps.policy();
        if (!isAllowed(policy, allowed, command)) {
            deps.log('warn', `exec ${command}: refused by loupixdeck.commandPolicy`);
            throw new BridgeError(Codes.denied, `${command} is not in loupixdeck.allowedCommands`);
        }
        const args = reviveArgs(message.args ?? [], vscode.Uri);
        await focusFirst(message.focus);
        deps.log('info', `exec ${command}`);
        deps.onCommand?.(command);
        let result;
        try {
            result = await vscode.commands.executeCommand(command, ...args);
        } catch (error) {
            const text = errorMessage(error);
            if (/^command '.*' not found$/i.test(text)) throw new BridgeError(Codes.unknownCommand, text);
            deps.log('warn', `exec ${command} failed: ${text}`);
            throw new BridgeError(Codes.failed, text);
        }
        return toJsonSafe(result);
    }

    /**
     * Wraps an editor call so a plain exception becomes `failed` and a BridgeError passes through.
     * @param {() => Promise<unknown>} action
     */
    async function guarded(action) {
        try {
            return await action();
        } catch (error) {
            if (error instanceof BridgeError) throw error;
            throw new BridgeError(Codes.failed, errorMessage(error));
        }
    }

    return async function handle(op, message) {
        switch (op) {
            case 'exec':
                return exec(message);
            case 'list':
                return guarded(() => deps.lists.list(message.what));
            case 'run':
                await focusFirst(message.focus);
                deps.log('info', `run ${String(message.what)} item`);
                return guarded(() => deps.lists.run(message.what, message.ref));
            case 'runTask':
                await focusFirst(message.focus);
                deps.log('info', `runTask ${String(message.name)}`);
                return guarded(() => deps.lists.runTask(message.name));
            case 'startDebug':
                await focusFirst(message.focus);
                deps.log('info', `startDebug ${String(message.name)}`);
                return guarded(() => deps.lists.startDebug(message.name));
            case 'focus':
                await focusFirst(true);
                return null;
            case 'state':
                return deps.tracker.snapshot();
            case 'ping':
                return 'pong';
            default:
                throw new BridgeError(Codes.unknownOp, `Unknown op ${op}`);
        }
    };
}

module.exports = { createHandler, FOCUS_WINDOW };

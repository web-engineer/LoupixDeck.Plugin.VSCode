// @ts-check
'use strict';

// The wire format of the companion bridge (docs/PROTOCOL.md): NDJSON framing, the token check,
// argument revival and JSON-safe results. Nothing in here needs the editor, so the tests load it
// as it is.

const crypto = require('crypto');

const PROTOCOL = 1;

/** Lines longer than this are an error and close the connection. */
const MAX_LINE = 1024 * 1024;

/** The error codes a reply can carry. */
const Codes = Object.freeze({
    badRequest: 'bad_request',
    unknownOp: 'unknown_op',
    unknownCommand: 'unknown_command',
    notFound: 'not_found',
    failed: 'failed',
    denied: 'denied',
    busy: 'busy',
});

/** An error that goes back to the client as `{"ok":false,"error":{code,message}}`. */
class BridgeError extends Error {
    /**
     * @param {string} code one of {@link Codes}
     * @param {string} message
     */
    constructor(code, message) {
        super(message);
        this.code = code;
    }
}

/**
 * Splits a byte stream into lines. Bytes are kept until a newline arrives, so a multi-byte
 * character split across two chunks still decodes. A line over {@link MAX_LINE} bytes throws a
 * {@link BridgeError}, and the caller closes the connection.
 */
class LineReader {
    /**
     * @param {(line: string) => void} onLine called once per non-empty line, without the newline
     * @param {number} [max]
     */
    constructor(onLine, max = MAX_LINE) {
        this._onLine = onLine;
        this._max = max;
        /** @type {Buffer[]} */
        this._parts = [];
        this._length = 0;
    }

    /** @param {Buffer | string} chunk */
    push(chunk) {
        let data = typeof chunk === 'string' ? Buffer.from(chunk, 'utf8') : chunk;
        let newline = data.indexOf(0x0a);
        while (newline !== -1) {
            if (this._length + newline > this._max) throw tooLong();
            const head = data.subarray(0, newline);
            const line = this._parts.length === 0
                ? head
                : Buffer.concat([...this._parts, head], this._length + head.length);
            this._parts = [];
            this._length = 0;
            data = data.subarray(newline + 1);
            let text = line.toString('utf8');
            if (text.endsWith('\r')) text = text.slice(0, -1);
            if (text.trim().length > 0) this._onLine(text);
            newline = data.indexOf(0x0a);
        }
        if (data.length === 0) return;
        if (this._length + data.length > this._max) throw tooLong();
        this._parts.push(data);
        this._length += data.length;
    }
}

function tooLong() {
    return new BridgeError(Codes.badRequest, `Line longer than ${MAX_LINE} bytes`);
}

/** @param {unknown} message */
function encode(message) {
    return JSON.stringify(message) + '\n';
}

/** @returns {string} a fresh 32-byte token, base64url. */
function newToken() {
    return crypto.randomBytes(32).toString('base64url');
}

/**
 * Compares a token from a client with ours in constant time. A length mismatch still runs one
 * comparison so the timing does not give the length away.
 * @param {string} expected
 * @param {unknown} given
 */
function tokenEquals(expected, given) {
    const want = Buffer.from(expected, 'utf8');
    const got = Buffer.from(typeof given === 'string' ? given : '', 'utf8');
    if (got.length !== want.length) {
        crypto.timingSafeEqual(want, want);
        return false;
    }
    return crypto.timingSafeEqual(want, got);
}

/**
 * Parses one request line. Throws a {@link BridgeError} for anything that is not a JSON object
 * with an integer `id` and a string `op`.
 * @param {string} line
 * @returns {{id: number, op: string, [key: string]: any}}
 */
function parseRequest(line) {
    let message;
    try {
        message = JSON.parse(line);
    } catch {
        throw new BridgeError(Codes.badRequest, 'Not valid JSON');
    }
    if (message === null || typeof message !== 'object' || Array.isArray(message)) {
        throw new BridgeError(Codes.badRequest, 'Expected a JSON object');
    }
    if (!Number.isSafeInteger(message.id)) throw new BridgeError(Codes.badRequest, 'Missing integer id');
    if (typeof message.op !== 'string' || message.op.length === 0) {
        throw new BridgeError(Codes.badRequest, 'Missing op');
    }
    return message;
}

/** @param {unknown} value */
function isPlainObject(value) {
    if (value === null || typeof value !== 'object') return false;
    const proto = Object.getPrototypeOf(value);
    return proto === Object.prototype || proto === null;
}

/**
 * Turns `{"$uri":"…"}` objects into `vscode.Uri`s, at any depth. Everything else is returned as
 * plain JSON.
 * @param {unknown} value
 * @param {{parse(value: string): any}} Uri `vscode.Uri`
 * @returns {any}
 */
function reviveArgs(value, Uri, depth = 0) {
    if (depth > 64) throw new BridgeError(Codes.badRequest, 'Arguments nested too deeply');
    if (Array.isArray(value)) return value.map(item => reviveArgs(item, Uri, depth + 1));
    if (!isPlainObject(value)) return value;
    const record = /** @type {Record<string, unknown>} */ (value);
    const keys = Object.keys(record);
    if (keys.length === 1 && keys[0] === '$uri') {
        if (typeof record.$uri !== 'string') throw new BridgeError(Codes.badRequest, '$uri must be a string');
        try {
            return Uri.parse(record.$uri);
        } catch (error) {
            throw new BridgeError(Codes.badRequest, `Bad $uri: ${errorMessage(error)}`);
        }
    }
    /** @type {Record<string, unknown>} */
    const out = {};
    for (const key of keys) out[key] = reviveArgs(record[key], Uri, depth + 1);
    return out;
}

/**
 * True for a `vscode.Uri`, by shape, so it also works for the URI objects other extensions
 * return from their own copy of the class.
 * @param {any} value
 */
function isUri(value) {
    return value !== null && typeof value === 'object'
        && typeof value.scheme === 'string' && typeof value.path === 'string'
        && typeof value.authority === 'string' && typeof value.fsPath === 'string'
        && typeof value.with === 'function' && typeof value.toString === 'function';
}

/**
 * Makes a command's return value safe to send: a Uri becomes `{"$uri":"…"}`, functions and
 * cycles are dropped, non-finite numbers become null, and anything that still cannot be
 * serialised becomes null.
 * @param {unknown} value
 * @returns {any}
 */
function toJsonSafe(value) {
    try {
        const safe = convert(value, new Set(), 0);
        return safe === undefined ? null : safe;
    } catch {
        return null;
    }
}

/**
 * @param {any} value
 * @param {Set<object>} ancestors the objects on the path from the root, to spot cycles
 * @param {number} depth
 * @returns {any} undefined means "drop this"
 */
function convert(value, ancestors, depth) {
    switch (typeof value) {
        case 'undefined':
        case 'function':
        case 'symbol':
            return undefined;
        case 'string':
        case 'boolean':
            return value;
        case 'number':
            return Number.isFinite(value) ? value : null;
        case 'bigint':
            return value.toString();
    }
    if (value === null) return null;
    if (depth > 32) return null;
    if (ancestors.has(value)) return undefined;
    if (isUri(value)) return { $uri: value.toString() };
    if (value instanceof Date) return Number.isNaN(value.getTime()) ? null : value.toISOString();
    if (ArrayBuffer.isView(value) || value instanceof ArrayBuffer) return null;

    ancestors.add(value);
    try {
        if (Array.isArray(value) || value instanceof Set) {
            const out = [];
            for (const item of value) {
                const safe = convert(item, ancestors, depth + 1);
                out.push(safe === undefined ? null : safe);
            }
            return out;
        }
        if (value instanceof Map) {
            /** @type {Record<string, any>} */
            const out = {};
            for (const [key, item] of value) {
                const safe = convert(item, ancestors, depth + 1);
                if (safe !== undefined) out[String(key)] = safe;
            }
            return out;
        }
        if (typeof value.toJSON === 'function') {
            const json = value.toJSON();
            if (json !== value) return convert(json, ancestors, depth + 1);
        }
        /** @type {Record<string, any>} */
        const out = {};
        for (const key of Object.keys(value)) {
            let item;
            try {
                item = value[key];
            } catch {
                continue;
            }
            const safe = convert(item, ancestors, depth + 1);
            if (safe !== undefined) out[key] = safe;
        }
        return out;
    } finally {
        ancestors.delete(value);
    }
}

/**
 * The command policy: `any` runs every command, `allowList` only the listed ones.
 * @param {string | undefined} policy
 * @param {readonly string[] | undefined} allowed
 * @param {string} command
 */
function isAllowed(policy, allowed, command) {
    if (policy !== 'allowList') return true;
    return Array.isArray(allowed) && allowed.includes(command);
}

/** @param {unknown} error */
function errorMessage(error) {
    if (error instanceof Error) return error.message;
    return String(error);
}

module.exports = {
    PROTOCOL,
    MAX_LINE,
    Codes,
    BridgeError,
    LineReader,
    encode,
    newToken,
    tokenEquals,
    parseRequest,
    reviveArgs,
    isUri,
    toJsonSafe,
    isAllowed,
    errorMessage,
};

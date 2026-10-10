// @ts-check
'use strict';

// The socket server: accepts clients, checks the hello token, frames requests and replies, and
// pushes events to every client that has said hello. What each op does lives in handlers.js.

const fs = require('fs');
const net = require('net');
const { BridgeError, Codes, LineReader, encode, errorMessage, parseRequest, tokenEquals } = require('./protocol');
const { isPipe, tryUnlink } = require('./registration');

/** A client that has not said hello by then is dropped. */
const HELLO_TIMEOUT_MS = 10_000;

/** Requests one client may have running at once; more get `busy`. */
const MAX_IN_FLIGHT = 32;

/** A client that stops reading is dropped once this much output is queued for it. */
const MAX_QUEUED_BYTES = 8 * 1024 * 1024;

/**
 * @typedef {object} Client
 * @property {number} id
 * @property {net.Socket} socket
 * @property {boolean} authed true once the hello token was accepted
 * @property {boolean} ready true once the hello reply was sent; only ready clients get events
 * @property {string} name what the client called itself in its hello
 * @property {number} connectedAt epoch ms
 * @property {number} inFlight
 */

/**
 * @typedef {object} ServerOptions
 * @property {string} token
 * @property {(client: Client) => Promise<unknown>} hello builds the hello result
 * @property {(op: string, message: Record<string, any>, client: Client) => Promise<unknown>} handle
 *   runs every other op; throws a BridgeError to reply with an error
 * @property {(level: 'info' | 'warn' | 'error', text: string) => void} log
 * @property {() => void} [onClientsChanged]
 */

class BridgeServer {
    /** @param {ServerOptions} options */
    constructor(options) {
        this._options = options;
        /** @type {Set<Client>} */
        this.clients = new Set();
        /** @type {net.Server | undefined} */
        this._server = undefined;
        this._nextId = 1;
        /** @type {string | undefined} */
        this.socketPath = undefined;
    }

    /** Clients that have said hello. */
    get readyClients() {
        return [...this.clients].filter(c => c.ready);
    }

    /**
     * Starts listening. A socket file left behind by an earlier run is removed first, and once
     * more if the listen still reports EADDRINUSE.
     * @param {string} socketPath
     */
    async listen(socketPath) {
        const pipe = isPipe(socketPath);
        if (!pipe) tryUnlink(socketPath);
        try {
            await this._listenOnce(socketPath);
        } catch (error) {
            if (pipe || /** @type {NodeJS.ErrnoException} */ (error).code !== 'EADDRINUSE') throw error;
            this._options.log('warn', `Socket ${socketPath} was in use; removing the stale file`);
            tryUnlink(socketPath);
            await this._listenOnce(socketPath);
        }
        if (!pipe) {
            try {
                fs.chmodSync(socketPath, 0o600);
            } catch {
                // The directory is already user-only.
            }
        }
        this.socketPath = socketPath;
    }

    /** @param {string} socketPath */
    _listenOnce(socketPath) {
        return new Promise((resolve, reject) => {
            const server = net.createServer(socket => this._accept(socket));
            /** @param {Error} error */
            const onError = error => {
                server.close();
                reject(error);
            };
            server.once('error', onError);
            server.listen(socketPath, () => {
                server.off('error', onError);
                server.on('error', error => this._options.log('error', `Server error: ${error.message}`));
                this._server = server;
                resolve(undefined);
            });
        });
    }

    /** Stops listening, drops every client and removes the socket file. */
    close() {
        for (const client of this.clients) client.socket.destroy();
        this.clients.clear();
        if (this._server) {
            this._server.close();
            this._server = undefined;
        }
        if (this.socketPath && !isPipe(this.socketPath)) tryUnlink(this.socketPath);
        this.socketPath = undefined;
    }

    /**
     * Sends an event to every client that has said hello.
     * @param {Record<string, unknown>} event
     */
    broadcast(event) {
        const ready = this.readyClients;
        if (ready.length === 0) return;
        const line = encode(event);
        for (const client of ready) this._write(client, line);
    }

    /** @param {net.Socket} socket */
    _accept(socket) {
        /** @type {Client} */
        const client = { id: this._nextId++, socket, authed: false, ready: false, name: '', connectedAt: Date.now(), inFlight: 0 };
        this.clients.add(client);
        socket.setNoDelay?.(true);
        const timer = setTimeout(() => {
            if (!client.authed) this._drop(client, 'no hello');
        }, HELLO_TIMEOUT_MS);
        timer.unref?.();

        const reader = new LineReader(line => this._onLine(client, line));
        socket.on('data', chunk => {
            try {
                reader.push(chunk);
            } catch (error) {
                this._drop(client, errorMessage(error));
            }
        });
        socket.on('error', error => this._options.log('warn', `Client ${client.id}: ${error.message}`));
        socket.on('close', () => {
            clearTimeout(timer);
            const wasReady = client.ready;
            this.clients.delete(client);
            if (wasReady) {
                this._options.log('info', `Client ${client.id} (${client.name}) disconnected`);
                this._clientsChanged();
            }
        });
    }

    /**
     * @param {Client} client
     * @param {string} line
     */
    _onLine(client, line) {
        if (client.socket.destroyed) return;
        if (!client.authed) {
            this._onHello(client, line);
            return;
        }
        let request;
        try {
            request = parseRequest(line);
        } catch (error) {
            this._reply(client, null, error);
            return;
        }
        if (request.op === 'hello') {
            this._reply(client, request.id, new BridgeError(Codes.badRequest, 'Already said hello'));
            return;
        }
        if (client.inFlight >= MAX_IN_FLIGHT) {
            this._reply(client, request.id, new BridgeError(Codes.busy, 'Too many requests at once'));
            return;
        }
        client.inFlight++;
        Promise.resolve()
            .then(() => this._options.handle(request.op, request, client))
            .then(
                result => this._reply(client, request.id, undefined, result),
                error => this._reply(client, request.id, error),
            )
            .finally(() => client.inFlight--);
    }

    /**
     * The first line must be a hello with the right token; anything else closes the connection.
     * @param {Client} client
     * @param {string} line
     */
    _onHello(client, line) {
        let request;
        try {
            request = parseRequest(line);
        } catch {
            this._drop(client, 'first line was not a request');
            return;
        }
        if (request.op !== 'hello' || !tokenEquals(this._options.token, request.token)) {
            this._drop(client, request.op === 'hello' ? 'wrong token' : 'first request was not hello');
            return;
        }
        client.authed = true;
        client.name = typeof request.client === 'string' ? request.client.slice(0, 100) : 'unknown client';
        this._options.log('info', `Client ${client.id} (${client.name}) connected`);
        Promise.resolve()
            .then(() => this._options.hello(client))
            .then(
                result => {
                    this._reply(client, request.id, undefined, result);
                    client.ready = !client.socket.destroyed;
                    if (client.ready) this._clientsChanged();
                },
                error => {
                    this._reply(client, request.id, error);
                    this._drop(client, `hello failed: ${errorMessage(error)}`);
                },
            );
    }

    /**
     * @param {Client} client
     * @param {number | null} id
     * @param {unknown} [error]
     * @param {unknown} [result]
     */
    _reply(client, id, error, result) {
        if (client.socket.destroyed) return;
        let line;
        if (error === undefined) {
            try {
                line = encode({ id, ok: true, result: result === undefined ? null : result });
            } catch (failure) {
                error = new BridgeError(Codes.failed, `Result could not be serialised: ${errorMessage(failure)}`);
            }
        }
        if (line === undefined) {
            const code = error instanceof BridgeError ? error.code : Codes.failed;
            line = encode({ id, ok: false, error: { code, message: errorMessage(error) } });
        }
        this._write(client, line);
    }

    /**
     * @param {Client} client
     * @param {string} line
     */
    _write(client, line) {
        if (client.socket.destroyed) return;
        if (client.socket.writableLength > MAX_QUEUED_BYTES) {
            this._drop(client, 'not reading its replies');
            return;
        }
        try {
            client.socket.write(line);
        } catch (error) {
            this._drop(client, errorMessage(error));
        }
    }

    /**
     * @param {Client} client
     * @param {string} reason
     */
    _drop(client, reason) {
        this._options.log('warn', `Dropping client ${client.id}: ${reason}`);
        client.socket.destroy();
    }

    _clientsChanged() {
        try {
            this._options.onClientsChanged?.();
        } catch (error) {
            this._options.log('error', `onClientsChanged: ${errorMessage(error)}`);
        }
    }
}

module.exports = { BridgeServer, HELLO_TIMEOUT_MS, MAX_IN_FLIGHT };

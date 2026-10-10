// @ts-check
'use strict';

// A minimal NDJSON client for the server tests.

const net = require('net');

/**
 * @param {string} socketPath
 */
function connect(socketPath) {
    const socket = net.createConnection(socketPath);
    /** @type {any[]} */
    const received = [];
    /** @type {((message: any) => void)[]} */
    const waiters = [];
    let buffer = '';
    let closed = false;
    /** @type {(() => void)[]} */
    const closeWaiters = [];
    socket.setEncoding('utf8');
    socket.on('data', chunk => {
        buffer += chunk;
        let newline;
        while ((newline = buffer.indexOf('\n')) !== -1) {
            const message = JSON.parse(buffer.slice(0, newline));
            buffer = buffer.slice(newline + 1);
            received.push(message);
            waiters.splice(0).forEach(w => w(message));
        }
    });
    socket.on('error', () => undefined);
    socket.on('close', () => {
        closed = true;
        closeWaiters.splice(0).forEach(w => w());
    });

    const client = {
        socket,
        received,
        get closed() {
            return closed;
        },
        /** @param {unknown} message */
        send(message) {
            socket.write((typeof message === 'string' ? message : JSON.stringify(message)) + '\n');
        },
        /**
         * Resolves with the first received message matching the predicate (already received or not).
         * @param {(message: any) => boolean} predicate
         * @param {number} [timeout]
         * @returns {Promise<any>}
         */
        waitFor(predicate, timeout = 3000) {
            const found = received.find(predicate);
            if (found) return Promise.resolve(found);
            return new Promise((resolve, reject) => {
                const timer = setTimeout(() => reject(new Error('timed out waiting for a message')), timeout);
                const check = /** @param {any} message */ message => {
                    if (predicate(message)) {
                        clearTimeout(timer);
                        resolve(message);
                    } else {
                        waiters.push(check);
                    }
                };
                waiters.push(check);
            });
        },
        /**
         * Sends a request and waits for its reply.
         * @param {Record<string, unknown>} message
         */
        request(message) {
            client.send(message);
            return client.waitFor(m => m.id === message.id);
        },
        /** @returns {Promise<void>} */
        waitClosed(timeout = 3000) {
            if (closed) return Promise.resolve();
            return new Promise((resolve, reject) => {
                const timer = setTimeout(() => reject(new Error('connection stayed open')), timeout);
                closeWaiters.push(() => {
                    clearTimeout(timer);
                    resolve();
                });
            });
        },
        close() {
            socket.destroy();
        },
    };
    return new Promise((resolve, reject) => {
        socket.once('connect', () => resolve(client));
        socket.once('error', reject);
    });
}

module.exports = { connect };

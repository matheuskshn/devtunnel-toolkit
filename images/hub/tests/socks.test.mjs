import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import net from 'node:net';
import test from 'node:test';
import { SocksProxy } from '../dist/socks.js';

const greeting = Buffer.from([5, 1, 0]);
const connected = Buffer.from([5, 0, 0, 1, 0, 0, 0, 0, 0, 0]);
const tick = () => new Promise(resolve => setTimeout(resolve, 2));

function request(domain = 'target.example.test', port = 443, command = 1) {
  const name = Buffer.isBuffer(domain) ? domain : Buffer.from(domain);
  const suffix = Buffer.alloc(2);
  suffix.writeUInt16BE(port);
  return Buffer.concat([Buffer.from([5, command, 0, 3, name.length]), name, suffix]);
}

async function freePort() {
  const server = net.createServer();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
}

async function proxy(t, handler = (socket, _headers, early) => {
  socket.write('HTTP/1.1 200 Connection established\r\n\r\n');
  if (early.length) socket.write(early);
  socket.pipe(socket);
}) {
  const sockets = new Set();
  const requests = [];
  const server = net.createServer({ allowHalfOpen: true }, socket => {
    sockets.add(socket);
    socket.on('error', () => {});
    socket.on('close', () => sockets.delete(socket));
    let input = Buffer.alloc(0);
    const parse = data => {
      input = Buffer.concat([input, data]);
      const end = input.indexOf('\r\n\r\n');
      if (end < 0) return;
      socket.off('data', parse);
      const headers = input.subarray(0, end + 4).toString('ascii');
      requests.push(headers);
      handler(socket, headers, input.subarray(end + 4));
    };
    socket.on('data', parse);
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(async () => {
    for (const socket of sockets) socket.destroy();
    await new Promise(resolve => server.close(resolve));
  });
  return { port: server.address().port, requests, sockets };
}

async function adapter(t, upstream, options = {}) {
  const port = await freePort();
  const server = new SocksProxy({ listenPort: port, proxyPort: upstream.port, ...options });
  await server.listen();
  t.after(() => server.close());
  return { port, server };
}

async function client(t, port) {
  const socket = net.createConnection({ host: '127.0.0.1', port, allowHalfOpen: true });
  socket.on('error', () => {});
  t.after(() => socket.destroy());
  let input = Buffer.alloc(0);
  let ended = false;
  const waiters = [];
  const flush = () => {
    for (const waiter of [...waiters]) {
      if (input.length >= waiter.size) {
        const result = input.subarray(0, waiter.size);
        input = input.subarray(waiter.size);
        waiters.splice(waiters.indexOf(waiter), 1);
        clearTimeout(waiter.timer);
        waiter.resolve(result);
      } else if (ended) {
        waiters.splice(waiters.indexOf(waiter), 1);
        clearTimeout(waiter.timer);
        waiter.reject(Error('Socket ended before expected data'));
      }
    }
  };
  socket.on('data', data => { input = Buffer.concat([input, data]); flush(); });
  socket.on('end', () => { ended = true; flush(); });
  const closed = new Promise(resolve => socket.once('close', () => { ended = true; flush(); resolve(); }));
  await once(socket, 'connect');
  return {
    socket,
    closed,
    remaining: () => input,
    read(size) {
      return new Promise((resolve, reject) => {
        const waiter = { size, resolve, reject, timer: undefined };
        waiter.timer = setTimeout(() => {
          waiters.splice(waiters.indexOf(waiter), 1);
          reject(Error(`Timed out reading ${size} bytes`));
        }, 2000);
        waiters.push(waiter);
        flush();
      });
    },
  };
}

async function negotiate(connection, connectRequest = request()) {
  connection.socket.write(Buffer.concat([greeting, connectRequest]));
  assert.deepEqual(await connection.read(2), Buffer.from([5, 0]));
  return connection.read(10);
}

test('SOCKS routes domain CONNECT only through its own Squid listener with binary pipelining', async t => {
  const targetBanner = Buffer.from([0, 255, 18, 13, 10]);
  const fake = await proxy(t, (socket, _headers, early) => {
    socket.write(Buffer.concat([Buffer.from('HTTP/1.1 200 OK\r\nServer: synthetic\r\n\r\n'), targetBanner]));
    if (early.length) socket.write(early);
    socket.pipe(socket);
  });
  const socks = await adapter(t, fake);
  const connection = await client(t, socks.port);
  const payload = Buffer.from([0, 2, 255, 13, 10, 128]);
  connection.socket.write(Buffer.concat([greeting, request('No-Dns-Exists.Example.Invalid.'), payload]));
  assert.deepEqual(await connection.read(2), Buffer.from([5, 0]));
  assert.deepEqual(await connection.read(10), connected);
  assert.deepEqual(await connection.read(targetBanner.length), targetBanner);
  assert.deepEqual(await connection.read(payload.length), payload);
  assert.deepEqual(fake.requests, ['CONNECT no-dns-exists.example.invalid:443 HTTP/1.1\r\nHost: no-dns-exists.example.invalid:443\r\n\r\n']);
});

test('split SOCKS greeting/request and split HTTP response preserve protocol ordering', async t => {
  const fake = await proxy(t, socket => {
    void (async () => {
      for (const byte of Buffer.from('HTTP/1.1 200 OK\r\n\r\nhello')) { socket.write(Buffer.from([byte])); await tick(); }
    })();
    socket.pipe(socket);
  });
  const socks = await adapter(t, fake);
  const connection = await client(t, socks.port);
  for (const byte of Buffer.concat([greeting, request()])) { connection.socket.write(Buffer.from([byte])); await tick(); }
  assert.deepEqual(await connection.read(2), Buffer.from([5, 0]));
  assert.deepEqual(await connection.read(10), connected);
  assert.equal(String(await connection.read(5)), 'hello');
});

test('IPv4 and IPv6 CONNECT authorities are encoded unambiguously without target lookup', async t => {
  const fake = await proxy(t);
  const socks = await adapter(t, fake);
  const v4 = await client(t, socks.port);
  assert.deepEqual(await negotiate(v4, Buffer.from([5, 1, 0, 1, 192, 0, 2, 1, 0, 22])), connected);
  const v6 = await client(t, socks.port);
  assert.deepEqual(await negotiate(v6, Buffer.from([5, 1, 0, 4, 0x20, 1, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0xbb])), connected);
  assert.match(fake.requests[0], /^CONNECT 192\.0\.2\.1:22 /);
  assert.match(fake.requests[1], /^CONNECT \[2001:db8:0:0:0:0:0:1\]:443 /);
});

test('NOAUTH may be one offered method; credentials are neither accepted nor forwarded', async t => {
  const fake = await proxy(t);
  const socks = await adapter(t, fake);
  const connection = await client(t, socks.port);
  connection.socket.write(Buffer.from([5, 3, 1, 2, 0]));
  assert.deepEqual(await connection.read(2), Buffer.from([5, 0]));
  connection.socket.write(request());
  assert.deepEqual(await connection.read(10), connected);
  assert.ok(!fake.requests[0].includes('Authorization'));
  for (const methods of [[5, 1, 2], [5, 2, 1, 2], [5, 0]]) {
    const denied = await client(t, socks.port);
    denied.socket.write(Buffer.from(methods));
    assert.deepEqual(await denied.read(2), Buffer.from([5, 255]));
    denied.socket.end();
    await denied.closed;
  }
  assert.equal(fake.requests.length, 1);
});

test('SOCKS4, BIND, UDP ASSOCIATE, unknown commands/types and nonzero reserved bytes fail closed', async t => {
  const fake = await proxy(t);
  const socks = await adapter(t, fake);
  const old = await client(t, socks.port);
  old.socket.write(Buffer.from([4, 1, 1, 187, 192, 0, 2, 1, 0]));
  old.socket.end();
  await old.closed;
  assert.equal(old.remaining().length, 0);
  for (const [bytes, expected] of [
    [request('example.test', 443, 2), 7],
    [request('example.test', 443, 3), 7],
    [request('example.test', 443, 255), 7],
    [Buffer.from([5, 1, 0, 9]), 8],
    [Buffer.from([5, 1, 1, 3]), 1],
    [Buffer.from([4, 1, 0, 3]), 1],
    [request('example.test', 0), 1],
  ]) {
    const denied = await client(t, socks.port);
    assert.equal((await negotiate(denied, bytes))[1], expected);
    denied.socket.end();
    await denied.closed;
  }
  assert.equal(fake.requests.length, 0);
});

test('domain CRLF, control, high bytes, ambiguous authorities and invalid DNS labels never reach Squid', async t => {
  const fake = await proxy(t);
  const socks = await adapter(t, fake);
  for (const name of [
    '', '.', 'example.test\r\nX-Injected: yes', 'evil.test\0good.test', 'bad name',
    'user@example.test', 'example.test:443', '[::1]', 'example.test/path',
    'example..test', '-bad.test', 'bad-.test', `${'a'.repeat(64)}.test`,
    'a'.repeat(254), Buffer.from([0xff, 0x61]),
  ]) {
    const connection = await client(t, socks.port);
    assert.equal((await negotiate(connection, request(name)))[1], 8);
    connection.socket.end();
    await connection.closed;
  }
  assert.equal(fake.requests.length, 0);
});

test('HTTP denial and failures become SOCKS failures, never a bypass or leaked proxy response', async t => {
  for (const [status, expected] of [[403, 2], [405, 2], [407, 2], [502, 4], [503, 3], [504, 6], [201, 1], [500, 1]]) {
    const fake = await proxy(t, socket => socket.end(`HTTP/1.1 ${status} Denied\r\n\r\nprivate-proxy-body`));
    const socks = await adapter(t, fake);
    const connection = await client(t, socks.port);
    assert.equal((await negotiate(connection))[1], expected);
    connection.socket.end();
    await connection.closed;
    assert.equal(connection.remaining().length, 0);
  }
});

test('malformed and oversized HTTP responses fail closed, including header controls and folding', async t => {
  for (const response of [
    'NOTHTTP 200 OK\r\n\r\n', 'HTTP/1.1 2000 OK\r\n\r\n',
    'HTTP/1.1 200 OK\r\nBad Header: value\r\n\r\n',
    'HTTP/1.1 200 OK\r\nGood: ok\r\n folded\r\n\r\n',
    'HTTP/1.1 200 OK\r\nBad: \0\r\n\r\n',
    `HTTP/1.1 200 OK\r\nHuge: ${'x'.repeat(8192)}\r\n\r\n`,
    'x'.repeat(9000),
  ]) {
    const fake = await proxy(t, socket => socket.write(response));
    const socks = await adapter(t, fake);
    const connection = await client(t, socks.port);
    assert.equal((await negotiate(connection))[1], 1);
    connection.socket.end();
    await connection.closed;
  }
});

test('incomplete greeting and request cannot extend the absolute handshake deadline', async t => {
  const fake = await proxy(t);
  const socks = await adapter(t, fake, { handshakeTimeoutMs: 50 });
  const partial = await client(t, socks.port);
  partial.socket.write(Buffer.from([5]));
  const started = Date.now();
  await once(partial.socket, 'end');
  partial.socket.end();
  await partial.closed;
  assert.ok(Date.now() - started >= 30 && Date.now() - started < 1000);
  const slow = await client(t, socks.port);
  slow.socket.write(Buffer.concat([greeting, Buffer.from([5, 1, 0, 3, 200])]));
  assert.deepEqual(await slow.read(2), Buffer.from([5, 0]));
  const interval = setInterval(() => slow.socket.write('a'), 10);
  try { assert.equal((await slow.read(10))[1], 6); }
  finally { clearInterval(interval); slow.socket.end(); }
  await slow.closed;
  assert.equal(fake.requests.length, 0);
});

test('hung or truncated Squid CONNECT has a bounded deadline and releases the upstream socket', async t => {
  const fake = await proxy(t, socket => {
    socket.write('HTTP/1.1 200');
    socket.on('end', () => socket.end());
  });
  const socks = await adapter(t, fake, { handshakeTimeoutMs: 50 });
  const connection = await client(t, socks.port);
  assert.equal((await negotiate(connection))[1], 6);
  connection.socket.end();
  await connection.closed;
  await tick();
  assert.equal(fake.sockets.size, 0);
  const ended = await proxy(t, socket => socket.end('HTTP/1.1 200'));
  const second = await adapter(t, ended);
  const truncated = await client(t, second.port);
  assert.equal((await negotiate(truncated))[1], 4);
});

test('unavailable Squid returns connection refused and does not try the requested destination', async t => {
  const socks = await adapter(t, { port: await freePort() });
  const connection = await client(t, socks.port);
  assert.equal((await negotiate(connection, request('unresolvable.example.invalid')))[1], 5);
});

test('per-session adapters preserve listener isolation and stop independently', async t => {
  const first = await proxy(t, socket => socket.write('HTTP/1.1 200 OK\r\n\r\nuser-a'));
  const second = await proxy(t, socket => socket.write('HTTP/1.1 200 OK\r\n\r\nuser-b'));
  const a = await adapter(t, first);
  const b = await adapter(t, second);
  const one = await client(t, a.port);
  const two = await client(t, b.port);
  assert.deepEqual(await negotiate(one), connected);
  assert.deepEqual(await negotiate(two), connected);
  assert.equal(String(await one.read(6)), 'user-a');
  assert.equal(String(await two.read(6)), 'user-b');
  await a.server.close();
  one.socket.end();
  await one.closed;
  assert.equal(two.socket.destroyed, false);
  const again = await client(t, b.port);
  assert.deepEqual(await negotiate(again), connected);
  assert.equal(String(await again.read(6)), 'user-b');
});

test('bounded connection admission releases capacity after closure', async t => {
  const fake = await proxy(t);
  const socks = await adapter(t, fake, { maxConnections: 1 });
  const first = await client(t, socks.port);
  assert.deepEqual(await negotiate(first), connected);
  const rejected = await client(t, socks.port);
  rejected.socket.end();
  await rejected.closed;
  assert.equal(rejected.remaining().length, 0);
  first.socket.destroy();
  await first.closed;
  await tick();
  const next = await client(t, socks.port);
  assert.deepEqual(await negotiate(next), connected);
  assert.equal(fake.requests.length, 2);
});

test('streaming uses backpressure and preserves large binary data across client half-close', async t => {
  const fake = await proxy(t, socket => {
    socket.write('HTTP/1.1 200 OK\r\n\r\n');
    socket.pipe(socket);
  });
  const socks = await adapter(t, fake);
  const connection = await client(t, socks.port);
  assert.deepEqual(await negotiate(connection), connected);
  const payload = Buffer.alloc(2 * 1024 * 1024);
  for (let i = 0; i < payload.length; i++) payload[i] = i % 251;
  const received = connection.read(payload.length);
  connection.socket.end(payload);
  assert.equal(createHash('sha256').update(await received).digest('hex'), createHash('sha256').update(payload).digest('hex'));
  await connection.closed;
});

test('idle TCP tunnels close without injecting a SOCKS frame into application data', async t => {
  const fake = await proxy(t);
  const socks = await adapter(t, fake, { idleTimeoutMs: 40 });
  const connection = await client(t, socks.port);
  assert.deepEqual(await negotiate(connection), connected);
  const ended = once(connection.socket, 'end');
  await ended;
  assert.equal(connection.remaining().length, 0);
  connection.socket.end();
  await connection.closed;
});

test('configuration rejects privileged, duplicate, fractional and unbounded settings', () => {
  const defaults = { listenPort: 19001, proxyPort: 18001 };
  for (const options of [
    { listenPort: 0 }, { listenPort: 1023 }, { proxyPort: 22 }, { proxyPort: 65536 },
    { listenPort: 18001 }, { listenPort: 19001.5 }, { proxyPort: '18001' },
    { maxConnections: 0 }, { maxConnections: 4097 }, { maxConnections: Infinity },
    { handshakeTimeoutMs: 0 }, { handshakeTimeoutMs: 60001 },
    { idleTimeoutMs: -1 }, { idleTimeoutMs: 86400001 },
  ]) assert.throws(() => new SocksProxy({ ...defaults, ...options }), TypeError);
});

test('listener startup conflicts reject cleanly, and shutdown is idempotent even during startup', async t => {
  const fake = await proxy(t);
  const socks = await adapter(t, fake);
  await assert.rejects(socks.server.listen(), /already started/);
  const conflict = new SocksProxy({ listenPort: socks.port, proxyPort: fake.port });
  await assert.rejects(conflict.listen(), { code: 'EADDRINUSE' });
  await conflict.close();
  await conflict.close();
  const neverStarted = new SocksProxy({ listenPort: await freePort(), proxyPort: fake.port });
  await neverStarted.close();
  await assert.rejects(neverStarted.listen(), /closed/);
  const concurrent = new SocksProxy({ listenPort: await freePort(), proxyPort: fake.port });
  await Promise.all([concurrent.listen(), concurrent.close()]);
  await concurrent.close();
});

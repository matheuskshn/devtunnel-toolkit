import net from "node:net";
import {
  connectRequest,
  greetingLength,
  SocksProtocolError,
} from "./socks-protocol.js";

export interface SocksProxyOptions {
  /** Private, per-session listener. Never bind or publish this socket directly. */
  listenPort: number;
  /** The same session's Squid listener, which owns destination policy and audit. */
  proxyPort: number;
  maxConnections?: number;
  handshakeTimeoutMs?: number;
  idleTimeoutMs?: number;
}

const MAX_CLIENT_BYTES = 64 * 1024;
const MAX_HEADER_BYTES = 8 * 1024;
const reply = (code: number) => Buffer.from([5, code, 0, 1, 0, 0, 0, 0, 0, 0]);
type Phase = "greeting" | "request" | "proxy" | "stream" | "closed";

/**
 * SOCKS5 CONNECT translated to HTTP CONNECT, always through the owning session's
 * loopback Squid listener. NOAUTH here does not grant public access: the private
 * Dev Tunnel authenticates the caller, and immutable listeners attribute access.
 * No target DNS lookup, direct target connection, BIND, or UDP is implemented.
 */
export class SocksProxy {
  private readonly server: net.Server;
  private readonly clients = new Set<net.Socket>();
  private readonly options: Required<SocksProxyOptions>;
  private closing = false;
  private listenPromise?: Promise<void>;
  private closePromise?: Promise<void>;

  constructor(options: SocksProxyOptions) {
    this.options = {
      maxConnections: 128,
      handshakeTimeoutMs: 10_000,
      idleTimeoutMs: 300_000,
      ...options,
    };
    const {
      listenPort,
      proxyPort,
      maxConnections,
      handshakeTimeoutMs,
      idleTimeoutMs,
    } = this.options;
    const between = (n: number, min: number, max: number) =>
      Number.isInteger(n) && n >= min && n <= max;
    if (
      !between(listenPort, 1024, 65535) ||
      !between(proxyPort, 1024, 65535) ||
      listenPort === proxyPort ||
      !between(maxConnections, 1, 4096) ||
      !between(handshakeTimeoutMs, 10, 60_000) ||
      !between(idleTimeoutMs, 10, 86_400_000)
    ) {
      throw new TypeError("Invalid SOCKS proxy configuration");
    }
    this.server = net.createServer({ allowHalfOpen: true }, (client) =>
      this.accept(client),
    );
  }

  listen(): Promise<void> {
    if (this.closing || this.listenPromise) {
      return Promise.reject(new Error("SOCKS proxy already started or closed"));
    }
    this.listenPromise = new Promise<void>((resolve, reject) => {
      const onError = (error: Error) => {
        this.server.off("listening", onListening);
        reject(error);
      };
      const onListening = () => {
        this.server.off("error", onError);
        resolve();
      };
      this.server.once("error", onError);
      this.server.once("listening", onListening);
      this.server.listen(this.options.listenPort, "127.0.0.1");
    });
    return this.listenPromise;
  }

  close(): Promise<void> {
    if (this.closePromise) {
      return this.closePromise;
    }
    this.closing = true;
    this.closePromise = (async () => {
      await this.listenPromise?.catch(() => undefined);
      for (const client of this.clients) {
        client.destroy();
      }
      if (this.server.listening) {
        await new Promise<void>((resolve) =>
          this.server.close(() => resolve()),
        );
      }
    })();
    return this.closePromise;
  }

  private accept(client: net.Socket): void {
    if (this.closing || this.clients.size >= this.options.maxConnections) {
      client.destroy();
      return;
    }
    this.clients.add(client);
    client.setNoDelay(true);
    let phase: Phase = "greeting";
    let input: Buffer = Buffer.alloc(0);
    let headers: Buffer = Buffer.alloc(0);
    let upstream: net.Socket | undefined;
    let idleTimer: NodeJS.Timeout | undefined;
    const handshakeTimer = setTimeout(
      () => fail(6),
      this.options.handshakeTimeoutMs,
    );
    handshakeTimer.unref();
    const touch = () => idleTimer?.refresh();
    const clearTimers = () => {
      clearTimeout(handshakeTimer);
      clearTimeout(idleTimer);
    };
    const fail = (code: number, method = false) => {
      if (phase === "closed") {
        return;
      }
      const frame = method
        ? Buffer.from([5, 255])
        : phase === "greeting"
          ? undefined
          : reply(code);
      const wasStreaming = phase === "stream";
      phase = "closed";
      clearTimers();
      input = headers = Buffer.alloc(0);
      upstream?.destroy();
      if (!wasStreaming && frame && client.writable) {
        client.end(frame);
        client.destroySoon();
      } else {
        client.destroy();
      }
    };
    client.on("error", () => fail(1));
    client.on("close", () => {
      phase = "closed";
      clearTimers();
      upstream?.destroy();
      this.clients.delete(client);
    });
    client.on("end", () => {
      if (phase !== "stream") {
        fail(1);
      }
    });

    const proxyData = (data: Buffer) => {
      if (phase !== "proxy" || !upstream) {
        return;
      }
      // A single read may contain a valid header and an early target banner.
      if (headers.length + data.length > MAX_HEADER_BYTES + MAX_CLIENT_BYTES) {
        fail(1);
        return;
      }
      headers = Buffer.concat([headers, data]);
      const end = headers.indexOf("\r\n\r\n");
      if (end < 0) {
        if (headers.length > MAX_HEADER_BYTES) {
          fail(1);
        }
        return;
      }
      if (end + 4 > MAX_HEADER_BYTES) {
        fail(1);
        return;
      }
      const lines = headers.subarray(0, end).toString("latin1").split("\r\n");
      const status = /^HTTP\/1\.[01] ([0-9]{3})(?: [\x20-\x7e]*)?$/.exec(
        lines.shift() ?? "",
      );
      if (
        !status ||
        !lines.every((line) =>
          /^[!#$%&'*+.^_`|~0-9A-Za-z-]+:[\t\x20-\x7e]*$/.test(line),
        )
      ) {
        fail(1);
        return;
      }
      if (status[1] !== "200") {
        fail(
          (
            {
              "403": 2,
              "405": 2,
              "407": 2,
              "502": 4,
              "503": 3,
              "504": 6,
            } as Record<string, number>
          )[status[1]] ?? 1,
        );
        return;
      }
      upstream.pause();
      upstream.off("data", proxyData);
      client.off("data", clientData);
      phase = "stream";
      clearTimeout(handshakeTimer);
      idleTimer = setTimeout(() => fail(6), this.options.idleTimeoutMs);
      idleTimer.unref();
      client.on("data", touch);
      upstream.on("data", touch);
      client.write(reply(0));
      // Put coalesced bytes back into the streams so pipe handles backpressure,
      // binary payloads, ordering, and independent TCP half-closes in both ways.
      if (input.length) {
        client.unshift(input);
      }
      if (headers.length > end + 4) {
        upstream.unshift(headers.subarray(end + 4));
      }
      input = headers = Buffer.alloc(0);
      upstream.pipe(client);
      client.pipe(upstream);
    };

    const connect = (host: string, port: number) => {
      phase = "proxy";
      client.pause();
      upstream = net.createConnection({
        host: "127.0.0.1",
        port: this.options.proxyPort,
        allowHalfOpen: true,
      });
      upstream.setNoDelay(true);
      upstream.on("error", (error: NodeJS.ErrnoException) => {
        fail(
          (
            {
              ECONNREFUSED: 5,
              ENETUNREACH: 3,
              EHOSTUNREACH: 4,
              ETIMEDOUT: 6,
            } as Record<string, number>
          )[error.code ?? ""] ?? 1,
        );
      });
      upstream.on("end", () => {
        if (phase !== "stream") {
          fail(4);
        }
      });
      upstream.on("close", (hadError) => {
        if (hadError || !upstream?.readableEnded) {
          fail(4);
        }
      });
      upstream.once("connect", () => {
        if (phase !== "proxy") {
          return;
        }
        const authority = `${host}:${port}`;
        upstream!.write(
          `CONNECT ${authority} HTTP/1.1\r\nHost: ${authority}\r\n\r\n`,
        );
      });
      upstream.on("data", proxyData);
    };

    const clientData = (data: Buffer) => {
      if (phase !== "greeting" && phase !== "request") {
        return;
      }
      if (input.length + data.length > MAX_CLIENT_BYTES) {
        fail(1);
        return;
      }
      input = Buffer.concat([input, data]);
      try {
        if (phase === "greeting") {
          const end = greetingLength(input);
          if (end === undefined) {
            return;
          }
          input = input.subarray(end);
          client.write(Buffer.from([5, 0]));
          phase = "request";
        }
        const target = connectRequest(input);
        if (!target) {
          return;
        }
        input = input.subarray(target.end);
        connect(target.host, target.port);
      } catch (error) {
        if (!(error instanceof SocksProtocolError)) {
          throw error;
        }
        fail(error.replyCode, error.method);
      }
    };
    client.on("data", clientData);
  }
}

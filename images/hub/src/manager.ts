import {
  createServer as createSocketServer,
  createConnection,
  type Socket,
  type Server,
} from "node:net";
import {
  createServer as createHttpServer,
  type Server as HttpServer,
} from "node:http";
import { chmod, rm, rename } from "node:fs/promises";
import {
  openSync,
  createReadStream,
  writeSync,
  constants,
  type ReadStream,
} from "node:fs";
import path from "node:path";
import { fork, type ChildProcess } from "node:child_process";
import { createInterface } from "node:readline";
import { setTimeout as delay } from "node:timers/promises";
import {
  check,
  DEFAULT_PROXY_PORT,
  errorCode,
  HubError,
  parseConfig,
  type Session,
  type Config,
  type Provider,
} from "./model.js";
import {
  StateStore,
  privateDirectory,
  atomicWrite,
  type Persistence,
} from "./state.js";
import {
  SessionRuntime,
  parseJson,
  canonicalTunnel,
  privateTunnel,
} from "./auth.js";
import { squidConfig, auditRecord } from "./squid.js";
import { launch, terminate, command, cleanEnvironment } from "./processes.js";
import { ControlStore } from "./web/control.js";
import { webOptions } from "./web/security.js";
import { WebConsole } from "./web/server.js";

export class Manager {
  readonly store: StateStore;
  private squid?: ChildProcess;
  private socket?: Server;
  private health?: HttpServer;
  private stopping = false;
  private healthy = false;
  private readonly runtimes = new Map<string, SessionRuntime>();
  private readonly workers = new Map<string, ChildProcess>();
  private readonly busy = new Set<string>();
  private readonly retries = new Map<string, NodeJS.Timeout>();
  private readonly failures = new Map<string, number>();
  private readonly maintenance = new Map<string, number>();
  private configQueue: Promise<void> = Promise.resolve();
  private readonly operations = new Set<Promise<unknown>>();
  private readonly clients = new Set<Socket>();
  private auditStream?: ReadStream;
  private auditFd?: number;
  private web?: WebConsole;
  private control?: ControlStore;
  private changingPolicy = false;
  private webPort?: number;
  constructor(
    readonly config: Config,
    readonly dataDir: string,
    readonly runDir: string,
    readonly persistence?: Persistence,
  ) {
    this.store = new StateStore(dataDir, config, persistence);
  }
  log(event: string, session?: Session, code?: string): void {
    const record = {
      time: new Date().toISOString(),
      event,
      session_id: session?.id,
      code,
    };
    this.web?.record(record);
    process.stdout.write(JSON.stringify(record) + "\n");
  }
  async open(): Promise<void> {
    await privateDirectory(this.runDir);
    await this.store.load();
    const options = webOptions(process.env);
    if (options) {
      this.webPort = options.port;
      this.control = new ControlStore(this.store, options.key, () =>
        this.fence(),
      );
      await this.control.load();
      if (this.control.data.policy)
        Object.assign(
          this.config,
          parseConfig({ ...this.config, ...this.control.data.policy }),
        );
      check(
        options.port !== this.config.healthPort &&
          options.port !== this.config.proxyPort &&
          options.port !== this.config.socksPort &&
          !(
            options.port >= this.config.listenerStart &&
            options.port <= this.config.listenerEnd
          ),
        "WEB_PORT_COLLISION",
      );
      this.web = new WebConsole(
        {
          config: this.config,
          sessions: () => this.store.state.sessions,
          ready: () => this.healthy && !this.stopping,
          dispatch: (args, output) => this.dispatch(args, output),
          policy: (value, persist) => this.applyPolicy(value, persist),
          track: (operation) => this.track(operation),
        },
        this.control,
        options,
      );
    }
    for (const s of this.store.state.sessions)
      if (["running", "starting"].includes(s.status)) s.status = "stopped";
    await this.store.save();
    const fifo = path.join(this.runDir, "audit.fifo");
    await rm(fifo, { force: true });
    await command("mkfifo", ["-m", "600", fifo], cleanEnvironment());
    this.auditFd = openSync(fifo, constants.O_RDWR);
    this.auditStream = createReadStream(fifo, { fd: this.auditFd });
    const lines = createInterface({ input: this.auditStream });
    lines.on("line", (line) => {
      const record = auditRecord(line, this.store.state.sessions);
      if (record) this.web?.record(record);
      if (record && !process.stdout.write(JSON.stringify(record) + "\n")) {
        this.auditStream!.pause();
        process.stdout.once("drain", () => this.auditStream?.resume());
      }
    });
    await this.configureSquid();
    this.socket = createSocketServer((socket) => this.accept(socket));
    const address = path.join(this.runDir, "manager.sock");
    await rm(address, { force: true });
    await new Promise<void>((resolve, reject) => {
      this.socket!.once("error", reject);
      this.socket!.listen(address, resolve);
    });
    await chmod(address, 0o600);
    this.health = createHttpServer((req, res) => {
      const found = ["/live", "/ready"].includes(req.url ?? "");
      const ok =
        req.url === "/live" ? !this.stopping : this.healthy && !this.stopping;
      const healthStatus = ok ? 200 : 503;
      res.writeHead(found ? healthStatus : 404, {
        "Content-Type": "application/json",
      });
      res.end(JSON.stringify({ ok: found && ok }));
    });
    await new Promise<void>((resolve, reject) => {
      this.health!.once("error", reject);
      this.health!.listen(this.config.healthPort, "0.0.0.0", resolve);
    });
    await this.web?.open();
    this.log("manager_ready");
    for (const s of this.store.state.sessions.filter(
      (s) => s.desired && s.status !== "removed",
    )) {
      this.track(
        this.exclusive(s.id, () => this.start(s)).catch((e) => this.fail(s, e)),
      );
    }
  }
  track<T>(operation: Promise<T>): Promise<T> {
    this.operations.add(operation);
    void operation
      .finally(() => this.operations.delete(operation))
      .catch(() => {});
    return operation;
  }
  private async exclusive<T>(id: string, action: () => Promise<T>): Promise<T> {
    check(!this.stopping, "MANAGER_STOPPING");
    check(!this.changingPolicy && !this.busy.has(id), "SESSION_BUSY");
    this.busy.add(id);
    try {
      return await action();
    } finally {
      this.busy.delete(id);
    }
  }
  private async applyPolicy(
    value: Partial<Config>,
    persist: () => Promise<void>,
  ): Promise<void> {
    check(!this.stopping && !this.changingPolicy, "MANAGER_STOPPING");
    check(
      this.busy.size === 0 &&
        this.workers.size === 0 &&
        this.store.state.sessions.every((s) => !s.desired),
      "STOP_SESSIONS_FIRST",
    );
    const next = parseConfig({ ...this.config, ...value }),
      previous = { ...this.config };
    check(
      next.proxyPort !== this.webPort && next.socksPort !== this.webPort,
      "WEB_PORT_COLLISION",
    );
    if (next.socksEnabled)
      check(
        this.store.state.sessions.filter(
          (s) => s.status !== "removed" && s.socks_listener === undefined,
        ).length <=
          this.config.listenerEnd - this.store.state.next_listener + 1,
        "LISTENER_POOL_EXHAUSTED",
      );
    check(
      this.store.state.sessions.filter((s) => s.status !== "removed").length <=
        next.maxSessions,
      "SESSION_LIMIT_BELOW_USAGE",
    );
    this.changingPolicy = true;
    try {
      await Promise.all([...this.runtimes.values()].map((r) => r.close()));
      this.runtimes.clear();
      Object.assign(this.config, next);
      await this.configureSquid();
      await persist();
    } catch (e) {
      Object.assign(this.config, previous);
      try {
        await this.configureSquid();
      } catch {
        this.fence();
      }
      throw e;
    } finally {
      this.changingPolicy = false;
    }
  }
  private configureSquid(): Promise<void> {
    const task = this.configQueue.then(async () => {
      const candidate = path.join(this.runDir, "squid-candidate.conf");
      const current = path.join(this.runDir, "squid.conf");
      await atomicWrite(
        candidate,
        squidConfig(this.config, this.store.state.sessions, this.runDir),
      );
      await command(
        "squid",
        ["-k", "parse", "-f", candidate],
        cleanEnvironment(),
      );
      this.healthy = false;
      await rename(candidate, current);
      if (this.squid) {
        await command(
          "squid",
          ["-k", "reconfigure", "-f", current],
          cleanEnvironment(),
        );
      } else {
        await rm(path.join(this.runDir, "squid.pid"), { force: true });
        this.squid = launch("squid", ["-N", "-f", current], cleanEnvironment());
        this.squid.stdout?.resume();
        this.squid.once("exit", () => {
          this.healthy = false;
          if (!this.stopping) {
            this.log("squid_failed");
            void this.close().finally(() => process.exit(1));
          }
        });
      }
      const ports = this.store.state.sessions
        .filter((s) => s.identity && s.status !== "removed")
        .map((s) => s.listener);
      if (!ports.length) ports.push(this.config.proxyPort);
      await Promise.all(
        ports.map(async (port) => {
          for (let attempt = 0; attempt < 50; attempt++) {
            const reachable = await new Promise<boolean>((resolve) => {
              const socket = createConnection({ host: "127.0.0.1", port });
              const finish = (ok: boolean) => {
                socket.destroy();
                resolve(ok);
              };
              socket.once("connect", () => finish(true));
              socket.once("error", () => finish(false));
              socket.setTimeout(200, () => finish(false));
            });
            if (reachable) return;
            if (this.squid?.exitCode !== null || this.stopping) break;
            await delay(100);
          }
          throw new HubError("SQUID_LISTENER_NOT_READY");
        }),
      );
      this.healthy = true;
    });
    this.configQueue = task.catch(() => {});
    return task;
  }
  private async runtime(s: Session): Promise<SessionRuntime> {
    let runtime = this.runtimes.get(s.id);
    if (!runtime) {
      const directory = path.join(this.runDir, `session-${s.id}`);
      await rm(directory, { recursive: true, force: true });
      runtime = new SessionRuntime(
        s,
        this.dataDir,
        directory,
        () => {
          if (!this.stopping) {
            s.status = "error";
            s.error = "SESSION_SERVICE_FAILED";
            void this.stopWorker(s)
              .then(() => this.store.save())
              .catch(() => {});
          }
        },
        this.config.allowedMicrosoftTenants,
        this.persistence,
      );
      this.runtimes.set(s.id, runtime);
      try {
        await runtime.open();
      } catch (e) {
        await runtime.close();
        this.runtimes.delete(s.id);
        throw e;
      }
    }
    return runtime;
  }
  private async provision(s: Session, runtime: SessionRuntime): Promise<void> {
    const name = s.tunnel_name;
    check(name, "TUNNEL_NAME_UNRESOLVED");
    const create = async () => {
      // create is explicit and named, never silently adopt another account's tunnel.
      // CLI create accepts a name, not a name.cluster for a deleted resource.
      const created = parseJson(
        await runtime.cli(["create", name, "--expiration", "2d", "--json"]),
      );
      const id = canonicalTunnel(created);
      check(id.split(".")[0] === s.tunnel_name, "TUNNEL_NAME_CHANGED");
      if (s.tunnel_id && id !== s.tunnel_id) {
        // Compensate only for the explicitly named resource just created by us.
        await runtime.cli(["delete", id, "--force", "--json"]);
        throw new HubError("TUNNEL_CLUSTER_CHANGED");
      }
      s.tunnel_id = id;
      await this.store.save();
    };
    if (!s.tunnel_id) await create();
    let tunnel;
    try {
      tunnel = await runtime.details();
    } catch (e) {
      // Only a confirmed missing resource allows recreation, with the SAME name
      // and cluster. 401/403, timeouts, conflicts and unknown responses never do.
      if (!(e instanceof HubError) || e.code !== "TUNNEL_NOT_FOUND") throw e;
      await create();
      tunnel = await runtime.details();
    }
    check(canonicalTunnel(tunnel) === s.tunnel_id, "TUNNEL_ID_CHANGED");
    check(
      tunnel.accessControl &&
        Array.isArray(tunnel.accessControl.entries) &&
        tunnel.accessControl.entries.length === 0,
      "TUNNEL_NOT_OWNER_ONLY",
    );
    check(Array.isArray(tunnel.ports), "TUNNEL_SCHEMA_UNSUPPORTED");
    const targets = [
      this.config.proxyPort,
      ...(this.config.socksEnabled ? [this.config.socksPort] : []),
    ];
    const known = new Set([
      s.proxy_port ?? DEFAULT_PROXY_PORT,
      ...targets,
      ...(s.socks_port === undefined ? [] : [s.socks_port]),
    ]);
    let current = tunnel.ports.map((p: any) => p?.portNumber) as number[];
    check(
      current.every((p) => known.has(p)),
      "TUNNEL_PORT_POLICY_CHANGED",
    );
    privateTunnel(tunnel, s, current); // Validate every ACL and duplicate before mutation.
    // Only recorded or desired ports are ever touched. Accept known subsets to
    // recover an interrupted multi-port migration, but verify each exact read-back.
    for (const port of current.filter((p) => !targets.includes(p))) {
      await runtime.cli([
        "port",
        "delete",
        s.tunnel_id,
        "--port-number",
        String(port),
        "--json",
      ]);
      current = current.filter((p) => p !== port);
      tunnel = await runtime.details();
      privateTunnel(tunnel, s, current);
    }
    for (const port of targets.filter((p) => !current.includes(p))) {
      await runtime.cli([
        "port",
        "create",
        s.tunnel_id,
        "--port-number",
        String(port),
        "--protocol",
        "auto",
        "--json",
      ]);
      current = [...current, port];
      tunnel = await runtime.details();
      privateTunnel(tunnel, s, current);
    }
    privateTunnel(tunnel, s, targets);
    // A crash after a remote change is recoverable: the next start accepts either
    // the last recorded port, the desired port, or an empty (partially migrated) tunnel.
    s.proxy_port = this.config.proxyPort;
    if (this.config.socksEnabled) s.socks_port = this.config.socksPort;
    else delete s.socks_port;
    await this.store.save();
  }
  private async start(s: Session): Promise<void> {
    check(
      this.config.allowedProviders.includes(s.provider),
      "PROVIDER_NOT_ALLOWED",
    );
    check(s.identity, "AUTH_REQUIRED");
    check(!this.workers.has(s.id), "SESSION_ALREADY_RUNNING");
    if (this.retries.has(s.id)) clearTimeout(this.retries.get(s.id));
    this.retries.delete(s.id);
    const runtime = await this.runtime(s);
    // Never mutate a remote tunnel until the cached identity is checked.
    const identity = await runtime.identity();
    check(
      identity.user_id === s.identity.user_id &&
        identity.provider === s.provider,
      "IDENTITY_CHANGED",
    );
    this.store.resolveName(s);
    if (this.config.socksEnabled) this.store.ensureSocksListener(s);
    await this.store.save();
    await this.provision(s, runtime);
    await runtime.credentials();
    await this.configureSquid();
    s.desired = true;
    s.status = "starting";
    delete s.error;
    await this.store.save();
    const worker = fork(new URL("./worker.js", import.meta.url), [], {
      env: runtime.env,
      stdio: ["ignore", "ignore", "ignore", "ipc"],
    });
    this.workers.set(s.id, worker);
    worker.once("error", () => {
      void this.fail(s, new HubError("WORKER_FAILED"));
    });
    worker.on("message", (message: any) => {
      if (message.type === "credentials") {
        this.track(this.credentialsForWorker(s, runtime, worker, message.id));
      } else if (message.type === "ready" || message.type === "status") {
        if (this.workers.get(s.id) !== worker) return;
        s.status =
          message.type === "ready" || message.status === "connected"
            ? "running"
            : "starting";
        if (s.status === "running") this.failures.delete(s.id);
        void this.store.save().catch(() => {
          void this.close();
        });
      }
    });
    worker.once("exit", (code) => {
      if (this.workers.get(s.id) !== worker) return;
      this.workers.delete(s.id);
      if (this.stopping || !s.desired) return;
      void this.fail(
        s,
        new HubError(code === 75 ? "AUTH_REQUIRED" : "WORKER_FAILED"),
      );
    });
    worker.send({
      type: "start",
      listener: s.listener,
      proxyPort: this.config.proxyPort,
      socks: this.config.socksEnabled
        ? { proxyPort: this.config.socksPort, listenerPort: s.socks_listener }
        : undefined,
      maintenanceSeconds: this.config.maintenanceSeconds,
    });
    this.log("session_starting", s);
  }
  private async credentialsForWorker(
    s: Session,
    runtime: SessionRuntime,
    worker: ChildProcess,
    id: number,
  ): Promise<void> {
    if (this.workers.get(s.id) !== worker) return;
    try {
      const tunnel = await runtime.credentials();
      if (this.workers.get(s.id) !== worker) return;
      // Resource lease maintenance is distinct from refreshing cached credentials.
      if (Date.now() - (this.maintenance.get(s.id) ?? 0) > 12 * 3600000) {
        await runtime.cli([
          "update",
          s.tunnel_id!,
          "--expiration",
          "2d",
          "--json",
        ]);
        this.maintenance.set(s.id, Date.now());
      }
      if (worker.connected)
        worker.send({ type: "credentials", id, tunnel }, () => {});
    } catch (e) {
      if (this.workers.get(s.id) !== worker) return;
      if (worker.connected)
        worker.send({ type: "credentials", id, error: errorCode(e) }, () => {});
      await this.fail(s, e);
    }
  }
  private async fail(s: Session, e: unknown): Promise<void> {
    if (this.stopping) return;
    const code = errorCode(e);
    await this.stopWorker(s);
    s.status = code === "AUTH_REQUIRED" ? "reauth_required" : "error";
    s.error = code;
    this.log("session_failed", s, code);
    await this.store.save();
    // Policy/identity errors fail closed and require intervention. Only transient
    // worker/command failures are retried, bounded and isolated to this session.
    if (
      s.desired &&
      [
        "WORKER_FAILED",
        "COMMAND_FAILED",
        "COMMAND_CANCELLED_OR_TIMEOUT",
        "TUNNEL_NOT_FOUND",
      ].includes(code)
    ) {
      const attempts = (this.failures.get(s.id) ?? 0) + 1;
      this.failures.set(s.id, attempts);
      if (attempts <= 8)
        this.retries.set(
          s.id,
          setTimeout(
            () => {
              this.track(
                this.exclusive(s.id, () => this.start(s)).catch((error) =>
                  this.fail(s, error),
                ),
              );
            },
            Math.min(300000, 1000 * 2 ** attempts),
          ),
        );
    }
  }
  private async stopWorker(s: Session): Promise<void> {
    clearTimeout(this.retries.get(s.id));
    this.retries.delete(s.id);
    const worker = this.workers.get(s.id);
    this.workers.delete(s.id);
    await terminate(worker);
  }
  async dispatch(
    args: string[],
    output: (chunk: string) => void,
  ): Promise<unknown> {
    if (args[0] === "admin") {
      check(!this.stopping && this.control, "WEB_DISABLED");
      check(args.length === 4 && args[1] === "reset-password", "USAGE");
      await this.control.resetPassword(args[2], args[3]);
      this.log("console_password_reset_cli");
      return {
        ok: true,
        passwordChangePending: true,
        passwordChangeRequired: this.web?.options.requirePasswordChange ?? true,
      };
    }
    check(args[0] === "session", "USAGE");
    const [, operation, id, ...flags] = args;
    if (operation === "list") {
      check(args.length === 2, "USAGE");
      return this.store.state.sessions.filter((s) => s.status !== "removed");
    }
    if (operation === "status") {
      check(args.length === 3, "USAGE");
      return this.store.get(id);
    }
    return this.exclusive(id, async () => {
      if (operation === "add") {
        check(flags.length % 2 === 0, "USAGE");
        const options = new Map<string, string>();
        for (let i = 0; i < flags.length; i += 2) {
          check(
            ["--provider", "--tunnel-name"].includes(flags[i]) &&
              !options.has(flags[i]),
            "USAGE",
          );
          options.set(flags[i], flags[i + 1]);
        }
        const s = this.store.add(
          id,
          (options.get("--provider") ?? "microsoft") as Provider,
          options.get("--tunnel-name"),
        );
        await this.store.save();
        return s;
      }
      check(
        flags.length === 0 &&
          ["login", "start", "stop", "logout", "remove"].includes(operation),
        "USAGE",
      );
      const s = this.store.get(id);
      try {
        if (operation === "start") {
          await this.start(s);
          return s;
        }
        if (operation === "stop") {
          s.desired = false;
          await this.stopWorker(s);
          s.status = s.identity ? "stopped" : "login_required";
        } else if (operation === "login") {
          s.desired = false;
          await this.stopWorker(s);
          const runtime = await this.runtime(s);
          await runtime.login(output);
          this.store.resolveName(s);
          s.status = "ready";
          delete s.error;
          await this.store.save();
          await this.configureSquid();
        } else if (operation === "logout" || operation === "remove") {
          s.desired = false;
          await this.stopWorker(s);
          const runtime = await this.runtime(s);
          await runtime.cli(["user", "logout"]);
          await runtime.close();
          this.runtimes.delete(s.id);
          s.status = operation === "remove" ? "removed" : "login_required";
          // Keep immutable identity and home. Logout clears CLI credentials; removal
          // tombstones the session. Purging backups/remote resources is admin work.
          await this.configureSquid();
        }
        await this.store.save();
        this.log(`session_${operation}`, s);
        return s;
      } catch (e) {
        await this.fail(s, e);
        throw e;
      }
    });
  }
  private accept(socket: Socket): void {
    this.clients.add(socket);
    socket.on("close", () => this.clients.delete(socket));
    socket.on("error", () => {});
    socket.setTimeout(650000, () => socket.destroy());
    let buffer = "";
    let received = false;
    socket.on("data", (chunk) => {
      if (received) return;
      buffer += chunk;
      if (buffer.length > 8192) {
        socket.destroy();
        return;
      }
      if (!buffer.includes("\n")) {
        return;
      }
      received = true;
      this.track(
        (async () => {
          try {
            const args = JSON.parse(buffer.split("\n")[0]);
            check(
              Array.isArray(args) && args.every((x) => typeof x === "string"),
              "INVALID_REQUEST",
            );
            const result = await this.dispatch(args, (chunk) => {
              // Device code is delivered ONLY to the connected administrator socket.
              if (!socket.destroyed)
                socket.write(JSON.stringify({ output: chunk }) + "\n");
            });
            socket.end(JSON.stringify({ result }) + "\n");
          } catch (e) {
            socket.end(JSON.stringify({ error: errorCode(e) }) + "\n");
          }
        })(),
      );
    });
  }
  async close(): Promise<void> {
    if (this.stopping) {
      return;
    }
    this.stopping = true;
    this.healthy = false;
    this.web?.close();
    this.socket?.close();
    this.health?.close();
    for (const client of this.clients) client.destroy();
    for (const timer of this.retries.values()) clearTimeout(timer);
    for (const runtime of this.runtimes.values()) runtime.abort.abort();
    await Promise.all([...this.workers.values()].map((w) => terminate(w)));
    this.workers.clear();
    await Promise.allSettled(this.operations);
    await Promise.all([...this.runtimes.values()].map((r) => r.close()));
    this.runtimes.clear();
    await terminate(this.squid);
    if (this.auditFd !== undefined) writeSync(this.auditFd, "\n");
    this.auditStream?.destroy();
    if (this.store.state) {
      await this.store.save();
    }
    this.log("manager_stopped");
  }
  fence(): void {
    this.healthy = false;
    this.web?.close();
    // A lost database session must not leave an old relay host serving traffic.
    for (const runtime of this.runtimes.values()) runtime.abort.abort();
    for (const worker of this.workers.values()) worker.kill("SIGKILL");
    this.squid?.kill("SIGKILL");
  }
}

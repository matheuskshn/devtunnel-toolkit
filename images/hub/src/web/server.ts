import {
  createServer,
  type IncomingMessage,
  type ServerResponse,
  type Server,
} from "node:http";
import { readFile } from "node:fs/promises";
import { createHash, randomUUID } from "node:crypto";
import {
  check,
  DEFAULT_PROXY_PORT,
  errorCode,
  type Config,
  type Session,
  validId,
  validTunnelName,
} from "../model.js";
import {
  ControlStore,
  publicUser,
  publicProvider,
  visible,
  roles,
  usernameValid,
  hashPassword,
  verifyPassword,
  validateProvider,
  type User,
  type AuthProvider,
  type ControlData,
} from "./control.js";
import {
  Sessions,
  RateLimit,
  body,
  fields,
  cookie,
  setCookie,
  randomToken,
  securityHeaders,
  type WebOptions,
} from "./security.js";
import {
  beginLogin,
  finishLogin,
  ldapLogin,
  type LoginFlow,
} from "./providers.js";

export interface ConsoleHost {
  config: Config;
  sessions(): Session[];
  ready(): boolean;
  dispatch(args: string[], output: (chunk: string) => void): Promise<unknown>;
  policy(value: Partial<Config>, persist: () => Promise<void>): Promise<void>;
  track<T>(operation: Promise<T>): Promise<T>;
}
interface Job {
  id: string;
  owner: string;
  action: string;
  session: string;
  status: "running" | "done" | "failed";
  createdAt: string;
  finishedAt?: string;
  error?: string;
  device?: { url: string; code: string };
}
interface EventClient {
  req: IncomingMessage;
  res: ServerResponse;
  user: string;
  fingerprint: { overview: string; logs: string; catalog: number };
  heartbeat: number;
  sequence: number;
}
const policyKeys = [
  "proxyPort",
  "socksEnabled",
  "socksPort",
  "allowedDomains",
  "allowAllDomains",
  "allowedPorts",
  "connectPorts",
  "allowedProviders",
  "allowedMicrosoftTenants",
  "tunnelNameTemplate",
  "maxSessions",
  "maintenanceSeconds",
];
const providerKeys = [
  "id",
  "kind",
  "label",
  "enabled",
  "registration",
  "issuer",
  "clientId",
  "clientSecret",
  "url",
  "baseDN",
  "bindDN",
  "bindPassword",
  "loginAttribute",
  "idAttribute",
  "ca",
];
const json = (res: ServerResponse, value: unknown, status = 200) => {
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8" });
  res.end(JSON.stringify(value));
};
const string = (value: unknown, max = 120): string => {
  check(
    typeof value === "string" && value.length > 0 && value.length <= max,
    "INVALID_FIELD",
  );
  return value;
};
const revision = (value: unknown): number => {
  check(Number.isSafeInteger(value) && Number(value) >= 0, "REVISION_REQUIRED");
  return Number(value);
};
const errorStatuses = new Map<string, number>([
  ["LOGIN_REQUIRED", 401],
  ["FORBIDDEN", 403],
  ["CSRF_REJECTED", 403],
  ["NOT_FOUND", 404],
  ["RATE_LIMITED", 429],
  ["AUTH_BUSY", 429],
  ["CONFIG_CONFLICT", 409],
  ["SESSION_BUSY", 409],
  ["STOP_SESSIONS_FIRST", 409],
  ["INTERNAL_ERROR", 500],
]);
export class WebConsole {
  private server?: Server;
  readonly browsers: Sessions;
  private limits = new RateLimit();
  private authenticating = 0;
  private flows = new Map<string, LoginFlow>();
  private jobs = new Map<string, Job>();
  private records: Record<string, unknown>[] = [];
  private sequence = 0;
  private closed = false;
  private streams = new Set<EventClient>();
  private streamTimer?: NodeJS.Timeout;
  private streamEpoch = randomUUID();
  constructor(
    readonly host: ConsoleHost,
    readonly control: ControlStore,
    readonly options: WebOptions,
    readonly assets: URL = new URL("../../web/", import.meta.url),
  ) {
    this.browsers = new Sessions(control, options.secure);
  }
  record(value: Record<string, unknown>) {
    this.records.push({ ...value, seq: ++this.sequence });
    if (this.records.length > 1000) {
      this.records.shift();
    }
  }
  private audit(
    event: string,
    user: User,
    detail: Record<string, unknown> = {},
  ) {
    const record = {
      time: new Date().toISOString(),
      event,
      actor_id: user.id,
      actor: user.username,
      ...detail,
    };
    this.record(record);
    process.stdout.write(JSON.stringify(record) + "\n");
  }
  async open() {
    this.server = createServer((req, res) => {
      void this.host.track(this.request(req, res)).catch((error) => {
        if (res.headersSent) {
          res.destroy();
          return;
        }
        const code = errorCode(error);
        json(res, { error: code }, errorStatuses.get(code) ?? 400);
      });
    });
    this.server.requestTimeout = 15000;
    this.server.headersTimeout = 10000;
    this.server.keepAliveTimeout = 5000;
    this.server.maxHeadersCount = 64;
    await new Promise<void>((resolve, reject) => {
      this.server!.once("error", reject);
      this.server!.listen(this.options.port, "0.0.0.0", resolve);
    });
  }
  close() {
    this.closed = true;
    clearInterval(this.streamTimer);
    for (const client of this.streams) {
      client.res.destroy();
    }
    this.streams.clear();
    this.server?.close();
    this.server?.closeAllConnections();
    this.browsers.clear();
    this.flows.clear();
  }
  private overview(user: User) {
    return {
      hubId: this.host.config.hubId,
      proxyPort: this.host.config.proxyPort,
      socksEnabled: this.host.config.socksEnabled,
      socksPort: this.host.config.socksPort,
      ready: this.host.ready(),
      sessions: this.host
        .sessions()
        .filter((s) => s.status !== "removed" && visible(user, s.id))
        .map((s) => ({
          ...s,
          proxy_port: s.tunnel_id
            ? (s.proxy_port ?? DEFAULT_PROXY_PORT)
            : this.host.config.proxyPort,
        })),
      jobs: [...this.jobs.values()]
        .filter((j) => j.owner === user.id || user.role === "admin")
        .map((j) => this.safeJob(j, user)),
      revision: this.control.data.revision,
      providers: this.host.config.allowedProviders,
    };
  }
  private visibleRecord(user: User, r: Record<string, unknown>) {
    return (
      user.role === "admin" ||
      r.actor_id === user.id ||
      (typeof r.session_id === "string" && visible(user, r.session_id))
    );
  }
  private fingerprint(user: User): EventClient["fingerprint"] {
    // Only compare the same per-user projections that the authorized HTTP APIs expose.
    // Digests, session identities, device codes and log contents never enter the stream.
    const digest = (value: unknown) =>
      createHash("sha256").update(JSON.stringify(value)).digest("hex");
    return {
      overview: digest(this.overview(user)),
      logs: digest(
        this.records
          .filter((r) => this.visibleRecord(user, r))
          .map((r) => r.seq),
      ),
      catalog: this.control.data.revision,
    };
  }
  private sendEvent(
    client: EventClient,
    event: "sync" | "change",
    topics: string[],
    reset = false,
  ) {
    // Bound Node's write buffer: a slow client reconnects and resnapshots instead of
    // accumulating an unbounded replay queue or blocking the manager.
    if (client.res.writableLength > 16384) {
      client.res.destroy();
      return;
    }
    client.res.write(
      `id: ${this.streamEpoch}:${++client.sequence}\nevent: ${event}\ndata: ${JSON.stringify({ reset, topics })}\n\n`,
    );
  }
  private checkStreams() {
    const fingerprints = new Map<string, EventClient["fingerprint"]>();
    for (const client of this.streams) {
      try {
        const { user } = this.browsers.get(client.req, false);
        check(
          !this.closed &&
            (!this.options.requirePasswordChange || !user.mustChange),
          "LOGIN_REQUIRED",
        );
        let fingerprint = fingerprints.get(user.id);
        if (!fingerprint) {
          fingerprint = this.fingerprint(user);
          fingerprints.set(user.id, fingerprint);
        }
        const topics = (["overview", "logs", "catalog"] as const).filter(
          (key) => fingerprint[key] !== client.fingerprint[key],
        );
        if (topics.length) {
          client.fingerprint = fingerprint;
          this.sendEvent(client, "change", topics);
        }
        this.heartbeat(client);
      } catch {
        // No further state is sent after revocation, expiry, fencing or shutdown.
        client.res.end("event: auth\ndata: {}\n\n");
        this.streams.delete(client);
      }
    }
    if (!this.streams.size) {
      clearInterval(this.streamTimer);
      this.streamTimer = undefined;
    }
  }
  private heartbeat(client: EventClient): void {
    if (Date.now() - client.heartbeat < 15000) {
      return;
    }
    if (client.res.writableLength > 16384) {
      client.res.destroy();
      return;
    }
    client.res.write("event: ping\ndata: {}\n\n");
    client.heartbeat = Date.now();
  }
  private events(
    req: IncomingMessage,
    res: ServerResponse,
    user: User,
    url: URL,
  ) {
    check(!url.search, "INVALID_URL");
    check(
      !req.headers.origin || req.headers.origin === this.options.origin,
      "CSRF_REJECTED",
    );
    check(
      !req.headers["sec-fetch-site"] ||
        req.headers["sec-fetch-site"] === "same-origin",
      "CSRF_REJECTED",
    );
    check(
      req.headers.accept
        ?.split(",")
        .some((v) => v.trim() === "text/event-stream"),
      "INVALID_ACCEPT",
    );
    this.limits.take(`events:${user.id}`, 30);
    check(
      this.streams.size < 128 &&
        [...this.streams].filter((s) => s.user === user.id).length < 4,
      "RATE_LIMITED",
    );
    const client: EventClient = {
      req,
      res,
      user: user.id,
      fingerprint: this.fingerprint(user),
      heartbeat: Date.now(),
      sequence: 0,
    };
    res.writeHead(200, {
      "Content-Type": "text/event-stream; charset=utf-8",
      "Cache-Control": "no-store, no-transform",
      "X-Accel-Buffering": "no",
      Connection: "keep-alive",
    });
    res.flushHeaders();
    res.write("retry: 5000\n\n");
    this.streams.add(client);
    // Last-Event-ID is deliberately not replayed: every reconnect gets an authorized
    // full resnapshot, including a reset cursor after manager replacement.
    this.sendEvent(client, "sync", ["overview", "logs", "catalog"], true);
    res.once("close", () => {
      this.streams.delete(client);
      if (!this.streams.size) {
        clearInterval(this.streamTimer);
        this.streamTimer = undefined;
      }
    });
    if (!this.streamTimer) {
      this.streamTimer = setInterval(() => this.checkStreams(), 1000).unref();
    }
  }
  private async authenticate<T>(
    req: IncomingMessage,
    action: () => Promise<T>,
  ): Promise<T> {
    // Deliberately do not trust forwarded headers. Shared reverse proxies share this budget.
    this.limits.take(`auth:${req.socket.remoteAddress}`, 30);
    check(this.authenticating < 2, "AUTH_BUSY");
    this.authenticating++;
    try {
      return await action();
    } finally {
      this.authenticating--;
    }
  }
  private admin(user: User) {
    check(user.role === "admin", "FORBIDDEN");
  }
  private update(
    req: IncomingMessage,
    user: User,
    edit: (draft: ControlData) => void,
    expected?: number,
  ) {
    return this.control.update((d) => {
      check(!this.closed, "MANAGER_STOPPING");
      this.browsers.get(req);
      const current = d.users.find((u) => u.id === user.id);
      check(
        current && !current.disabled && current.epoch === user.epoch,
        "LOGIN_REQUIRED",
      );
      edit(d);
    }, expected);
  }

  private requestUrl(req: IncomingMessage, res: ServerResponse): URL {
    securityHeaders(res, this.options.secure);
    check(!this.closed, "MANAGER_STOPPING");
    check(
      req.headers.host === new URL(this.options.origin).host,
      "INVALID_HOST",
    );
    check(
      req.url &&
        req.url.length <= 8192 &&
        req.url.startsWith("/") &&
        !req.url.startsWith("//"),
      "INVALID_URL",
    );
    check(req.method === "GET" || req.method === "POST", "METHOD_NOT_ALLOWED");
    if (req.method === "POST") {
      check(req.headers.origin === this.options.origin, "CSRF_REJECTED");
    }
    return new URL(req.url, this.options.origin);
  }
  private authorizedBody(
    req: IncomingMessage,
    url: URL,
    user: User,
    csrf: string,
  ): Promise<Record<string, unknown>> | undefined {
    check(req.headers["x-csrf-token"] === csrf, "CSRF_REJECTED");
    this.limits.take(`write:${user.id}`, 60);
    if (url.pathname === "/api/auth/logout") {
      return undefined;
    }
    return body(req);
  }
  private async request(
    req: IncomingMessage,
    res: ServerResponse,
  ): Promise<void> {
    const url = this.requestUrl(req, res);
    const publicTask = this.publicRequest(req, res, url);
    if (publicTask) {
      await publicTask;
      return;
    }
    const passive =
      req.method === "GET" &&
      (url.pathname === "/api/events" ||
        req.headers["x-hub-background"] === "true");
    const { session, user } = this.browsers.get(req, !passive);
    // GET projections stay synchronous after authentication. Do not introduce an
    // await that would let revocation interleave with a captured user's grants.
    const pendingInput =
      req.method === "POST"
        ? this.authorizedBody(req, url, user, session.csrf)
        : undefined;
    let input: Record<string, unknown> = {};
    if (pendingInput) {
      input = await pendingInput;
      // A slow body must not retain authority revoked while bytes arrived.
      this.browsers.get(req);
      check(!this.closed, "MANAGER_STOPPING");
    }
    const accountTask = this.accountRequest(
      req,
      res,
      url,
      user,
      input,
      session.csrf,
    );
    if (accountTask) {
      await accountTask;
      return;
    }
    check(
      !this.options.requirePasswordChange || !user.mustChange,
      "PASSWORD_CHANGE_REQUIRED",
    );
    await this.applicationRequest(req, res, url, user, input);
  }

  private publicRequest(
    req: IncomingMessage,
    res: ServerResponse,
    url: URL,
  ): Promise<void> | undefined {
    const route = url.pathname;
    if (
      req.method === "GET" &&
      ["/", "/app.js", "/app.css", "/theme.js"].includes(route)
    ) {
      return this.serveAsset(res, route);
    }
    switch (req.method + ":" + route) {
      case "GET:/favicon.ico":
        return this.serveFavicon(res);
      case "GET:/api/auth/options":
        return this.authOptions(res);
      case "POST:/api/auth/login":
        return this.authLogin(req, res);
      case "POST:/api/auth/begin":
        return this.authBegin(req, res);
      case "GET:/auth/callback":
        return this.authCallback(req, res, url);
      default:
        return undefined;
    }
  }
  private accountRequest(
    req: IncomingMessage,
    res: ServerResponse,
    url: URL,
    user: User,
    input: Record<string, unknown>,
    csrf: string,
  ): Promise<void> | undefined {
    switch (req.method + ":" + url.pathname) {
      case "GET:/api/me":
        return this.currentUser(res, user, csrf);
      case "POST:/api/auth/logout":
        return this.authLogout(req, res);
      case "POST:/api/me/password":
        return this.changePassword(req, res, user, input);
      default:
        return undefined;
    }
  }
  private applicationRequest(
    req: IncomingMessage,
    res: ServerResponse,
    url: URL,
    user: User,
    input: Record<string, unknown>,
  ): Promise<void> {
    switch (req.method + ":" + url.pathname) {
      case "GET:/api/events":
        return this.streamEvents(req, res, user, url);
      case "GET:/api/overview":
        return this.serveOverview(res, user);
      case "GET:/api/logs":
        return this.serveLogs(res, user, url);
      case "POST:/api/jobs":
        return this.createJob(res, user, input);
      case "GET:/api/users":
        return this.listUsers(res, user);
      case "POST:/api/users":
        return this.updateUser(req, res, user, input);
      case "GET:/api/providers":
        return this.listProviders(res, user);
      case "POST:/api/providers":
        return this.updateProvider(req, res, user, input);
      case "GET:/api/config":
        return this.configuration(res, user);
      case "POST:/api/config":
        return this.updatePolicy(req, res, user, input);
      default:
        check(false, "NOT_FOUND");
    }
  }
  private async serveFavicon(res: ServerResponse): Promise<void> {
    res.writeHead(204);
    res.end();
  }
  private async serveAsset(res: ServerResponse, route: string): Promise<void> {
    const file = route === "/" ? "index.html" : route.slice(1);
    const data = await readFile(new URL(file, this.assets));
    res.writeHead(200, {
      "Content-Type": file.endsWith(".html")
        ? "text/html; charset=utf-8"
        : file.endsWith(".css")
          ? "text/css; charset=utf-8"
          : "text/javascript; charset=utf-8",
    });
    res.end(data);
  }
  private async authOptions(res: ServerResponse): Promise<void> {
    json(res, {
      providers: this.control.data.providers
        .filter((p) => p.enabled)
        .map((p) => ({ id: p.id, kind: p.kind, label: p.label })),
    });
  }
  private async authLogin(
    req: IncomingMessage,
    res: ServerResponse,
  ): Promise<void> {
    const input = await body(req);
    fields(input, ["username", "password", "provider"]);
    const username = string(input.username, 256),
      password = string(input.password, 1024);
    await this.authenticate(req, async () => {
      this.limits.take(`account:${username.toLowerCase()}`, 8);
      let user: User;
      if (input.provider) {
        const p = this.control.data.providers.find(
          (p) => p.id === input.provider,
        );
        check(p?.enabled && p.kind === "ldap", "LOGIN_FAILED");
        const epoch = p.epoch;
        let identity;
        try {
          identity = await ldapLogin(p, username, password);
        } catch {
          check(false, "LOGIN_FAILED");
        }
        check(
          this.control.data.providers.some(
            (x) => x.id === p.id && x.epoch === epoch && x.enabled,
          ),
          "LOGIN_FAILED",
        );
        user = await this.control.external(p, identity.subject, identity.name);
      } else {
        const candidate = this.control.data.users.find(
          (u) => u.local && u.username === username.toLowerCase(),
        );
        const ok = await verifyPassword(password, candidate?.password);
        check(ok && candidate && !candidate.disabled, "LOGIN_FAILED");
        user = this.control.user(candidate.id);
        check(user.epoch === candidate.epoch, "LOGIN_FAILED");
      }
      this.browsers.remove(req, res);
      this.browsers.create(res, user, input.provider as string | undefined);
      this.audit("console_login", user);
      json(res, { ok: true });
    });
  }
  private async authBegin(
    req: IncomingMessage,
    res: ServerResponse,
  ): Promise<void> {
    const input = await body(req);
    fields(input, ["provider"]);
    await this.authenticate(req, async () => {
      const p = this.control.data.providers.find(
        (p) => p.id === input.provider,
      );
      check(p?.enabled && p.kind !== "ldap", "LOGIN_FAILED");
      for (const [k, flow] of this.flows) {
        if (flow.expires < Date.now()) {
          this.flows.delete(k);
        }
      }
      check(this.flows.size < 100, "RATE_LIMITED");
      const result = await beginLogin(
          p,
          `${this.options.origin}/auth/callback`,
        ),
        key = randomToken();
      const old = cookie(req, this.flowCookie);
      if (old) {
        this.flows.delete(old);
      }
      this.flows.set(key, result.flow);
      setCookie(res, this.flowCookie, key, this.options.secure, 600);
      json(res, { url: result.url });
    });
  }
  private async authCallback(
    req: IncomingMessage,
    res: ServerResponse,
    url: URL,
  ): Promise<void> {
    try {
      await this.authenticate(req, async () => {
        const key = cookie(req, this.flowCookie),
          flow = key ? this.flows.get(key) : undefined;
        if (key) {
          this.flows.delete(key);
        }
        setCookie(res, this.flowCookie, "", this.options.secure, 0);
        check(flow, "LOGIN_EXPIRED");
        const p = this.control.data.providers.find(
          (p) => p.id === flow.provider,
        );
        check(p?.enabled, "LOGIN_FAILED");
        const identity = await finishLogin(p, flow, url);
        check(
          this.control.data.providers.some(
            (x) => x.id === p.id && x.epoch === flow.epoch && x.enabled,
          ),
          "LOGIN_FAILED",
        );
        const user = await this.control.external(
          p,
          identity.subject,
          identity.name,
        );
        this.browsers.remove(req, res);
        this.browsers.create(res, user, p.id);
        this.audit("console_login", user);
      });
      res.writeHead(303, { Location: "/" });
      res.end();
    } catch (e) {
      const code = errorCode(e);
      res.writeHead(303, {
        Location: `/?login=${code === "ACCOUNT_PENDING_APPROVAL" ? code : "LOGIN_FAILED"}`,
      });
      res.end();
    }
  }
  private async currentUser(
    res: ServerResponse,
    user: User,
    csrf: string,
  ): Promise<void> {
    json(res, {
      user: publicUser(user),
      csrf: csrf,
      requirePasswordChange: this.options.requirePasswordChange,
    });
  }
  private async authLogout(
    req: IncomingMessage,
    res: ServerResponse,
  ): Promise<void> {
    this.browsers.remove(req, res);
    json(res, { ok: true });
  }
  private async changePassword(
    req: IncomingMessage,
    res: ServerResponse,
    user: User,
    input: Record<string, unknown>,
  ): Promise<void> {
    fields(input, ["current", "password"]);
    check(user.local, "LOCAL_USER_REQUIRED");
    await this.authenticate(req, async () => {
      check(await verifyPassword(input.current, user.password), "LOGIN_FAILED");
      check(input.current !== input.password, "PASSWORD_REUSE");
      const hash = await hashPassword(string(input.password, 1024));
      await this.update(req, user, (d) => {
        const u = d.users.find((u) => u.id === user.id)!;
        u.password = hash;
        u.mustChange = false;
        u.epoch++;
      });
      this.browsers.remove(req, res);
      this.browsers.create(res, this.control.user(user.id));
      this.audit("console_password_changed", user);
      json(res, { ok: true });
    });
  }
  private async streamEvents(
    req: IncomingMessage,
    res: ServerResponse,
    user: User,
    url: URL,
  ): Promise<void> {
    this.events(req, res, user, url);
  }
  private async serveOverview(res: ServerResponse, user: User): Promise<void> {
    json(res, this.overview(user));
  }
  private async serveLogs(
    res: ServerResponse,
    user: User,
    url: URL,
  ): Promise<void> {
    const after = Number(url.searchParams.get("after") ?? 0);
    check(Number.isSafeInteger(after) && after >= 0, "INVALID_CURSOR");
    json(res, {
      cursor: this.sequence,
      records: this.records
        .filter((r) => Number(r.seq) > after && this.visibleRecord(user, r))
        .slice(-300),
    });
  }
  private async createJob(
    res: ServerResponse,
    user: User,
    input: Record<string, unknown>,
  ): Promise<void> {
    fields(input, ["action", "session", "provider", "tunnelName"]);
    check(user.role !== "viewer", "FORBIDDEN");
    const action = string(input.action),
      id = string(input.session, 32);
    check(validId(id), "INVALID_SESSION_ID");
    check(
      ["add", "login", "start", "stop", "logout", "remove"].includes(action),
      "INVALID_ACTION",
    );
    if (action === "add" || action === "remove") {
      this.admin(user);
    } else {
      check(visible(user, id), "FORBIDDEN");
    }
    const args = ["session", action, id];
    if (action === "add") {
      check(
        this.host.config.allowedProviders.includes(input.provider as never),
        "INVALID_PROVIDER",
      );
      args.push("--provider", String(input.provider));
      if (input.tunnelName) {
        check(validTunnelName(input.tunnelName), "INVALID_TUNNEL_NAME");
        args.push("--tunnel-name", input.tunnelName);
      }
    } else {
      check(
        input.provider === undefined && input.tunnelName === undefined,
        "UNKNOWN_FIELD",
      );
      this.host.sessions().find((s) => s.id === id && s.status !== "removed") ||
        check(false, "NOT_FOUND");
    }
    for (const [k, j] of this.jobs) {
      if (j.finishedAt && Date.now() - Date.parse(j.finishedAt) > 600000) {
        this.jobs.delete(k);
      }
    }
    check(
      this.jobs.size < 100 &&
        [...this.jobs.values()].filter((j) => j.status === "running").length <
          8,
      "RATE_LIMITED",
    );
    const job: Job = {
      id: randomUUID(),
      owner: user.id,
      action,
      session: id,
      status: "running",
      createdAt: new Date().toISOString(),
    };
    this.jobs.set(job.id, job);
    this.audit("console_action_started", user, { action, session_id: id });
    let output = "";
    const task = this.host
      .dispatch(args, (chunk) => {
        // The raw CLI stream is never returned to the browser or global logs.
        output = (output + chunk).slice(-8192);
        const match = output.match(
          /https:\/\/(?:github\.com\/login\/device|(?:login\.microsoftonline\.com|login\.microsoft\.com|microsoft\.com)\/device(?:login)?)/i,
        );
        const code = output.match(
          /\b(?:code|código)\s+([A-Z0-9]{4}-[A-Z0-9]{4}|[A-Z0-9]{8,12})\b/i,
        );
        if (match && code) {
          job.device = { url: match[0], code: code[1] };
        }
      })
      .then(
        () => {
          job.status = "done";
        },
        (e) => {
          job.status = "failed";
          job.error = errorCode(e);
        },
      )
      .finally(() => {
        output = "";
        delete job.device;
        job.finishedAt = new Date().toISOString();
        this.audit("console_action_finished", user, {
          action,
          session_id: id,
          status: job.status,
          code: job.error,
        });
      });
    this.host.track(task);
    json(res, { job: this.safeJob(job, user) }, 202);
  }
  private async listUsers(res: ServerResponse, user: User): Promise<void> {
    this.admin(user);
    json(res, {
      users: this.control.data.users.map(publicUser),
      revision: this.control.data.revision,
    });
  }
  private async updateUser(
    req: IncomingMessage,
    res: ServerResponse,
    user: User,
    input: Record<string, unknown>,
  ): Promise<void> {
    this.admin(user);
    fields(input, [
      "id",
      "username",
      "name",
      "role",
      "disabled",
      "sessions",
      "password",
      "revision",
    ]);
    check(
      usernameValid(input.username) &&
        roles.includes(input.role as never) &&
        typeof input.disabled === "boolean",
      "INVALID_USER",
    );
    const name = string(input.name);
    check(
      Array.isArray(input.sessions) &&
        input.sessions.length <= 500 &&
        input.sessions.every(
          (id) =>
            typeof id === "string" &&
            this.host
              .sessions()
              .some((s) => s.id === id && s.status !== "removed"),
        ),
      "INVALID_SESSION_GRANTS",
    );
    const hash = input.password
      ? await this.authenticate(req, () =>
          hashPassword(string(input.password, 1024)),
        )
      : undefined;
    await this.update(
      req,
      user,
      (d) => {
        let target = d.users.find((u) => u.id === input.id);
        check(!input.id || target, "NOT_FOUND");
        check(
          !d.users.some(
            (u) => u.username === input.username && u.id !== target?.id,
          ),
          "USERNAME_EXISTS",
        );
        if (!target) {
          check(hash, "PASSWORD_REQUIRED");
          target = {
            id: randomUUID(),
            username: String(input.username),
            name,
            role: "viewer",
            disabled: false,
            local: true,
            mustChange: true,
            epoch: 0,
            sessions: [],
            identities: [],
            createdAt: new Date().toISOString(),
          };
          d.users.push(target);
        }
        if (target.id === "admin") {
          check(
            input.username === "admin" &&
              input.role === "admin" &&
              !input.disabled,
            "RECOVERY_ADMIN_PROTECTED",
          );
        }
        if (target.id === user.id) {
          check(
            input.role === "admin" && !input.disabled,
            "SELF_LOCKOUT_PREVENTED",
          );
        }
        Object.assign(target, {
          username: input.username,
          name,
          role: input.role,
          disabled: input.disabled,
          sessions: [...new Set(input.sessions as string[])],
          epoch: target.epoch + 1,
        });
        if (hash) {
          check(target.local, "LOCAL_USER_REQUIRED");
          target.password = hash;
          target.mustChange = true;
        }
      },
      revision(input.revision),
    );
    this.audit("console_user_updated", user, {
      target_id: input.id ?? "new",
    });
    json(res, { ok: true });
  }
  private async listProviders(res: ServerResponse, user: User): Promise<void> {
    this.admin(user);
    json(res, {
      providers: this.control.data.providers.map(publicProvider),
      revision: this.control.data.revision,
      callback: `${this.options.origin}/auth/callback`,
    });
  }
  private async updateProvider(
    req: IncomingMessage,
    res: ServerResponse,
    user: User,
    input: Record<string, unknown>,
  ): Promise<void> {
    this.admin(user);
    fields(input, [...providerKeys, "revision"]);
    await this.update(
      req,
      user,
      (d) => {
        const old = d.providers.find((p) => p.id === input.id);
        const candidate = {
          ...old,
          ...Object.fromEntries(
            providerKeys
              .filter((k) => input[k] !== undefined)
              .map((k) => [k, input[k]]),
          ),
          epoch: (old?.epoch ?? 0) + 1,
        } as AuthProvider;
        if (!input.clientSecret && old) {
          candidate.clientSecret = old.clientSecret;
        }
        if (!input.bindPassword && old) {
          candidate.bindPassword = old.bindPassword;
        }
        validateProvider(candidate);
        if (old) {
          // Bindings must never move to a different identity namespace, even if disabled.
          for (const k of [
            "kind",
            "issuer",
            "clientId",
            "url",
            "baseDN",
            "idAttribute",
            "loginAttribute",
          ] as const) {
            check(old[k] === candidate[k], "PROVIDER_IDENTITY_IMMUTABLE");
          }
          d.providers[d.providers.indexOf(old)] = candidate;
        } else {
          d.providers.push(candidate);
        }
      },
      revision(input.revision),
    );
    this.audit("console_provider_updated", user, { provider_id: input.id });
    json(res, { ok: true });
  }
  private async configuration(res: ServerResponse, user: User): Promise<void> {
    this.admin(user);
    json(res, {
      config: this.host.config,
      policyKeys,
      revision: this.control.data.revision,
      override: !!this.control.data.policy,
    });
  }
  private async updatePolicy(
    req: IncomingMessage,
    res: ServerResponse,
    user: User,
    input: Record<string, unknown>,
  ): Promise<void> {
    this.admin(user);
    fields(input, ["policy", "revision"]);
    check(
      input.policy &&
        typeof input.policy === "object" &&
        !Array.isArray(input.policy),
      "INVALID_CONFIG",
    );
    fields(input.policy as Record<string, unknown>, policyKeys);
    const policy = input.policy as Partial<Config>;
    const expected = revision(input.revision);
    await this.host.policy(policy, () =>
      this.update(
        req,
        user,
        (d) => {
          d.policy = { ...d.policy, ...policy };
        },
        expected,
      ),
    );
    this.audit("console_policy_updated", user);
    json(res, { ok: true });
  }
  private get flowCookie() {
    return this.options.secure ? "__Host-hub-flow" : "hub-local-flow";
  }
  private safeJob(job: Job, user: User) {
    const { device, ...safe } = job;
    return {
      ...safe,
      ...(user.id === job.owner && visible(user, job.session)
        ? { device }
        : {}),
    };
  }
}

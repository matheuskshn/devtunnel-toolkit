import { open, readFile, rename, mkdir, lstat } from "node:fs/promises";
import path from "node:path";
import { randomUUID } from "node:crypto";
import {
  check,
  validId,
  validTunnelName,
  validCanonicalId,
  validNameTemplate,
  resolveTunnelName,
  type Config,
  type State,
  type Session,
  type Provider,
} from "./model.js";

export async function privateDirectory(dir: string): Promise<void> {
  await mkdir(dir, { recursive: true, mode: 0o700 });
  const st = await lstat(dir);
  check(st.isDirectory() && !st.isSymbolicLink(), "UNSAFE_DATA_DIRECTORY");
}
export async function atomicWrite(
  file: string,
  contents: string,
): Promise<void> {
  const temporary = `${file}.${randomUUID()}.tmp`;
  const handle = await open(temporary, "wx", 0o600);
  try {
    await handle.writeFile(contents);
    await handle.sync();
  } finally {
    await handle.close();
  }
  await rename(temporary, file);
  const directory = await open(path.dirname(file), "r");
  try {
    await directory.sync();
  } finally {
    await directory.close();
  }
}
export interface Persistence {
  read(): Promise<State | undefined>;
  write(state: State): Promise<void>;
  restore(id: string, home: string): Promise<void>;
  beginAuth(id: string): Promise<void>;
  checkpoint(id: string, home: string): Promise<void>;
}

function validateSessionName(session: Session): void {
  const named = validTunnelName(session.tunnel_name);
  check(
    named
      ? session.tunnel_name_template === undefined
      : session.tunnel_name === undefined &&
          validNameTemplate(session.tunnel_name_template) &&
          session.tunnel_id === undefined &&
          !session.desired &&
          [
            "login_required",
            "error",
            "reauth_required",
            "stopped",
            "removed",
          ].includes(session.status),
    "INVALID_STATE",
  );
  check(
    session.tunnel_id === undefined ||
      (validCanonicalId(session.tunnel_id) &&
        session.tunnel_id.split(".")[0] === session.tunnel_name),
    "INVALID_STATE",
  );
}
function validateSessionPorts(session: Session): void {
  check(
    session.proxy_port === undefined ||
      (Number.isInteger(session.proxy_port) &&
        session.proxy_port >= 1024 &&
        session.proxy_port <= 65535 &&
        session.proxy_port !== session.listener),
    "INVALID_STATE_PROXY_PORT",
  );
  check(
    session.socks_port === undefined ||
      (Number.isInteger(session.socks_port) &&
        session.socks_port >= 1024 &&
        session.socks_port <= 65535 &&
        session.socks_port !== session.proxy_port &&
        session.socks_port !== session.listener &&
        session.socks_port !== session.socks_listener &&
        session.socks_listener !== undefined &&
        session.proxy_port !== undefined),
    "INVALID_STATE_SOCKS_PORT",
  );
}
function validateSessionIdentity(session: Session): void {
  if (session.identity) {
    check(
      session.identity.provider === session.provider &&
        typeof session.identity.user_id === "string" &&
        !!session.identity.user_id &&
        typeof session.identity.user_login === "string" &&
        !!session.identity.user_login,
      "INVALID_IDENTITY_STATE",
    );
  }
}
function validateSession(session: Session): void {
  check(
    validId(session.id) && ["microsoft", "github"].includes(session.provider),
    "INVALID_STATE",
  );

  validateSessionName(session);
  validateSessionPorts(session);
  check(
    typeof session.desired === "boolean" &&
      [
        "login_required",
        "ready",
        "starting",
        "running",
        "stopped",
        "reauth_required",
        "error",
        "removed",
      ].includes(session.status),
    "INVALID_STATE",
  );

  validateSessionIdentity(session);
}
class StateMappings {
  readonly ids = new Set<string>();
  readonly listeners = new Set<number>();
  readonly names = new Set<string>();
  readonly tunnels = new Set<string>();
  reserve(session: Session, config: Config): void {
    const { ids, listeners, names, tunnels } = this;
    check(
      !ids.has(session.id) &&
        !listeners.has(session.listener) &&
        (!session.tunnel_name || !names.has(session.tunnel_name)) &&
        (!session.tunnel_id || !tunnels.has(session.tunnel_id)),
      "DUPLICATE_STATE_MAPPING",
    );
    check(
      Number.isInteger(session.listener) &&
        session.listener >= config.listenerStart &&
        session.listener <= config.listenerEnd,
      "STATE_CONFIG_MISMATCH",
    );

    ids.add(session.id);
    listeners.add(session.listener);
    if (session.tunnel_name) {
      names.add(session.tunnel_name);
    }
    if (session.tunnel_id) {
      tunnels.add(session.tunnel_id);
    }
    if (session.socks_listener !== undefined) {
      check(
        Number.isInteger(session.socks_listener) &&
          session.socks_listener >= config.listenerStart &&
          session.socks_listener <= config.listenerEnd &&
          session.socks_listener !== session.proxy_port,
        "STATE_CONFIG_MISMATCH",
      );
      check(!listeners.has(session.socks_listener), "DUPLICATE_STATE_MAPPING");
      listeners.add(session.socks_listener);
    }
  }
}
function validateState(s: State, config: Config): void {
  check(
    s.version === 1 && s.hub_id === config.hubId && Array.isArray(s.sessions),
    "STATE_CONFIG_MISMATCH",
  );
  const mappings = new StateMappings();
  for (const session of s.sessions) {
    validateSession(session);
    mappings.reserve(session, config);
  }
  check(
    Number.isInteger(s.next_listener) &&
      s.next_listener >= config.listenerStart &&
      s.next_listener <= config.listenerEnd + 1 &&
      [...mappings.listeners].every((v) => v < s.next_listener),
    "INVALID_LISTENER_CURSOR",
  );
}

export class StateStore {
  state!: State;
  private writes: Promise<void> = Promise.resolve();
  constructor(
    readonly directory: string,
    readonly config: Config,
    readonly persistence?: Persistence,
  ) {}
  async load(): Promise<void> {
    await privateDirectory(this.directory);
    this.state = await this.readState();
    validateState(this.state, this.config);
    await this.save();
  }
  private initialState(): State {
    return {
      version: 1,
      hub_id: this.config.hubId,
      next_listener: this.config.listenerStart,
      sessions: [],
    };
  }
  private async readState(): Promise<State> {
    if (this.persistence) {
      return (await this.persistence.read()) ?? this.initialState();
    }
    try {
      const file = path.join(this.directory, "state.json");
      check(!(await lstat(file)).isSymbolicLink(), "UNSAFE_STATE_FILE");
      return JSON.parse(await readFile(file, "utf8")) as State;
    } catch (e) {
      if ((e as NodeJS.ErrnoException).code !== "ENOENT") {
        throw e;
      }
      return this.initialState();
    }
  }
  save(): Promise<void> {
    const snapshot = JSON.stringify(this.state, null, 2) + "\n";
    this.writes = this.writes.then(() =>
      this.persistence
        ? this.persistence.write(JSON.parse(snapshot))
        : atomicWrite(path.join(this.directory, "state.json"), snapshot),
    );
    return this.writes;
  }
  get(id: string): Session {
    check(validId(id), "INVALID_SESSION_ID");
    const session = this.state.sessions.find(
      (s) => s.id === id && s.status !== "removed",
    );
    check(session, "SESSION_NOT_FOUND");
    return session;
  }
  add(id: string, provider: Provider, name?: string): Session {
    check(validId(id), "INVALID_SESSION_ID");
    check(["microsoft", "github"].includes(provider), "INVALID_PROVIDER");
    check(
      this.config.allowedProviders.includes(provider),
      "PROVIDER_NOT_ALLOWED",
    );
    check(name === undefined || validTunnelName(name), "INVALID_TUNNEL_NAME");
    // Tombstones reserve names/listeners forever. Old audit records never change owner.
    check(
      !this.state.sessions.some(
        (s) => s.id === id || (name !== undefined && s.tunnel_name === name),
      ),
      "SESSION_OR_NAME_RESERVED",
    );
    check(
      this.state.sessions.filter((s) => s.status !== "removed").length <
        this.config.maxSessions,
      "SESSION_LIMIT",
    );
    const pendingSocks = this.config.socksEnabled
      ? this.state.sessions.filter(
          (s) => s.status !== "removed" && s.socks_listener === undefined,
        ).length
      : 0;
    check(
      this.state.next_listener +
        (this.config.socksEnabled ? 1 + pendingSocks : 0) <=
        this.config.listenerEnd,
      "LISTENER_POOL_EXHAUSTED",
    );
    const session: Session = {
      id,
      provider,
      ...(name === undefined
        ? { tunnel_name_template: this.config.tunnelNameTemplate }
        : { tunnel_name: name }),
      listener: this.state.next_listener++,
      desired: false,
      status: "login_required",
      created_at: new Date().toISOString(),
    };
    if (this.config.socksEnabled) {
      session.socks_listener = this.state.next_listener++;
    }
    this.state.sessions.push(session);
    return session;
  }
  ensureSocksListener(session: Session): void {
    if (session.socks_listener !== undefined) {
      return;
    }
    check(
      this.state.sessions.includes(session) && session.status !== "removed",
      "SESSION_NOT_FOUND",
    );
    check(
      this.state.next_listener <= this.config.listenerEnd,
      "LISTENER_POOL_EXHAUSTED",
    );
    // Use the same monotonically allocated pool. Never reassign old audit owners.
    session.socks_listener = this.state.next_listener++;
  }
  resolveName(session: Session): void {
    // No await between conflict check and reservation: concurrent logins cannot
    // reserve the same name in this manager. Backend locking excludes other managers.
    if (session.tunnel_name !== undefined) {
      return;
    }
    check(
      session.identity && session.identity.provider === session.provider,
      "AUTH_REQUIRED",
    );
    check(session.tunnel_name_template, "INVALID_TUNNEL_NAME_TEMPLATE");
    const name = resolveTunnelName(
      session.tunnel_name_template,
      this.config.hubId,
      session.identity,
    );
    check(
      !this.state.sessions.some((s) => s !== session && s.tunnel_name === name),
      "SESSION_OR_NAME_RESERVED",
    );
    session.tunnel_name = name;
    delete session.tunnel_name_template;
  }
}

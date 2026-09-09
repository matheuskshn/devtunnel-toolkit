import { open, readFile, rename, mkdir, lstat } from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { check, validId, validTunnelName, validCanonicalId, type Config, type State, type Session, type Provider } from './model.js';

export async function privateDirectory(dir: string): Promise<void> {
  await mkdir(dir, { recursive: true, mode: 0o700 });
  const st = await lstat(dir);
  check(st.isDirectory() && !st.isSymbolicLink(), 'UNSAFE_DATA_DIRECTORY');
}
export async function atomicWrite(file: string, contents: string): Promise<void> {
  const temporary = `${file}.${randomUUID()}.tmp`;
  const handle = await open(temporary, 'wx', 0o600);
  try { await handle.writeFile(contents); await handle.sync(); } finally { await handle.close(); }
  await rename(temporary, file);
  const directory = await open(path.dirname(file), 'r');
  try { await directory.sync(); } finally { await directory.close(); }
}
export interface Persistence {
  read(): Promise<State | undefined>;
  write(state: State): Promise<void>;
  restore(id: string, home: string): Promise<void>;
  beginAuth(id: string): Promise<void>;
  checkpoint(id: string, home: string): Promise<void>;
}
export class StateStore {
  state!: State;
  private writes: Promise<void> = Promise.resolve();
  constructor(readonly directory: string, readonly config: Config, readonly persistence?: Persistence) {}
  async load(): Promise<void> {
    await privateDirectory(this.directory);
    if (this.persistence) {
      this.state = await this.persistence.read() ?? { version: 1, hub_id: this.config.hubId, next_listener: this.config.listenerStart, sessions: [] };
    } else try {
      const file = path.join(this.directory, 'state.json');
      check(!(await lstat(file)).isSymbolicLink(), 'UNSAFE_STATE_FILE');
      this.state = JSON.parse(await readFile(file, 'utf8')) as State;
    } catch (e) {
      if ((e as NodeJS.ErrnoException).code !== 'ENOENT') throw e;
      this.state = { version: 1, hub_id: this.config.hubId, next_listener: this.config.listenerStart, sessions: [] };
    }
    const s = this.state;
    check(s.version === 1 && s.hub_id === this.config.hubId && Array.isArray(s.sessions), 'STATE_CONFIG_MISMATCH');
    const ids = new Set<string>(); const listeners = new Set<number>(); const names = new Set<string>();
    for (const session of s.sessions) {
      check(validId(session.id) && validTunnelName(session.tunnel_name) && ['microsoft', 'github'].includes(session.provider), 'INVALID_STATE');
      check(!session.tunnel_id || validCanonicalId(session.tunnel_id), 'INVALID_STATE');
      check(typeof session.desired === 'boolean' && ['login_required','ready','starting','running','stopped','reauth_required','error','removed'].includes(session.status), 'INVALID_STATE');
      check(!ids.has(session.id) && !listeners.has(session.listener) && !names.has(session.tunnel_name), 'DUPLICATE_STATE_MAPPING');
      check(Number.isInteger(session.listener) && session.listener >= this.config.listenerStart && session.listener <= this.config.listenerEnd, 'STATE_CONFIG_MISMATCH');
      if (session.identity) {
        check(session.identity.provider === session.provider && typeof session.identity.user_id === 'string' && !!session.identity.user_id && typeof session.identity.user_login === 'string' && !!session.identity.user_login, 'INVALID_IDENTITY_STATE');
      }
      ids.add(session.id); listeners.add(session.listener); names.add(session.tunnel_name);
    }
    check(Number.isInteger(s.next_listener) && s.next_listener >= this.config.listenerStart && s.next_listener <= this.config.listenerEnd + 1 && s.sessions.every(v => v.listener < s.next_listener), 'INVALID_LISTENER_CURSOR');
    await this.save();
  }
  save(): Promise<void> {
    const snapshot = JSON.stringify(this.state, null, 2) + '\n';
    this.writes = this.writes.then(() => this.persistence ? this.persistence.write(JSON.parse(snapshot)) : atomicWrite(path.join(this.directory, 'state.json'), snapshot));
    return this.writes;
  }
  get(id: string): Session {
    check(validId(id), 'INVALID_SESSION_ID');
    const session = this.state.sessions.find(s => s.id === id && s.status !== 'removed');
    check(session, 'SESSION_NOT_FOUND'); return session;
  }
  add(id: string, provider: Provider, name?: string): Session {
    check(validId(id), 'INVALID_SESSION_ID');
    check(['microsoft', 'github'].includes(provider), 'INVALID_PROVIDER');
    check(this.config.allowedProviders.includes(provider), 'PROVIDER_NOT_ALLOWED');
    const tunnelName = name ?? `${this.config.hubId}-${id}`;
    check(validTunnelName(tunnelName), 'INVALID_TUNNEL_NAME');
    // Tombstones reserve names/listeners forever. Old audit records never change owner.
    check(!this.state.sessions.some(s => s.id === id || s.tunnel_name === tunnelName), 'SESSION_OR_NAME_RESERVED');
    check(this.state.sessions.filter(s => s.status !== 'removed').length < this.config.maxSessions, 'SESSION_LIMIT');
    check(this.state.next_listener <= this.config.listenerEnd, 'LISTENER_POOL_EXHAUSTED');
    const session: Session = { id, provider, tunnel_name: tunnelName, listener: this.state.next_listener++,
      desired: false, status: 'login_required', created_at: new Date().toISOString() };
    this.state.sessions.push(session); return session;
  }
}

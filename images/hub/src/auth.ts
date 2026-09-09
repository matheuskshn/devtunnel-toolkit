import path from 'node:path';
import { rm } from 'node:fs/promises';
import type { ChildProcess } from 'node:child_process';
import type { Tunnel } from '@microsoft/dev-tunnels-contracts';
import { TunnelManagementHttpClient, ManagementApiVersions } from '@microsoft/dev-tunnels-management';
import { CancellationTokenSource } from '@microsoft/dev-tunnels-ssh';
import { bindIdentity, check, HubError, type Identity, type Provider, type Session, validCanonicalId } from './model.js';
import { privateDirectory, type Persistence } from './state.js';
import { cleanEnvironment, command, launch, terminate, waitFile, waitSecretService } from './processes.js';

export function parseJson(text: string): Record<string, any> {
  // The CLI prints a first-use license banner before JSON. Ignore only that prefix.
  const start = text.search(/^\s*\{/m);
  check(start >= 0, 'CLI_JSON_REQUIRED');
  try { return JSON.parse(text.slice(start)); } catch { throw new HubError('CLI_JSON_INVALID'); }
}
export function parseIdentity(value: Record<string, any>, expected: Provider): Identity {
  check(value.status !== 'Not logged in', 'AUTH_REQUIRED');
  const user = value.user ?? value;
  const provider = String(user.provider ?? value.provider ?? '').toLowerCase();
  check(['microsoft', 'entra', 'github'].includes(provider), 'IDENTITY_SCHEMA_UNSUPPORTED');
  const normalized = provider === 'entra' ? 'microsoft' : provider;
  check(normalized === expected, 'IDENTITY_PROVIDER_MISMATCH');
  const login = user.username ?? user.userName ?? user.login;
  const id = user.objectId ?? user.userId ?? user.id;
  // Do not substitute an operator-supplied alias or mutable login for a stable ID.
  check(typeof login === 'string' && login.length > 0 && login.length <= 320 && !/[\x00-\x1f\x7f]/.test(login), 'IDENTITY_SCHEMA_UNSUPPORTED');
  check((typeof id === 'string' || typeof id === 'number') && String(id).length > 0 && String(id).length <= 256, 'IDENTITY_SCHEMA_UNSUPPORTED');
  const tenant = user.tenantId;
  check(!tenant || typeof tenant === 'string', 'IDENTITY_SCHEMA_UNSUPPORTED');
  return { provider: expected, user_login: login, user_id: `${expected}:${tenant ? `${tenant}:` : ''}${id}`,
    ...(tenant ? { tenant_id: tenant } : {}) };
}
export function canonicalTunnel(value: Record<string, any>): string {
  const tunnel = value.tunnel ?? value;
  const id = String(tunnel.tunnelId ?? '');
  const canonical = id.includes('.') ? id : `${id}.${tunnel.clusterId ?? ''}`;
  check(validCanonicalId(canonical), 'TUNNEL_SCHEMA_UNSUPPORTED'); return canonical;
}
export function privateTunnel(value: Record<string, any>, session: Session): Tunnel {
  const tunnel = value.tunnel ?? value;
  check(canonicalTunnel(value) === session.tunnel_id, 'TUNNEL_ID_CHANGED');
  // Owner permissions are implicit. Any explicit or inherited grant is rejected.
  check(tunnel.accessControl && Array.isArray(tunnel.accessControl.entries), 'ACL_SCHEMA_UNSUPPORTED');
  check(tunnel.accessControl.entries.length === 0, 'TUNNEL_NOT_OWNER_ONLY');
  check(Array.isArray(tunnel.ports) && tunnel.ports.length === 1 && tunnel.ports[0].portNumber === 3140, 'TUNNEL_PORT_POLICY_CHANGED');
  for (const port of tunnel.ports) {
    check(!port.accessControl || (Array.isArray(port.accessControl.entries) && port.accessControl.entries.length === 0), 'PORT_NOT_OWNER_ONLY');
  }
  const canonical = session.tunnel_id!.split('.');
  return { ...tunnel, tunnelId: canonical[0], clusterId: canonical[1], accessTokens: undefined };
}

export class SessionRuntime {
  env: NodeJS.ProcessEnv;
  private bus?: ChildProcess;
  private keyring?: ChildProcess;
  private closing = false;
  private cycling = false;
  private servicesFailed = false;
  private queue: Promise<unknown> = Promise.resolve();
  readonly abort = new AbortController();
  constructor(readonly session: Session, readonly dataDir: string, readonly runDir: string,
    readonly onFailure: () => void, readonly allowedTenants: string[] = [], readonly persistence?: Persistence) {
    const home = path.join(dataDir, 'sessions', session.id, 'home');
    this.env = { ...cleanEnvironment(), HOME: home, XDG_CONFIG_HOME: `${home}/.config`,
      XDG_DATA_HOME: `${home}/.local/share`, XDG_CACHE_HOME: `${home}/.cache`,
      XDG_RUNTIME_DIR: runDir, DBUS_SESSION_BUS_ADDRESS: `unix:path=${runDir}/bus`,
      GNOME_KEYRING_CONTROL: `${runDir}/keyring` };
  }
  async open(): Promise<void> {
    await privateDirectory(this.env.HOME!);
    await this.persistence?.restore(this.session.id, this.env.HOME!);
    if (!this.persistence) await this.openServices();
  }
  private async openServices(): Promise<void> {
    check(!this.closing && !this.abort.signal.aborted, 'COMMAND_CANCELLED_OR_TIMEOUT');
    this.cycling = false;
    await privateDirectory(this.env.XDG_CONFIG_HOME!);
    await privateDirectory(this.env.XDG_DATA_HOME!);
    await privateDirectory(this.env.XDG_CACHE_HOME!);
    await privateDirectory(this.runDir);
    this.bus = launch('dbus-daemon', ['--session', '--nofork', `--address=${this.env.DBUS_SESSION_BUS_ADDRESS}`], this.env);
    this.bus.stdout?.resume();
    this.bus.on('exit', () => this.serviceExit());
    await waitFile(`${this.runDir}/bus`, this.bus);
    this.keyring = launch('gnome-keyring-daemon', ['--foreground', '--unlock', '--components=secrets', `--control-directory=${this.env.GNOME_KEYRING_CONTROL}`], this.env);
    this.keyring.stdout?.resume(); this.keyring.stdin?.end('\n');
    this.keyring.on('exit', () => this.serviceExit());
    await waitSecretService(this.env, this.keyring, this.abort.signal);
  }
  private serviceExit(): void {
    if (!this.closing && !this.cycling) {
      this.servicesFailed = true;
      if (this.persistence) this.abort.abort();
      this.onFailure();
    }
  }
  cli(args: string[], output?: (chunk: string) => void): Promise<string> {
    return this.withCredentials(() => command('devtunnel', args, this.env, {
      timeout: output ? 600000 : 60000, output, signal: this.abort.signal,
    }));
  }
  withCredentials<T>(action: () => Promise<T>): Promise<T> {
    const task = this.queue.then(async () => {
      check(!this.closing && !this.abort.signal.aborted, 'COMMAND_CANCELLED_OR_TIMEOUT');
      if (this.persistence) {
        await this.persistence.beginAuth(this.session.id);
        await this.openServices().catch(async e => { await this.stopServices(); throw e; });
      }
      try {
        return await action();
      } finally {
        if (this.persistence) {
          // Serialize a quiescent home, including changes from failed/refresh commands.
          // The DB dirty marker remains set if this process dies before commit.
          await this.stopServices();
          check(!this.servicesFailed, 'SESSION_SERVICE_FAILED');
          await this.persistence.checkpoint(this.session.id, this.env.HOME!);
        }
      }
    });
    this.queue = task.catch(() => {}); return task;
  }
  async identity(): Promise<Identity> {
    const identity = parseIdentity(parseJson(await this.cli(['user', 'show', '--json'])), this.session.provider);
    check(identity.provider !== 'microsoft' || !this.allowedTenants.length || (identity.tenant_id && this.allowedTenants.includes(identity.tenant_id)), 'IDENTITY_TENANT_NOT_ALLOWED');
    return identity;
  }
  async login(output: (chunk: string) => void): Promise<void> {
    // A manager restart or a corrected schema adapter can bind an existing cache
    // without asking the user to approve another device code.
    try { bindIdentity(this.session, await this.identity()); return; }
    catch (e) { if (!(e instanceof HubError) || e.code !== 'AUTH_REQUIRED') throw e; }
    await this.cli(['user', 'login', this.session.provider === 'github' ? '--github' : '--entra', '--use-device-code-auth'], output);
    const identity = await this.identity();
    try { bindIdentity(this.session, identity); } catch (e) { await this.cli(['user', 'logout']); throw e; }
  }
  async credentials(): Promise<Tunnel> {
    bindIdentity(this.session, await this.identity());
    const details = await this.details();
    return { ...privateTunnel(details, this.session), accessTokens: details.accessTokens };
  }
  async details(): Promise<Tunnel> {
    check(this.session.tunnel_id, 'TUNNEL_NOT_PROVISIONED');
    const response = parseJson(await this.cli(['token', this.session.tunnel_id, '--scopes', 'host', '--json']));
    const token = response.token ?? response.accessToken;
    check(typeof token === 'string' && token.length > 20, 'TOKEN_SCHEMA_UNSUPPORTED');
    const [tunnelId, clusterId] = this.session.tunnel_id.split('.');
    const client = new TunnelManagementHttpClient('devtunnel-toolkit-hub', ManagementApiVersions.Version20230927preview);
    client.enableEventsReporting = false; client.trace = () => {};
    const cancellation = new CancellationTokenSource();
    const abort = () => cancellation.cancel();
    const timer = setTimeout(abort, 60000);
    this.abort.signal.addEventListener('abort', abort, { once: true });
    if (this.abort.signal.aborted) abort();
    try {
      // CLI show --json is a human-oriented summary (ACL arrays, formatted rates,
      // omitted empty ports). Use the SDK contract for security-sensitive policy.
      const tunnel = await client.getTunnel({ tunnelId, clusterId, accessTokens: { host: token } },
        { includePorts: true, includeAccessControl: true }, cancellation.token);
      check(tunnel, 'TUNNEL_NOT_FOUND');
      return { ...tunnel, accessTokens: { host: token } };
    } catch (e) {
      if (e instanceof HubError) throw e;
      const status = (e as { response?: { status?: number } }).response?.status;
      throw new HubError(status === 401 ? 'AUTH_REQUIRED' : status === 403 ? 'TUNNEL_ACCESS_DENIED' : 'COMMAND_FAILED');
    } finally {
      clearTimeout(timer); this.abort.signal.removeEventListener('abort', abort); cancellation.dispose();
    }
  }
  async close(): Promise<void> {
    this.closing = true; this.abort.abort();
    await this.queue;
    await this.stopServices();
    if (this.persistence) await rm(this.env.HOME!, {recursive:true, force:true});
  }
  private async stopServices(): Promise<void> {
    this.cycling = true;
    await terminate(this.keyring); await terminate(this.bus);
    const killed = this.keyring?.signalCode === 'SIGKILL';
    this.keyring = undefined; this.bus = undefined;
    await rm(this.runDir, { recursive: true, force: true });
    check(!killed, 'CREDENTIAL_SERVICE_FORCED_EXIT');
  }
}

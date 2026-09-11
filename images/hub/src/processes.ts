import { spawn, type ChildProcess } from 'node:child_process';
import { access } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';
import { HubError } from './model.js';

export function cleanEnvironment(): NodeJS.ProcessEnv {
  // Do not leak the administrator's tokens, proxy configuration or home to workers.
  return { PATH: '/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin', LANG: 'C.UTF-8',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1' };
}
export function launch(file: string, args: string[], env: NodeJS.ProcessEnv): ChildProcess {
  const child = spawn(file, args, { env, stdio: ['pipe', 'pipe', 'pipe'] });
  // Never forward raw subprocess stderr. It can contain URLs or tokens.
  child.stderr?.resume();
  child.stdin?.on('error', () => {}); // A short-lived command may close stdin first.
  child.on('error', () => {});
  return child;
}
export async function terminate(child?: ChildProcess): Promise<void> {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  await new Promise<void>(resolve => {
    child.once('close', () => { clearTimeout(timer); resolve(); });
    const timer = setTimeout(() => child.kill('SIGKILL'), 5000);
    child.kill('SIGTERM');
  });
}
export async function waitFile(file: string, child: ChildProcess): Promise<void> {
  for (let i = 0; i < 100; i++) {
    if (child.exitCode !== null || child.signalCode !== null) throw new HubError('SESSION_SERVICE_FAILED');
    try { await access(file); return; } catch { await delay(50); }
  }
  throw new HubError('SESSION_SERVICE_TIMEOUT');
}
function secretServiceOwner(reply: string): boolean | undefined {
  let start = 0;
  let foundFalse = false;
  for (let index = 0; index <= reply.length; index++) {
    if (index !== reply.length && !'\n\r\u2028\u2029'.includes(reply[index])) { continue; }
    const line = reply.slice(start, index).trim();
    if (line === 'boolean true') { return true; }
    if (line === 'boolean false') { foundFalse = true; }
    start = index + 1;
  }
  return foundFalse ? false : undefined;
}

export async function waitSecretService(env: NodeJS.ProcessEnv, child: ChildProcess, signal: AbortSignal,
  options: { timeout?: number; query?: typeof command } = {}): Promise<void> {
  const deadline = Date.now() + (options.timeout ?? 5000);
  const checkRunning = () => {
    if (signal.aborted) throw new HubError('COMMAND_CANCELLED_OR_TIMEOUT');
    if (child.exitCode !== null || child.signalCode !== null) throw new HubError('SESSION_SERVICE_FAILED');
  };
  while (Date.now() < deadline) {
    checkRunning();
    // Query the bus itself: pinging an unowned service can auto-start a second,
    // locked keyring before our explicitly unlocked daemon has registered.
    const reply = await (options.query ?? command)('dbus-send', ['--session', '--print-reply',
      '--reply-timeout=1000', '--dest=org.freedesktop.DBus', '/org/freedesktop/DBus',
      'org.freedesktop.DBus.NameHasOwner', 'string:org.freedesktop.secrets'], env,
    { timeout: Math.max(1, Math.min(1000, deadline - Date.now())), signal });
    checkRunning();
    const owned = secretServiceOwner(reply);
    if (owned === true) { return; }
    if (owned === undefined) { throw new HubError('SESSION_SERVICE_INVALID_REPLY'); }
    await delay(Math.max(0, Math.min(50, deadline - Date.now())));
  }
  checkRunning();
  throw new HubError('SESSION_SERVICE_TIMEOUT');
}
export async function command(file: string, args: string[], env: NodeJS.ProcessEnv, options: {
  timeout?: number; input?: string; output?: (chunk: string) => void; signal?: AbortSignal;
} = {}): Promise<string> {
  const child = launch(file, args, env);
  return new Promise((resolve, reject) => {
    let stdout = ''; let stderr = ''; let exhausted = false;
    const kill = () => { exhausted = true; child.kill('SIGKILL'); };
    const timer = setTimeout(kill, options.timeout ?? 60000);
    options.signal?.addEventListener('abort', kill, { once: true });
    if (options.signal?.aborted) kill();
    child.stdout?.on('data', chunk => {
      stdout += chunk;
      if (stdout.length > 1024 * 1024) kill();
      options.output?.(String(chunk));
    });
    child.stderr?.on('data', chunk => { if (stderr.length < 65536) stderr += chunk; });
    child.stdin?.end(options.input ?? '');
    const cleanup = () => { clearTimeout(timer); options.signal?.removeEventListener('abort', kill); };
    child.once('error', () => { cleanup(); reject(new HubError('COMMAND_UNAVAILABLE')); });
    child.once('close', code => {
      cleanup();
      if (exhausted) return reject(new HubError('COMMAND_CANCELLED_OR_TIMEOUT'));
      if (code === 0) return resolve(stdout);
      // Classify without returning the raw message, arguments or credentials.
      if (/unauthorized|AADSTS|login.*expired|not logged in|sign.in required/i.test(stdout + stderr)) return reject(new HubError('AUTH_REQUIRED'));
      if (/^Tunnel not found(?: in [a-z0-9]+)?: [a-z0-9.-]+\s*$/im.test(stdout + stderr)) return reject(new HubError('TUNNEL_NOT_FOUND'));
      if (/Conflict with existing entity/i.test(stdout + stderr)) return reject(new HubError('TUNNEL_NAME_CONFLICT'));
      reject(new HubError('COMMAND_FAILED'));
    });
  });
}

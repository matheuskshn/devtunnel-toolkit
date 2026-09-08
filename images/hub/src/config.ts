import { readFile } from 'node:fs/promises';
import { check, HubError, parseConfig, type Config } from './model.js';

const fields = {
  HUB_ID: ['hubId', 'string'],
  HUB_LISTENER_START: ['listenerStart', 'number'],
  HUB_LISTENER_END: ['listenerEnd', 'number'],
  HUB_MAX_SESSIONS: ['maxSessions', 'number'],
  HUB_ALLOWED_DOMAINS: ['allowedDomains', 'list'],
  HUB_ALLOWED_PORTS: ['allowedPorts', 'numbers'],
  HUB_CONNECT_PORTS: ['connectPorts', 'numbers'],
  HUB_ALLOWED_PROVIDERS: ['allowedProviders', 'list'],
  HUB_ALLOWED_MICROSOFT_TENANTS: ['allowedMicrosoftTenants', 'list'],
  HUB_HEALTH_PORT: ['healthPort', 'number'],
  HUB_MAINTENANCE_SECONDS: ['maintenanceSeconds', 'number'],
} as const;

export async function loadConfig(file?: string, env: NodeJS.ProcessEnv = process.env): Promise<Config> {
  const selected = file ?? env.HUB_CONFIG;
  check(selected === undefined || selected.trim().length > 0, 'INVALID_CONFIG_PATH');
  let input: unknown = {};
  try { input = JSON.parse(await readFile(selected ?? '/config/hub.json', 'utf8')); }
  catch (e) {
    if ((e as NodeJS.ErrnoException).code === 'ENOENT') {
      check(selected === undefined, 'CONFIG_FILE_NOT_FOUND');
    } else {
      throw new HubError(e instanceof SyntaxError ? 'INVALID_CONFIG_JSON' : 'CONFIG_FILE_UNREADABLE');
    }
  }
  // Validate the file independently: environment overrides must not hide file errors.
  const merged: Record<string, unknown> = { ...parseConfig(input) };
  for (const [name, [key, kind]] of Object.entries(fields)) {
    const raw = env[name];
    if (raw === undefined) continue;
    const value = raw.trim();
    const number = (item: string): number => {
      check(/^\d+$/.test(item) && Number.isSafeInteger(Number(item)), `INVALID_${name}`);
      return Number(item);
    };
    if (kind === 'string') merged[key] = value;
    else if (kind === 'number') merged[key] = number(value);
    else {
      const items = value === '' ? [] : value.split(',').map(item => item.trim());
      check(items.every(item => item.length > 0), `INVALID_${name}`);
      merged[key] = kind === 'numbers' ? items.map(number) : items;
    }
  }
  return parseConfig(merged);
}

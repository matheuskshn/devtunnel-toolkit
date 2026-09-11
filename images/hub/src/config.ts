import { readFile } from "node:fs/promises";
import { check, HubError, parseConfig, type Config } from "./model.js";

const fields = {
  HUB_ID: ["hubId", "string"],
  HUB_TUNNEL_NAME_TEMPLATE: ["tunnelNameTemplate", "string"],
  HUB_PROXY_PORT: ["proxyPort", "number"],
  HUB_SOCKS_PORT: ["socksPort", "number"],
  HUB_SOCKS_ENABLED: ["socksEnabled", "boolean"],
  HUB_LISTENER_START: ["listenerStart", "number"],
  HUB_LISTENER_END: ["listenerEnd", "number"],
  HUB_MAX_SESSIONS: ["maxSessions", "number"],
  HUB_ALLOWED_DOMAINS: ["allowedDomains", "list"],
  HUB_ALLOW_ALL_DOMAINS: ["allowAllDomains", "boolean"],
  HUB_ALLOWED_PORTS: ["allowedPorts", "numbers"],
  HUB_CONNECT_PORTS: ["connectPorts", "numbers"],
  HUB_ALLOWED_PROVIDERS: ["allowedProviders", "list"],
  HUB_ALLOWED_MICROSOFT_TENANTS: ["allowedMicrosoftTenants", "list"],
  HUB_HEALTH_PORT: ["healthPort", "number"],
  HUB_MAINTENANCE_SECONDS: ["maintenanceSeconds", "number"],
} as const;

async function readConfigFile(selected?: string): Promise<Config> {
  try {
    return parseConfig(
      JSON.parse(await readFile(selected ?? "/config/hub.json", "utf8")),
    );
  } catch (e) {
    if ((e as NodeJS.ErrnoException).code === "ENOENT") {
      check(selected === undefined, "CONFIG_FILE_NOT_FOUND");
      return parseConfig({});
    }
    if (e instanceof HubError) {
      throw e;
    }
    throw new HubError(
      e instanceof SyntaxError
        ? "INVALID_CONFIG_JSON"
        : "CONFIG_FILE_UNREADABLE",
    );
  }
}

function environmentNumber(item: string, name: string): number {
  check(
    /^\d+$/.test(item) && Number.isSafeInteger(Number(item)),
    `INVALID_${name}`,
  );
  return Number(item);
}

function environmentValue(raw: string, kind: string, name: string): unknown {
  const value = raw.trim();
  switch (kind) {
    case "string":
      return value;
    case "boolean":
      check(value === "true" || value === "false", `INVALID_${name}`);
      return value === "true";
    case "number":
      return environmentNumber(value, name);
    default: {
      const items =
        value === "" ? [] : value.split(",").map((item) => item.trim());
      check(
        items.every((item) => item.length > 0),
        `INVALID_${name}`,
      );
      return kind === "numbers"
        ? items.map((item) => environmentNumber(item, name))
        : items;
    }
  }
}

export async function loadConfig(
  file?: string,
  env: NodeJS.ProcessEnv = process.env,
): Promise<Config> {
  const selected = file ?? env.HUB_CONFIG;
  check(
    selected === undefined || selected.trim().length > 0,
    "INVALID_CONFIG_PATH",
  );
  // Validate the file independently: environment overrides must not hide file errors.
  const merged: Record<string, unknown> = {
    ...(await readConfigFile(selected)),
  };
  for (const [name, [key, kind]] of Object.entries(fields)) {
    const raw = env[name];
    if (raw !== undefined) {
      merged[key] = environmentValue(raw, kind, name);
    }
  }
  return parseConfig(merged);
}

import path from "node:path";
import { check, validId } from "./model.js";

export const managedEnvironmentNames = [
  "HUB_RUN_DIR",
  "HUB_ID",
  "HUB_STORAGE_BACKEND",
  "HUB_PG_HOST",
  "HUB_PG_PORT",
  "HUB_PG_DATABASE",
  "HUB_PG_USER",
  "HUB_PG_SSLMODE",
  "HUB_PG_PASSWORD",
  "HUB_CREDENTIAL_KEY",
  "HUB_CREDENTIAL_KEY_ID",
] as const;

export type ManagedEnvironmentName = (typeof managedEnvironmentNames)[number];

export type InfrastructureProfile = Partial<
  Record<ManagedEnvironmentName, string>
>;

export interface PublicInfrastructureProfileField {
  name: ManagedEnvironmentName;
  sensitive: boolean;
  configured: true;
  reference?: string;
  value?: string;
}

export function environmentReference(value: string | undefined): string | undefined {
  if (!value?.startsWith("env://")) return;
  const match = /^env:\/\/([A-Za-z_][A-Za-z0-9_]*)$/.exec(value);
  check(match, "INVALID_ENV_REFERENCE");
  return match[1];
}

export function resolveRuntimeEnvironment(
  source: NodeJS.ProcessEnv,
): NodeJS.ProcessEnv {
  const resolved = { ...source };
  for (const name of managedEnvironmentNames) {
    const reference = environmentReference(source[name]);
    if (!reference) continue;
    check(reference !== name, "INVALID_ENV_REFERENCE");
    const value = source[reference];
    check(value !== undefined && !value.startsWith("env://"), "ENV_REFERENCE_REQUIRED");
    resolved[name] = value;
  }
  return resolved;
}

export interface InfrastructureField {
  name: ManagedEnvironmentName;
  sensitive: boolean;
  configured: boolean;
  source: "environment" | "environment-reference" | "default";
  reference?: string;
  value?: string;
  restartRequired: true;
}

const sensitive = new Set<ManagedEnvironmentName>([
  "HUB_PG_PASSWORD",
  "HUB_CREDENTIAL_KEY",
]);

export function sensitiveInfrastructureName(
  name: ManagedEnvironmentName,
): boolean {
  return sensitive.has(name);
}

function plainValue(name: ManagedEnvironmentName, value: string): void {
  check(
    value.length > 0 && value.length <= 4096 && !/[\0\r\n]/.test(value),
    "INVALID_INFRASTRUCTURE_VALUE",
  );
  switch (name) {
    case "HUB_RUN_DIR":
      check(
        path.isAbsolute(value) && path.normalize(value) === value,
        "INVALID_INFRASTRUCTURE_VALUE",
      );
      break;
    case "HUB_ID":
      check(validId(value), "INVALID_INFRASTRUCTURE_VALUE");
      break;
    case "HUB_STORAGE_BACKEND":
      check(
        value === "filesystem" || value === "postgres",
        "INVALID_INFRASTRUCTURE_VALUE",
      );
      break;
    case "HUB_PG_PORT": {
      const port = Number(value);
      check(
        /^\d+$/.test(value) && Number.isInteger(port) && port > 0 && port < 65536,
        "INVALID_INFRASTRUCTURE_VALUE",
      );
      break;
    }
    case "HUB_PG_SSLMODE":
      check(
        value === "verify-full" || value === "disable",
        "INVALID_INFRASTRUCTURE_VALUE",
      );
      break;
    case "HUB_CREDENTIAL_KEY":
      check(
        /^[A-Za-z0-9+/]{43}=$/.test(value),
        "INVALID_INFRASTRUCTURE_VALUE",
      );
      break;
    case "HUB_CREDENTIAL_KEY_ID":
      check(
        /^[A-Za-z0-9._-]{1,64}$/.test(value),
        "INVALID_INFRASTRUCTURE_VALUE",
      );
      break;
  }
}

function profileValue(name: ManagedEnvironmentName, value: unknown): string {
  check(typeof value === "string", "INVALID_INFRASTRUCTURE_VALUE");
  if (value.startsWith("env://")) {
    environmentReference(value);
  } else {
    plainValue(name, value);
  }
  return value;
}

export function updateInfrastructureProfile(
  current: InfrastructureProfile = {},
  input: unknown,
): InfrastructureProfile {
  check(input && typeof input === "object" && !Array.isArray(input), "INVALID_INFRASTRUCTURE");
  const next = { ...current };
  for (const [rawName, value] of Object.entries(input)) {
    check(
      managedEnvironmentNames.includes(rawName as ManagedEnvironmentName),
      "UNKNOWN_INFRASTRUCTURE_FIELD",
    );
    const name = rawName as ManagedEnvironmentName;
    if (value === null) {
      delete next[name];
    } else {
      next[name] = profileValue(name, value);
    }
  }
  return next;
}

export function publicInfrastructureProfile(
  profile: InfrastructureProfile = {},
): PublicInfrastructureProfileField[] {
  return managedEnvironmentNames.flatMap((name) => {
    const stored = profile[name];
    if (stored === undefined) return [];
    const reference = environmentReference(stored);
    const secret = sensitiveInfrastructureName(name);
    return [
      {
        name,
        sensitive: secret,
        configured: true as const,
        ...(reference ? { reference } : {}),
        ...(!secret && !reference ? { value: stored } : {}),
      },
    ];
  });
}

export function infrastructureSecrets(
  profile: InfrastructureProfile = {},
): Partial<Record<ManagedEnvironmentName, string>> {
  return Object.fromEntries(
    managedEnvironmentNames.flatMap((name) => {
      const value = profile[name];
      return value !== undefined && sensitiveInfrastructureName(name)
        ? [[name, value]]
        : [];
    }),
  );
}

export function infrastructureFields(
  raw: NodeJS.ProcessEnv,
  resolved: NodeJS.ProcessEnv,
  effective: Partial<Record<ManagedEnvironmentName, string>>,
): InfrastructureField[] {
  return managedEnvironmentNames.map((name) => {
    const reference = environmentReference(raw[name]);
    const value = effective[name] ?? resolved[name];
    const secret = sensitive.has(name);
    return {
      name,
      sensitive: secret,
      configured: value !== undefined && value !== "",
      source:
        raw[name] === undefined
          ? "default"
          : reference
            ? "environment-reference"
            : "environment",
      ...(reference ? { reference } : {}),
      ...(!secret && value !== undefined ? { value } : {}),
      restartRequired: true,
    };
  });
}

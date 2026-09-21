import pg, { type ClientConfig } from "pg";
import { readFile, mkdtemp, rm } from "node:fs/promises";
import path from "node:path";
import { createHash } from "node:crypto";
import { check, HubError, type State } from "./model.js";
import { privateDirectory, type Persistence } from "./state.js";
import {
  credentialKeys,
  seal,
  unseal,
  packHome,
  unpackHome,
  type CredentialKeys,
} from "./credentials.js";

const LEADER_POLL_MS = 1000;
const LEADER_LEASE_SECONDS = 30;
const LEADER_WAIT_MS = 180_000;

export type LeadershipState =
  | "leadership_requested"
  | "waiting_for_leadership"
  | "leadership_acquired"
  | "draining_for_handoff";

export interface PostgresLifecycle {
  onLost(): void;
  onTakeover?(): void;
  onLeadershipState?(state: LeadershipState): void;
  preflight?(state: State | undefined): void | Promise<void>;
}

export async function postgresConfig(
  env: NodeJS.ProcessEnv,
): Promise<ClientConfig> {
  const mode = env.HUB_PG_SSLMODE ?? "verify-full";
  check(["verify-full", "disable"].includes(mode), "INVALID_POSTGRES_TLS_MODE");
  check(
    env.HUB_PG_HOST &&
      env.HUB_PG_DATABASE &&
      env.HUB_PG_USER &&
      env.HUB_PG_PASSWORD,
    "POSTGRES_CONFIG_REQUIRED",
  );
  const port = Number(env.HUB_PG_PORT ?? "5432");
  check(
    Number.isInteger(port) && port > 0 && port < 65536,
    "INVALID_POSTGRES_PORT",
  );
  // No connection-string SSL options can override certificate verification.
  return {
    host: env.HUB_PG_HOST,
    port,
    database: env.HUB_PG_DATABASE,
    user: env.HUB_PG_USER,
    password: env.HUB_PG_PASSWORD,
    ssl:
      mode === "disable"
        ? false
        : {
            rejectUnauthorized: true,
            ...(env.HUB_PG_CA_FILE
              ? { ca: await readFile(env.HUB_PG_CA_FILE, "utf8") }
              : {}),
          },
    connectionTimeoutMillis: 5000,
    query_timeout: 5000,
    statement_timeout: 4000,
    keepAlive: true,
    keepAliveInitialDelayMillis: 1000,
    application_name: "devtunnel-toolkit-hub",
  };
}
export class PostgresPersistence implements Persistence {
  private readonly client: pg.Client;
  private readonly keys: CredentialKeys;
  private queue: Promise<unknown> = Promise.resolve();
  private timer?: NodeJS.Timeout;
  private broken = false;
  private closing = false;
  private acquired = false;
  private handoffRequested = false;
  private deploymentFailed = false;
  private managesDeploymentStatus = false;
  private readonly lockKey: string;
  private readonly deploymentId?: string;
  private readonly coordinated: boolean;
  private generation?: string;
  private deploymentStatus?: string;
  private deploymentFresh = false;
  private revision = "0";
  directory = "";
  constructor(
    config: ClientConfig,
    readonly hubId: string,
    env: NodeJS.ProcessEnv,
    readonly lifecycle: PostgresLifecycle,
  ) {
    this.client = new pg.Client(config);
    this.keys = credentialKeys(env);
    this.lockKey = createHash("sha256")
      .update(`devtunnel-hub:${hubId}`)
      .digest()
      .readBigInt64BE()
      .toString();
    this.deploymentId = env.HUB_DEPLOYMENT_ID ?? env.CONTAINER_APP_REVISION;
    if (this.deploymentId !== undefined) {
      check(
        this.deploymentId.length > 0 &&
          this.deploymentId.length <= 128 &&
          !/[\0\r\n]/.test(this.deploymentId),
        "INVALID_DEPLOYMENT_ID",
      );
    }
    const handoff = env.HUB_LEADER_HANDOFF;
    check(
      handoff === undefined || handoff === "true" || handoff === "false",
      "INVALID_LEADER_HANDOFF",
    );
    this.coordinated =
      handoff === "true" || (handoff === undefined && !!this.deploymentId);
    check(!this.coordinated || !!this.deploymentId, "DEPLOYMENT_ID_REQUIRED");
    this.client.on("error", () => this.lost());
    this.client.on("end", () => {
      if (!this.closing) this.lost();
    });
  }
  private lost(): void {
    if (this.broken || this.closing) return;
    this.broken = true;
    clearInterval(this.timer);
    if (this.acquired) this.lifecycle.onLost();
  }
  private async sql(
    text: string,
    values: unknown[] = [],
  ): Promise<pg.QueryResult> {
    check(!this.broken && !this.closing, "POSTGRES_UNAVAILABLE");
    try {
      return await this.client.query(text, values);
    } catch {
      this.lost();
      throw new HubError("POSTGRES_UNAVAILABLE");
    }
  }
  private serial<T>(operation: () => Promise<T>): Promise<T> {
    const task = this.queue.then(operation);
    this.queue = task.catch(() => {});
    return task;
  }
  private async tryLock(): Promise<boolean> {
    const lock = await this.sql(
      "SELECT pg_try_advisory_lock($1::bigint) AS acquired",
      [this.lockKey],
    );
    return lock.rows[0].acquired === true;
  }
  private async prepareSchema(): Promise<State | undefined> {
    try {
      await this.sql("BEGIN");
      await this.sql("SELECT pg_advisory_xact_lock(714036112)");
      await this.sql(`CREATE SCHEMA IF NOT EXISTS devtunnel_hub;
        CREATE TABLE IF NOT EXISTS devtunnel_hub.hubs (
          hub_id text PRIMARY KEY, schema_version integer NOT NULL DEFAULT 1,
          state jsonb, revision bigint NOT NULL DEFAULT 0, key_check bytea NOT NULL);
        CREATE TABLE IF NOT EXISTS devtunnel_hub.credentials (
          hub_id text NOT NULL REFERENCES devtunnel_hub.hubs(hub_id), session_id text NOT NULL,
          generation bigint NOT NULL DEFAULT 0, dirty boolean NOT NULL DEFAULT false,
          package bytea, PRIMARY KEY(hub_id, session_id));
        CREATE TABLE IF NOT EXISTS devtunnel_hub.deployments (
          generation bigint GENERATED ALWAYS AS IDENTITY,
          hub_id text NOT NULL REFERENCES devtunnel_hub.hubs(hub_id),
          deployment_id text NOT NULL, status text NOT NULL,
          heartbeat timestamptz NOT NULL DEFAULT clock_timestamp(),
          started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
          PRIMARY KEY(hub_id,deployment_id), UNIQUE(hub_id,generation));
        CREATE TABLE IF NOT EXISTS devtunnel_hub.leadership (
          hub_id text PRIMARY KEY REFERENCES devtunnel_hub.hubs(hub_id),
          desired_generation bigint NOT NULL, requested_at timestamptz NOT NULL DEFAULT clock_timestamp());`);
      await this.sql(
        "INSERT INTO devtunnel_hub.hubs(hub_id,key_check) VALUES($1,$2) ON CONFLICT DO NOTHING",
        [
          this.hubId,
          seal(
            Buffer.from("hub-key-check-v1"),
            `${this.hubId}:key-check`,
            this.keys,
          ),
        ],
      );
      const row = (
        await this.sql(
          "SELECT schema_version,key_check,revision,state FROM devtunnel_hub.hubs WHERE hub_id=$1",
          [this.hubId],
        )
      ).rows[0];
      check(row.schema_version === 1, "POSTGRES_SCHEMA_UNSUPPORTED");
      check(
        unseal(row.key_check, `${this.hubId}:key-check`, this.keys).toString() ===
          "hub-key-check-v1",
        "CREDENTIAL_DECRYPTION_FAILED",
      );
      this.revision = row.revision;
      const state = (row.state as State | null) ?? undefined;
      await this.lifecycle.preflight?.(state);
      await this.sql("COMMIT");
      return state;
    } catch (error) {
      await this.client.query("ROLLBACK").catch(() => {});
      throw error;
    }
  }
  private async refreshValidatedState(): Promise<void> {
    const row = (
      await this.sql(
        "SELECT schema_version,key_check,revision,state FROM devtunnel_hub.hubs WHERE hub_id=$1",
        [this.hubId],
      )
    ).rows[0];
    check(row.schema_version === 1, "POSTGRES_SCHEMA_UNSUPPORTED");
    check(
      unseal(row.key_check, `${this.hubId}:key-check`, this.keys).toString() ===
        "hub-key-check-v1",
      "CREDENTIAL_DECRYPTION_FAILED",
    );
    this.revision = row.revision;
    await this.lifecycle.preflight?.((row.state as State | null) ?? undefined);
  }
  private async registerDeployment(): Promise<void> {
    const row = (
      await this.sql(
        `INSERT INTO devtunnel_hub.deployments(hub_id,deployment_id,status)
         VALUES($1,$2,'standby')
         ON CONFLICT(hub_id,deployment_id) DO UPDATE
         SET deployment_id=EXCLUDED.deployment_id
         RETURNING generation,status,
           heartbeat > clock_timestamp() - interval '${LEADER_LEASE_SECONDS} seconds' AS fresh`,
        [this.hubId, this.deploymentId],
      )
    ).rows[0];
    check(row.status !== "failed", "DEPLOYMENT_PREVIOUSLY_FAILED");
    this.generation = String(row.generation);
    this.deploymentStatus = row.status;
    this.deploymentFresh = row.fresh === true;
  }
  private async proposeLeadership(): Promise<boolean> {
    const result = await this.sql(
      `INSERT INTO devtunnel_hub.leadership(hub_id,desired_generation)
       VALUES($1,$2::bigint)
       ON CONFLICT(hub_id) DO UPDATE
       SET desired_generation=EXCLUDED.desired_generation,requested_at=clock_timestamp()
       WHERE EXCLUDED.desired_generation > devtunnel_hub.leadership.desired_generation
          OR NOT EXISTS (
            SELECT 1 FROM devtunnel_hub.deployments d
            WHERE d.hub_id=$1 AND d.generation=devtunnel_hub.leadership.desired_generation
              AND d.status <> 'failed'
              AND d.heartbeat > clock_timestamp() - interval '${LEADER_LEASE_SECONDS} seconds')
       RETURNING desired_generation`,
      [this.hubId, this.generation],
    );
    const selected =
      result.rowCount === 1 &&
      String(result.rows[0].desired_generation) === this.generation;
    await this.sql(
      `UPDATE devtunnel_hub.deployments SET status=$3,heartbeat=clock_timestamp()
       WHERE hub_id=$1 AND deployment_id=$2`,
      [this.hubId, this.deploymentId, selected ? "candidate" : "standby"],
    );
    return selected;
  }
  private async activateDeployment(): Promise<void> {
    await this.sql(
      `UPDATE devtunnel_hub.deployments SET status='leader',heartbeat=clock_timestamp()
       WHERE hub_id=$1 AND deployment_id=$2`,
      [this.hubId, this.deploymentId],
    );
    await this.sql(
      `INSERT INTO devtunnel_hub.leadership(hub_id,desired_generation)
       VALUES($1,$2::bigint) ON CONFLICT(hub_id) DO UPDATE
       SET desired_generation=EXCLUDED.desired_generation,requested_at=clock_timestamp()`,
      [this.hubId, this.generation],
    );
    this.lifecycle.onLeadershipState?.("leadership_acquired");
  }
  private async awaitLeadership(): Promise<void> {
    const deadline = Date.now() + LEADER_WAIT_MS;
    this.lifecycle.onLeadershipState?.("waiting_for_leadership");
    while (Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, LEADER_POLL_MS));
      await this.sql(
        `UPDATE devtunnel_hub.deployments SET heartbeat=clock_timestamp()
         WHERE hub_id=$1 AND deployment_id=$2`,
        [this.hubId, this.deploymentId],
      );
      const desired = (
        await this.sql(
          "SELECT desired_generation FROM devtunnel_hub.leadership WHERE hub_id=$1",
          [this.hubId],
        )
      ).rows[0];
      if (String(desired?.desired_generation) !== this.generation) {
        throw new HubError("DEPLOYMENT_SUPERSEDED");
      }
      if (await this.tryLock()) {
        this.acquired = true;
        this.managesDeploymentStatus = true;
        return;
      }
    }
    throw new HubError("LEADERSHIP_TIMEOUT");
  }
  private startMonitor(): void {
    this.timer = setInterval(() => {
      void this.serial(async () => {
        if (this.coordinated) {
          await this.sql(
            `UPDATE devtunnel_hub.deployments SET heartbeat=clock_timestamp()
             WHERE hub_id=$1 AND deployment_id=$2`,
            [this.hubId, this.deploymentId],
          );
          const desired = (
            await this.sql(
              `SELECT l.desired_generation,d.status,
                      d.heartbeat > clock_timestamp() - interval '${LEADER_LEASE_SECONDS} seconds' AS fresh
               FROM devtunnel_hub.leadership l
               JOIN devtunnel_hub.deployments d
                 ON d.hub_id=l.hub_id AND d.generation=l.desired_generation
               WHERE l.hub_id=$1`,
              [this.hubId],
            )
          ).rows[0];
          if (
            desired &&
            String(desired.desired_generation) !== this.generation &&
            desired.status === "candidate" &&
            desired.fresh === true
          ) {
            this.handoffRequested = true;
            clearInterval(this.timer);
            await this.sql(
              `UPDATE devtunnel_hub.deployments SET status='draining',heartbeat=clock_timestamp()
               WHERE hub_id=$1 AND deployment_id=$2`,
              [this.hubId, this.deploymentId],
            );
            this.lifecycle.onLeadershipState?.("draining_for_handoff");
            this.lifecycle.onTakeover?.();
            return;
          }
        }
        await this.sql("SELECT 1");
      }).catch(() => {});
    }, 2000);
  }
  async open(runDir: string): Promise<void> {
    try {
      await this.client.connect();
      await this.prepareSchema();
      if (this.coordinated) {
        await this.registerDeployment();
      }
      if (await this.tryLock()) {
        this.acquired = true;
        this.managesDeploymentStatus = this.coordinated;
      } else if (!this.coordinated) {
        throw new HubError("HUB_ALREADY_ACTIVE");
      } else {
        check(
          this.deploymentStatus !== "leader" &&
            this.deploymentStatus !== "draining" &&
            !(
              this.deploymentStatus === "candidate" &&
              this.deploymentFresh
            ),
          "HUB_ALREADY_ACTIVE",
        );
        const selected = await this.proposeLeadership();
        if (!selected) throw new HubError("DEPLOYMENT_SUPERSEDED");
        this.managesDeploymentStatus = true;
        this.lifecycle.onLeadershipState?.("leadership_requested");
        await this.awaitLeadership();
      }
      await this.refreshValidatedState();
      await this.sql(
        "UPDATE devtunnel_hub.hubs SET key_check=$2 WHERE hub_id=$1",
        [
          this.hubId,
          seal(
            Buffer.from("hub-key-check-v1"),
            `${this.hubId}:key-check`,
            this.keys,
          ),
        ],
      );
      if (this.coordinated) await this.activateDeployment();
      await privateDirectory(runDir);
      this.directory = await mkdtemp(path.join(runDir, "postgres-data-"));
      // Dedicated connection, never transaction-pooled. Query timeout bounds uncertainty.
      this.startMonitor();
    } catch (error) {
      if (error instanceof HubError) throw error;
      throw new HubError("POSTGRES_UNAVAILABLE");
    }
  }
  async read(): Promise<State | undefined> {
    return this.serial(async () => {
      const result = await this.sql(
        "SELECT state FROM devtunnel_hub.hubs WHERE hub_id=$1",
        [this.hubId],
      );
      const state = result.rows[0].state as State | null;
      const dirty = await this.sql(
        "SELECT session_id FROM devtunnel_hub.credentials WHERE hub_id=$1 AND dirty",
        [this.hubId],
      );
      for (const row of dirty.rows) {
        const session = state?.sessions.find((s) => s.id === row.session_id);
        if (session && session.status !== "removed") {
          session.status = "reauth_required";
          session.desired = false;
          session.error = "CREDENTIAL_CHECKPOINT_INTERRUPTED";
        }
      }
      return state ?? undefined;
    });
  }
  async write(state: State): Promise<void> {
    const snapshot = JSON.stringify(state);
    return this.serial(async () => {
      const result = await this.sql(
        `UPDATE devtunnel_hub.hubs SET state=$2::jsonb,revision=revision+1
        WHERE hub_id=$1 AND revision=$3 RETURNING revision`,
        [this.hubId, snapshot, this.revision],
      );
      if (result.rowCount !== 1) {
        this.lost();
        throw new HubError("POSTGRES_STATE_CONFLICT");
      }
      this.revision = result.rows[0].revision;
    });
  }
  async restore(id: string, home: string): Promise<void> {
    const row = await this.serial(
      async () =>
        (
          await this.sql(
            "SELECT generation,dirty,package FROM devtunnel_hub.credentials WHERE hub_id=$1 AND session_id=$2",
            [this.hubId, id],
          )
        ).rows[0],
    );
    await privateDirectory(home);
    if (row && !row.dirty && row.package)
      await unpackHome(
        unseal(row.package, `${this.hubId}:${id}:${row.generation}`, this.keys),
        home,
      );
  }
  async beginAuth(id: string): Promise<void> {
    return this.serial(async () => {
      await this.sql(
        `INSERT INTO devtunnel_hub.credentials(hub_id,session_id,dirty)
      VALUES($1,$2,true) ON CONFLICT(hub_id,session_id) DO UPDATE SET dirty=true`,
        [this.hubId, id],
      );
    });
  }
  async checkpoint(id: string, home: string): Promise<void> {
    const packed = await packHome(home);
    return this.serial(async () => {
      const row = (
        await this.sql(
          "SELECT generation FROM devtunnel_hub.credentials WHERE hub_id=$1 AND session_id=$2 AND dirty",
          [this.hubId, id],
        )
      ).rows[0];
      check(row, "CREDENTIAL_CHECKPOINT_NOT_STARTED");
      const generation = (BigInt(row.generation) + 1n).toString();
      await this.sql(
        "UPDATE devtunnel_hub.credentials SET generation=$3,package=$4,dirty=false WHERE hub_id=$1 AND session_id=$2",
        [
          this.hubId,
          id,
          generation,
          seal(packed, `${this.hubId}:${id}:${generation}`, this.keys),
        ],
      );
    });
  }
  async markFailed(): Promise<void> {
    if (
      !this.coordinated ||
      !this.deploymentId ||
      !this.managesDeploymentStatus ||
      this.closing ||
      this.broken
    ) {
      return;
    }
    await this.serial(async () => {
      await this.sql(
        `UPDATE devtunnel_hub.deployments SET status='failed',heartbeat=clock_timestamp()
         WHERE hub_id=$1 AND deployment_id=$2`,
        [this.hubId, this.deploymentId],
      );
      this.deploymentFailed = true;
    });
  }
  async close(): Promise<void> {
    clearInterval(this.timer);
    await this.queue;
    if (
      this.coordinated &&
      this.deploymentId &&
      this.managesDeploymentStatus &&
      !this.broken
    ) {
      await this.sql(
        `UPDATE devtunnel_hub.deployments
         SET status=$3,heartbeat=clock_timestamp()
         WHERE hub_id=$1 AND deployment_id=$2`,
        [
          this.hubId,
          this.deploymentId,
          this.deploymentFailed
            ? "failed"
            : this.handoffRequested
              ? "draining"
              : "standby",
        ],
      ).catch(() => {});
    }
    this.closing = true;
    // End the connection only after the manager has stopped all external workers.
    await this.client.end().catch(() => {});
    this.acquired = false;
    if (this.directory) {
      await rm(this.directory, { recursive: true, force: true });
      this.directory = "";
    }
  }
}

// Local-only integration of the standalone images using synthetic destinations.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { setTimeout as delay } from "node:timers/promises";
import { trustedExecutable } from "./trusted-executable.mjs";

const prefix = `toolkit-proxy-smoke-${process.pid}`;
const network = `${prefix}-net`,
  service = `${prefix}-service`;
const squid = `${prefix}-squid`,
  route = `${prefix}-route`;
const serviceImage =
  process.env.PROXY_SERVICE_TEST_IMAGE ?? "devtunnel-toolkit-hub:web-local";
const squidImage =
  process.env.SQUID_TEST_IMAGE ?? "devtunnel-toolkit-squid:hardening-local";
const routeImage =
  process.env.ROUTE_PROXY_TEST_IMAGE ??
  "devtunnel-toolkit-route-proxy:hardening-local";
const dockerExecutable = trustedExecutable("docker");
function docker(args, ok = true) {
  const result = spawnSync(dockerExecutable, args, {
    encoding: "utf8",
    timeout: 30000,
  });
  if (ok && result.status !== 0)
    throw new Error(
      `docker ${args.slice(0, 2).join(" ")} failed: ${result.stderr || result.stdout}`,
    );
  return result;
}
async function ready(args) {
  for (let i = 0; i < 40; i++) {
    if (docker(args, false).status === 0) return;
    await delay(200);
  }
  throw new Error("Proxy readiness timeout");
}
const fetchThrough = (host, port, destination, pathname) => `
  const assert=require('node:assert/strict'),http=require('node:http');
  const req=http.get({hostname:${JSON.stringify(host)},port:${port},path:${JSON.stringify(`http://${destination}:18080/${pathname}`)},
    headers:{Host:${JSON.stringify(`${destination}:18080`)}}},res=>{
      let body='';res.on('data',chunk=>body+=chunk);res.on('end',()=>{assert.equal(res.statusCode,200);assert.equal(body,'synthetic-proxy-success');});
    });req.setTimeout(5000,()=>req.destroy(new Error('Timeout')));req.on('error',error=>{console.error(error.message);process.exitCode=1;});`;
try {
  docker(["network", "create", "--internal", network]);
  docker([
    "run",
    "--detach",
    "--name",
    service,
    "--network",
    network,
    "--network-alias",
    "service.example.com",
    "--network-alias",
    "direct.example.com",
    "--read-only",
    "--cap-drop",
    "ALL",
    "--security-opt",
    "no-new-privileges",
    "--entrypoint",
    "node",
    serviceImage,
    "-e",
    'require("node:http").createServer((q,s)=>s.end("synthetic-proxy-success")).listen(18080,"0.0.0.0")',
  ]);
  docker([
    "run",
    "--detach",
    "--name",
    squid,
    "--network",
    network,
    "--cap-drop",
    "ALL",
    "--security-opt",
    "no-new-privileges",
    "--env",
    "SQUID_LISTEN_ADDRESS=0.0.0.0",
    "--env",
    "SQUID_HTTP_PORT=3149",
    squidImage,
  ]);
  await ready(["exec", squid, "squid", "-k", "check", "-f", "/tmp/squid.conf"]);
  docker([
    "exec",
    service,
    "node",
    "-e",
    fetchThrough(squid, 3149, "service.example.com", "standalone"),
  ]);
  docker([
    "run",
    "--detach",
    "--name",
    route,
    "--network",
    network,
    "--cap-drop",
    "ALL",
    "--security-opt",
    "no-new-privileges",
    "--env",
    "ROUTE_PROXY_PORT=8889",
    "--env",
    `ROUTE_PROXY_UPSTREAM_HOST=${squid}`,
    "--env",
    "ROUTE_PROXY_UPSTREAM_PORT=3149",
    "--env",
    "ROUTE_PROXY_HOSTS=service.example.com",
    routeImage,
  ]);
  const throughRoute = (destination, pathname) => [
    "run",
    "--rm",
    "--network",
    `container:${route}`,
    "--read-only",
    "--cap-drop",
    "ALL",
    "--security-opt",
    "no-new-privileges",
    "--entrypoint",
    "node",
    serviceImage,
    "-e",
    fetchThrough("127.0.0.1", 8889, destination, pathname),
  ];
  await ready(throughRoute("service.example.com", "routed"));
  docker(throughRoute("direct.example.com", "direct"));
  docker(["exec", route, "/usr/local/bin/tinyproxy-entrypoint", "healthcheck"]);
  assert.notEqual(
    docker(
      [
        "exec",
        "--env",
        "ROUTE_PROXY_PORT=8890",
        route,
        "/usr/local/bin/tinyproxy-entrypoint",
        "healthcheck",
      ],
      false,
    ).status,
    0,
  );
  const access = docker([
    "exec",
    squid,
    "cat",
    "/var/log/squid/access.log",
  ]).stdout;
  assert.match(access, /service\.example\.com:18080\/standalone/);
  assert.match(access, /service\.example\.com:18080\/routed/);
  assert.doesNotMatch(access, /direct\.example\.com:18080\/direct/);
  for (const container of [squid, route]) {
    const info = JSON.parse(docker(["inspect", container]).stdout)[0];
    assert.ok(
      info.Config.User &&
        info.Config.User !== "root" &&
        info.Config.User !== "0",
    );
    assert.equal(info.HostConfig.Privileged, false);
    assert.deepEqual(info.HostConfig.CapDrop, ["ALL"]);
    assert.equal(
      docker(["exec", container, "dpkg", "--audit"]).stdout.trim(),
      "",
    );
  }
  console.log(
    "PASS standalone Squid HTTP, selective Tinyproxy upstream/direct routing, custom ports, real proxy audit, non-root and intact package inventory on an isolated Docker network",
  );
} catch (error) {
  for (const name of [squid, route]) {
    const logs = docker(["logs", name], false);
    console.error(logs.stdout + logs.stderr);
  }
  throw error;
} finally {
  for (const name of [route, squid, service])
    docker(["rm", "--force", name], false);
  docker(["network", "rm", network], false);
}

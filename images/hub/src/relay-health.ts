import { type Duplex } from "node:stream";
import { TunnelRelayTunnelClient, type TunnelConnectionOptions } from "@microsoft/dev-tunnels-connections";
import { type Tunnel } from "@microsoft/dev-tunnels-contracts";
import { type TunnelManagementClient } from "@microsoft/dev-tunnels-management";
import { CancellationTokenSource, type CancellationToken } from "@microsoft/dev-tunnels-ssh";

interface ProbeClient {
  acceptLocalConnectionsForForwardedPorts: boolean;
  connect(tunnel: Tunnel, options?: TunnelConnectionOptions, token?: CancellationToken): Promise<void>;
  waitForForwardedPort(port: number, token?: CancellationToken): Promise<void>;
  connectToForwardedPort(port: number, token?: CancellationToken): Promise<Duplex | null>;
  dispose(): Promise<void>;
}

function proxyResponse(stream: Duplex, socks: boolean): Promise<void> {
  return new Promise((resolve, reject) => {
    let received = Buffer.alloc(0);
    const finish = (valid: boolean) => {
      stream.off("data", data); stream.off("error", failed); stream.off("end", failed); stream.off("close", failed);
      if (valid) resolve(); else reject(new Error("RECONNECT_DATA_PATH_FAILED"));
    };
    const failed = () => finish(false);
    const data = (chunk: Buffer) => {
      received = Buffer.concat([received, chunk.subarray(0, 4096 - received.length)]);
      if (socks) { if (received.length >= 2) finish(received[0] === 5 && received[1] === 0); }
      else {
        const end = received.indexOf("\r\n");
        if (end >= 0) finish(/^HTTP\/1\.[01] [1-5][0-9]{2}(?: |\r|$)/.test(received.subarray(0, end).toString("ascii")));
        else if (received.length === 4096) finish(false);
      }
    };
    stream.on("data", data); stream.once("error", failed); stream.once("end", failed); stream.once("close", failed);
    stream.write(socks ? Buffer.from([5, 1, 0]) : Buffer.from("OPTIONS * HTTP/1.1\r\nHost: proxy.invalid\r\nConnection: close\r\n\r\n"));
  });
}

/** Probe exactly the candidate host through the real relay, without publishing sockets or contacting a destination. */
export async function verifyHostedProxy(management: TunnelManagementClient, tunnel: Tunnel, hostId: string,
  ports: { port: number; socks: boolean }[],
  createClient: () => ProbeClient = () => new TunnelRelayTunnelClient(management, () => {}), timeoutMs = 15000): Promise<void> {
  const client = createClient();
  client.acceptLocalConnectionsForForwardedPorts = false;
  const cancellation = new CancellationTokenSource();
  let timeout: NodeJS.Timeout | undefined;
  const expired = new Promise<never>((_, reject) => { timeout = setTimeout(() => { cancellation.cancel(); reject(new Error("RECONNECT_DATA_PATH_FAILED")); }, timeoutMs); });
  try {
    await Promise.race([(async () => {
      await client.connect(tunnel, { hostId, enableRetry: false, enableReconnect: false }, cancellation.token);
      for (const { port, socks } of ports) {
        await client.waitForForwardedPort(port, cancellation.token);
        const stream = await client.connectToForwardedPort(port, cancellation.token);
        if (!stream) throw new Error("RECONNECT_DATA_PATH_FAILED");
        try { await proxyResponse(stream, socks); }
        finally { stream.destroy(); }
      }
    })(), expired]);
  } finally {
    clearTimeout(timeout); cancellation.cancel();
    try { await client.dispose(); } finally { cancellation.dispose(); }
  }
}

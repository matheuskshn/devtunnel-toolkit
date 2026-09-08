import assert from 'node:assert/strict';
import { once } from 'node:events';
import net from 'node:net';
import { duplexPair } from 'node:stream';
import test from 'node:test';
import { NodeStream, SshClientSession, SshServerSession, SshSessionConfiguration } from '@microsoft/dev-tunnels-ssh';
import { PortForwardingService } from '@microsoft/dev-tunnels-ssh-tcp';
import { ManagementApiVersions, TunnelManagementHttpClient } from '@microsoft/dev-tunnels-management';
import { MappedTunnelHost } from '../dist/mapped-host.js';

function createHost(port) {
  const management = new TunnelManagementHttpClient('hub-local-test',ManagementApiVersions.Version20230927preview,
    undefined,'https://management.example.com/',undefined,async()=>{throw Error('No cloud API allowed');});
  management.enableEventsReporting=false;
  return new MappedTunnelHost(management,port);
}
async function listener(t,label) {
  const sockets=new Set();
  const server=net.createServer(s=>{sockets.add(s);s.on('error',()=>{});s.on('close',()=>sockets.delete(s));s.on('data',d=>s.write(`${label}:${d}`));});
  server.listen(0,'127.0.0.1'); await once(server,'listening');
  t.after(()=>{for(const s of sockets)s.destroy();server.close();});
  return server.address().port;
}
async function session(t,host) {
  // Unencrypted, in-memory SSH is TEST ONLY. The production relay uses SDK defaults.
  const cfg=new SshSessionConfiguration(false);cfg.addService(PortForwardingService);
  const server=new SshServerSession(cfg),client=new SshClientSession(cfg);
  const [a,b]=duplexPair();
  const serverPfs=server.activateService(PortForwardingService),clientPfs=client.activateService(PortForwardingService);
  clientPfs.acceptLocalConnectionsForForwardedPorts=false;
  client.onRequest(e=>{e.isAuthorized=['tcpip-forward','cancel-tcpip-forward'].includes(e.request.requestType);});
  t.after(async()=>{await host.dispose();server.dispose();client.dispose();a.destroy();b.destroy();});
  await Promise.all([server.connect(new NodeStream(a)),client.connect(new NodeStream(b))]);
  await host.forwardPort(serverPfs,{portNumber:3140});return {serverPfs,clientPfs};
}
async function exchange(pfs,payload='ping') {
  const stream=await pfs.connectToForwardedPort(3140);
  try { const result=once(stream,'data');stream.write(payload);return String((await result)[0]); }
  finally{stream.destroy();}
}
test('two private sessions publish 3140, reach separate listeners and stop independently',async t=>{
  const a=createHost(await listener(t,'user-a')),b=createHost(await listener(t,'user-b'));
  const [sa,sb]=await Promise.all([session(t,a),session(t,b)]);
  assert.deepEqual(await Promise.all([exchange(sa.clientPfs),exchange(sb.clientPfs)]),['user-a:ping','user-b:ping']);
  await a.dispose();await a.dispose();
  await assert.rejects(a.forwardPort(sa.serverPfs,{portNumber:3140}),/disposed/);
  assert.equal(await exchange(sb.clientPfs),'user-b:ping');
});
test('idempotent registration, direct TCP bypass and additional ports are rejected',async t=>{
  const a=createHost(await listener(t,'user-a'));const {serverPfs,clientPfs}=await session(t,a);
  await a.forwardPort(serverPfs,{portNumber:3140});assert.equal(a.remoteForwarders.size,1);
  await assert.rejects(a.forwardPort(serverPfs,{portNumber:18002}),/only publishes/);
  await assert.rejects(clientPfs.connectToForwardedPort(a.listenerPort));
  await assert.rejects(clientPfs.streamToRemotePort('127.0.0.1',a.listenerPort));
  const b=createHost(await listener(t,'user-b'));t.after(()=>b.dispose());
  await assert.rejects(b.forwardPort(serverPfs,{portNumber:3140}),/different listener/);
});
test('fresh client session after reconnect retains mapping and immutable port refresh',async t=>{
  const host=createHost(await listener(t,'user-a'));
  const original=await session(t,host);const reconnected=await session(t,host);
  assert.equal(await exchange(original.clientPfs),'user-a:ping');
  host.refreshTunnel=async()=>{host.tunnel={ports:[{portNumber:3140}]};return true;};
  await host.refreshPorts();
  assert.equal(await exchange(reconnected.clientPfs),'user-a:ping');
  host.refreshTunnel=async()=>{host.tunnel={ports:[{portNumber:3140},{portNumber:22}]};return true;};
  await assert.rejects(host.refreshPorts(),/POLICY_CHANGED/);
});
test('closed destination fails without affecting another session',async t=>{
  const reservation=net.createServer();reservation.listen(0,'127.0.0.1');await once(reservation,'listening');
  const port=reservation.address().port;await new Promise(resolve=>reservation.close(resolve));
  const broken=await session(t,createHost(port));
  const working=await session(t,createHost(await listener(t,'user-b')));
  await assert.rejects(broken.clientPfs.connectToForwardedPort(3140));
  assert.equal(await exchange(working.clientPfs),'user-b:ping');
});
test('invalid and reserved listeners are rejected',()=>{
  for(const port of [0,22,3140,65536,18001.5,'18001'])assert.throws(()=>createHost(port),TypeError);
});

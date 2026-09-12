import assert from 'node:assert/strict';
import { once } from 'node:events';
import net from 'node:net';
import { duplexPair } from 'node:stream';
import test from 'node:test';
import { NodeStream, SshAlgorithms, SshAuthenticationType, SshClientSession, SshServerSession, SshSessionConfiguration } from '@microsoft/dev-tunnels-ssh';
import { PortForwardingService } from '@microsoft/dev-tunnels-ssh-tcp';
import { ManagementApiVersions, TunnelManagementHttpClient } from '@microsoft/dev-tunnels-management';
import { MappedTunnelHost } from '../dist/mapped-host.js';

class CopyingNodeStream extends NodeStream {
  // Model a wire's byte ownership. duplexPair retains writes by reference while
  // the SDK reuses its unencrypted packet buffer (also during key exchange).
  write(data, cancellation) { return super.write(Buffer.from(data), cancellation); }
}

function createHost(port, proxyPort = 3140, socks) {
  const management = new TunnelManagementHttpClient('hub-local-test',ManagementApiVersions.Version20230927preview,
    undefined,'https://management.example.com/',undefined,async()=>{throw Error('No cloud API allowed');});
  management.enableEventsReporting=false;
  return new MappedTunnelHost(management,port,proxyPort,socks);
}
async function listener(t,label) {
  const sockets=new Set();
  const server=net.createServer(s=>{sockets.add(s);s.on('error',()=>{});s.on('close',()=>sockets.delete(s));s.on('data',d=>s.write(`${label}:${d}`));});
  server.listen(0,'127.0.0.1'); await once(server,'listening');
  t.after(()=>{for(const s of sockets)s.destroy();server.close();});
  return server.address().port;
}
async function session(t,host) {
  // Exercise concurrent channels with the production encrypted SSH defaults,
  // an ephemeral host key pinned by the client, and synthetic client identity.
  const cfg=new SshSessionConfiguration();cfg.addService(PortForwardingService);
  const server=new SshServerSession(cfg),client=new SshClientSession(cfg);
  const hostKey=await SshAlgorithms.publicKey.ecdsaSha2Nistp384.generateKeyPair();
  const hostPublicKey=await hostKey.getPublicKeyBytes();
  server.credentials.publicKeys.push(hostKey);
  client.onAuthenticating(e=>{
    e.authenticationPromise=(async()=>e.authenticationType===SshAuthenticationType.serverPublicKey &&
      hostPublicKey.equals(await e.publicKey.getPublicKeyBytes()) ? {name:'synthetic-mapping-server'} : null)();
  });
  server.onAuthenticating(e=>{
    e.authenticationPromise=Promise.resolve(e.authenticationType===SshAuthenticationType.clientNone &&
      e.username==='synthetic-mapping-client' ? {name:e.username} : null);
  });
  const [a,b]=duplexPair();
  const serverPfs=server.activateService(PortForwardingService),clientPfs=client.activateService(PortForwardingService);
  clientPfs.acceptLocalConnectionsForForwardedPorts=false;
  client.onRequest(e=>{e.isAuthorized=['tcpip-forward','cancel-tcpip-forward'].includes(e.request.requestType);});
  t.after(async()=>{await host.dispose();server.dispose();client.dispose();a.destroy();b.destroy();hostKey.dispose();});
  await Promise.all([server.connect(new CopyingNodeStream(a)),client.connect(new CopyingNodeStream(b))]);
  assert.equal(await client.authenticate({username:'synthetic-mapping-client'}),true);
  await host.forwardPort(serverPfs,{portNumber:host.proxyPort});return {serverPfs,clientPfs};
}
async function exchange(pfs,payload='ping',proxyPort=3140) {
  const stream=await pfs.connectToForwardedPort(proxyPort);
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

test('custom published port reaches isolated listeners and rejects the former default',async t=>{
  const a=createHost(await listener(t,'user-a'),3210),b=createHost(await listener(t,'user-b'),3210);
  const [sa,sb]=await Promise.all([session(t,a),session(t,b)]);
  assert.deepEqual(await Promise.all([exchange(sa.clientPfs,'ping',3210),exchange(sb.clientPfs,'ping',3210)]),['user-a:ping','user-b:ping']);
  await assert.rejects(sa.clientPfs.connectToForwardedPort(3140));
  await assert.rejects(a.forwardPort(sa.serverPfs,{portNumber:3140}),/only publishes/);
  a.refreshTunnel=async()=>{a.tunnel={ports:[{portNumber:3210}]};return true;};
  await a.refreshPorts();assert.equal(await exchange(sa.clientPfs,'again',3210),'user-a:again');
  a.refreshTunnel=async()=>{a.tunnel={ports:[{portNumber:3140}]};return true;};
  await assert.rejects(a.refreshPorts(),/POLICY_CHANGED/);
});

test('invalid custom proxy ports and listener collisions fail closed',()=>{
  for(const port of [0,22,1023,65536,3210.5,'3210',NaN])assert.throws(()=>createHost(18001,port),TypeError);
  assert.throws(()=>createHost(3210,3210),TypeError);
});

test('HTTP and SOCKS ports share a tunnel but remain isolated across two users and reconnect',async t=>{
  const a=createHost(await listener(t,'http-a'),3140,{proxyPort:3180,listenerPort:await listener(t,'socks-a')});
  const b=createHost(await listener(t,'http-b'),3140,{proxyPort:3180,listenerPort:await listener(t,'socks-b')});
  const [sa,sb]=await Promise.all([session(t,a),session(t,b)]);
  await a.forwardPort(sa.serverPfs,{portNumber:3180});await b.forwardPort(sb.serverPfs,{portNumber:3180});
  assert.deepEqual(await Promise.all([exchange(sa.clientPfs),exchange(sb.clientPfs),exchange(sa.clientPfs,'ping',3180),exchange(sb.clientPfs,'ping',3180)]),['http-a:ping','http-b:ping','socks-a:ping','socks-b:ping']);
  for(let i=0;i<10;i++)assert.deepEqual(await Promise.all([
    exchange(sa.clientPfs,`round-${i}`),exchange(sb.clientPfs,`round-${i}`),
    exchange(sa.clientPfs,`round-${i}`,3180),exchange(sb.clientPfs,`round-${i}`,3180),
  ]),[`http-a:round-${i}`,`http-b:round-${i}`,`socks-a:round-${i}`,`socks-b:round-${i}`]);
  await a.forwardPort(sa.serverPfs,{portNumber:3180});assert.equal(a.remoteForwarders.size,2);
  await assert.rejects(sa.clientPfs.streamToRemotePort('127.0.0.1',b.listenerPort));
  const reconnected=await session(t,a);await a.forwardPort(reconnected.serverPfs,{portNumber:3180});
  a.refreshTunnel=async()=>{a.tunnel={ports:[{portNumber:3180},{portNumber:3140}]};return true;};
  await a.refreshPorts();assert.equal(await exchange(reconnected.clientPfs,'again',3180),'socks-a:again');
  a.refreshTunnel=async()=>{a.tunnel={ports:[{portNumber:3140},{portNumber:3140}]};return true;};
  await assert.rejects(a.refreshPorts(),/POLICY_CHANGED/);
  assert.equal(await exchange(sb.clientPfs,'still',3180),'socks-b:still');
});
test('SOCKS mapping validates every port and cannot alias another service',()=>{
  for(const socks of [{proxyPort:3140,listenerPort:18002},{proxyPort:3180,listenerPort:18001},{proxyPort:18001,listenerPort:18002},{proxyPort:3180,listenerPort:3140},{proxyPort:3180,listenerPort:3180},{proxyPort:3180,listenerPort:22},{proxyPort:'3180',listenerPort:18002}])assert.throws(()=>createHost(18001,3140,socks),TypeError);
});

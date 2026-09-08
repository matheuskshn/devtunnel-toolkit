// Runs inside the hardened hub container using synthetic session identities.
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createRequire } from 'node:module';
import { duplexPair } from 'node:stream';
import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import { MappedTunnelHost } from '/opt/hub/dist/mapped-host.js';
const require=createRequire('/opt/hub/package.json');
const {NodeStream,SshClientSession,SshServerSession,SshSessionConfiguration}=require('@microsoft/dev-tunnels-ssh');
const {PortForwardingService}=require('@microsoft/dev-tunnels-ssh-tcp');
const {TunnelManagementHttpClient,ManagementApiVersions}=require('@microsoft/dev-tunnels-management');
const backendAddress=Object.values(os.networkInterfaces()).flat().find(a=>a.family==='IPv4'&&!a.internal)?.address;
assert.ok(backendAddress,'A private Docker bridge address is required');
const sockets=new Set();
const backend=http.createServer((_req,res)=>res.end('synthetic-backend-ok'));
backend.on('connection',s=>{sockets.add(s);s.on('close',()=>sockets.delete(s));});
backend.listen(18080,'0.0.0.0');await once(backend,'listening');
const resources=[];
async function client(listener) {
  const management=new TunnelManagementHttpClient('hub-local-proxy-test',ManagementApiVersions.Version20230927preview,
    undefined,'https://management.example.com/',undefined,async()=>{throw Error('No relay in local test');});
  management.enableEventsReporting=false;
  const host=new MappedTunnelHost(management,listener);
  const cfg=new SshSessionConfiguration(false);cfg.addService(PortForwardingService);
  const server=new SshServerSession(cfg),client=new SshClientSession(cfg);
  const [a,b]=duplexPair();
  const serverPfs=server.activateService(PortForwardingService),clientPfs=client.activateService(PortForwardingService);
  clientPfs.acceptLocalConnectionsForForwardedPorts=false;
  client.onRequest(e=>{e.isAuthorized=['tcpip-forward','cancel-tcpip-forward'].includes(e.request.requestType);});
  await Promise.all([server.connect(new NodeStream(a)),client.connect(new NodeStream(b))]);
  await host.forwardPort(serverPfs,{portNumber:3140});
  resources.push(async()=>{await host.dispose();server.dispose();client.dispose();a.destroy();b.destroy();});
  return {host,pfs:clientPfs};
}
async function request(pfs,request) {
  const stream=await pfs.connectToForwardedPort(3140);
  let output='';stream.on('data',chunk=>output+=chunk);stream.on('error',()=>{});
  const end=once(stream,'end');stream.write(request);await end;stream.destroy();return output;
}
try {
  const [a,b]=await Promise.all([client(18001),client(18002)]);
  const httpRequest='GET http://service.example.com:18080/private/path?token=synthetic-query-secret HTTP/1.1\r\nHost: service.example.com:18080\r\nAuthorization: Bearer synthetic-header-secret\r\nConnection: close\r\n\r\n';
  const results=await Promise.all([request(a.pfs,httpRequest),request(b.pfs,httpRequest)]);
  assert.ok(results.every(r=>r.startsWith('HTTP/1.1 200')&&r.includes('synthetic-backend-ok')));
  const denied=await request(a.pfs,'CONNECT denied.example.com:443 HTTP/1.1\r\nHost: denied.example.com:443\r\nConnection: close\r\n\r\n');
  assert.match(denied,/^HTTP\/1\.1 403/);
  const portDenied=await request(a.pfs,'CONNECT service.example.com:22 HTTP/1.1\r\nHost: service.example.com:22\r\nConnection: close\r\n\r\n');
  assert.match(portDenied,/^HTTP\/1\.1 403/);
  // CONNECT carries arbitrary TCP bytes. No TLS decryption/inspection is added.
  const stream=await b.pfs.connectToForwardedPort(3140);
  let response=once(stream,'data');stream.write('CONNECT service.example.com:18080 HTTP/1.1\r\nHost: service.example.com:18080\r\n\r\n');
  assert.match(String((await response)[0]),/^HTTP\/1\.1 200/);
  response=once(stream,'data');stream.write('GET / HTTP/1.1\r\nHost: service.example.com\r\nConnection: close\r\n\r\n');
  assert.match(String((await response)[0]),/synthetic-backend-ok/);stream.destroy();
  await a.host.dispose();
  assert.match(await request(b.pfs,httpRequest),/^HTTP\/1\.1 200/);
  // Listeners must never be reachable via the container's non-loopback interface.
  await assert.rejects(new Promise((resolve,reject)=>{
    const socket=net.connect(18001,backendAddress);socket.once('connect',()=>{socket.destroy();resolve();});socket.once('error',reject);
  }));
  console.log('PASS two SDK sessions -> shared Squid, HTTP/CONNECT, deny rules, listener isolation');
} finally {
  await Promise.all(resources.map(dispose=>dispose()));
  for(const socket of sockets)socket.destroy();backend.close();
}

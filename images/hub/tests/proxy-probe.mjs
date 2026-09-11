// Runs inside the hardened hub container using synthetic session identities.
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createRequire } from 'node:module';
import { duplexPair } from 'node:stream';
import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import { MappedTunnelHost } from '/opt/hub/dist/mapped-host.js';
import { SocksProxy } from '/opt/hub/dist/socks.js';
const require=createRequire('/opt/hub/package.json');
const {NodeStream,SshAlgorithms,SshAuthenticationType,SshClientSession,SshServerSession,SshSessionConfiguration}=require('@microsoft/dev-tunnels-ssh');
const {PortForwardingService}=require('@microsoft/dev-tunnels-ssh-tcp');
const {TunnelManagementHttpClient,ManagementApiVersions}=require('@microsoft/dev-tunnels-management');
class CopyingNodeStream extends NodeStream {
  // A wire owns written bytes; duplexPair otherwise retains the SDK's mutable
  // packet buffer by reference, corrupting concurrent/key-exchange frames.
  write(data,cancellation){return super.write(Buffer.from(data),cancellation);}
}
const backendAddress=Object.values(os.networkInterfaces()).flat().find(a=>a.family==='IPv4'&&!a.internal)?.address;
assert.ok(backendAddress,'A private Docker bridge address is required');
const sockets=new Set();
const backend=http.createServer((_req,res)=>res.end('synthetic-backend-ok'));
backend.on('connection',s=>{sockets.add(s);s.on('close',()=>sockets.delete(s));});
backend.listen(18080,'0.0.0.0');await once(backend,'listening');
const resources=[];
async function client(listener, socksListener) {
  const management=new TunnelManagementHttpClient('hub-local-proxy-test',ManagementApiVersions.Version20230927preview,
    undefined,'https://management.example.com/',undefined,async()=>{throw Error('No relay in local test');});
  management.enableEventsReporting=false;
  const socks=new SocksProxy({listenPort:socksListener,proxyPort:listener});await socks.listen();
  resources.push(()=>socks.close());
  const host=new MappedTunnelHost(management,listener,3140,{proxyPort:3180,listenerPort:socksListener});
  const cfg=new SshSessionConfiguration();cfg.addService(PortForwardingService);
  const server=new SshServerSession(cfg),client=new SshClientSession(cfg);
  const hostKey=await SshAlgorithms.publicKey.ecdsaSha2Nistp384.generateKeyPair();
  const hostPublicKey=await hostKey.getPublicKeyBytes();
  server.credentials.publicKeys.push(hostKey);
  client.onAuthenticating(e=>{
    e.authenticationPromise=(async()=>e.authenticationType===SshAuthenticationType.serverPublicKey &&
      hostPublicKey.equals(await e.publicKey.getPublicKeyBytes()) ? {name:'synthetic-proxy-server'} : null)();
  });
  server.onAuthenticating(e=>{
    e.authenticationPromise=Promise.resolve(e.authenticationType===SshAuthenticationType.clientNone &&
      e.username==='synthetic-proxy-client' ? {name:e.username} : null);
  });
  const [a,b]=duplexPair();
  resources.push(async()=>{await host.dispose();server.dispose();client.dispose();a.destroy();b.destroy();hostKey.dispose();});
  const serverPfs=server.activateService(PortForwardingService),clientPfs=client.activateService(PortForwardingService);
  clientPfs.acceptLocalConnectionsForForwardedPorts=false;
  client.onRequest(e=>{e.isAuthorized=['tcpip-forward','cancel-tcpip-forward'].includes(e.request.requestType);});
  await Promise.all([server.connect(new CopyingNodeStream(a)),client.connect(new CopyingNodeStream(b))]);
  assert.equal(await client.authenticate({username:'synthetic-proxy-client'}),true);
  await host.forwardPort(serverPfs,{portNumber:3140});
  await host.forwardPort(serverPfs,{portNumber:3180});
  return {host,pfs:clientPfs};
}
async function request(pfs,request) {
  const stream=await pfs.connectToForwardedPort(3140);
  let output='';stream.on('data',chunk=>output+=chunk);stream.on('error',()=>{});
  const end=once(stream,'end');stream.write(request);await end;stream.destroy();return output;
}
try {
  const [a,b]=await Promise.all([client(18001,19001),client(18002,19002)]);
  const httpRequest='GET http://service.example.com:18080/private/path?token=synthetic-query-secret HTTP/1.1\r\nHost: service.example.com:18080\r\nAuthorization: Bearer synthetic-header-secret\r\nConnection: close\r\n\r\n';
  const results=await Promise.all([request(a.pfs,httpRequest),request(b.pfs,httpRequest)]);
  assert.ok(results.every(r=>r.startsWith('HTTP/1.1 200')&&r.includes('synthetic-backend-ok')));
  const other=await request(a.pfs,'GET http://other.example.com:18080/ HTTP/1.1\r\nHost: other.example.com:18080\r\nConnection: close\r\n\r\n');
  assert.match(other,process.env.HUB_ALLOW_ALL_DOMAINS==='true'?/^HTTP\/1\.1 200/:/^HTTP\/1\.1 403/);
  for(const destination of ['127.0.0.1','0.0.0.0','169.254.169.254']){
    const denied=await request(a.pfs,`GET http://${destination}/ HTTP/1.1\r\nHost: ${destination}\r\nConnection: close\r\n\r\n`);
    assert.match(denied,/^HTTP\/1\.1 403/);
  }
  const portDenied=await request(a.pfs,'CONNECT service.example.com:22 HTTP/1.1\r\nHost: service.example.com:22\r\nConnection: close\r\n\r\n');
  assert.match(portDenied,/^HTTP\/1\.1 403/);
  // CONNECT carries arbitrary TCP bytes. No TLS decryption/inspection is added.
  const stream=await b.pfs.connectToForwardedPort(3140);
  let response=once(stream,'data');stream.write('CONNECT service.example.com:18080 HTTP/1.1\r\nHost: service.example.com:18080\r\n\r\n');
  assert.match(String((await response)[0]),/^HTTP\/1\.1 200/);
  response=once(stream,'data');stream.write('GET / HTTP/1.1\r\nHost: service.example.com\r\nConnection: close\r\n\r\n');
  assert.match(String((await response)[0]),/synthetic-backend-ok/);stream.destroy();
  // The SOCKS adapter must use this same real Squid ACL/audit path, never direct egress.
  async function socksRequest(pfs, destination, port, allowed) {
    const stream=await pfs.connectToForwardedPort(3180);
    const iterator=stream.iterator({destroyOnReturn:false});let buffer=Buffer.alloc(0);
    const read=async length=>{while(buffer.length<length){const part=await iterator.next();assert.ok(!part.done);buffer=Buffer.concat([buffer,part.value]);}const out=buffer.subarray(0,length);buffer=buffer.subarray(length);return out;};
    try {
      stream.write(Buffer.from([5,1,0]));assert.deepEqual(await read(2),Buffer.from([5,0]));
      const host=Buffer.from(destination);const target=Buffer.alloc(2);target.writeUInt16BE(port);
      stream.write(Buffer.concat([Buffer.from([5,1,0,3,host.length]),host,target]));
      const reply=await read(10);assert.equal(reply[0],5);assert.equal(reply[1],allowed?0:2);
      if(allowed){stream.write('GET / HTTP/1.1\r\nHost: service.example.com\r\nConnection: close\r\n\r\n');
        let output=buffer.toString();for await(const part of iterator)output+=part.toString();assert.match(output,/synthetic-backend-ok/);}
    } finally {stream.destroy();}
  }
  await Promise.all([socksRequest(a.pfs,'service.example.com',18080,true),socksRequest(b.pfs,'service.example.com',18080,true)]);
  await socksRequest(a.pfs,'other.example.com',18080,process.env.HUB_ALLOW_ALL_DOMAINS==='true');
  for(const destination of ['127.0.0.1','0.0.0.0','169.254.169.254'])await socksRequest(a.pfs,destination,18080,false);
  await socksRequest(a.pfs,'service.example.com',22,false);
  await a.host.dispose();
  assert.match(await request(b.pfs,httpRequest),/^HTTP\/1\.1 200/);
  // Listeners must never be reachable via the container's non-loopback interface.
  await assert.rejects(new Promise((resolve,reject)=>{
    const socket=net.connect(18001,backendAddress);socket.once('connect',()=>{socket.destroy();resolve();});socket.once('error',reject);
  }));
  await assert.rejects(new Promise((resolve,reject)=>{
    const socket=net.connect(19001,backendAddress);socket.once('connect',()=>{socket.destroy();resolve();});socket.once('error',reject);
  }));
  console.log('PASS two SDK sessions -> shared Squid, HTTP + SOCKS5 CONNECT, deny rules, DNS, listener isolation');
} finally {
  await Promise.all(resources.map(dispose=>dispose()));
  for(const socket of sockets)socket.destroy();backend.close();
}

import test from 'node:test';
import assert from 'node:assert/strict';
import {Duplex} from 'node:stream';
import {verifyHostedProxy} from '../dist/relay-health.js';

function fixture(response) {
  const requests=[],streams=[];let options,disposed=false;
  const client={acceptLocalConnectionsForForwardedPorts:true,
    connect:async(_tunnel,value)=>{options=value;},waitForForwardedPort:async()=>{},
    connectToForwardedPort:async port=>{
      const stream=new Duplex({read(){},write(chunk,_encoding,done){requests.push({port,bytes:Buffer.from(chunk)});done();if(response!==null)this.push(response);}});
      streams.push(stream);return stream;
    },dispose:async()=>{disposed=true;for(const stream of streams)stream.destroy();}};
  return {client,requests,options:()=>options,disposed:()=>disposed};
}
test('candidate HTTP probe targets the exact host, uses no local sockets and accepts a policy response',async()=>{
  const f=fixture(Buffer.from('HTTP/1.1 403 Forbidden\r\n'+ 'x'.repeat(10000)));
  await verifyHostedProxy({}, {}, 'candidate-host', [{port:3140,socks:false}],()=>f.client);
  assert.equal(f.options().hostId,'candidate-host');assert.equal(f.options().enableReconnect,false);
  assert.equal(f.client.acceptLocalConnectionsForForwardedPorts,false);assert.equal(f.disposed(),true);
  assert.equal(f.requests[0].port,3140);assert.match(f.requests[0].bytes.toString(),/^OPTIONS \* HTTP\/1.1\r\n/);
  assert.doesNotMatch(f.requests[0].bytes.toString(),/Authorization|CONNECT/);
});
test('candidate SOCKS probe sends a greeting without a destination address',async()=>{
  const f=fixture(Buffer.from([5,0]));
  await verifyHostedProxy({}, {}, 'candidate-host', [{port:3180,socks:true}],()=>f.client);
  assert.deepEqual(f.requests[0].bytes,Buffer.from([5,1,0]));assert.equal(f.disposed(),true);
});
test('non-proxy response fails readiness instead of accepting an open TCP stream',async()=>{
  const f=fixture(Buffer.from('arbitrary bytes\r\n'));
  await assert.rejects(verifyHostedProxy({}, {}, 'candidate-host', [{port:3140,socks:false}],()=>f.client),/RECONNECT_DATA_PATH_FAILED/);
  assert.equal(f.disposed(),true);
});
test('a silent proxy has a bounded readiness deadline and disposes its client',async()=>{
  const f=fixture(null);
  await assert.rejects(verifyHostedProxy({}, {}, 'candidate-host', [{port:3140,socks:false}],()=>f.client,50),/RECONNECT_DATA_PATH_FAILED/);
  assert.equal(f.disposed(),true);
});

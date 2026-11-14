import test from 'node:test';
import assert from 'node:assert/strict';
import {relayDiagnosticCode,isRelayDiagnosticCode,reportRelayDiagnostic} from '../dist/relay-diagnostics.js';

test('projects nested connection failures without copying provider messages', () => {
  const secret='fixture-private-credential';
  const cases=[
    [{message:'Unexpected server response: 429 https://private.test/'+secret},'STARTUP',undefined,'RELAY_STARTUP_HTTP_429'],
    [{cause:{code:'EAI_AGAIN',message:secret}},'STARTUP',undefined,'RELAY_STARTUP_EAI_AGAIN'],
    [{errorContext:{statusCode:503},message:'Service temporarily unavailable (503)'},'STARTUP',7,'RELAY_STARTUP_HTTP_503'],
    [{message:'Rate limit exceeded (429). Too many requests in a given amount of time.'},'DISCONNECT',7,'RELAY_DISCONNECT_HTTP_429'],
    [{response:{status:403},message:secret},'STARTUP',undefined,'RELAY_STARTUP_HTTP_403'],
    [{message:'No hosts are currently accepting connections for the tunnel.'},'DATA_PATH',undefined,'RELAY_DATA_PATH_NO_HOST'],
    [undefined,'DISCONNECT',12,'RELAY_DISCONNECT_SSH_12'],
    [{code:secret,message:secret},'STARTUP',undefined,'RELAY_STARTUP_UNKNOWN'],
  ];
  for(const [error,stage,reason,expected]of cases){
    const code=relayDiagnosticCode(error,stage,reason);
    assert.equal(code,expected);
    assert(isRelayDiagnosticCode(code));
    assert(!code.includes(secret));
  }
  const circular={message:secret};circular.cause=circular;
  assert.equal(relayDiagnosticCode(circular,'STARTUP'),'RELAY_STARTUP_UNKNOWN');
  assert(!isRelayDiagnosticCode('RELAY_STARTUP_HTTP_200\n'+secret));
  assert(!isRelayDiagnosticCode('RELAY_STARTUP_private_identity'));
});

test('sends only the projected code and tolerates a lost IPC channel', async () => {
  const originalSend=process.send,originalConnected=process.connected;
  try{
    const messages=[];
    process.connected=true;
    process.send=(message,callback)=>{messages.push(message);callback();return true;};
    await reportRelayDiagnostic({code:'ECONNRESET',message:'fixture-private-credential'},'STARTUP');
    assert.deepEqual(messages,[{type:'diagnostic',code:'RELAY_STARTUP_ECONNRESET'}]);
    process.send=()=>{throw new Error('fixture-private-credential');};
    await reportRelayDiagnostic(new Error('fixture-private-credential'),'STARTUP');
    process.connected=false;
    await reportRelayDiagnostic(new Error('fixture-private-credential'),'STARTUP');
  }finally{
    if(originalSend===undefined)delete process.send;else process.send=originalSend;
    if(originalConnected===undefined)delete process.connected;else process.connected=originalConnected;
  }
});

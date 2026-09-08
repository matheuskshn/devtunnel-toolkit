import assert from 'node:assert/strict';
import test from 'node:test';
import {waitSecretService} from '../dist/processes.js';

const running = () => ({exitCode:null,signalCode:null});
test('keyring readiness waits for its owner without auto-activating another daemon', async () => {
  let calls = 0;
  await waitSecretService({},running(),new AbortController().signal,{query:async (file,args) => {
    assert.equal(file,'dbus-send');
    assert.ok(args.includes('--dest=org.freedesktop.DBus'));
    assert.ok(args.includes('org.freedesktop.DBus.NameHasOwner'));
    assert.ok(args.includes('string:org.freedesktop.secrets'));
    assert.ok(!args.includes('--dest=org.freedesktop.secrets'));
    return `method return\n   boolean ${++calls > 1}\n`;
  }});
  assert.equal(calls,2);
});
test('keyring readiness is bounded and fails closed on exit, cancellation or malformed replies', async () => {
  const controller = new AbortController();
  await assert.rejects(waitSecretService({},running(),controller.signal,
    {timeout:10,query:async ()=>'boolean false'}),/SESSION_SERVICE_TIMEOUT/);
  await assert.rejects(waitSecretService({},{exitCode:1,signalCode:null},controller.signal),/SESSION_SERVICE_FAILED/);
  await assert.rejects(waitSecretService({},running(),controller.signal,
    {query:async ()=>'unexpected reply'}),/SESSION_SERVICE_INVALID_REPLY/);
  controller.abort();
  await assert.rejects(waitSecretService({},running(),controller.signal),/CANCELLED_OR_TIMEOUT/);
  const child = running();
  await assert.rejects(waitSecretService({},child,new AbortController().signal,
    {query:async ()=>{child.exitCode=1;return 'boolean true';}}),/SESSION_SERVICE_FAILED/);
});

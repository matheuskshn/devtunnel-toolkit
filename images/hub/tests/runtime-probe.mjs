// Executed only by the local container smoke test, never copied into the image.
import assert from 'node:assert/strict';
import { SessionRuntime } from '/opt/hub/dist/auth.js';
import { command } from '/opt/hub/dist/processes.js';

const runtimes = ['user-a','user-b'].map(id => new SessionRuntime(
  { id, provider:'microsoft' }, '/data/probe', `/run/hub/probe-${id}`, () => {},
));
for (const runtime of runtimes) runtime.env.PATH = `/tests/fixtures:${runtime.env.PATH}`;
let phase = 'open isolated runtimes';
try {
  await Promise.all(runtimes.map(r => r.open()));
  assert.notEqual(runtimes[0].env.HOME,runtimes[1].env.HOME);
  assert.notEqual(runtimes[0].env.DBUS_SESSION_BUS_ADDRESS,runtimes[1].env.DBUS_SESSION_BUS_ADDRESS);
  phase = 'verify unauthenticated identities';
  await Promise.all(runtimes.map(r => assert.rejects(r.identity(), /AUTH_REQUIRED/)));
  // Exercise keyring persistence with a fake secret, not an account credential.
  phase = 'store synthetic keyring item';
  await command('secret-tool',['store','--label=hub-test','hub-test','user-a'],runtimes[0].env,{input:'synthetic-fixture'});
  const lookupA = await command('secret-tool',['lookup','hub-test','user-a'],runtimes[0].env);
  assert.equal(lookupA.trim(),'synthetic-fixture');
  await assert.rejects(command('secret-tool',['lookup','hub-test','user-a'],runtimes[1].env));
  phase = 'restart isolated runtime';
  await runtimes[0].close();
  runtimes[0] = new SessionRuntime({id:'user-a',provider:'microsoft'},'/data/probe','/run/hub/probe-user-a',()=>{});
  runtimes[0].env.PATH = `/tests/fixtures:${runtimes[0].env.PATH}`;
  await runtimes[0].open();
  phase = 'read persisted synthetic keyring item';
  assert.equal((await command('secret-tool',['lookup','hub-test','user-a'],runtimes[0].env)).trim(),'synthetic-fixture');
  console.log('PASS independent D-Bus, homes, keyrings and keyring restart persistence');
} catch (error) {
  throw new Error(`Runtime probe failed during: ${phase}`, {cause:error});
} finally { await Promise.all(runtimes.map(r => r.close())); }

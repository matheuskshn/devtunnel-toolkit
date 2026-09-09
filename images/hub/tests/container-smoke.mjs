import assert from 'node:assert/strict';
import { spawnSync, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const exec=promisify(execFile);
const image=process.env.HUB_TEST_IMAGE ?? 'devtunnel-toolkit-hub:local';
const prefix=`hub-test-${process.pid}`;
const container=`${prefix}-app`, volume=`${prefix}-data`, network=`${prefix}-net`;
const tests=path.dirname(fileURLToPath(import.meta.url));
function docker(args,ok=true) {
  const result=spawnSync('docker',args,{encoding:'utf8',timeout:30000});
  if(ok&&result.status!==0)throw Error(`docker ${args.slice(0,2).join(' ')} failed: ${result.stderr || result.stdout}`);
  return result;
}
const restrictions=['--read-only','--cap-drop','ALL','--security-opt','no-new-privileges',
  '--tmpfs','/run/hub:uid=1000,gid=1000,mode=0700','--tmpfs','/tmp:mode=1777',
  '--mount',`type=volume,src=${volume},dst=/data`];
async function ready() {
  for(let i=0;i<30;i++) {
    if(docker(['exec',container,'hub','health'],false).status===0)return;
    await delay(200);
  }
  const logs=docker(['logs',container],false);
  throw Error('Readiness timed out: '+logs.stdout+logs.stderr+'\n'+docker(['top',container],false).stdout);
}
try {
  docker(['volume','create',volume]);docker(['network','create','--internal',network]);
  // Docker named volumes inherit the image's UID on first mount.
  docker(['run','--rm','--network','none',...restrictions,'--entrypoint','node',image,'--input-type=module','-e',
    `import {StateStore} from '/opt/hub/dist/state.js';import {parseConfig} from '/opt/hub/dist/model.js';
     const config=parseConfig({allowedDomains:['service.example.com'],allowedPorts:[80,443,18080],connectPorts:[443,18080]});
     const store=new StateStore('/data',config);await store.load();
     for(const [index,id] of ['user-a','user-b'].entries()){const s=store.add(id,'github');
       s.identity={provider:'github',user_id:'github:'+String(1001+index),user_login:id};s.tunnel_id='devhub-'+id+'.use1';s.status='ready';}
     await store.save();`]);
  const invalid=docker(['run','--rm','--network','none',...restrictions,
    '--env','HUB_ALLOWED_DOMAINS=*',image],false);
  assert.equal(invalid.status,1);assert.match(invalid.stderr,/INVALID_DOMAIN_ALLOWLIST/);
  docker(['create','--name',container,'--network',network,'--network-alias','service.example.com','--network-alias','other.example.com',...restrictions,
    '--env','HUB_ALLOWED_DOMAINS=service.example.com','--env','HUB_ALLOWED_PORTS=80,443,18080',
    '--env','HUB_CONNECT_PORTS=443,18080','--env','HUB_HEALTH_PORT=8081',
    '--mount',`type=bind,src=${tests},dst=/tests,readonly`,image]);
  // Docker DNS resolves the test-only alias on an isolated bridge.
  docker(['start',container]);await ready();
  const inspect=JSON.parse(docker(['inspect',container]).stdout)[0];
  assert.equal(inspect.Config.User,'1000:1000');assert.equal(inspect.HostConfig.ReadonlyRootfs,true);
  const second=docker(['run','--rm','--network','none',...restrictions,image],false);
  assert.equal(second.status,75,'Second manager must be excluded by the shared-volume lock');
  assert.equal(JSON.parse(docker(['exec',container,'hub','session','list']).stdout).length,2);
  docker(['exec',container,'hub','session','add','user-c','--provider','microsoft']);
  const unauthenticated=docker(['exec',container,'hub','session','start','user-c'],false);
  assert.notEqual(unauthenticated.status,0);assert.match(unauthenticated.stderr,/AUTH_REQUIRED/);
  console.log((await exec('docker',['exec',container,'node','/tests/proxy-probe.mjs'],{timeout:30000})).stdout.trim());
  console.log((await exec('docker',['exec',container,'node','/tests/runtime-probe.mjs'],{timeout:30000})).stdout.trim());
  await delay(300);
  const logs=docker(['logs',container]).stdout;
  const audits=logs.split('\n').filter(Boolean).map(l=>JSON.parse(l)).filter(l=>l.event==='proxy_access');
  assert.ok(audits.some(l=>l.user_login==='user-a'&&l.status===200));
  assert.ok(audits.some(l=>l.user_login==='user-b'&&l.method==='CONNECT'&&l.status===200));
  assert.ok(audits.some(l=>l.status===403));
  assert.doesNotMatch(logs,/synthetic-query-secret|synthetic-header-secret|private\/path|synthetic-fixture/);
  docker(['restart','--timeout','15',container]);await ready();
  const sessions=JSON.parse(docker(['exec',container,'hub','session','list']).stdout);
  assert.equal(sessions.find(s=>s.id==='user-a').listener,18001);
  assert.equal(sessions.find(s=>s.id==='user-a').tunnel_id,'devhub-user-a.use1');
  assert.equal(sessions.find(s=>s.id==='user-c').provider,'microsoft');
  docker(['stop','--timeout','15',container]);
  assert.equal(JSON.parse(docker(['inspect',container]).stdout)[0].State.ExitCode,0);
  docker(['rm',container]);
  docker(['run','--detach','--name',container,'--network',network,'--network-alias','service.example.com','--network-alias','other.example.com',...restrictions,
    '--env','HUB_ALLOW_ALL_DOMAINS=true','--env','HUB_ALLOWED_DOMAINS=service.example.com',
    '--env','HUB_ALLOWED_PORTS=80,443,18080','--env','HUB_CONNECT_PORTS=443,18080',
    '--mount',`type=bind,src=${tests},dst=/tests,readonly`,image]);await ready();
  console.log((await exec('docker',['exec',container,'node','/tests/proxy-probe.mjs'],{timeout:30000})).stdout.trim());
  docker(['stop','--timeout','15',container]);
  console.log('PASS explicit all-domain access permits an unlisted destination while retaining metadata, loopback, port and listener restrictions');
  console.log('PASS environment-only policy, custom health port, invalid-config rejection, non-root/read-only runtime, storage lock, CLI, redacted audit, restart and SIGTERM');
} finally {
  // These exact ephemeral names were created by this test; no shared resources touched.
  docker(['rm','--force',container],false);docker(['volume','rm',volume],false);docker(['network','rm',network],false);
}

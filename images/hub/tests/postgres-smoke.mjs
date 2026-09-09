import {execFileSync,spawnSync} from 'node:child_process';
import {setTimeout as delay} from 'node:timers/promises';
import {fileURLToPath} from 'node:url';import path from 'node:path';
import assert from 'node:assert/strict';import {randomBytes} from 'node:crypto';
const prefix=`hub-pg-test-${process.pid}`,network=`${prefix}-net`,db=`${prefix}-db`;
const image=process.env.HUB_TEST_IMAGE??'devtunnel-toolkit-hub:local';
const tests=path.dirname(fileURLToPath(import.meta.url));
const app=`${prefix}-app`,second=`${prefix}-second`;
function docker(args){return execFileSync('docker',args,{encoding:'utf8',stdio:['ignore','pipe','pipe'],timeout:120000});}
try {
  docker(['network','create','--internal',network]);
  docker(['run','--detach','--name',db,'--network',network,'--network-alias','postgres',
    '--env','POSTGRES_DB=hubtest','--env','POSTGRES_PASSWORD=synthetic-test-only','postgres:16-bookworm']);
  for(let i=0;i<60;i++){
    if(spawnSync('docker',['exec',db,'pg_isready','-h','127.0.0.1','-U','postgres'],{stdio:'ignore'}).status===0)break;
    if(i===59)throw Error('POSTGRES_READINESS_TIMEOUT');await delay(500);
  }
  console.log(docker(['run','--rm','--network',network,'--read-only','--cap-drop','ALL','--security-opt','no-new-privileges',
    '--tmpfs','/run/hub:uid=1000,gid=1000,mode=0700','--tmpfs','/tmp:mode=1777',
    '--mount',`type=bind,src=${tests},dst=/tests,readonly`,'--entrypoint','node',image,'/tests/postgres-probe.mjs']).trim());
  const settings=['--network',network,'--read-only','--cap-drop','ALL','--security-opt','no-new-privileges',
    '--tmpfs','/run/hub:uid=1000,gid=1000,mode=0700','--tmpfs','/tmp:mode=1777',
    '--env','HUB_STORAGE_BACKEND=postgres','--env','HUB_ID=runtimehub','--env','HUB_PG_HOST=postgres',
    '--env','HUB_PG_DATABASE=hubtest','--env','HUB_PG_USER=postgres','--env','HUB_PG_PASSWORD=synthetic-test-only',
    '--env','HUB_PG_SSLMODE=disable','--env',`HUB_CREDENTIAL_KEY=${randomBytes(32).toString('base64')}`];
  async function ready(){for(let i=0;i<40;i++){if(spawnSync('docker',['exec',app,'hub','health'],{stdio:'ignore'}).status===0)return;await delay(250);}throw Error('HUB_READINESS_FAILED');}
  docker(['run','--detach','--name',app,...settings,image]);await ready();
  docker(['exec',app,'hub','session','add','user-a']);
  const conflict=spawnSync('docker',['run','--rm','--name',second,...settings,image],{encoding:'utf8',timeout:15000});
  assert.equal(conflict.status,75);
  const auth=spawnSync('docker',['exec',app,'hub','session','start','user-a'],{encoding:'utf8'});assert.notEqual(auth.status,0);
  // SIGKILL and replacement use no shared filesystem, only the database.
  docker(['kill','--signal','KILL',app]);docker(['rm',app]);
  docker(['run','--detach','--name',app,...settings,image]);await ready();
  assert.equal(JSON.parse(docker(['exec',app,'hub','session','list']))[0].listener,18001);
  docker(['exec',db,'psql','-U','postgres','-d','hubtest','-c',"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name='devtunnel-toolkit-hub'"]);
  for(let i=0;i<40;i++){if(docker(['inspect','--format','{{.State.Running}}',app]).trim()==='false')break;await delay(250);}
  assert.equal(docker(['inspect','--format','{{.State.Running}}',app]).trim(),'false');
  console.log('PASS PostgreSQL manager CLI, read-only root without /data volume, competing containers, SIGKILL/replacement and fail-closed connection loss');
} catch(error){console.error(error.stderr?.toString()||error.message);process.exitCode=1;}
finally {for(const name of [app,second,db])spawnSync('docker',['rm','--force','--volumes',name],{stdio:'ignore'});spawnSync('docker',['network','rm',network],{stdio:'ignore'});}

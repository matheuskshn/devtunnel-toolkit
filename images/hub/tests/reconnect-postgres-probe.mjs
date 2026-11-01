// Run only against an isolated local test database. No real accounts or provider traffic.
import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {mkdtemp, mkdir, writeFile, readFile, rm} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import pg from 'pg';
import {PostgresPersistence,postgresConfig} from '../dist/postgres.js';
import {StagedCredentials} from '../dist/staged-credentials.js';

assert.match(process.env.HUB_TEST_PG_PORT??'',/^\d{1,5}$/,'An isolated local test database port is required');
const env={HUB_PG_HOST:'127.0.0.1',HUB_PG_PORT:process.env.HUB_TEST_PG_PORT,HUB_PG_DATABASE:'hubtest',
  HUB_PG_USER:'postgres',HUB_PG_PASSWORD:'synthetic-test-only',HUB_PG_SSLMODE:'disable',HUB_CREDENTIAL_KEY:randomBytes(32).toString('base64')};
const root=await mkdtemp(path.join(os.tmpdir(),'hub-replacement-pg-'));
const config=await postgresConfig(env),control=new pg.Client(config);
const store=new PostgresPersistence(config,'replacement-test',env,{onLost(){}});
try {
  await control.connect();await store.open(root);
  const old=path.join(root,'old'),candidate=path.join(root,'candidate');
  await mkdir(old);await mkdir(candidate);
  await writeFile(path.join(old,'fixture'),'synthetic-old');await writeFile(path.join(candidate,'fixture'),'synthetic-new');
  await store.beginAuth('user-a');await store.checkpoint('user-a',old);
  const row=async()=>(await control.query("SELECT generation,dirty,package FROM devtunnel_hub.credentials WHERE hub_id='replacement-test' AND session_id='user-a'")).rows[0];
  const original=await row();
  const staged=new StagedCredentials(store);await staged.beginAuth('user-a');await staged.checkpoint('user-a',candidate);
  assert.deepEqual(await row(),original);
  const restoredOld=path.join(root,'restore-old');await store.restore('user-a',restoredOld);
  assert.equal(await readFile(path.join(restoredOld,'fixture'),'utf8'),'synthetic-old');
  await staged.promote('user-a',candidate);
  const replacement=await row();assert.equal(BigInt(replacement.generation),BigInt(original.generation)+1n);
  assert.equal(replacement.dirty,false);assert.ok(!replacement.package.includes('synthetic-'));
  const restoredNew=path.join(root,'restore-new');await store.restore('user-a',restoredNew);
  assert.equal(await readFile(path.join(restoredNew,'fixture'),'utf8'),'synthetic-new');
  await store.beginAuth('user-a');
  await assert.rejects(store.replaceCredentials('user-a',old),/CREDENTIAL_CHECKPOINT_IN_PROGRESS/);
  const unchanged=await row();assert.equal(unchanged.generation,replacement.generation);assert.deepEqual(unchanged.package,replacement.package);
  await store.checkpoint('user-a',candidate);
  console.log('PASS PostgreSQL candidate isolation, unchanged active credentials, atomic generation promotion, encrypted restore and rollback on dirty-row conflict');
} finally {await store.close();await control.end();await rm(root,{recursive:true,force:true});}

import assert from 'node:assert/strict';
import test from 'node:test';
import {readFile} from 'node:fs/promises';
import {parse,parseAllDocuments} from 'yaml';
import {loadConfig} from '../dist/config.js';
const read=async relative=>readFile(new URL(relative,import.meta.url),'utf8');

test('Kubernetes example has one non-root replica, persistent data and no ingress',async()=>{
  const documents=parseAllDocuments(await read('../examples/kubernetes/hub.yaml'));
  for(const document of documents)assert.deepEqual(document.errors,[]);
  const values=documents.map(d=>d.toJS());
  const spec=values.find(d=>d.kind==='Deployment').spec;
  assert.equal(spec.replicas,1);assert.equal(spec.strategy.type,'Recreate');
  assert.equal(spec.template.spec.securityContext.runAsUser,1000);
  assert.equal(spec.template.spec.containers[0].securityContext.readOnlyRootFilesystem,true);
  assert.deepEqual(spec.template.spec.containers[0].securityContext.capabilities.drop,['ALL']);
  assert.equal(values.some(v=>['Service','Ingress'].includes(v.kind)),false);
});
test('ACA example uses environment-only policy, persistent data, singleton sizing and no ingress',async()=>{
  const app=parse(await read('../examples/aca/containerapp.yaml'));
  const {configuration,template}=app.properties;
  assert.equal(configuration.activeRevisionsMode,'Single');assert.equal(configuration.ingress,undefined);
  assert.deepEqual(template.scale,{minReplicas:1,maxReplicas:1});
  assert.equal(template.volumes.find(v=>v.name==='data').storageType,'AzureFile');
  assert.equal(template.volumes.some(v=>v.name==='config'),false);
  const env=Object.fromEntries(template.containers[0].env.map(v=>[v.name,v.value]));
  assert.ok(Object.values(env).every(v=>typeof v==='string'));
  const config=await loadConfig(undefined,env);
  assert.deepEqual(config.allowedProviders,['microsoft']);
  assert.equal(config.allowedMicrosoftTenants.length,1);
  assert.equal(config.healthPort,template.containers[0].probes[0].httpGet.port);
});
test('environment-only Compose injects an external env file and retains isolated runtime mounts',async()=>{
  const compose=parse(await read('../examples/compose/compose.environment.yml'));
  assert.match(compose.services.hub.env_file[0],/HUB_ENV_FILE/);
  assert.deepEqual(compose.services.hub.volumes,['hub-data:/data']);
  assert.equal(compose.services.hub.ports,undefined);
  assert.equal(compose.services.hub.read_only,true);
  assert.deepEqual(compose.services.hub.cap_drop,['ALL']);
});
test('PostgreSQL ACA example uses secret references, verified TLS and no durable volume',async()=>{
  const {properties:{configuration,template}}=parse(await read('../examples/aca/containerapp.postgres.yaml'));
  assert.equal(configuration.activeRevisionsMode,'Single');
  assert.equal(configuration.ingress,undefined);
  assert.deepEqual(template.scale,{minReplicas:1,maxReplicas:1});
  assert.equal(template.volumes,undefined);
  const container=template.containers[0];
  assert.equal(container.volumeMounts,undefined);
  const env=Object.fromEntries(container.env.map(e=>[e.name,e]));
  assert.equal(env.HUB_STORAGE_BACKEND.value,'postgres');
  assert.equal(env.HUB_PG_SSLMODE.value,'verify-full');
  for(const name of ['HUB_PG_PASSWORD','HUB_CREDENTIAL_KEY']){
    assert.ok(env[name].secretRef);assert.equal(env[name].value,undefined);
  }
  assert.equal(env.HUB_RUN_DIR.value,'/tmp/hub');
});
test('Hub publishing requires tests and both architectures; PR validation has no registry credentials',async()=>{
  const workflow=parse(await read('../../../.github/workflows/hub.yml'));
  assert.deepEqual(workflow.permissions,{contents:'read'});
  assert.deepEqual(workflow.on.push.branches,['main']);
  assert.equal(workflow.on.push.tags,undefined);
  assert.equal(workflow.on.pull_request_target,undefined);
  assert.equal(workflow.env,undefined);
  const {test: tests,architectures,publish}=workflow.jobs;
  assert.deepEqual(architectures.needs,['changes','test']);
  assert.equal(architectures.if,"needs.changes.outputs.hub == 'true'");
  assert.equal(workflow.jobs.changes.uses,'./.github/workflows/image-changes.yml');
  assert.deepEqual(publish.needs,['test','architectures']);
  assert.equal(publish.if,"(github.event_name == 'push' && github.ref == 'refs/heads/main') || github.event_name == 'workflow_dispatch'");
  assert.deepEqual(publish.permissions,{contents:'read',packages:'write'});
  for(const job of [tests,architectures]){
    assert.equal(job.env,undefined);
    assert.ok(job.steps.every(s=>!s.uses?.includes('login-action')));
    assert.ok(job.steps.filter(s=>s.uses?.startsWith('docker/build-push-action')).every(s=>s.with.push===false));
  }
  assert.ok(tests.steps.some(s=>s.run==='npm run test:container'));
  assert.equal(tests.steps.find(s=>s.run==='npm run test:container').if,"needs.changes.outputs.hub == 'true'");
  assert.equal(tests.steps.find(s=>s.run==='npm run test:postgres').if,"needs.changes.outputs.hub == 'true'");
  assert.ok(tests.steps.some(s=>s.run==='npm audit --omit=dev --audit-level=high'));
  const build=publish.steps.find(s=>s.uses?.startsWith('docker/build-push-action')).with;
  assert.equal(build.context,'images/hub');assert.equal(build.platforms,'linux/amd64,linux/arm64');
  assert.equal(build.push,true);assert.equal(build.provenance,true);assert.equal(build.sbom,true);
  const login=publish.steps.find(s=>s.name==='Login to Docker Hub');
  assert.equal(login.if,"env.DOCKERHUB_USERNAME != '' && env.DOCKERHUB_TOKEN != ''");
  const metadata=publish.steps.find(s=>s.id==='meta').with;
  assert.equal(metadata.flavor.trim(),'latest=false');
  assert.match(metadata.tags,/github.run_id/);
  assert.doesNotMatch(metadata.tags,/type=ref,event=tag|value=latest|type=semver/);
});
test('legacy image workflow uses a filtered matrix and skips an empty selection',async()=>{
  const workflow=parse(await read('../../../.github/workflows/docker.yml'));
  assert.equal(workflow.jobs.changes.uses,'./.github/workflows/image-changes.yml');
  assert.equal(workflow.jobs.build.needs,'changes');
  assert.equal(workflow.jobs.build.if,"needs.changes.outputs.legacy == 'true'");
  assert.equal(workflow.jobs.build.strategy.matrix,'${{ fromJSON(needs.changes.outputs.matrix) }}');
});
test('Hub build context is source-only',async()=>{
  const ignore=await read('../.dockerignore');
  assert.ok(ignore.startsWith('**\n'));assert.doesNotMatch(ignore,/!tests|!examples|!data|!\.env/);
  const dockerfile=await read('../Dockerfile');
  assert.match(dockerfile,/USER 1000:1000/);assert.match(dockerfile,/sha256sum -c -/);
});

import assert from 'node:assert/strict';
import test from 'node:test';
import {readFile} from 'node:fs/promises';
import {parse,parseAllDocuments} from 'yaml';
import {loadConfig} from '../dist/config.js';
const read=async relative=>readFile(new URL(relative,import.meta.url),'utf8');

test('Ubuntu acceptance uses native runners and cannot publish or bypass failed tests',async()=>{
  const source=await read('../../../.github/workflows/hub-ubuntu-acceptance.yml');
  const workflow=parse(source);
  assert.deepEqual(Object.keys(workflow.on),['workflow_dispatch','pull_request']);
  assert.ok(workflow.on.pull_request.paths.includes('images/hub/Dockerfile.ubuntu'));
  assert.ok(workflow.on.pull_request.paths.includes('images/hub/src/**'));
  assert.ok(workflow.on.pull_request.paths.every(p=>!p.endsWith('.md') && p!=='images/hub/**'));
  assert.deepEqual(workflow.permissions,{contents:'read'});
  assert.equal(workflow.defaults.run.shell,'bash');
  const job=workflow.jobs.native;
  assert.ok(Object.values(job.env).every(value=>!String(value).includes('runner.')));
  assert.equal(job.strategy['fail-fast'],false);
  assert.deepEqual(job.strategy.matrix.include,[
    {runner:'ubuntu-24.04',arch:'amd64',machine:'x86_64'},
    {runner:'ubuntu-24.04-arm',arch:'arm64',machine:'aarch64'},
  ]);
  assert.equal(job['runs-on'],'${{ matrix.runner }}');
  assert.ok(job.steps.some(s=>s.run?.includes('test "$(uname -m)" = "$EXPECTED_MACHINE"')));
  assert.ok(job.steps.some(s=>s.run?.includes('npm run test:container')));
  assert.ok(job.steps.some(s=>s.run?.includes('npm run test:postgres')));
  assert.ok(job.steps.some(s=>s.run?.includes('native-package-audit.mjs')));
  assert.ok(job.steps.some(s=>s.run?.includes('cc -O2 -Wall -Wextra /tests/native-library-regression.c')));
  assert.ok(job.steps.some(s=>s.run?.includes('cc -O2 -Wall -Wextra /tests/elf-string-regression.c')));
  assert.ok(job.steps.some(s=>s.run?.includes("-ec '/probes/native-library-regression && /probes/elf-string-regression'")));
  assert.ok(job.steps.some(s=>s.run?.includes('--severity HIGH,CRITICAL --exit-code 1')));
  assert.doesNotMatch(source,/setup-qemu|push:\s*true|docker push|login-action|secrets\.|continue-on-error|ignore-unfixed|detect_leaks=0|docker\.sock/);
  for (const step of job.steps.filter(s=>s.uses)) assert.match(step.uses,/@[a-f0-9]{40}$/);
  const artifact=job.steps.find(s=>s.uses?.startsWith('actions/upload-artifact@'));
  assert.equal(artifact.if,'always()');
  assert.equal(artifact.with['retention-days'],7);
  assert.match(artifact.with.path,/reports\/\*\.json/);
  assert.doesNotMatch(artifact.with.path,/\.tar|\.env|\/data/);
});

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
  assert.deepEqual(publish.needs,['test','architectures','security']);
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
  assert.ok(tests.steps.some(s=>s.run==='npm audit --audit-level=low'));
  assert.equal(tests.needs, 'changes');
  assert.equal(workflow.jobs.security.uses, './.github/workflows/security.yml');
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
  assert.deepEqual(workflow.jobs.build.needs,['changes','security']);
  assert.equal(workflow.jobs.build.if,"needs.changes.outputs.legacy == 'true'");
  assert.equal(workflow.jobs.build.strategy.matrix,'${{ fromJSON(needs.changes.outputs.matrix) }}');
});
test('security gate scans changed images and never hides unpatched high or critical findings', async () => {
  const workflow = parse(await read('../../../.github/workflows/security.yml'));
  assert.deepEqual(workflow.permissions, {contents: 'read'});
  assert.equal(workflow.jobs.images.if, "needs.plan.outputs.any == 'true'");
  const scan = workflow.jobs.images.steps.find(s => s.name?.startsWith('Block high')).run;
  assert.match(scan, /--severity HIGH,CRITICAL/);
  assert.match(scan, /--exit-code 1/);
  assert.doesNotMatch(scan, /ignore-unfixed|ignorefile|exit-code 0/);
  assert.ok(workflow.jobs.source.steps.some(s => s.run?.includes('gitleaks') && s.run.includes('--redact')));
  for (const job of Object.values(workflow.jobs))
    for (const step of job.steps ?? [])
      if (step.uses) assert.match(step.uses, /@[a-f0-9]{40}$/);
});
test('release publishing requires the complete security workflow without suppressing functional checks', async () => {
  const checks = parse(await read('../../../.github/workflows/release-checks.yml'));
  const suite = parse(await read('../../../.github/workflows/release-suite.yml'));
  const config = JSON.parse(await read('../../../release-please-config.json'));
  assert.equal(checks.jobs.security.uses, './.github/workflows/security.yml');
  assert.equal(checks.jobs.security.with.ref, '${{ inputs.ref }}');
  assert.equal(checks.jobs.security.with['scan-images'], '${{ inputs.container }}');
  assert.equal(checks.jobs.checks.needs, undefined);
  assert.equal(suite.jobs.checks.uses, './.github/workflows/release-checks.yml');
  assert.ok(suite.jobs.build.needs.includes('checks'));
  assert.ok(suite.jobs.finalize.needs.includes('checks'));
  assert.ok(suite.jobs.finalize.needs.includes('build'));
  assert.equal(suite.jobs.build['continue-on-error'], undefined);
  assert.equal(suite.jobs.finalize['continue-on-error'], undefined);
  assert.equal(config.packages['.'].draft, true);
});
test('Hub build context is source-only',async()=>{
  const ignore=await read('../.dockerignore');
  assert.ok(ignore.startsWith('**\n'));assert.doesNotMatch(ignore,/!tests|!examples|!data|!\.env/);
  const dockerfile=await read('../Dockerfile');
  assert.match(dockerfile,/USER 1000:1000/);assert.match(dockerfile,/sha256sum -c -/);
});

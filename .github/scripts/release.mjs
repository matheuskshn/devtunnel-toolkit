import {execFileSync} from 'node:child_process';
import {appendFileSync,readFileSync,writeFileSync,mkdirSync,readdirSync} from 'node:fs';
import {createHash} from 'node:crypto';
import {homedir} from 'node:os';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {checkVersions,immutableTag,releaseAliases,suiteImages,validateAttestation,validateManifest,versionInfo} from './release-policy.mjs';

const run=(file,args)=>execFileSync(file,args,{encoding:'utf8',maxBuffer:32*1024*1024,stdio:['ignore','pipe','pipe']});
export async function github(endpoint,{method='GET',body,optional=false}={}){
  const repo=process.env.GITHUB_REPOSITORY;
  if(!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo??''))throw Error('INVALID_REPOSITORY');
  const response=await fetch(`https://api.github.com/repos/${repo}/${endpoint}`,{
    method,headers:{Authorization:`Bearer ${process.env.GH_TOKEN}`,Accept:'application/vnd.github+json','Content-Type':'application/json',
      'X-GitHub-Api-Version':'2022-11-28'},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(30000)});
  if(optional&&response.status===404)return undefined;
  if(!response.ok)throw Error(`GITHUB_API_${response.status}`);
  return response.status===204?undefined:response.json();
}
async function releases(api){
  const all=[];
  for(let page=1;page<=100;page++){
    const items=await api(`releases?per_page=100&page=${page}`);all.push(...items);
    if(items.length<100)return all;
  }
  throw Error('RELEASE_LIST_LIMIT');
}
export async function remoteTag(api,tag){
  let ref=await api(`git/ref/tags/${tag}`,{optional:true});
  if(!ref)return undefined;
  for(let depth=0;depth<5;depth++){
    if(ref.object.type==='commit')return ref.object.sha;
    if(ref.object.type!=='tag')throw Error('INVALID_TAG_TARGET');
    ref=await api(`git/tags/${ref.object.sha}`);
  }
  throw Error('TAG_DEPTH_LIMIT');
}
export async function prepareRelease({api=github,version,sha,notes}){
  const info=versionInfo(version);
  if(!/^[a-f0-9]{40}$/.test(sha))throw Error('INVALID_RELEASE_SHA');
  const currentTag=await remoteTag(api,info.tag);
  if(currentTag&&currentTag!==sha)throw Error('RELEASE_TAG_MOVED');
  const existing=await releases(api);
  const matches=existing.filter(r=>r.tag_name===info.tag);
  if(matches.length>1)throw Error('DUPLICATE_RELEASE');
  if(matches[0]&&!matches[0].draft)throw Error('RELEASE_ALREADY_PUBLISHED');
  if(/^[a-f0-9]{40}$/.test(matches[0]?.target_commitish??'')&&matches[0].target_commitish!==sha)throw Error('RELEASE_DRAFT_COMMIT_MISMATCH');
  if(!info.prerelease){
    const core=v=>[v.major,v.minor,v.patch].map(BigInt);
    for(const release of existing.filter(r=>!r.draft&&!r.prerelease)){
      let previous;try{previous=versionInfo(release.tag_name.slice(1));}catch{continue;}
      const a=core(previous),b=core(info);const different=a.findIndex((n,i)=>n!==b[i]);
      if(different>=0&&a[different]>b[different])throw Error('RELEASE_OUT_OF_ORDER');
    }
  }
  if(!currentTag){
    const annotated=await api('git/tags',{method:'POST',body:{tag:info.tag,message:`Release ${info.tag}`,object:sha,type:'commit'}});
    await api('git/refs',{method:'POST',body:{ref:`refs/tags/${info.tag}`,sha:annotated.sha}});
  }
  return matches[0]??api('releases',{method:'POST',body:{tag_name:info.tag,target_commitish:sha,name:info.tag,
    body:notes,draft:true,prerelease:info.prerelease}});
}
const skopeo=(command,args)=>run('skopeo',[command,'--authfile',path.join(homedir(),'.docker/config.json'),...args]);
export function registryDigest(ref){
  try{return `sha256:${createHash('sha256').update(skopeo('inspect',['--raw',`docker://${ref}`])).digest('hex')}`;}
  catch(e){
    // Only an explicit missing-manifest/name response allows a new tag.
    // Authentication failures, throttling and transport errors never authorize overwrite.
    if(/manifest unknown|name unknown|manifest_unknown|name_unknown/i.test(String(e.stderr)))return undefined;
    throw Error('REGISTRY_INSPECTION_FAILED');
  }
}
function repositories(image){
  const owner=process.env.GITHUB_REPOSITORY_OWNER?.toLowerCase();
  if(!/^[a-z0-9][a-z0-9-]*$/.test(owner??''))throw Error('INVALID_REGISTRY_OWNER');
  const result=[`ghcr.io/${owner}/${image}`];
  if(process.env.DOCKERHUB_USERNAME&&process.env.DOCKERHUB_TOKEN){
    const namespace=(process.env.DOCKERHUB_NAMESPACE||process.env.DOCKERHUB_USERNAME).toLowerCase();
    if(!/^[a-z0-9][a-z0-9_-]*$/.test(namespace))throw Error('INVALID_DOCKERHUB_NAMESPACE');
    result.push(`docker.io/${namespace}/${image}`);
  }
  return result;
}
function candidate(image,version,sha){
  const repository=repositories(image)[0],ref=`${repository}:candidate-${version}`;
  const actual=registryDigest(ref);
  if(!actual)return {ref,repository};
  const raw=JSON.parse(skopeo('inspect',['--raw',`docker://${repository}@${actual}`]));
  const platforms=(raw.manifests??[]).filter(m=>m.platform?.os==='linux').map(m=>`${m.platform.os}/${m.platform.architecture}`);
  for(const arch of ['amd64','arm64']){
    const config=JSON.parse(skopeo('inspect',['--override-arch',arch,`docker://${repository}@${actual}`]));
    if(config.Labels?.['org.opencontainers.image.revision']!==sha||config.Labels?.['org.opencontainers.image.version']!==version)throw Error('CANDIDATE_IDENTITY_MISMATCH');
  }
  if(JSON.stringify([...platforms].sort())!==JSON.stringify(['linux/amd64','linux/arm64']))throw Error('INCOMPLETE_ARCHITECTURES');
  return {ref,repository,digest:actual,platforms};
}
export async function promoteSuite({records,version,sha,inspect,copy,repoFor,attach,publish,verifyTag}){
  validateManifest(records,version,sha);
  await verifyTag();
  const immutable=records.flatMap(record=>repoFor(record.image).flatMap(repository=>
    [version,`v${version}`].map(tag=>({source:`${repoFor(record.image)[0]}@${record.digest}`,target:`${repository}:${tag}`,digest:record.digest}))));
  // Preflight the whole suite before writing the first version tag.
  for(const item of immutable)immutableTag(await inspect(item.target),item.digest);
  for(const item of immutable){
    if(immutableTag(await inspect(item.target),item.digest))await copy(item.source,item.target);
    if(await inspect(item.target)!==item.digest)throw Error('PUBLISHED_DIGEST_MISMATCH');
  }
  await attach();
  // Mutable aliases only move after every immutable tag and release asset is verified.
  for(const record of records)for(const repository of repoFor(record.image))for(const alias of releaseAliases(version)){
    const target=`${repository}:${alias}`;
    await copy(`${repoFor(record.image)[0]}@${record.digest}`,target);
    if(await inspect(target)!==record.digest)throw Error('ALIAS_DIGEST_MISMATCH');
  }
  await verifyTag();
  await publish();
}
function output(key,value){appendFileSync(process.env.GITHUB_OUTPUT,`${key}=${typeof value==='string'?value:JSON.stringify(value)}\n`);}
async function uploadAssets(release,files){
  const existing=await github(`releases/${release.id}/assets?per_page=100`);
  for(const file of files){
    const data=readFileSync(file),name=path.basename(file),checksum=`sha256:${createHash('sha256').update(data).digest('hex')}`;
    const previous=existing.find(asset=>asset.name===name);
    if(previous){
      if(previous.digest!==checksum)throw Error('RELEASE_ASSET_CONFLICT');
      continue;
    }
    const url=new URL(release.upload_url.split('{')[0]);
    if(url.origin!=='https://uploads.github.com')throw Error('INVALID_ASSET_HOST');
    url.searchParams.set('name',name);
    const response=await fetch(url,{method:'POST',headers:{Authorization:`Bearer ${process.env.GH_TOKEN}`,'Content-Type':'application/octet-stream'},body:data,signal:AbortSignal.timeout(120000)});
    if(!response.ok)throw Error(`ASSET_UPLOAD_${response.status}`);
    const asset=await response.json();
    if(asset.digest!==checksum)throw Error('RELEASE_ASSET_DIGEST_MISMATCH');
  }
}
async function main(){
  const mode=process.argv[2],info=checkVersions(process.cwd(),process.env.RELEASE_TAG);
  const sha=run('git',['rev-parse','HEAD']).trim();
  run('git',['merge-base','--is-ancestor',sha,'origin/main']);
  if(mode==='prepare'){
    await prepareRelease({version:info.version,sha,notes:readFileSync('CHANGELOG.md','utf8')});
    output('version',info.version);output('sha',sha);output('matrix',{include:suiteImages});return;
  }
  if(mode==='candidate'||mode==='record'){
    const image=process.env.RELEASE_IMAGE;
    if(!suiteImages.some(i=>i.image===image))throw Error('UNKNOWN_RELEASE_IMAGE');
    const built=candidate(image,info.version,sha);
    if(mode==='candidate'){output('exists',!!built.digest);output('ref',built.ref);return;}
    if(!built.digest)throw Error('CANDIDATE_MISSING');
    mkdirSync('release-assets',{recursive:true});
    const record={image,version:info.version,sha,digest:built.digest,platforms:built.platforms};
    writeFileSync(`release-assets/${image}.json`,JSON.stringify(record,null,2)+'\n');
    for(const [key,property] of [['sbom','SBOM'],['provenance','Provenance']]){
      const data=JSON.parse(run('docker',['buildx','imagetools','inspect',`${built.repository}@${built.digest}`,'--format',`{{json .${property}}}`]));
      writeFileSync(`release-assets/${image}.${key}.json`,JSON.stringify(validateAttestation(data,key),null,2)+'\n');
    }
    return;
  }
  if(mode==='finalize'){
    const release=await prepareRelease({version:info.version,sha,notes:readFileSync('CHANGELOG.md','utf8')});
    const records=suiteImages.map(i=>JSON.parse(readFileSync(`release-assets/${i.image}.json`,'utf8')));
    const verified=validateManifest(records,info.version,sha).sort((a,b)=>a.image.localeCompare(b.image));
    for(const record of verified)for(const kind of ['sbom','provenance']){
      const data=JSON.parse(readFileSync(`release-assets/${record.image}.${kind}.json`,'utf8'));
      validateAttestation(data,kind);
    }
    writeFileSync('release-assets/release-manifest.json',JSON.stringify({version:info.version,sha,images:verified.map(r=>({...r,repositories:repositories(r.image)}))},null,2)+'\n');
    const files=readdirSync('release-assets').filter(f=>f.endsWith('.json')).sort().map(f=>`release-assets/${f}`);
    writeFileSync('release-assets/SHA256SUMS',files.map(f=>`${createHash('sha256').update(readFileSync(f)).digest('hex')}  ${path.basename(f)}\n`).join(''));
    await promoteSuite({records:verified,version:info.version,sha,inspect:registryDigest,repoFor:repositories,
      copy:(source,target)=>skopeo('copy',['--all','--preserve-digests',`docker://${source}`,`docker://${target}`]),
      verifyTag:async()=>{if(await remoteTag(github,info.tag)!==sha)throw Error('RELEASE_TAG_MOVED');},
      attach:()=>uploadAssets(release,[...files,'release-assets/SHA256SUMS']),
      publish:()=>github(`releases/${release.id}`,{method:'PATCH',body:{draft:false,prerelease:info.prerelease,make_latest:info.prerelease?'false':'true',
        body:`${release.body??''}\n\n## Container images\n\n${verified.flatMap(r=>repositories(r.image).map(repo=>`- \`${repo}:${info.version}\` - \`${r.digest}\``)).join('\n')}\n\nUse image digests to pin deployments. See release-manifest.json, SBOMs, provenance and SHA256SUMS below.`}})});
    return;
  }
  throw Error('UNKNOWN_RELEASE_COMMAND');
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href)main().catch(e=>{
  console.error(/^[A-Z][A-Z0-9_]+$/.test(e.message)?e.message:'RELEASE_OPERATION_FAILED');process.exitCode=1;
});

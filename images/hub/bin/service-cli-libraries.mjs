// Service only the checksum-pinned Microsoft library closure identified by the pinned advisory.
import {createHash} from 'node:crypto';
import {execFileSync} from 'node:child_process';
import {cpSync,mkdirSync,mkdtempSync,readFileSync,readdirSync,rmSync,writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {pathToFileURL} from 'node:url';

const digest=bytes=>createHash('sha256').update(bytes).digest('hex');
const requireCondition=(value,code)=>{if(!value)throw new Error(code);};
const names=['Microsoft.AspNetCore.WebUtilities','Microsoft.Net.Http.Headers','Microsoft.Extensions.Primitives'];

export function serviceCliLibraries(input,output,evidence,manifest,assets) {
  requireCondition(manifest.schemaVersion===1 && manifest.cliVersion===evidence.cliVersion && manifest.packages?.length===3,'CLI_SERVICING_MANIFEST');
  requireCondition(manifest.packages.every((p,i)=>p.name===names[i] && p.version==='10.0.15' && /^[a-f0-9]{128}$/.test(p.sha512) && p.source===`https://api.nuget.org/v3-flatcontainer/${p.name.toLowerCase()}/${p.version}/${p.name.toLowerCase()}.${p.version}.nupkg` && p.asset===`lib/net10.0/${p.name}.dll`),'CLI_SERVICING_PIN');
  const deps=JSON.parse(readFileSync(path.join(input,'devtunnel.deps.json'),'utf8'));
  requireCondition(deps.libraries?.['Microsoft.AspNetCore.WebUtilities/10.0.7']?.type==='package','CLI_SERVICING_SOURCE_VERSION');
  const core=evidence.applicationFiles.find(f=>f.name==='devtunnel.dll');
  requireCondition(core && digest(readFileSync(path.join(input,core.name)))===core.sha256,'CLI_SERVICING_CORE_CHANGED');
  const prepared=manifest.packages.map(p=>{
    const bytes=assets.get(p.name);
    requireCondition(Buffer.isBuffer(bytes) && bytes.length>=4096 && bytes.length<4*1024*1024 && bytes[0]===0x4d && bytes[1]===0x5a,'CLI_SERVICING_ASSEMBLY');
    const filename=p.name+'.dll';
    const original=evidence.applicationFiles.find(f=>f.name===filename);
    if(original)requireCondition(digest(readFileSync(path.join(input,filename)))===original.sha256,'CLI_SERVICING_SOURCE_CHANGED');
    return {...p,filename,bytes,originalSha256:original?.sha256??null,sha256:digest(bytes)};
  });
  for(const p of prepared) {
    const old=Object.keys(deps.libraries).filter(k=>k.startsWith(p.name+'/'));
    requireCondition(old.length<=1,'CLI_SERVICING_AMBIGUOUS_LIBRARY');
    for(const key of old)delete deps.libraries[key];
    deps.libraries[p.name+'/'+p.version]={type:'package',serviceable:true,sha512:Buffer.from(p.sha512,'hex').toString('base64'),path:p.name.toLowerCase()+'/'+p.version,hashPath:p.name.toLowerCase()+'.'+p.version+'.nupkg.sha512'};
    for(const target of Object.values(deps.targets)) {
      if(!Object.keys(target).length)continue;
      for(const key of Object.keys(target).filter(k=>k.startsWith(p.name+'/')))delete target[key];
      const dependencies=p.name===names[0]?{[names[1]]:p.version}:p.name===names[1]?{[names[2]]:p.version}:{};
      target[p.name+'/'+p.version]={dependencies,runtime:{[p.filename]:{}}};
      for(const library of Object.values(target))if(library.dependencies?.[p.name])library.dependencies[p.name]=p.version;
    }
  }
  mkdirSync(output);
  for(const entry of readdirSync(input))cpSync(path.join(input,entry),path.join(output,entry),{recursive:true,errorOnExist:true,force:false});
  for(const p of prepared)writeFileSync(path.join(output,p.filename),p.bytes,{mode:0o644});
  writeFileSync(path.join(output,'devtunnel.deps.json'),JSON.stringify(deps,null,2)+'\n');
  requireCondition(digest(readFileSync(path.join(output,core.name)))===core.sha256,'CLI_SERVICING_CORE_CHANGED');
  return {...evidence,servicingAdvisory:manifest.advisory,servicedLibraries:prepared.map(({bytes,asset,...record})=>record)};
}

async function main() {
  const [input,evidenceFile,output]=process.argv.slice(2);
  requireCondition(input && evidenceFile && output,'CLI_SERVICING_USAGE');
  const manifest=JSON.parse(readFileSync(new URL('./cli-library-patches.json',import.meta.url)));
  const evidence=JSON.parse(readFileSync(evidenceFile));
  const work=mkdtempSync(path.join(tmpdir(),'cli-library-servicing-'));
  const assets=new Map();
  try {
    mkdirSync(output);
    mkdirSync(path.join(output,'evidence'));
    mkdirSync(path.join(output,'evidence','nuget'));
    for(const p of manifest.packages) {
      const bytes=execFileSync('curl',['--silent','--show-error','--fail','--retry','2','--connect-timeout','15','--max-time','120','--max-filesize','4194304','--proto','=https','--tlsv1.2',p.source],{maxBuffer:4*1024*1024,timeout:180000});
      requireCondition(createHash('sha512').update(bytes).digest('hex')===p.sha512,'CLI_SERVICING_PACKAGE_HASH');
      const archive=path.join(work,p.name+'.nupkg');writeFileSync(archive,bytes);
      assets.set(p.name,execFileSync('unzip',['-p',archive,p.asset],{maxBuffer:4*1024*1024,timeout:10000}));
      writeFileSync(path.join(output,'evidence','nuget',p.name+'.'+p.version+'.nupkg'),bytes);
    }
    const patched=serviceCliLibraries(input,path.join(output,'cli'),evidence,manifest,assets);
    writeFileSync(path.join(output,'evidence','packaging.json'),JSON.stringify(patched,null,2)+'\n');
  }finally{rmSync(work,{recursive:true,force:true});}
}
if(process.argv[1] && import.meta.url===pathToFileURL(path.resolve(process.argv[1])).href)await main();

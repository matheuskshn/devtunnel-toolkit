import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const excluded=new Set(['node_modules','dist','.git','coverage']);
const credentials=[/-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/,
  /\bgh[pousr]_[A-Za-z0-9]{30,}\b/, /\bgithub_pat_[A-Za-z0-9_]{30,}\b/,
  /\beyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\b/];
let files=0;
async function scan(directory) {
  for(const entry of await readdir(directory,{withFileTypes:true})) {
    if(excluded.has(entry.name))continue;
    const file=path.join(directory,entry.name);
    if(entry.isDirectory()){await scan(file);continue;}
    if(entry.isSymbolicLink())throw Error('Unexpected symlink in public source');
    const publicEnvExample = file === path.join(root, '.env.example');
    if(!publicEnvExample && /^\.env(?:\.|$)|\.(?:key|pem|pfx|p12|log)$/.test(entry.name))throw Error(`Forbidden runtime/credential file: ${path.relative(root,file)}`);
    const content=await readFile(file,'utf8');
    if(credentials.some(pattern=>pattern.test(content)))throw Error(`Potential credential: ${path.relative(root,file)}`);
    files++;
  }
}
await scan(root);
console.log(`PASS public-content credential check (${files} files). Environment-specific data still requires human review.`);

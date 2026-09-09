import {createCipheriv, createDecipheriv, randomBytes} from 'node:crypto';
import {readdir, readFile, lstat, mkdir, writeFile} from 'node:fs/promises';
import path from 'node:path';
import {check, HubError} from './model.js';

const limit = 16 * 1024 * 1024;
export interface CredentialKeys { active: string; keys: Record<string, Buffer> }
export function credentialKeys(env: NodeJS.ProcessEnv): CredentialKeys {
  const active = env.HUB_CREDENTIAL_KEY_ID ?? 'primary';
  check(/^[a-zA-Z0-9_-]{1,64}$/.test(active), 'INVALID_CREDENTIAL_KEY_ID');
  let previous: Record<string, string>;
  try { previous = JSON.parse(env.HUB_CREDENTIAL_PREVIOUS_KEYS ?? '{}'); }
  catch { throw new HubError('INVALID_CREDENTIAL_KEYS'); }
  check(previous && typeof previous === 'object' && !Array.isArray(previous), 'INVALID_CREDENTIAL_KEYS');
  const values = {...previous, [active]: env.HUB_CREDENTIAL_KEY};
  const keys: Record<string, Buffer> = Object.create(null);
  check(Object.keys(values).length <= 8, 'INVALID_CREDENTIAL_KEYS');
  for (const [id, value] of Object.entries(values)) {
    check(/^[a-zA-Z0-9_-]{1,64}$/.test(id) && typeof value === 'string' && /^[A-Za-z0-9+/]{43}=$/.test(value), 'INVALID_CREDENTIAL_KEYS');
    keys[id] = Buffer.from(value, 'base64'); check(keys[id].length === 32, 'INVALID_CREDENTIAL_KEYS');
  }
  return {active, keys};
}
export function seal(data: Buffer, context: string, ring: CredentialKeys): Buffer {
  check(data.length <= limit, 'CREDENTIAL_PACKAGE_TOO_LARGE');
  const iv = randomBytes(12), cipher = createCipheriv('aes-256-gcm', ring.keys[ring.active], iv);
  cipher.setAAD(Buffer.from(`${context}:${ring.active}`));
  const ciphertext = Buffer.concat([cipher.update(data), cipher.final()]);
  return Buffer.from(JSON.stringify({v:1, key:ring.active, iv:iv.toString('base64'),
    tag:cipher.getAuthTag().toString('base64'), data:ciphertext.toString('base64')}));
}
export function unseal(encrypted: Buffer, context: string, ring: CredentialKeys): Buffer {
  try {
    check(encrypted.length <= limit * 2, 'INVALID_CREDENTIAL_PACKAGE');
    const value = JSON.parse(encrypted.toString());
    check(value.v === 1 && Object.hasOwn(ring.keys, value.key), 'CREDENTIAL_KEY_UNAVAILABLE');
    const iv = Buffer.from(value.iv, 'base64'), tag = Buffer.from(value.tag, 'base64');
    check(iv.length === 12 && tag.length === 16, 'INVALID_CREDENTIAL_PACKAGE');
    const cipher = createDecipheriv('aes-256-gcm', ring.keys[value.key], iv);
    cipher.setAAD(Buffer.from(`${context}:${value.key}`)); cipher.setAuthTag(tag);
    const result = Buffer.concat([cipher.update(Buffer.from(value.data, 'base64')), cipher.final()]);
    check(result.length <= limit, 'CREDENTIAL_PACKAGE_TOO_LARGE'); return result;
  } catch { throw new HubError('CREDENTIAL_DECRYPTION_FAILED'); }
}
interface Entry { path: string; data: string }
function safeName(name: unknown): asserts name is string {
  check(typeof name === 'string' && name.length > 0 && name.length <= 1024 && !name.includes('\\') &&
    !/[\x00-\x1f\x7f]/.test(name) && !path.isAbsolute(name) &&
    name.split('/').every(p => p !== '' && p !== '.' && p !== '..'), 'UNSAFE_CREDENTIAL_PATH');
}
export async function packHome(directory: string): Promise<Buffer> {
  const entries: Entry[] = []; let bytes = 0;
  async function walk(relative: string): Promise<void> {
    for (const entry of await readdir(path.join(directory, relative), {withFileTypes:true})) {
      const name = relative ? `${relative}/${entry.name}` : entry.name; safeName(name);
      // .NET single-file executable extraction is disposable code, not login state.
      if (name === '.net') continue;
      const file = path.join(directory, name), st = await lstat(file);
      check(!st.isSymbolicLink(), 'UNSAFE_CREDENTIAL_PATH');
      if (st.isDirectory()) { await walk(name); continue; }
      check(st.isFile() && st.nlink === 1 && st.size <= 4 * 1024 * 1024, 'UNSAFE_CREDENTIAL_FILE');
      bytes += st.size; check(bytes <= 10 * 1024 * 1024 && entries.length < 512, 'CREDENTIAL_PACKAGE_TOO_LARGE');
      entries.push({path:name, data:(await readFile(file)).toString('base64')});
    }
  }
  await walk(''); return Buffer.from(JSON.stringify({v:1, files:entries}));
}
export async function unpackHome(data: Buffer, directory: string): Promise<void> {
  check(data.length <= limit, 'CREDENTIAL_PACKAGE_TOO_LARGE');
  let value: {v:number; files:Entry[]};
  try { value = JSON.parse(data.toString()); } catch { throw new HubError('INVALID_CREDENTIAL_PACKAGE'); }
  check(value.v === 1 && Array.isArray(value.files) && value.files.length <= 512, 'INVALID_CREDENTIAL_PACKAGE');
  const names = new Set<string>(); let size = 0;
  for (const entry of value.files) {
    safeName(entry.path); check(!names.has(entry.path), 'UNSAFE_CREDENTIAL_PATH'); names.add(entry.path);
    check(entry.path !== '.net' && !entry.path.startsWith('.net/'), 'UNSAFE_CREDENTIAL_PATH');
    check(typeof entry.data === 'string' && /^[A-Za-z0-9+/]*={0,2}$/.test(entry.data), 'INVALID_CREDENTIAL_PACKAGE');
    const contents = Buffer.from(entry.data, 'base64'); size += contents.length;
    check(contents.length <= 4 * 1024 * 1024 && size <= 10 * 1024 * 1024 && contents.toString('base64') === entry.data, 'INVALID_CREDENTIAL_PACKAGE');
    // The caller supplies a newly created, private, empty directory. Never overwrite.
    await mkdir(path.dirname(path.join(directory, entry.path)), {recursive:true, mode:0o700});
    await writeFile(path.join(directory, entry.path), contents, {mode:0o600, flag:'wx'});
  }
}

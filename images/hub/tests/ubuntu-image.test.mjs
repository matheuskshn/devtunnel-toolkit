import assert from 'node:assert/strict';
import { readFile, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

test('native phase helper rejects incomplete all/default recipes before invoking build tools',()=>{
  const script=fileURLToPath(new URL('../bin/build-native-libraries',import.meta.url));
  for (const args of [[],['all'],['unknown']]) {
    const result=spawnSync('/bin/sh',[script,...args],{encoding:'utf8',env:{PATH:'/nonexistent'},timeout:5000});
    assert.equal(result.status,64);
    assert.match(result.stderr,/use Dockerfile\.ubuntu for the complete build/);
    assert.doesNotMatch(result.stderr,/not found|Permission denied/);
  }
});

test('Ubuntu candidate pins the LTS base and preserves the Hub runtime contract', async () => {
  const candidate = await readFile(new URL('../Dockerfile.ubuntu', import.meta.url), 'utf8');
  const baseline = await readFile(new URL('../Dockerfile', import.meta.url), 'utf8');
  assert.match(candidate, /^FROM ubuntu:26\.04@sha256:[a-f0-9]{64}$/m);
  assert.match(candidate, /libicu78/);
  assert.doesNotMatch(candidate, /libicu76|--allow-remove-essential|trusted=yes|allow-unauthenticated/);
  for (const value of ['dbus-x11', 'gnome-keyring', 'libsecret-tools', 'squid', 'systemd-standalone-sysusers'])
    assert.ok(candidate.includes(value));
  assert.match(candidate, /test "\$\(id -u ubuntu\)" = 1000/);
  assert.match(candidate, /test "\$\(id -g ubuntu\)" = 1000/);
  assert.match(candidate, /usermod --login hub --home \/home\/hub --move-home --shell \/usr\/sbin\/nologin/);
  assert.match(candidate, /! dpkg-query --search \/usr\/bin\/pebble/);
  assert.match(candidate, /&& rm \/usr\/bin\/pebble/);
  assert.match(candidate, /source=bin\/use-gnu-coreutils/);
  assert.equal(candidate.split('COPY --from=build /usr/local/bin/node').at(-1), baseline.split('COPY --from=build /usr/local/bin/node').at(-1));
  const cli = /RUN case "\$\{TARGETARCH\}"[\s\S]*?&& apt-get purge -y --auto-remove curl/;
  assert.equal(candidate.match(cli)?.[0], baseline.match(cli)?.[0]);
});

test('native rebuild uses pinned sources, upstream regressions and registered packages', async () => {
  const builder=await readFile(new URL('../bin/build-native-libraries',import.meta.url),'utf8');
  const packager=await readFile(new URL('../bin/package-native-libraries.mjs',import.meta.url),'utf8');
  const dockerfile=await readFile(new URL('../Dockerfile.ubuntu',import.meta.url),'utf8');
  assert.equal((builder.match(/\.tar\.xz [a-f0-9]{64}/g)??[]).length,3);
  assert.match(builder,/sha256sum -c -/);
  assert.match(builder,/--fuzz=0/);
  assert.match(builder,/for variant in char ushort/);
  assert.match(builder,/ctest --test-dir/);
  assert.match(builder,/-Dtrust_module=enabled/);
  assert.match(builder,/-Dtrust_paths=\/etc\/ssl\/certs\/ca-certificates\.crt/);
  assert.match(packager,/ABI symbols removed/);
  assert.match(packager,/ABI SONAME changed/);
  assert.match(packager,/--strip-unneeded/);
  assert.match(packager,/Ambiguous\/missing executable/);
  assert.match(packager,/dpkg-shlibdeps/);
  assert.doesNotMatch(packager,/ignore-missing-info|force-depends|allow-unauthenticated/);
  assert.match(dockerfile,/apt-get install -y --no-install-recommends \/native-debs\/\*\.deb/);
  assert.match(dockerfile,/\/usr\/local\/share\/devtunnel\/native\/sources/);
  const modulePatch=await readFile(new URL('../bin/p11-kit-module-soname.patch',import.meta.url),'utf8');
  for (const module of ['p11-kit-client','p11-kit-trust'])
    assert.ok(modulePatch.includes(`-Wl,-soname,${module}.so`));
});

test('MIME regression must reproduce the original memory fault before passing the fix', async () => {
  const script=await readFile(new URL('../bin/build-mime-backport',import.meta.url),'utf8');
  const probe=await readFile(new URL('../bin/native-mime-regression.c',import.meta.url),'utf8');
  assert.match(script,/AddressSanitizer: heap-buffer-overflow/);
  assert.match(script,/Expected the unpatched parser to fail/);
  assert.match(script,/--fuzz=0/);
  assert.match(script,/ASAN_OPTIONS=detect_leaks=1/);
  assert.match(probe,/_xdg_mime_magic_parse_magic_line/);
  assert.match(probe,/word <= 4/);
  assert.match(probe,/mask <= 1/);
  assert.match(probe,/match\.matchlet->value\[i\] == expected/);
});

for (const [name, plan, accepted] of [
  ['exact provider replacement', 'Remv coreutils-from-uutils [1]\nInst coreutils-from-gnu (1)\nRemv rust-coreutils [1]', true],
  ['unexpected dependency removal', 'Remv coreutils-from-uutils [1]\nRemv rust-coreutils [1]\nRemv gnome-keyring [1]', false],
  ['missing removal', 'Remv coreutils-from-uutils [1]', false],
  ['duplicate removal', 'Remv coreutils-from-uutils [1]\nRemv rust-coreutils [1]\nRemv rust-coreutils [1]', false],
]) {
  test(`GNU provider helper validates ${name} before invoking installation`, async () => {
    const directory = await mkdtemp(path.join(tmpdir(), 'hub-provider-test-'));
    const log = path.join(directory, 'calls');
    try {
      await writeFile(path.join(directory, 'apt-get'), '#!/bin/sh\ncase "$1" in\n--simulate) printf "%s\\n" "$TEST_PLAN";;\ninstall) printf "install\\n" >> "$TEST_LOG";;\n*) exit 90;;\nesac\n', {mode: 0o755});
      await writeFile(path.join(directory, 'dpkg-query'), '#!/bin/sh\nif test "$4" = rust-coreutils; then exit 1; fi\nprintf installed\n', {mode: 0o755});
      await writeFile(path.join(directory, 'dpkg'), '#!/bin/sh\nexit 0\n', {mode: 0o755});
      const script = fileURLToPath(new URL('../bin/use-gnu-coreutils', import.meta.url));
      const result = spawnSync('/bin/sh', [script], {encoding: 'utf8', timeout: 5000,
        env: {...process.env, PATH: `${directory}:/usr/bin:/bin`, TEST_PLAN: plan, TEST_LOG: log}});
      assert.equal(result.status === 0, accepted, result.stderr);
      let calls = '';
      try { calls = await readFile(log, 'utf8'); } catch (error) { if (error.code !== 'ENOENT') throw error; }
      assert.equal(calls, accepted ? 'install\n' : '');
    } finally {
      await rm(directory, {recursive: true, force: true});
    }
  });
}

import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const filename = fileURLToPath(new URL("../web/app.js", import.meta.url));
const source = await readFile(filename, "utf8");
function fixture() {
  const elements = new Map(),
    events = {},
    requests = [],
    timers = new Map();
  let nextTimer = 0;
  let initialBootstrap = true;
  function element(selector) {
    if (!elements.has(selector))
      elements.set(selector, {
        innerHTML: "",
        textContent: "",
        value: "",
        dataset: {},
        events: {},
        open: false,
        classList: { toggle() {}, add() {}, remove() {} },
        addEventListener(name, callback) {
          this.events[name] = callback;
        },
        querySelector: (child) => element(`${selector} ${child}`),
        querySelectorAll: () => [],
        insertAdjacentHTML(_position, html) {
          this.innerHTML += html;
        },
        replaceChildren() {
          this.innerHTML = "";
        },
        close() {
          this.open = false;
          this.events.close?.();
        },
        showModal() {
          this.open = true;
        },
        contains: () => false,
        matches: () => false,
      });
    return elements.get(selector);
  }
  const document = {
    hidden: false,
    activeElement: null,
    documentElement: { dataset: { theme: "light" } },
    querySelector: element,
    querySelectorAll: () => [],
    addEventListener: (name, callback) => (events[name] = callback),
  };
  class FormData extends Map {
    constructor(form) {
      super(form?.values);
    }
  }
  const context = vm.createContext({
    document,
    FormData,
    URL,
    CSS: { escape: (value) => value },
    location: {
      href: "http://localhost/",
      assign: (url) => requests.push({ redirect: url }),
    },
    history: { replaceState() {} },
    window: { addEventListener() {}, scrollX: 0, scrollY: 0, scrollTo() {} },
    setInterval() {},
    clearTimeout: (id) => timers.delete(id),
    setTimeout: (callback) => {
      timers.set(++nextTimer, callback);
      return nextTimer;
    },
    fetch: async (url, options) => {
      if (initialBootstrap) {
        initialBootstrap = false;
        return new Promise(() => {});
      }
      requests.push({ url, options });
      return { ok: true, json: async () => ({}) };
    },
  });
  vm.runInContext(source, context, { filename });
  const run = (code) => vm.runInContext(code, context);
  run(`me = {id:'admin',name:'Administrator',role:'admin',local:true,mustChange:false};
    overview = {hubId:'testhub',ready:true,proxyPort:3140,socksEnabled:true,socksPort:3180,providers:['github','microsoft'],jobs:[],sessions:[
      {id:'session-one',provider:'github',status:'running',proxy_port:3140,listener:18001,socks_port:3180,socks_listener:18002,tunnel_id:'testhub-one.test',created_at:'2026-01-01T00:00:00Z',identity:{user_id:'subject-one',user_login:'tester'}},
      {id:'session-two',provider:'microsoft',status:'login_required',proxy_port:3140,listener:18003,created_at:'2026-01-01T00:00:00Z'}]};
    catalog = {users:{revision:1,users:[]},providers:{revision:1,callback:'https://hub.example.com/auth/callback',providers:[]},settings:{revision:1,config:{hubId:'testhub',proxyPort:3140,socksPort:3180,socksEnabled:true,allowAllDomains:false,allowedDomains:[],allowedPorts:[80,443],connectPorts:[443],allowedProviders:['github','microsoft'],allowedMicrosoftTenants:[],tunnelNameTemplate:'{hub_id}-{username}',maxSessions:50,maintenanceSeconds:300,listenerStart:18001,listenerEnd:18999,healthPort:8080}}};
    dialog.querySelector = (selector) => selector === '[data-wait-job]' ? null : document.querySelector('#dialog '+selector);`);
  return { run, element, document, events, requests, context };
}

test("HTML helpers escape content and attribute boundaries without using deprecated globals", () => {
  const f = fixture();
  const value = "<img src=x onerror=\"alert(1)\"> & 'test'";
  f.context.payload = value;
  assert.equal(
    f.run("escapeHtml(payload)"),
    "&lt;img src=x onerror=&quot;alert(1)&quot;&gt; &amp; &#39;test&#39;",
  );
  assert.equal(f.run("escapeHtml(null)"), "");
  for (const expression of [
    "field(payload,payload,payload,'text',payload)",
    "area(payload,payload,payload,payload)",
    "check(payload,payload,true)",
    "select(payload,payload,[[payload,payload]],payload)",
    "heading(payload,payload)",
    "empty(payload,payload)",
    "formEnd(payload)",
    "statusBadge(payload)",
    "detailsList([[payload,payload]])",
  ]) {
    const html = f.run(expression);
    assert.ok(!html.includes("<img"), expression);
    assert.ok(html.includes("&lt;img"), expression);
    assert.ok(!html.includes('"alert(1)"'), expression);
  }
  assert.doesNotMatch(source, /\bconst (escape|status)\s*=/);
  assert.equal(
    f.run('statusBadge("running")'),
    '<span class="badge running">Conectado</span>',
  );
});

test("session renderers preserve state labels, filters, mapping and role-specific actions", () => {
  const f = fixture();
  f.run('view="sessions";renderView();');
  assert.match(f.element("#workspace").innerHTML, /HTTP \/ CONNECT/);
  assert.match(f.element("#workspace").innerHTML, /SOCKS5 TCP/);
  assert.match(f.element("#session-table").innerHTML, /HTTP 3140 → 18001/);
  assert.match(f.element("#session-table").innerHTML, /SOCKS 3180 → 18002/);
  assert.match(f.element("#session-table").innerHTML, /data-action="stop"/);
  assert.match(f.element("#session-table").innerHTML, /data-action="login"/);
  f.run('me.role="viewer"; renderSessions();');
  assert.doesNotMatch(f.element("#session-table").innerHTML, /data-action=/);
  assert.match(f.element("#session-table").innerHTML, /data-detail=/);
  f.run('filter="session-one"; renderSessions();');
  assert.match(f.element("#session-table").innerHTML, /1 de 2 sessões/);
  f.run('statusFilter="stopped"; renderSessions();');
  assert.match(f.element("#session-table").innerHTML, /Nenhum resultado/);
  f.run('filter="";statusFilter="";overview.sessions=[];renderSessions();');
  assert.match(f.element("#session-table").innerHTML, /Nenhuma sessão ainda/);
  f.run("overview.socksEnabled=false;");
  assert.match(
    f.run("socksMapping({socks_port:3180,socks_listener:18002})"),
    /desabilitado ao iniciar/,
  );
  assert.equal(f.run("socksMapping({})"), "");
  assert.match(f.run("pendingPort(3140,3141)"), /Ao iniciar: 3141/);
});

test("views render users, providers, logs, settings and authorization without leaking secrets", () => {
  const f = fixture();
  f.run(`catalog.users.users=[{id:'user-one',username:'user-one',name:'<unsafe>',role:'viewer',local:false,disabled:true,mustChange:true,sessions:[],identities:[{provider:'github',subject:'opaque'}]}];
    catalog.providers.providers=[{id:'github-one',kind:'github',label:'<unsafe>',enabled:true,registration:true,clientSecret:'synthetic-secret'}];
    logs=[{seq:1,time:'2026-01-01T00:00:00Z',event:'<unsafe>',detail:{text:'<unsafe>'}}];`);
  for (const view of ["users", "providers", "logs", "operations", "settings"]) {
    f.run(`view=${JSON.stringify(view)};renderView();`);
    assert.ok(f.element("#workspace").innerHTML.length > 50);
    assert.ok(!f.element("#workspace").innerHTML.includes("<unsafe>"));
    assert.ok(!f.element("#workspace").innerHTML.includes("synthetic-secret"));
  }
  assert.match(f.element("#logs").innerHTML, /&lt;unsafe&gt;/);
  f.run('me.role="operator";renderShell();');
  for (const view of ["users", "providers", "settings"]) {
    assert.equal(f.run(`canView(${JSON.stringify(view)})`), false);
    assert.doesNotMatch(
      f.element("#app").innerHTML,
      new RegExp(`data-nav="${view}"`),
    );
  }
  assert.match(f.run('detailActions("session-one")'), /data-action="stop"/);
  assert.doesNotMatch(
    f.run('detailActions("session-one")'),
    /data-action="remove"/,
  );
  f.run('me.role="viewer";');
  assert.equal(f.run('detailActions("session-one")'), "");
  assert.match(
    f.run(
      'jobList([{id:"job-one",action:"login",session:"session-one",status:"running",createdAt:"2026-01-01",device:{code:"HIDDEN"}}])',
    ),
    /Exibir código/,
  );
  assert.doesNotMatch(
    f.run(
      'jobList([{id:"job-one",action:"login",session:"session-one",status:"running",createdAt:"2026-01-01",device:{code:"HIDDEN"}}])',
    ),
    /HIDDEN/,
  );
});

test("LDAP and OAuth forms retain immutable identity fields and write-only secrets", () => {
  const f = fixture();
  for (const kind of ["ldap", "microsoft", "github", "oidc"]) {
    f.run(`providerForm("new", ${JSON.stringify(kind)});`);
    const html = f.element("#dialog").innerHTML;
    assert.match(html, /Salvar provedor/);
    assert.match(
      html,
      kind === "ldap" ? /name="bindPassword"/ : /name="clientSecret"/,
    );
    if (kind === "github") assert.doesNotMatch(html, /name="issuer"/);
    if (kind === "microsoft") assert.match(html, /TENANT-UUID/);
  }
  f.run(
    `catalog.providers.providers=[{id:'oidc-one',kind:'oidc',label:'OIDC',enabled:false,registration:false,issuer:'https://identity.example.com',clientId:'synthetic',clientSecretConfigured:true,clientSecret:'NEVER_RENDER'}];providerForm('oidc-one');`,
  );
  assert.doesNotMatch(f.element("#dialog").innerHTML, /NEVER_RENDER/);
  assert.match(f.element("#dialog").innerHTML, /Vazio mantém o valor atual/);
  assert.equal(f.element("#dialog [name=kind]").disabled, true);
  assert.equal(f.element("#dialog [name=issuer]").readOnly, true);
  assert.equal(f.element("#dialog [name=clientId]").readOnly, true);
  assert.equal(f.element("#dialog [name=url]").readOnly, true);
});

test("user forms retain pending-password policy, external identities and revision-aware payloads", () => {
  const f = fixture();
  f.run('userForm("new");');
  assert.match(f.element("#dialog").innerHTML, /Senha temporária/);
  assert.match(f.element("#dialog").innerHTML, /Troca obrigatória/);
  f.run('requirePasswordChange=false;userForm("new");');
  assert.match(f.element("#dialog").innerHTML, /Um aviso permanece/);
  assert.match(f.run("userPasswordField({local:true})"), /Redefinir senha/);
  assert.equal(f.run("userPasswordField({local:false})"), "");
  f.run(
    `catalog.users.users=[{id:'external',username:'external',name:'External',role:'viewer',local:false,disabled:false,sessions:['session-one'],identities:[{provider:'github',subject:'<unsafe>'}]}];userForm('external');`,
  );
  assert.doesNotMatch(f.element("#dialog").innerHTML, /name="password"/);
  assert.match(f.element("#dialog").innerHTML, /&lt;unsafe&gt;/);
  const payload = JSON.parse(
    f.run(
      `JSON.stringify(userPayload(new Map([['username','user'],['name','User'],['role','viewer'],['enabled','on'],['session:session-one','on']]),{id:'existing'},7))`,
    ),
  );
  assert.deepEqual(payload, {
    id: "existing",
    username: "user",
    name: "User",
    role: "viewer",
    disabled: false,
    sessions: ["session-one"],
    revision: 7,
  });
});

test("modal workflows preserve controls, action confirmation and owner-only device display", async () => {
  const f = fixture();
  f.run('detail("session-one");');
  assert.match(f.element("#dialog").innerHTML, /SOCKS5 atual/);
  assert.match(f.element("#dialog").innerHTML, /data-action="remove"/);
  assert.equal(f.element("#dialog").open, true);
  assert.equal(f.element("#dialog").scrollTop, 0);
  f.run('confirmAction("remove","session-one");');
  assert.match(f.element("#dialog").innerHTML, /marcada como removida/);
  f.run('confirmAction("logout","session-one");');
  assert.match(f.element("#dialog").innerHTML, /credenciais do túnel/);
  f.run("newSession();");
  assert.match(f.element("#dialog").innerHTML, /SOCKS5 TCP: 3180/);
  f.run("policyForm();");
  assert.match(f.element("#dialog").innerHTML, /Porta do proxy/);
  assert.match(
    f.element("#dialog .form-grid").innerHTML,
    /Habilitar SOCKS5 TCP CONNECT/,
  );
  f.run("passwordForm(true);");
  assert.match(f.element("#dialog").innerHTML, /Troca obrigatória/);
  f.run(
    'device(undefined);device({id:"job",session:"session-one",device:{url:"https://github.com/login/device",code:"ABCD-1234"}});',
  );
  assert.match(f.element("#dialog").innerHTML, /ABCD-1234/);
  assert.match(f.element("#dialog").innerHTML, /somente para quem iniciou/);
});

test("HTTP requests retain same-origin cookies, CSRF and passive-refresh marker", async () => {
  const f = fixture();
  f.run('csrf="synthetic-csrf";');
  await f.run('api("/overview",undefined,true)');
  await f.run('api("/jobs",{action:"stop",session:"session-one"})');
  assert.equal(f.requests[0].options.credentials, "same-origin");
  assert.equal(f.requests[0].options.headers["X-Hub-Background"], "true");
  assert.equal(f.requests[1].options.headers["X-CSRF-Token"], "synthetic-csrf");
  assert.equal(f.requests[1].options.method, "POST");
  f.run('fetch=async()=>({ok:false,json:async()=>({error:"FORBIDDEN"})});');
  await assert.rejects(f.run('api("/users")'), /Seu perfil não permite/);
});

test("stale overview/catalog/log requests cannot populate a replacement login", async () => {
  const f = fixture();
  f.run(
    'api=async()=>{authGeneration++;return {sessions:[{id:"stale"}],users:[{id:"stale"}],cursor:99,records:[{seq:99}]}};',
  );
  assert.equal(await f.run("refreshOverview(authGeneration,true)"), false);
  assert.equal(f.run('overview.sessions.some(s=>s.id==="stale")'), false);
  f.run('view="users";');
  await f.run("loadCatalog(true)");
  assert.equal(f.run("catalog.users.users.length"), 0);
  assert.equal(await f.run("refreshLogs(authGeneration,true)"), false);
  assert.equal(f.run("cursor"), 0);
});

test("click dispatch ignores unrelated buttons and keeps structured actions and confirmation", async () => {
  const f = fixture();
  let calls = 0;
  f.context.capture = () => calls++;
  f.run("clickHandlers.refresh=capture;");
  await f.events.click({ target: { closest: () => null } });
  await f.events.click({
    target: { closest: () => ({ dataset: { themeToggle: "" } }) },
  });
  await f.events.click({
    target: { closest: () => ({ dataset: { refresh: "" } }) },
  });
  assert.equal(calls, 1);
  f.run(
    "globalThis.actionsRun=[];submitAction=async(...args)=>actionsRun.push(args);",
  );
  await f.run('runSessionAction({dataset:{action:"stop",id:"session-one"}})');
  assert.deepEqual(JSON.parse(f.run("JSON.stringify(actionsRun)")), [
    ["stop", "session-one"],
  ]);
  await f.run('runSessionAction({dataset:{action:"remove",id:"session-one"}})');
  assert.match(f.element("#dialog").innerHTML, /confirmar/);
  f.run('me.role="viewer";view="sessions";');
  await f.run('navigate({dataset:{nav:"users"}})');
  assert.equal(f.run("view"), "sessions");
});

test("logo foreground/background meet contrast and CSS no longer duplicates button selector", async () => {
  const css = await readFile(
    new URL("../web/app.css", import.meta.url),
    "utf8",
  );
  const background = css.match(
    /\.brand-mark\s*\{[^}]*background:\s*(#[a-f0-9]{6})/i,
  )[1];
  const linear = (value) =>
    value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  const channels = background
    .slice(1)
    .match(/../g)
    .map((value) => linear(Number.parseInt(value, 16) / 255));
  const luminance =
    channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
  assert.ok(1.05 / (luminance + 0.05) >= 4.5);
  assert.equal([...css.matchAll(/^button\s*\{/gm)].length, 1);
  const html = await readFile(
    new URL("../web/index.html", import.meta.url),
    "utf8",
  );
  assert.match(html, /<output id="toast" aria-live="polite"><\/output>/);
  assert.match(html, /<script src="\/app.js" type="module"><\/script>/);
});

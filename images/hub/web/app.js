"use strict";
const $ = (s) => document.querySelector(s),
  app = $("#app"),
  dialog = $("#dialog");
dialog.addEventListener("close", () => {
  if (!dialog.open) dialog.replaceChildren();
});
const escapeHtml = (v) =>
  String(v ?? "").replace(
    /[&<>"']/g,
    (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[
        c
      ],
  );
const icons = {
  grid: "M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z",
  tunnel:
    "M5 20V9a7 7 0 0 1 14 0v11 M2 12h13m-3-3 3 3-3 3 M22 17H9m3-3-3 3 3 3",
  activity: "M3 12h4l3-8 4 16 3-8h4",
  users:
    "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M16 3a4 4 0 0 1 0 8 M22 21v-2a4 4 0 0 0-3-3.87 M13 7a4 4 0 1 1-8 0 4 4 0 0 1 8 0",
  shield: "M12 3l8 3v6c0 5-8 9-8 9s-8-4-8-9V6z M8 12l3 3 5-6",
  settings:
    "M9 2h6l.5 2.7 1.5.9 2.6-.9 3 5.2-2.1 1.2v1.8l2.1 1.2-3 5.2-2.6-.9-1.5.9L15 22H9l-.5-2.7-1.5-.9-2.6.9-3-5.2 2.1-1.2v-1.8L1.4 9.9l3-5.2 2.6.9 1.5-.9z M15.5 12a3.5 3.5 0 1 1-7 0 3.5 3.5 0 0 1 7 0",
  sun: "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8 M12 2v2 M12 20v2 M2 12h2 M20 12h2 M5 5l1.5 1.5 M17.5 17.5 19 19 M5 19l1.5-1.5 M17.5 6.5 19 5",
  moon: "M20.5 13.2A9 9 0 0 1 10.8 3.5 9 9 0 1 0 20.5 13.2z",
  log: "M4 4h16v16H4z M8 8h8 M8 12h8 M8 16h5",
  plus: "M12 5v14 M5 12h14",
  search: "M21 21l-5-5 M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0",
  play: "M7 4l14 8-14 8z",
  stop: "M5 5h14v14H5z",
  key: "M14 8a5 5 0 1 1-10 0 5 5 0 0 1 10 0 M12 12l9 9 M16 16l3-3 M18 18l3-3",
  close: "M6 6l12 12 M6 18L18 6",
  arrow: "M5 12h14 M13 6l6 6-6 6",
  exit: "M9 4H4v16h5 M10 12h11 M17 8l4 4-4 4",
  refresh: "M20 7v5h-5 M4 17v-5h5 M6 6a8 8 0 0 1 14 6 M4 12a8 8 0 0 0 14 6",
  dots: "M5 12h.01 M12 12h.01 M19 12h.01",
  clock: "M12 8v4l3 2 M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0",
  check: "M5 12l4 4L19 6",
  copy: "M9 9h12v12H9z M15 5V3H3v12h2",
};
const icon = (name) =>
  `<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="${icons[name] ?? icons.tunnel}"/></svg>`;
const themeToggle = () => {
  const label =
    document.documentElement.dataset.theme === "dark"
      ? "Ativar modo claro"
      : "Ativar modo escuro";
  return `<button type="button" class="ghost theme-toggle" data-theme-toggle aria-label="${label}" title="${label}"><span class="theme-moon">${icon("moon")}</span><span class="theme-sun">${icon("sun")}</span></button>`;
};
const labels = {
  running: "Conectado",
  starting: "Conectando",
  ready: "Autenticado",
  stopped: "Parado",
  login_required: "Aguardando login",
  reauth_required: "Renovar login",
  error: "Erro",
  removed: "Removido",
  done: "Concluído",
  failed: "Falhou",
  active: "Ativo",
  pending: "Pendente",
};
const statusBadge = (value) =>
  `<span class="badge ${Object.hasOwn(labels, value) ? value : ""}">${escapeHtml(labels[value] ?? value)}</span>`;
const roleName = {
  admin: "Administrador",
  operator: "Operador",
  viewer: "Leitor",
};
const actions = {
  add: "Criar sessão",
  login: "Autenticar",
  start: "Iniciar",
  stop: "Parar",
  logout: "Desconectar conta",
  remove: "Remover sessão",
};
const messages = {
  LOGIN_FAILED: "Credenciais inválidas ou provedor indisponível.",
  LOGIN_REQUIRED: "Sua sessão expirou. Entre novamente.",
  PASSWORD_TOO_WEAK: "Use uma senha com pelo menos 14 caracteres.",
  PASSWORD_CHANGE_REQUIRED: "Altere sua senha para continuar.",
  ACCOUNT_PENDING_APPROVAL:
    "Conta registrada. Aguarde a aprovação de um administrador.",
  ACCOUNT_NOT_ENROLLED: "Conta não habilitada neste Hub.",
  STOP_SESSIONS_FIRST: "Pare todas as sessões antes de alterar a política.",
  INVALID_PROXY_PORT: "Informe uma porta inteira entre 1024 e 65535.",
  INVALID_SOCKS_PORT:
    "Informe uma porta SOCKS5 inteira entre 1024 e 65535, diferente da porta HTTP.",
  INVALID_SOCKS_ENABLED:
    "A opção SOCKS5 deve estar habilitada ou desabilitada.",
  RESERVED_PORT:
    "As portas dos proxies não podem estar na faixa dos listeners.",
  WEB_PORT_COLLISION: "Uma porta de proxy conflita com a porta do console.",
  INVALID_HEALTH_PORT: "Uma porta de proxy conflita com a porta de saúde.",
  TUNNEL_PORT_POLICY_CHANGED:
    "A porta remota não corresponde à configuração conhecida. Verifique o túnel antes de tentar novamente.",
  CONFIG_CONFLICT: "A configuração mudou. Feche e reabra o formulário.",
  CSRF_REJECTED: "A sessão de segurança mudou. Atualize a página.",
  FORBIDDEN: "Seu perfil não permite esta ação.",
  RATE_LIMITED: "Muitas tentativas. Aguarde um minuto.",
  AUTH_BUSY: "Autenticação ocupada. Tente novamente em instantes.",
  PROVIDER_IDENTITY_IMMUTABLE:
    "Para mudar a origem das identidades, cadastre um novo provedor com outro ID.",
  RECOVERY_ADMIN_PROTECTED:
    "O administrador local de recuperação não pode ser desativado ou rebaixado.",
  SELF_LOCKOUT_PREVENTED:
    "Você não pode remover seu próprio acesso administrativo.",
};
let me,
  csrf = "",
  requirePasswordChange = true,
  view = "sessions",
  overview = { sessions: [], jobs: [] },
  catalog = {},
  logs = [],
  cursor = 0,
  filter = "",
  statusFilter = "",
  paused = false,
  refreshing = false,
  authGeneration = 0,
  eventSource,
  liveState = "connecting",
  liveTimer,
  liveSeen = 0,
  liveReset = false,
  pendingTopics = new Set();
const mustChangePassword = () =>
  Boolean(me?.mustChange && requirePasswordChange);
function passwordWarning() {
  return me?.local && me.mustChange
    ? `<aside class="password-warning note warning" role="status"><div><strong>Sua senha ainda não foi alterada.</strong><p>Você está usando uma senha definida na criação da conta ou em uma redefinição. Recomendamos alterá-la.</p></div><button data-password>${icon("key")}Alterar senha</button></aside>`
    : "";
}
const nav = [
  ["sessions", "tunnel", "Túneis"],
  ["operations", "activity", "Operações"],
  ["logs", "log", "Logs e auditoria"],
  ["users", "users", "Usuários"],
  ["providers", "shield", "Autenticação"],
  ["settings", "settings", "Configurações"],
];
async function api(route, data, background = false) {
  const response = await fetch(`/api${route}`, {
    method: data === undefined ? "GET" : "POST",
    credentials: "same-origin",
    headers: {
      ...(background ? { "X-Hub-Background": "true" } : {}),
      ...(data === undefined
        ? {}
        : { "Content-Type": "application/json", "X-CSRF-Token": csrf }),
    },
    body: data === undefined ? undefined : JSON.stringify(data),
  });
  const result = await response.json();
  if (!response.ok) {
    const error = new Error(
      messages[result.error] ?? result.error ?? "Falha na operação",
    );
    error.code = result.error;
    throw error;
  }
  return result;
}
function toast(message) {
  $("#toast").textContent = message;
  $("#toast").classList.add("show");
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => $("#toast").classList.remove("show"), 5000);
}
const brand = () =>
  `<div class="brand"><span class="brand-mark">${icon("tunnel")}</span><div>DevTunnel Toolkit<small>HUB CONSOLE</small></div></div>`;
const empty = (title, text, kind = "tunnel") =>
  `<div class="empty">${icon(kind)}<h3>${escapeHtml(title)}</h3><p>${escapeHtml(text)}</p></div>`;
function modal(title, subtitle, content) {
  dialog.innerHTML = `<div class="dialog-head"><div><h2>${escapeHtml(title)}</h2><p>${escapeHtml(subtitle)}</p></div><button class="ghost" data-close aria-label="Fechar">${icon("close")}</button></div><div class="dialog-body">${content}</div>`;
  if (!dialog.open) dialog.showModal();
  dialog.scrollTop = 0;
}
function field(label, name, value = "", type = "text", hint = "", extra = "") {
  name = escapeHtml(name);
  type = escapeHtml(type);
  extra = extra.replaceAll("-]", String.raw`\-]`);
  return `<div class="field"><label for="f-${name}">${escapeHtml(label)}</label><input id="f-${name}" name="${name}" type="${type}" value="${escapeHtml(value)}" ${extra}>${fieldHint(hint)}</div>`;
}
function fieldHint(hint) {
  return hint ? `<span class="hint">${escapeHtml(hint)}</span>` : "";
}
function area(label, name, value = "", hint = "") {
  name = escapeHtml(name);
  return `<div class="field full"><label for="f-${name}">${escapeHtml(label)}</label><textarea id="f-${name}" name="${name}">${escapeHtml(value)}</textarea>${fieldHint(hint)}</div>`;
}
function check(label, name, checked) {
  name = escapeHtml(name);
  return `<label class="check"><input type="checkbox" name="${name}" ${checked ? "checked" : ""}>${escapeHtml(label)}</label>`;
}
function select(label, name, options, value) {
  name = escapeHtml(name);
  const content = options.map(([v, t]) => option(v, t, v === value)).join("");
  return `<div class="field"><label for="f-${name}">${escapeHtml(label)}</label><select id="f-${name}" name="${name}">${content}</select></div>`;
}
function option(value, label, selected) {
  return `<option value="${escapeHtml(value)}" ${selected ? "selected" : ""}>${escapeHtml(label)}</option>`;
}
function detailsList(entries) {
  return entries
    .map(
      ([label, value]) =>
        `<dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd>`,
    )
    .join("");
}
function checkedProviders(providers) {
  return providers
    .map(([id, label, enabled]) => check(label, id, enabled))
    .join("");
}
const formEnd = (label = "Salvar alterações") =>
  `<p class="form-error" role="alert"></p><div class="form-actions"><button type="button" data-close>Cancelar</button><button class="primary" type="submit">${icon("check")}${escapeHtml(label)}</button></div>`;
function wireForm(callback) {
  const form = dialog.querySelector("form");
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const button = form.querySelector("[type=submit]");
    button.disabled = true;
    form.querySelector(".form-error").textContent = "";
    try {
      await callback(new FormData(form));
    } catch (err) {
      form.querySelector(".form-error").textContent = err.message;
    } finally {
      button.disabled = false;
    }
  });
}
function loginProviderButton(provider) {
  return `<button data-sso="${escapeHtml(provider.id)}">${icon("shield")}Continuar com ${escapeHtml(provider.label)}${icon("arrow")}</button>`;
}
async function loginPage() {
  stopLive();
  authGeneration++;
  me = undefined;
  csrf = "";
  catalog = {};
  logs = [];
  cursor = 0;
  dialog.close();
  dialog.replaceChildren();
  const options = await api("/auth/options");
  const external = options.providers.filter((p) => p.kind !== "ldap"),
    ldap = options.providers.filter((p) => p.kind === "ldap");
  app.innerHTML = `<main class="login-form-area"><section class="login-form" aria-labelledby="login-title">${brand()}<h1 id="login-title">Entre na sua conta</h1><p>Gerencie suas conexões em um só lugar.</p><div class="login-providers">${external.map(loginProviderButton).join("")}</div>${external.length ? '<div class="or">ou use suas credenciais</div>' : ""}<form id="login-form">${ldap.length ? select("Autenticar em", "provider", [["", "Conta local"], ...ldap.map((p) => [p.id, p.label])], "") : ""}${field("Usuário", "username", "", "text", "", 'required autocomplete="username" maxlength="256"')}${field("Senha", "password", "", "password", "", 'required autocomplete="current-password" maxlength="1024"')}<button type="submit" class="primary">Entrar no console ${icon("arrow")}</button><p class="form-error" role="alert"></p></form><ul class="login-features" aria-label="Recursos do console"><li>${icon("users")}<span>Identidades<br>individuais</span></li><li>${icon("tunnel")}<span>Túneis<br>privados</span></li><li>${icon("shield")}<span>Auditoria<br>por sessão</span></li></ul><details class="login-help"><summary>Primeiro acesso ou recuperação do administrador?</summary><p>Defina a senha pelo CLI do container:<br><code>hub admin reset-password admin --password-stdin</code></p></details></section></main>`;
  $(".login-form-area").insertAdjacentHTML(
    "afterbegin",
    `<div class="login-theme">${themeToggle()}</div>`,
  );
  const loginError = new URL(location.href).searchParams.get("login");
  if (loginError) {
    $("#login-form .form-error").textContent =
      messages[loginError] ?? messages.LOGIN_FAILED;
    history.replaceState({}, "", "/");
  }
  $("#login-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const button = e.target.querySelector("[type=submit]");
    button.disabled = true;
    try {
      const data = Object.fromEntries(new FormData(e.target));
      if (!data.provider) delete data.provider;
      await api("/auth/login", data);
      await bootstrap();
    } catch (err) {
      $("#login-form .form-error").textContent = err.message;
    } finally {
      button.disabled = false;
    }
  });
}
async function bootstrap() {
  stopLive();
  authGeneration++;
  try {
    const response = await api("/me");
    me = response.user;
    csrf = response.csrf;
    requirePasswordChange = response.requirePasswordChange !== false;
  } catch (e) {
    if (e.code === "LOGIN_REQUIRED") {
      await loginPage();
      return;
    }
    throw e;
  }
  if (mustChangePassword()) {
    app.innerHTML = `<main class="login-form-area"><div class="login-form">${brand()}<h2>Proteja sua conta</h2><p>Defina sua senha pessoal para concluir o acesso.</p><button class="primary" data-password>Alterar senha</button><button class="ghost" data-logout>Sair</button></div></main>`;
    $(".login-form-area").insertAdjacentHTML(
      "afterbegin",
      `<div class="login-theme">${themeToggle()}</div>`,
    );
    passwordForm(true);
    return;
  }
  if (me.role !== "admin" && ["users", "providers", "settings"].includes(view))
    view = "sessions";
  renderShell();
  $(".account").insertAdjacentHTML("afterbegin", themeToggle());
  await refresh(true);
  startLive();
}
function canView(id) {
  return (
    me.role === "admin" || !["users", "providers", "settings"].includes(id)
  );
}
function navButton([id, kind, label]) {
  return `<button data-nav="${id}" class="${view === id ? "active" : ""}">${icon(kind)}${label}</button>`;
}
function renderShell() {
  const navigation = nav
    .filter(([id]) => canView(id))
    .map(navButton)
    .join("");
  const passwordButton = me.local
    ? `<button class="ghost small" data-password title="Alterar minha senha" aria-label="Alterar minha senha">${icon("key")}</button>`
    : "";
  app.innerHTML = `<div class="shell"><aside class="sidebar">${brand()}<div class="nav-label">Workspace</div><nav aria-label="Navegação principal">${navigation}</nav><div class="sidebar-bottom"><span class="badge active">Console autenticado</span><p><span data-live-status role="status">Conectando ao fluxo</span></p><p>Credenciais isoladas por sessão.<br>Tráfego identificado por listener.</p><div class="divider"></div><span>DevTunnel Toolkit Hub</span></div></aside><div class="main"><header class="topbar"><div class="breadcrumb">${icon("grid")} Workspace <span>/</span> <strong id="hub-label">Hub</strong></div><div class="account"><span class="avatar">${escapeHtml(me.name.slice(0, 2).toUpperCase())}</span><span class="account-label">${escapeHtml(me.name)}<small>${escapeHtml(roleName[me.role])}</small></span>${passwordButton}<button class="ghost small" data-logout title="Sair" aria-label="Sair">${icon("exit")}</button></div></header>${passwordWarning()}<main class="workspace" id="workspace"></main></div></div>`;
}
function heading(title, description, buttons = "") {
  return `<div class="page-heading"><div><span class="eyebrow">CONTROL PLANE</span><h1>${escapeHtml(title)}</h1><p>${escapeHtml(description)}</p></div><div class="heading-actions">${buttons}</div></div>`;
}
function renderView() {
  const workspace = $("#workspace");
  if (!workspace) return;
  document.querySelectorAll("[data-nav]").forEach((button) => {
    button.classList.toggle("active", button.dataset.nav === view);
  });
  $("#hub-label").textContent = overview.hubId ?? "Hub";
  const renderers = {
    sessions: renderSessionView,
    operations: renderOperations,
    logs: renderLogView,
    users: renderUsers,
    providers: renderProviders,
    settings: renderSettings,
  };
  renderers[view]?.(workspace);
  workspace
    .querySelector(".page-heading p")
    ?.insertAdjacentHTML(
      "afterend",
      '<p class="hint" data-live-status role="status"></p>',
    );
  liveStatus();
}
function sessionStats(sessions) {
  const running = sessions.filter(
    (session) => session.status === "running",
  ).length;
  const attention = sessions.filter((session) =>
    ["error", "reauth_required", "login_required"].includes(session.status),
  ).length;
  const users = new Set(
    sessions.map((session) => session.identity?.user_id).filter(Boolean),
  ).size;
  return [
    ["Sessões", "tunnel", sessions.length, "No seu escopo de acesso"],
    ["Conectadas", "activity", running, "Relays em execução"],
    ["Identidades", "users", users, "Contas autenticadas nos túneis"],
    ["Precisam de atenção", "shield", attention, "Login pendente ou erro"],
  ]
    .map(statCard)
    .join("");
}
function statCard([label, kind, count, foot]) {
  return `<div class="stat"><span class="eyebrow">${escapeHtml(label)}</span><span class="stat-icon">${icon(kind)}</span><strong>${count}</strong><span class="foot">${escapeHtml(foot)}</span></div>`;
}
function sessionFilters() {
  const states = [
    "running",
    "starting",
    "stopped",
    "ready",
    "login_required",
    "reauth_required",
    "error",
  ];
  const options = states
    .map((state) => option(state, labels[state], statusFilter === state))
    .join("");
  return `<div class="toolbar"><div class="search">${icon("search")}<input id="search" type="search" placeholder="Buscar sessão, conta ou túnel..." aria-label="Buscar sessões" value="${escapeHtml(filter)}"></div><select id="status-filter" aria-label="Filtrar estado"><option value="">Todos os estados</option>${options}</select></div>`;
}
function isolationPanel() {
  const socksPort = overview.socksEnabled ? overview.socksPort : "Desabilitado";
  return `<section class="panel"><div class="panel-head"><h2>Acesso e isolamento</h2>${icon("shield")}</div><div class="panel-body"><div class="note">O login no console controla a gerência. O login do túnel é individual e acontece pelo fluxo oficial do provedor.</div><div class="detail-grid"><span class="muted">HTTP / CONNECT</span><code>${escapeHtml(overview.proxyPort)}</code><span class="muted">SOCKS5 TCP</span><code>${escapeHtml(socksPort)}</code><span class="muted">Escopo remoto</span><span>Somente o proprietário do túnel</span><span class="muted">Seu perfil</span><span>${escapeHtml(roleName[me.role])}</span></div></div></section>`;
}
function renderSessionView(workspace) {
  const create =
    me.role === "admin"
      ? `<button class="primary" data-create>${icon("plus")}Nova sessão</button>`
      : "";
  const buttons = `<button data-refresh>${icon("refresh")}Atualizar</button>${create}`;
  const title = heading(
    "Túneis e sessões",
    "Controle conexões, identidades e disponibilidade em tempo real.",
    buttons,
  );
  workspace.innerHTML = `${title}<div class="stats">${sessionStats(overview.sessions)}</div><section class="panel"><div class="panel-head"><div><h2>Sessões do workspace</h2><p>Túnel privado por identidade. Squid compartilhado com listeners isolados.</p></div>${statusBadge(overview.ready ? "active" : "error")}</div>${sessionFilters()}<div id="session-table"></div></section><div class="split"><section class="panel"><div class="panel-head"><h2>Operações recentes</h2><button class="ghost small" data-nav="operations">Ver todas ${icon("arrow")}</button></div>${jobList(overview.jobs.slice(-4).reverse())}</section>${isolationPanel()}</div><div class="footer-note">SSE autenticado · Horários locais do navegador</div>`;
  renderSessions();
  $("#search").addEventListener("input", (event) => {
    filter = event.target.value;
    renderSessions();
  });
  $("#status-filter").addEventListener("change", (event) => {
    statusFilter = event.target.value;
    renderSessions();
  });
}
function renderOperations(workspace) {
  const title = heading(
    "Operações",
    "Ações estruturadas do CLI, com estado e resultado por execução.",
  );
  workspace.innerHTML = `${title}<section class="panel"><div class="panel-head"><h2>Histórico recente</h2><span class="muted">Retenção em memória: até 10 min após conclusão</span></div>${jobList([...overview.jobs].reverse())}</section><div class="note">Não há terminal shell remoto. Login, criação, início, parada, logout e remoção usam ações validadas. Um código de dispositivo só aparece para quem iniciou o login.</div>`;
}
function renderLogView(workspace) {
  const button = `<button data-pause>${icon(paused ? "play" : "stop")}${paused ? "Retomar" : "Pausar"}</button>`;
  const title = heading(
    "Logs e auditoria",
    "Eventos do gerenciador e tráfego do proxy, filtrados pelo seu acesso.",
    button,
  );
  workspace.innerHTML = `${title}<section class="panel"><div class="panel-head"><h2>Fluxo de eventos</h2><span class="badge ${paused ? "" : "active"}">${paused ? "Pausado" : "Ao vivo"}</span></div><div class="logs" id="logs"></div><div class="table-foot"><span>Buffer limitado · Sem caminhos, headers ou tokens</span><span>${logs.length} eventos no navegador</span></div></section><div class="note">O buffer é temporário e é limpo ao reiniciar o container. Para retenção durável, encaminhe os logs JSON do container ao coletor do seu ambiente.</div>`;
  renderLogs();
}
function pendingPort(actual, configured, label = "Ao iniciar") {
  return actual === configured
    ? ""
    : `<small>${escapeHtml(label)}: ${escapeHtml(configured)}</small>`;
}
function socksMapping(session) {
  if (!session.socks_port) {
    return overview.socksEnabled
      ? pendingPort(undefined, overview.socksPort, "SOCKS ao iniciar")
      : "";
  }
  const pending = overview.socksEnabled
    ? pendingPort(session.socks_port, overview.socksPort)
    : "<small>SOCKS desabilitado ao iniciar</small>";
  return `<code>SOCKS ${escapeHtml(session.socks_port)} → ${escapeHtml(session.socks_listener)}</code>${pending}`;
}
function proxyMapping(session) {
  return `<code>HTTP ${escapeHtml(session.proxy_port)} → ${escapeHtml(session.listener)}</code>${pendingPort(session.proxy_port, overview.proxyPort)}${socksMapping(session)}`;
}
function primaryAction(session) {
  if (["running", "starting"].includes(session.status))
    return ["stop", "stop", "Parar"];
  if (session.status.includes("login")) return ["login", "key", "Login"];
  return ["start", "play", "Iniciar"];
}
function sessionButtons(session) {
  const [action, kind, label] = primaryAction(session);
  const primary =
    me.role === "viewer"
      ? ""
      : `<button class="small" data-action="${action}" data-id="${escapeHtml(session.id)}">${icon(kind)}${label}</button>`;
  return `<div class="row-actions">${primary}<button class="small" data-detail="${escapeHtml(session.id)}" title="Detalhes e ações" aria-label="Detalhes de ${escapeHtml(session.id)}">${icon("dots")}</button></div>`;
}
function sessionRow(session) {
  const provider = session.provider === "github" ? "GitHub" : "Microsoft";
  return `<tr><td><div class="cell-title"><span class="cell-icon">${icon("tunnel")}</span><div><strong>${escapeHtml(session.id)}</strong><small>${escapeHtml(session.identity?.user_login ?? "Identidade ainda não vinculada")}</small></div></div></td><td><code>${escapeHtml(session.tunnel_id ?? session.tunnel_name ?? "Resolvido após login")}</code><small>${escapeHtml(session.error ?? "Acesso privado")}</small></td><td>${provider}</td><td>${statusBadge(session.status)}</td><td>${proxyMapping(session)}</td><td>${sessionButtons(session)}</td></tr>`;
}
function sessionMatches(session) {
  if (statusFilter && session.status !== statusFilter) return false;
  return [session.id, session.identity?.user_login, session.tunnel_id]
    .join(" ")
    .toLowerCase()
    .includes(filter.toLowerCase());
}
function renderSessions() {
  const target = $("#session-table");
  if (!target) return;
  const rows = overview.sessions.filter(sessionMatches);
  if (!rows.length) {
    const filtered = filter || statusFilter;
    target.innerHTML = empty(
      filtered ? "Nenhum resultado" : "Nenhuma sessão ainda",
      filtered
        ? "Tente ajustar os filtros."
        : "Crie uma sessão e autentique a conta que será proprietária do túnel.",
    );
    return;
  }
  const content = rows.map(sessionRow).join("");
  target.innerHTML = `<div class="table-wrap"><table><thead><tr><th>Sessão / identidade</th><th>Túnel</th><th>Provedor</th><th>Estado</th><th>Mapeamento</th><th>Ações</th></tr></thead><tbody>${content}</tbody></table></div><div class="table-foot"><span>${rows.length} de ${overview.sessions.length} sessões</span><span>Uma identidade por sessão</span></div>`;
}
function jobItem(job) {
  const error = job.error ? ` · ${escapeHtml(job.error)}` : "";
  const deviceButton = job.device
    ? `<button class="small" data-device="${escapeHtml(job.id)}">Exibir código</button>`
    : "";
  return `<div class="activity-item"><span class="cell-icon">${icon(job.action === "login" ? "key" : "activity")}</span><div class="body"><strong>${escapeHtml(actions[job.action] ?? job.action)} <span class="muted">/ ${escapeHtml(job.session)}</span></strong><small>${escapeHtml(new Date(job.createdAt).toLocaleString())}${error}</small></div>${statusBadge(job.status)}${deviceButton}</div>`;
}
function jobList(jobs) {
  if (!jobs.length)
    return empty(
      "Nenhuma operação recente",
      "As ações iniciadas pelo console aparecem aqui.",
      "activity",
    );
  const items = jobs.map(jobItem).join("");
  return `<div class="activity">${items}</div>`;
}
function logDetails(record) {
  return Object.entries(record)
    .filter(([key]) => !["time", "seq", "event"].includes(key))
    .map(([key, value]) => {
      const text = typeof value === "object" ? JSON.stringify(value) : value;
      return `${key}=${text}`;
    })
    .join(" ");
}
function logLine(record) {
  return `<div class="log-line"><span class="log-time">${escapeHtml(new Date(record.time).toLocaleTimeString())}</span> <span class="log-event">${escapeHtml(record.event)}</span> ${escapeHtml(logDetails(record))}</div>`;
}
function renderLogs() {
  const element = $("#logs");
  if (!element) return;
  const position = element.scrollTop;
  const follow = element.scrollHeight - element.clientHeight - position < 32;
  element.innerHTML = logs.length
    ? logs.map(logLine).join("")
    : "Aguardando eventos...";
  element.scrollTop = follow ? element.scrollHeight : position;
}
function pendingPassword(user) {
  if (!user.mustChange) return "";
  return requirePasswordChange
    ? "Troca de senha obrigatória"
    : "Senha ainda não alterada";
}
function userRow(user) {
  const origin = user.local
    ? "Local"
    : user.identities.map((identity) => identity.provider).join(", ");
  const scope = user.role === "admin" ? "Todas" : user.sessions.length;
  return `<tr><td><strong>${escapeHtml(user.name)}</strong><small>${escapeHtml(user.username)}</small></td><td>${escapeHtml(origin)}</td><td>${escapeHtml(roleName[user.role])}</td><td>${statusBadge(user.disabled ? "pending" : "active")}<small>${pendingPassword(user)}</small></td><td>${scope}</td><td><button class="small" data-user="${escapeHtml(user.id)}">Gerenciar ${icon("arrow")}</button></td></tr>`;
}
function renderUsers(target) {
  const users = catalog.users?.users ?? [];
  const title = heading(
    "Usuários e permissões",
    "Aprove contas, defina perfis e limite quais sessões cada pessoa pode gerenciar.",
    `<button class="primary" data-user="new">${icon("plus")}Novo usuário</button>`,
  );
  const rows = users.map(userRow).join("");
  target.innerHTML = `${title}<div class="note">Contas externas são identificadas pelo ID estável do provedor, nunca pelo e-mail. Novos cadastros externos precisam de aprovação. O usuário local <code>admin</code> é reservado para recuperação.</div><section class="panel"><div class="table-wrap"><table><thead><tr><th>Usuário</th><th>Origem</th><th>Perfil</th><th>Acesso</th><th>Sessões</th><th></th></tr></thead><tbody>${rows}</tbody></table></div></section>`;
}
function providerCard(provider) {
  const registration = provider.registration
    ? "Novas contas entram pendentes de aprovação."
    : "Novos cadastros desativados. Apenas contas já aprovadas.";
  return `<article class="provider-card"><div class="provider-title"><span class="cell-icon">${icon("shield")}</span><div><h3>${escapeHtml(provider.label)}</h3><small>${escapeHtml(provider.kind.toUpperCase())} / ${escapeHtml(provider.id)}</small></div></div><p>${registration}</p><div class="card-foot">${statusBadge(provider.enabled ? "active" : "stopped")}<button class="small" data-provider="${escapeHtml(provider.id)}">Configurar ${icon("arrow")}</button></div></article>`;
}
function renderProviders(target) {
  const providers = catalog.providers?.providers ?? [];
  const title = heading(
    "Autenticação",
    "Configure os provedores do console, sem compartilhar credenciais dos túneis.",
    `<button class="primary" data-provider="new">${icon("plus")}Adicionar provedor</button>`,
  );
  const cards = providers.map(providerCard).join("");
  target.innerHTML = `${title}<div class="note">Callback OAuth/OIDC: <code>${escapeHtml(catalog.providers?.callback ?? "")}</code><br>Cadastre esta URL no aplicativo do provedor. Segredos são gravados criptografados e nunca retornam ao navegador.</div><div class="cards"><article class="provider-card"><div class="provider-title"><span class="cell-icon">${icon("key")}</span><div><h3>Contas locais</h3><small>RECUPERAÇÃO E ACESSO DIRETO</small></div></div><p>Senhas com scrypt. Administrador de recuperação sempre disponível pelo CLI do container.</p><div class="card-foot">${statusBadge("active")}<button class="small" data-nav="users">Gerenciar usuários</button></div></article>${cards}</div>`;
}
function policyDetails(config) {
  return detailsList([
    [
      "Domínios",
      config.allowAllDomains
        ? "Todos (opt-in)"
        : config.allowedDomains.join(", ") || "Nenhum",
    ],
    ["Porta do proxy", config.proxyPort],
    ["SOCKS5 TCP CONNECT", config.socksEnabled ? "Habilitado" : "Desabilitado"],
    ["Porta SOCKS5", config.socksPort],
    ["Portas de destino", config.allowedPorts.join(", ")],
    ["CONNECT", config.connectPorts.join(", ")],
    ["Login do túnel", config.allowedProviders.join(", ")],
    [
      "Tenant permitido",
      config.allowedMicrosoftTenants.join(", ") || "Qualquer tenant",
    ],
    ["Nome automático", config.tunnelNameTemplate],
    ["Limite de sessões", config.maxSessions],
    ["Manutenção", `${config.maintenanceSeconds} s`],
  ]);
}
function renderSettings(target) {
  const config = catalog.settings?.config;
  if (!config) {
    target.innerHTML = empty("Carregando configuração", "Aguarde...");
    return;
  }
  const title = heading(
    "Configurações do Hub",
    "Políticas operacionais com validação antes de aplicar.",
    `<button class="primary" data-policy>${icon("settings")}Editar política</button>`,
  );
  const source = catalog.settings.override
    ? "Personalizada no painel"
    : "Ambiente / arquivo";
  target.innerHTML = `${title}<div class="note warning">Para alterar a política, pare todas as sessões primeiro. Os valores salvos no painel passam a prevalecer sobre as variáveis correspondentes enquanto o console estiver habilitado.</div><div class="split"><section class="panel"><div class="panel-head"><h2>Política operacional</h2><span class="badge">${source}</span></div><div class="panel-body"><dl class="detail-grid">${policyDetails(config)}</dl></div></section><section class="panel"><div class="panel-head"><h2>Infraestrutura</h2>${icon("shield")}</div><div class="panel-body"><div class="note">Estes campos exigem alteração no executor do container e reinício. O console não altera seu ambiente de nuvem, conexões de banco ou chaves mestras.</div><dl class="detail-grid"><dt>Hub ID</dt><dd><code>${escapeHtml(config.hubId)}</code></dd><dt>Listeners</dt><dd><code>${config.listenerStart}..${config.listenerEnd}</code></dd><dt>Health check</dt><dd><code>${config.healthPort}</code></dd></dl></div></section></div>`;
}
async function loadCatalog(background = false) {
  if (me?.role !== "admin") return;
  const generation = authGeneration;
  const target = view;
  const routes = {
    users: "/users",
    providers: "/providers",
    settings: "/config",
  };
  if (!routes[target]) return;
  const result = await api(routes[target], undefined, background);
  if (generation === authGeneration) catalog[target] = result;
}
function focusSelector(workspace, focused) {
  if (!workspace?.contains(focused)) return;
  if (focused.id) return `#${CSS.escape(focused.id)}`;
  if (!focused.matches("button")) return;
  const attributes = [...focused.attributes].filter((attribute) =>
    attribute.name.startsWith("data-"),
  );
  return (
    "button" +
    attributes
      .map(
        (attribute) => `[${attribute.name}="${CSS.escape(attribute.value)}"]`,
      )
      .join("")
  );
}
function renderLiveView() {
  const workspace = $("#workspace"),
    focused = document.activeElement;
  const selector = focusSelector(workspace, focused);
  const selection = focused?.matches("input[type=search],input[type=text]")
    ? [focused.selectionStart, focused.selectionEnd]
    : undefined;
  const position = [window.scrollX, window.scrollY];
  const scrolls = [
    ...(workspace?.querySelectorAll(".table-wrap,.logs") ?? []),
  ].map((e) => [e.scrollLeft, e.scrollTop]);
  renderView();
  if (selector) {
    const replacement = workspace?.querySelector(selector);
    replacement?.focus({ preventScroll: true });
    if (replacement && selection) replacement.setSelectionRange(...selection);
  }
  workspace?.querySelectorAll(".table-wrap,.logs").forEach((e, index) => {
    if (scrolls[index]) [e.scrollLeft, e.scrollTop] = scrolls[index];
  });
  window.scrollTo(...position);
}
async function refreshOverview(generation, background) {
  const result = await api("/overview", undefined, background);
  if (generation !== authGeneration) return false;
  overview = result;
  return true;
}
function needsViewRefresh(full, topics) {
  if (full) return true;
  if (["users", "providers", "settings"].includes(view))
    return topics.includes("catalog");
  return (
    ["sessions", "operations"].includes(view) && topics.includes("overview")
  );
}
async function refreshLogs(generation, background) {
  if (paused) return true;
  const result = await api(`/logs?after=${cursor}`, undefined, background);
  if (generation !== authGeneration) return false;
  cursor = result.cursor;
  logs = [...logs, ...result.records].slice(-1000);
  if (view === "logs") renderLogs();
  return true;
}
function updateWaitingJob() {
  const waiting = dialog.querySelector("[data-wait-job]");
  if (!waiting) return;
  const job = overview.jobs.find(
    (candidate) => candidate.id === waiting.dataset.waitJob,
  );
  if (job?.device && waiting.dataset.deviceCode !== job.device.code) {
    device(job);
    return;
  }
  if (job?.status !== "running") {
    dialog.close();
    toast(job?.error ?? "Operação concluída.");
  }
}
async function refreshFailure(error, generation, background) {
  if (generation !== authGeneration) return;
  if (["LOGIN_REQUIRED", "PASSWORD_CHANGE_REQUIRED"].includes(error.code)) {
    await bootstrap();
    return;
  }
  if (!background) toast(error.message);
}
async function refreshData(full, topics, generation, background) {
  if (full || topics.includes("overview")) {
    if (!(await refreshOverview(generation, background))) return false;
  }
  if (full || topics.includes("catalog")) await loadCatalog(background);
  return generation === authGeneration;
}
function renderRefresh(full, topics, background) {
  if (!needsViewRefresh(full, topics)) return;
  if (background) renderLiveView();
  else renderView();
}
async function refresh(
  full = false,
  topics = ["overview", "logs"],
  background = false,
) {
  if (!me || mustChangePassword() || refreshing) return false;
  const generation = authGeneration;
  refreshing = true;
  try {
    if (!(await refreshData(full, topics, generation, background)))
      return false;
    renderRefresh(full, topics, background);
    if (full || topics.includes("logs")) {
      if (!(await refreshLogs(generation, background))) return false;
    }
    updateWaitingJob();
    return true;
  } catch (e) {
    await refreshFailure(e, generation, background);
    return false;
  } finally {
    refreshing = false;
  }
}
function liveStatus() {
  const labels = {
    connecting: "Conectando ao fluxo",
    live: "Atualizações ao vivo",
    fallback: "Reconectando · atualização a cada 10 s",
    paused: "Atualização pausada nesta aba",
  };
  document.querySelectorAll("[data-live-status]").forEach((element) => {
    element.textContent = labels[liveState];
    element.classList.toggle("active", liveState === "live");
  });
}
function queueLive(topics, reset = false) {
  for (const topic of topics)
    if (["overview", "logs", "catalog"].includes(topic))
      pendingTopics.add(topic);
  liveReset ||= reset;
  if (liveTimer) return;
  liveTimer = setTimeout(async () => {
    liveTimer = undefined;
    if (!me || document.hidden) return;
    if (refreshing) {
      queueLive([]);
      return;
    }
    if (liveReset) {
      cursor = 0;
      logs = [];
      liveReset = false;
    }
    const topics = [...pendingTopics];
    pendingTopics.clear();
    const ok = await refresh(false, topics, true);
    if (!me) return;
    if (!ok) {
      for (const topic of topics) pendingTopics.add(topic);
      liveState = "fallback";
    } else if (
      eventSource?.readyState === 1 &&
      Date.now() - liveSeen <= 45000
    ) {
      liveState = "live";
    }
    liveStatus();
  }, 250);
}
function stopLive() {
  eventSource?.close();
  eventSource = undefined;
  clearTimeout(liveTimer);
  liveTimer = undefined;
  pendingTopics.clear();
  liveReset = false;
}
function startLive() {
  if (!me || mustChangePassword() || document.hidden || eventSource) return;
  liveState = "connecting";
  liveStatus();
  if (!("EventSource" in window)) {
    liveState = "fallback";
    liveStatus();
    return;
  }
  const source = new EventSource("/api/events");
  eventSource = source;
  source.onopen = () => {
    if (eventSource === source) {
      liveSeen = Date.now();
      liveState = "live";
      liveStatus();
    }
  };
  const receive = (event) => {
    if (eventSource !== source) return;
    liveSeen = Date.now();
    try {
      const data = JSON.parse(event.data);
      if (Array.isArray(data.topics))
        queueLive(data.topics, data.reset === true);
    } catch {
      /* Ignore malformed messages; reconnect always resnapshots. */
    }
  };
  source.addEventListener("sync", receive);
  source.addEventListener("change", receive);
  source.addEventListener("ping", () => {
    if (eventSource === source) liveSeen = Date.now();
  });
  source.addEventListener("auth", () => {
    if (eventSource !== source) return;
    stopLive();
    void bootstrap().catch(() => loginPage());
  });
  source.onerror = () => {
    if (eventSource !== source) return;
    liveState = "fallback";
    liveStatus();
    queueLive(["overview", "logs", "catalog"]);
  };
}
function passwordForm(required = false) {
  modal(
    "Alterar minha senha",
    required
      ? "Troca obrigatória após criação ou recuperação."
      : "As demais sessões de navegador serão encerradas.",
    `<form><div class="form-grid">${field("Senha atual", "current", "", "password", "", 'required autocomplete="current-password"')}${field("Nova senha", "password", "", "password", "Mínimo de 14 caracteres.", 'required minlength="14" maxlength="1024" autocomplete="new-password"')}${field("Confirme a nova senha", "confirm", "", "password", "", 'required autocomplete="new-password"')}</div>${formEnd("Alterar senha")}</form>`,
  );
  wireForm(async (data) => {
    if (data.get("password") !== data.get("confirm"))
      throw new Error("As novas senhas não conferem.");
    await api("/me/password", {
      current: data.get("current"),
      password: data.get("password"),
    });
    dialog.close();
    await bootstrap();
    toast("Senha alterada.");
  });
}
function newSession() {
  const socksNote = overview.socksEnabled
    ? ` SOCKS5 TCP: ${escapeHtml(overview.socksPort)}.`
    : "";
  modal(
    "Nova sessão",
    "Crie o vínculo local. O túnel remoto será provisionado ao iniciar.",
    `<form><div class="form-grid">${field("ID da sessão", "id", "", "text", "3 a 32 caracteres: letras minúsculas, números e hífen.", 'required pattern="[a-z][a-z0-9-]{1,30}[a-z0-9]"')}${select(
      "Conta do túnel",
      "provider",
      overview.providers.map((p) => [
        p,
        p === "github" ? "GitHub" : "Microsoft",
      ]),
      overview.providers[0],
    )}${field("Nome fixo opcional", "tunnelName", "", "text", "Deixe vazio para resolver pelo Hub ID e login verificado.", 'maxlength="49"')}</div><div class="note">Depois da criação, autentique a conta e inicie a sessão. HTTP/CONNECT: ${escapeHtml(overview.proxyPort)}.${socksNote}</div>${formEnd("Criar sessão")}</form>`,
  );
  wireForm(async (data) => {
    await submitAction("add", data.get("id"), {
      provider: data.get("provider"),
      ...(data.get("tunnelName") ? { tunnelName: data.get("tunnelName") } : {}),
    });
  });
}
async function submitAction(action, id, extra = {}) {
  const { job } = await api("/jobs", { action, session: id, ...extra });
  dialog.close();
  toast(`${actions[action]}: operação iniciada.`);
  if (action === "login")
    modal(
      "Autenticar conta do túnel",
      "Conclua o login no site oficial do provedor.",
      `<div data-wait-job="${escapeHtml(job.id)}">${empty("Preparando autenticação", "Aguardando o código de dispositivo. Não feche o serviço.", "key")}</div>`,
    );
  await refresh(true);
}
function device(job) {
  if (!job?.device) return;
  modal(
    "Autenticar conta do túnel",
    `Sessão ${job.session} - código visível somente para quem iniciou.`,
    `<div data-wait-job="${escapeHtml(job.id)}" data-device-code="${escapeHtml(job.device.code)}"><div class="note">Abra o site oficial em outra aba e informe o código abaixo. Esta autenticação não altera sua conta do console.</div><a href="${escapeHtml(job.device.url)}" target="_blank" rel="noopener noreferrer">${escapeHtml(job.device.url)}</a><div class="device-code">${escapeHtml(job.device.code)}</div><p class="hint">Aguardando a conclusão. O código será removido ao terminar.</p></div>`,
  );
}
const actionIcons = {
  login: "key",
  start: "play",
  stop: "stop",
  logout: "exit",
  remove: "exit",
};
function sessionActionButton(action, id) {
  return `<button class="${action === "remove" ? "danger" : ""}" data-action="${action}" data-id="${escapeHtml(id)}">${icon(actionIcons[action])}${actions[action]}</button>`;
}
function detailActions(id) {
  if (me.role === "viewer") return "";
  const allowed = ["login", "start", "stop", "logout"];
  if (me.role === "admin") allowed.push("remove");
  const buttons = allowed
    .map((action) => sessionActionButton(action, id))
    .join("");
  return `<div class="check-grid">${buttons}</div>`;
}
function sessionDetails(session) {
  const socks = session.socks_port
    ? `${session.socks_port} → ${session.socks_listener}`
    : "Não publicado";
  return detailsList([
    ["Estado", labels[session.status]],
    ["Provedor", session.provider],
    ["Login", session.identity?.user_login ?? "Não autenticado"],
    ["ID estável", session.identity?.user_id ?? "-"],
    ["Túnel", session.tunnel_id ?? session.tunnel_name ?? "A resolver"],
    ["Mapeamento atual", `${session.proxy_port} → ${session.listener}`],
    ["Porta configurada", overview.proxyPort],
    ["SOCKS5 atual", socks],
    [
      "SOCKS5 configurado",
      overview.socksEnabled ? overview.socksPort : "Desabilitado",
    ],
    ["Criada em", new Date(session.created_at).toLocaleString()],
    ["Último erro", session.error ?? "Nenhum"],
  ]);
}
function detail(id) {
  const session = overview.sessions.find((candidate) => candidate.id === id);
  if (!session) return;
  modal(
    session.id,
    "Identidade, mapeamento e operações desta sessão.",
    `<dl class="detail-grid">${sessionDetails(session)}</dl><div class="divider"></div>${detailActions(id)}<div class="divider"></div><p class="hint">Remover encerra a sessão local e preserva a reserva do nome. Não exclui o recurso remoto.</p>`,
  );
}
function confirmAction(action, id) {
  const warning =
    action === "remove"
      ? "A sessão será marcada como removida e não poderá ser reutilizada. O túnel remoto não será excluído."
      : "As credenciais do túnel serão desconectadas. Será necessário fazer login novamente.";
  modal(
    actions[action],
    `Sessão ${id}`,
    `<form><div class="note warning">${warning}</div>${field("Digite o ID da sessão para confirmar", "confirm", "", "text", "", 'required autocomplete="off"')}${formEnd("Confirmar")}</form>`,
  );
  wireForm(async (data) => {
    if (data.get("confirm") !== id)
      throw new Error("O ID informado não confere.");
    await submitAction(action, id);
  });
}
function userPasswordField(user) {
  if (user?.local === false) return "";
  let hint = "Deixe vazio para manter a senha.";
  if (!user)
    hint = requirePasswordChange
      ? "Troca obrigatória no primeiro acesso."
      : "Um aviso permanece até o usuário alterar a senha.";
  const label = user ? "Redefinir senha (opcional)" : "Senha temporária";
  const attributes =
    'autocomplete="new-password" minlength="14" ' + (user ? "" : "required");
  return field(label, "password", "", "password", hint, attributes);
}
function identityLabel(identity) {
  return `${escapeHtml(identity.provider)} / ${escapeHtml(identity.subject)}`;
}
function userIdentityFields(user) {
  if (!user?.identities.length) return "";
  const identities = user.identities.map(identityLabel).join("<br>");
  return `<div class="field full"><span class="field-label">Identidade verificada</span><p class="hint">${identities}</p></div>`;
}
function userSessionFields(user) {
  const fields = overview.sessions
    .map((session) =>
      check(
        session.id,
        `session:${session.id}`,
        user?.sessions.includes(session.id),
      ),
    )
    .join("");
  const content =
    fields || '<span class="hint">Nenhuma sessão cadastrada.</span>';
  return `<div class="field full"><span class="field-label">Sessões permitidas (administrador acessa todas)</span><div class="check-grid">${content}</div></div>`;
}
function userFields(user) {
  const fields = [
    field(
      "Usuário",
      "username",
      user?.username ?? "",
      "text",
      "",
      'required pattern="[a-z][a-z0-9._-]{2,63}"',
    ),
    field(
      "Nome de exibição",
      "name",
      user?.name ?? "",
      "text",
      "",
      'required maxlength="120"',
    ),
    select("Perfil", "role", Object.entries(roleName), user?.role ?? "viewer"),
    userPasswordField(user),
  ].join("");
  return `${fields}<div class="field full">${check("Conta habilitada", "enabled", !user?.disabled)}</div>${userSessionFields(user)}${userIdentityFields(user)}`;
}
function userPayload(data, user, revision) {
  return {
    ...(user ? { id: user.id } : {}),
    username: data.get("username"),
    name: data.get("name"),
    role: data.get("role"),
    disabled: !data.has("enabled"),
    sessions: [...data.keys()]
      .filter((key) => key.startsWith("session:"))
      .map((key) => key.slice(8)),
    ...(data.get("password") ? { password: data.get("password") } : {}),
    revision,
  };
}
function userForm(id) {
  const user = catalog.users.users.find((candidate) => candidate.id === id);
  const title = user ? `Gerenciar ${user.username}` : "Novo usuário local";
  modal(
    title,
    "Perfis e sessões são verificados no servidor a cada operação.",
    `<form><div class="form-grid">${userFields(user)}</div>${formEnd()}</form>`,
  );
  const expected = catalog.users.revision;
  wireForm(async (data) => {
    await api("/users", userPayload(data, user, expected));
    dialog.close();
    toast("Usuário atualizado. Sessões de navegador anteriores revogadas.");
    await refresh(true);
  });
}
const providerKinds = [
  ["microsoft", "Microsoft corporativo"],
  ["github", "GitHub"],
  ["oidc", "OpenID Connect"],
  ["ldap", "LDAP sobre TLS (LDAPS)"],
];
function ldapProviderFields(provider) {
  const secretHint = provider?.bindPasswordConfigured
    ? "Configurada. Vazio mantém o valor atual."
    : "Conta de consulta com privilégio mínimo.";
  return [
    field(
      "Servidor LDAPS",
      "url",
      provider?.url ?? "",
      "url",
      "Exemplo: ldaps://directory.example.com:636",
      "required",
    ),
    field("Base DN", "baseDN", provider?.baseDN ?? "", "text", "", "required"),
    field(
      "Bind DN de consulta",
      "bindDN",
      provider?.bindDN ?? "",
      "text",
      "",
      "required",
    ),
    field(
      "Senha do bind",
      "bindPassword",
      "",
      "password",
      secretHint,
      provider ? "" : "required",
    ),
    field(
      "Atributo de login",
      "loginAttribute",
      provider?.loginAttribute ?? "uid",
      "text",
      "",
      "required",
    ),
    field(
      "Atributo de ID estável",
      "idAttribute",
      provider?.idAttribute ?? "entryUUID",
      "text",
      "No Active Directory, use objectGUID.",
      "required",
    ),
    area(
      "CA adicional (PEM, opcional)",
      "ca",
      provider?.ca ?? "",
      "TLS sempre validado. Não informe chaves privadas.",
    ),
  ].join("");
}
function issuerField(provider, kind) {
  if (kind === "github") return "";
  const hint =
    kind === "microsoft"
      ? "https://login.microsoftonline.com/TENANT-UUID/v2.0"
      : "URL exata do issuer OpenID Connect.";
  return field(
    "Issuer HTTPS",
    "issuer",
    provider?.issuer ?? "",
    "url",
    hint,
    "required",
  );
}
function oauthProviderFields(provider, kind) {
  const secretHint = provider?.clientSecretConfigured
    ? "Configurado. Vazio mantém o valor atual."
    : "Crie um aplicativo confidencial no provedor.";
  return [
    issuerField(provider, kind),
    field(
      "Client ID",
      "clientId",
      provider?.clientId ?? "",
      "text",
      "",
      "required",
    ),
    field(
      "Client secret",
      "clientSecret",
      "",
      "password",
      secretHint,
      provider ? "" : "required",
    ),
  ].join("");
}
function providerFields(provider, kind) {
  const connection =
    kind === "ldap"
      ? ldapProviderFields(provider)
      : oauthProviderFields(provider, kind);
  const common = [
    select("Tipo", "kind", providerKinds, kind),
    field(
      "Identificador único",
      "id",
      provider?.id ?? "",
      "text",
      "A origem da identidade fica imutável após salvar.",
      "required " + (provider ? "readonly" : ""),
    ),
    field(
      "Nome do botão de login",
      "label",
      provider?.label ?? "",
      "text",
      "",
      'required maxlength="80"',
    ),
  ].join("");
  const flags = checkedProviders([
    ["enabled", "Provedor habilitado", provider?.enabled ?? true],
    [
      "registration",
      "Permitir cadastro pendente de aprovação",
      provider?.registration ?? true,
    ],
  ]);
  return `${common}${connection}<div class="field full check-grid">${flags}</div>`;
}
function providerNote(kind) {
  if (kind === "ldap")
    return "A conta de consulta localiza exatamente uma entrada. A senha é validada em uma conexão TLS separada com o DN do usuário.";
  return `Callback: ${escapeHtml(catalog.providers.callback)}`;
}
function bindProviderFields(provider) {
  const kindSelect = dialog.querySelector("[name=kind]");
  kindSelect.disabled = !!provider;
  if (!provider) {
    kindSelect.addEventListener("change", (event) =>
      providerForm("new", event.target.value),
    );
    return;
  }
  for (const key of [
    "issuer",
    "clientId",
    "url",
    "baseDN",
    "idAttribute",
    "loginAttribute",
  ]) {
    const input = dialog.querySelector(`[name=${key}]`);
    if (input) input.readOnly = true;
  }
}
function providerForm(id, selectedKind = "microsoft") {
  const provider = catalog.providers.providers.find(
    (candidate) => candidate.id === id,
  );
  const kind = provider?.kind ?? selectedKind;
  const title = provider
    ? `Configurar ${provider.label}`
    : "Adicionar provedor";
  modal(
    title,
    "O provedor autentica o console, não compartilha credenciais com os túneis.",
    `<form><div class="form-grid">${providerFields(provider, kind)}</div><div class="note">${providerNote(kind)}</div>${formEnd("Salvar provedor")}</form>`,
  );
  bindProviderFields(provider);
  const expected = catalog.providers.revision;
  wireForm(async (data) => {
    const value = Object.fromEntries(data);
    value.kind = kind;
    value.enabled = data.has("enabled");
    value.registration = data.has("registration");
    value.revision = expected;
    await api("/providers", value);
    dialog.close();
    toast("Provedor salvo.");
    await refresh(true);
  });
}
function policyForm() {
  const c = catalog.settings.config;
  modal(
    "Editar política operacional",
    "Pare todas as sessões antes de salvar. Nomes já resolvidos não mudam.",
    `<form><div class="form-grid">${field("Porta do proxy", "proxyPort", c.proxyPort, "number", "De 1024 a 65535, sem conflito com listeners, saúde ou console. Atualizada no túnel ao iniciar a sessão. Reconecte o cliente e ajuste seu proxy local.", 'required min="1024" max="65535" step="1"')}${field("Template de nome", "tunnelNameTemplate", c.tunnelNameTemplate, "text", "", "required")}${field("Limite de sessões", "maxSessions", c.maxSessions, "number", "", 'required min="1" max="500"')}${field("Manutenção (segundos)", "maintenanceSeconds", c.maintenanceSeconds, "number", "", 'required min="60" max="3600"')}${field("Portas permitidas", "allowedPorts", c.allowedPorts.join(","), "text", "Separadas por vírgula.", "required")}${field("Portas CONNECT", "connectPorts", c.connectPorts.join(","), "text", "", "required")}${area("Domínios permitidos", "allowedDomains", c.allowedDomains.join("\n"), "Um por linha. Um ponto inicial inclui subdomínios.")}${area("Tenants Microsoft permitidos no túnel", "allowedMicrosoftTenants", c.allowedMicrosoftTenants.join("\n"), "UUIDs, um por linha. Vazio permite qualquer tenant.")}<div class="field full check-grid">${check("Microsoft nos túneis", "microsoft", c.allowedProviders.includes("microsoft"))}${check("GitHub nos túneis", "github", c.allowedProviders.includes("github"))}</div><div class="field full">${check("Permitir todos os domínios (inclui IPs literais)", "allowAllDomains", c.allowAllDomains)}</div></div>${formEnd("Aplicar política")}</form>`,
  );
  dialog
    .querySelector(".form-grid")
    .insertAdjacentHTML(
      "afterbegin",
      `<div class="field full">${check("Habilitar SOCKS5 TCP CONNECT", "socksEnabled", c.socksEnabled)}<span class="hint">Usa a mesma política e auditoria do Squid. UDP e BIND não são suportados.</span></div>${field("Porta SOCKS5", "socksPort", c.socksPort, "number", "Padrão 3180. Deve ser diferente das portas HTTP, saúde, console e listeners. Reconecte os clientes após alterar.", 'required min="1024" max="65535" step="1"')}`,
    );
  const expected = catalog.settings.revision;
  wireForm(async (data) => {
    const policy = {};
    for (const k of ["tunnelNameTemplate"]) policy[k] = data.get(k);
    for (const k of [
      "proxyPort",
      "socksPort",
      "maxSessions",
      "maintenanceSeconds",
    ])
      policy[k] = Number(data.get(k));
    for (const k of ["allowedPorts", "connectPorts"])
      policy[k] = String(data.get(k))
        .split(",")
        .map((s) => Number(s.trim()));
    for (const k of ["allowedDomains", "allowedMicrosoftTenants"])
      policy[k] = String(data.get(k))
        .split(/[\n,]/)
        .map((s) => s.trim())
        .filter(Boolean);
    policy.allowAllDomains = data.has("allowAllDomains");
    policy.socksEnabled = data.has("socksEnabled");
    policy.allowedProviders = ["microsoft", "github"].filter((p) =>
      data.has(p),
    );
    await api("/config", { policy, revision: expected });
    dialog.close();
    toast("Política aplicada.");
    await refresh(true);
  });
}
async function beginProviderLogin(button) {
  button.disabled = true;
  const result = await api("/auth/begin", { provider: button.dataset.sso });
  location.assign(result.url);
}
async function logout() {
  await api("/auth/logout", {});
  await loginPage();
}
async function navigate(button) {
  if (!canView(button.dataset.nav)) return;
  view = button.dataset.nav;
  await loadCatalog();
  renderView();
}
async function runSessionAction(button) {
  const { action, id } = button.dataset;
  if (["logout", "remove"].includes(action)) {
    confirmAction(action, id);
    return;
  }
  button.disabled = true;
  await submitAction(action, id);
}
function pauseLogs() {
  paused = !paused;
  renderView();
  if (!paused) queueLive(["logs"]);
}
const clickHandlers = {
  close: () => dialog.close(),
  sso: beginProviderLogin,
  logout,
  password: () => passwordForm(mustChangePassword()),
  nav: navigate,
  refresh: () => refresh(true),
  create: newSession,
  detail: (button) => detail(button.dataset.detail),
  action: runSessionAction,
  device: (button) =>
    device(overview.jobs.find((job) => job.id === button.dataset.device)),
  user: (button) => userForm(button.dataset.user),
  provider: (button) => providerForm(button.dataset.provider),
  policy: policyForm,
  pause: pauseLogs,
};
document.addEventListener("click", async (event) => {
  const button = event.target.closest("button");
  if (!button) return;
  const key = Object.keys(clickHandlers).find((name) =>
    Object.hasOwn(button.dataset, name),
  );
  if (!key) return;
  try {
    await clickHandlers[key](button);
  } catch (error) {
    toast(error.message);
  } finally {
    button.disabled = false;
  }
});
setInterval(() => {
  if (
    me &&
    !document.hidden &&
    ((liveState === "live" && Date.now() - liveSeen > 45000) ||
      eventSource?.readyState === 2)
  ) {
    stopLive();
    startLive();
  }
  if (me && !document.hidden && liveState !== "live")
    queueLive(["overview", "logs", "catalog"]);
}, 10000);
document.addEventListener("visibilitychange", () => {
  if (document.hidden) {
    stopLive();
    liveState = "paused";
    liveStatus();
  } else startLive();
});
window.addEventListener("pagehide", stopLive);
window.addEventListener("pageshow", (event) => {
  if (event.persisted) startLive();
});
void bootstrap().catch((e) => {
  app.textContent = `Não foi possível abrir o Hub: ${e.message}`;
});

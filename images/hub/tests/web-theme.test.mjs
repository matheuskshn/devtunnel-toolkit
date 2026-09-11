import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const filename = fileURLToPath(new URL("../web/theme.js", import.meta.url));
const source = await readFile(filename, "utf8");
function fixture({ stored = null, dark = false, blocked = false } = {}) {
  const docEvents = {},
    winEvents = {},
    mediaEvents = {},
    attributes = {};
  const media = {
    matches: dark,
    addEventListener: (name, fn) => {
      mediaEvents[name] = fn;
    },
  };
  const document = {
    documentElement: { dataset: {} },
    querySelectorAll: () => [
      {
        setAttribute: (key, value) => {
          attributes[key] = value;
        },
      },
    ],
    addEventListener: (name, fn) => {
      docEvents[name] = fn;
    },
  };
  const localStorage = {
    getItem: () => {
      if (blocked) throw Error("STORAGE_BLOCKED");
      return stored;
    },
    setItem: (_, value) => {
      if (blocked) throw Error("STORAGE_BLOCKED");
      stored = value;
    },
  };
  vm.runInNewContext(
    source,
    {
      document,
      localStorage,
      window: {
        matchMedia: () => media,
        addEventListener: (name, fn) => {
          winEvents[name] = fn;
        },
      },
    },
    { filename },
  );
  return {
    theme: () => document.documentElement.dataset.theme,
    stored: () => stored,
    label: () => attributes["aria-label"],
    click: (toggle = true) =>
      docEvents.click({ target: { closest: () => toggle } }),
    system: (value) => {
      media.matches = value;
      mediaEvents.change();
    },
    storage: (value, key = "devtunnel-toolkit-theme") =>
      winEvents.storage({ key, newValue: value }),
  };
}
test("theme follows system until explicitly selected and updates accessible labels", () => {
  const f = fixture();
  assert.equal(f.theme(), "light");
  assert.equal(f.label(), "Ativar modo escuro");
  f.system(true);
  assert.equal(f.theme(), "dark");
  assert.equal(f.label(), "Ativar modo claro");
  f.click();
  assert.equal(f.theme(), "light");
  assert.equal(f.stored(), "light");
  f.system(true);
  assert.equal(f.theme(), "light");
});
test("theme restores an explicit preference before app rendering", () => {
  assert.equal(fixture({ stored: "dark" }).theme(), "dark");
  assert.equal(fixture({ stored: "light", dark: true }).theme(), "light");
});
test("invalid stored theme is ignored and other clicks do not change it", () => {
  const f = fixture({ stored: "unexpected", dark: true });
  assert.equal(f.theme(), "dark");
  f.click(false);
  assert.equal(f.theme(), "dark");
});
test("theme selection still works with blocked browser storage", () => {
  const f = fixture({ blocked: true, dark: true });
  f.click();
  assert.equal(f.theme(), "light");
  f.click();
  assert.equal(f.theme(), "dark");
});
test("theme syncs across tabs and resumes following system when cleared", () => {
  const f = fixture({ dark: true });
  f.storage("light");
  assert.equal(f.theme(), "light");
  f.storage("dark", "unrelated");
  assert.equal(f.theme(), "light");
  f.storage(null, null);
  assert.equal(f.theme(), "dark");
  f.system(false);
  assert.equal(f.theme(), "light");
});
test("theme initialization is an external early script compatible with CSP", async () => {
  const html = await readFile(
    new URL("../web/index.html", import.meta.url),
    "utf8",
  );
  assert.match(html, /<script src="\/theme.js"><\/script>/);
  assert.ok(html.indexOf("/theme.js") < html.indexOf("/app.css"));
  assert.match(html, /name="color-scheme" content="light dark"/);
});

import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const filename = fileURLToPath(new URL("../web/app.js", import.meta.url));
const source = await readFile(filename, "utf8");

function fixture() {
  const timers = new Map(),
    intervals = [],
    docEvents = {},
    winEvents = {},
    streams = [];
  let timerId = 0;
  const element = {
    addEventListener() {},
    querySelector: () => null,
    classList: { toggle() {} },
  };
  class EventSource {
    constructor(url) {
      this.url = url;
      this.events = {};
      this.readyState = 0;
      streams.push(this);
    }
    addEventListener(event, callback) {
      this.events[event] = callback;
    }
    close() {
      this.readyState = 2;
      this.closed = true;
    }
    open() {
      this.readyState = 1;
      this.onopen();
    }
    emit(event, data) {
      this.events[event]?.({ data: JSON.stringify(data) });
    }
  }
  const document = {
    hidden: false,
    querySelector: () => element,
    querySelectorAll: () => [],
    addEventListener: (event, callback) => (docEvents[event] = callback),
  };
  const context = vm.createContext({
    document,
    // Keep automatic bootstrap pending; explicit test operations use the same
    // complete source and V8 offsets as the production file.
    fetch: () => new Promise(() => {}),
    window: {
      EventSource,
      addEventListener: (event, callback) => (winEvents[event] = callback),
    },
    EventSource,
    setInterval: (callback, delay) => intervals.push({ callback, delay }),
    setTimeout: (callback) => {
      timers.set(++timerId, callback);
      return timerId;
    },
    clearTimeout: (id) => timers.delete(id),
  });
  vm.runInContext(source, context, { filename });
  const run = (script) => vm.runInContext(script, context);
  run('me = { id: "synthetic", role: "admin", mustChange: false };');
  async function flush() {
    for (let i = 0; timers.size && i < 10; i++) {
      const [id, callback] = timers.entries().next().value;
      timers.delete(id);
      await callback();
    }
    assert.equal(timers.size, 0, "unbounded refresh scheduling");
  }
  return {
    run,
    document,
    docEvents,
    winEvents,
    streams,
    timers,
    intervals,
    flush,
  };
}

test("UI uses one authenticated URL, coalesces invalidations and resnapshots cursor on reconnect", async () => {
  const f = fixture();
  f.run(
    "globalThis.calls = []; refresh = async (...args) => { calls.push(args); return true; }; logs = [{seq: 42}]; cursor = 42; startLive(); startLive();",
  );
  assert.equal(f.streams.length, 1);
  assert.equal(f.streams[0].url, "/api/events");
  f.streams[0].open();
  f.streams[0].emit("sync", {
    reset: true,
    topics: ["overview", "logs", "catalog"],
  });
  f.streams[0].emit("change", { topics: ["logs", "overview"] });
  assert.equal(f.timers.size, 1);
  await f.flush();
  assert.equal(f.run("calls.length"), 1);
  assert.equal(f.run("calls[0][2]"), true);
  assert.equal(f.run("cursor"), 0);
  assert.equal(f.run("logs.length"), 0);
  assert.equal(f.run("liveState"), "live");
  assert.deepEqual(JSON.parse(f.run("JSON.stringify(calls[0][1])")), [
    "overview",
    "logs",
    "catalog",
  ]);
});

test("UI fetches only log delta for log invalidations and does not redraw session panel", async () => {
  const f = fixture();
  f.run(
    "globalThis.routes = []; globalThis.renders = 0; api = async (route, data, background) => { routes.push([route, background]); return {cursor: 4, records: [{seq: 4}]}; }; renderLiveView = () => renders++; cursor = 3;",
  );
  assert.equal(await f.run('refresh(false, ["logs"], true)'), true);
  assert.deepEqual(JSON.parse(f.run("JSON.stringify(routes)")), [
    ["/logs?after=3", true],
  ]);
  assert.equal(f.run("renders"), 0);
  assert.equal(f.run("cursor"), 4);
});

test("UI keeps updates pending while a request is in flight and retries failed snapshots via fallback", async () => {
  const f = fixture();
  f.run(
    "globalThis.calls = 0; refresh = async () => { calls++; return false; }; startLive();",
  );
  f.streams[0].open();
  f.streams[0].emit("change", { topics: ["logs"] });
  await f.flush();
  assert.equal(f.run("calls"), 1);
  assert.equal(f.run("liveState"), "fallback");
  assert.equal(f.run('pendingTopics.has("logs")'), true);
  assert.equal(f.intervals[0].delay, 10000);
  f.intervals[0].callback();
  f.run("refresh = async () => { calls++; return true; };");
  await f.flush();
  assert.equal(f.run("calls"), 2);
  assert.equal(f.run("liveState"), "live");
});

test("UI closes hidden/logged-out streams, ignores stale callbacks and reconnects silent connections", async () => {
  const f = fixture();
  f.run("refresh = async () => true; startLive();");
  const old = f.streams[0];
  old.open();
  f.document.hidden = true;
  f.docEvents.visibilitychange();
  assert.equal(old.closed, true);
  old.emit("change", { topics: ["logs"] });
  assert.equal(f.timers.size, 0);
  f.document.hidden = false;
  f.docEvents.visibilitychange();
  assert.equal(f.streams.length, 2);
  f.streams[1].open();
  f.run("liveSeen = Date.now() - 46000;");
  f.intervals[0].callback();
  assert.equal(f.streams[1].closed, true);
  assert.equal(f.streams.length, 3);
  f.run("stopLive(); me = undefined;");
  f.intervals[0].callback();
  assert.equal(f.timers.size, 0);
});

test("UI does not replace a displayed device-code modal when its code is unchanged", async () => {
  const f = fixture();
  f.run(
    'globalThis.deviceCalls = 0; globalThis.waiting = {dataset: {waitJob: "job-one", deviceCode: "ABCD-1234"}}; dialog.querySelector = () => waiting; overview = {jobs: [{id: "job-one", status: "running", device: {code: "ABCD-1234"}}]}; device = () => deviceCalls++;',
  );
  await f.run("refresh(false, [], true)");
  assert.equal(f.run("deviceCalls"), 0);
  f.run('waiting.dataset.deviceCode = "";');
  await f.run("refresh(false, [], true)");
  assert.equal(f.run("deviceCalls"), 1);
});

test("UI reconnects after a fatal EventSource response and back-forward-cache restoration", () => {
  const f = fixture();
  f.run("startLive();");
  f.streams[0].readyState = 2;
  f.streams[0].onerror();
  f.intervals[0].callback();
  assert.equal(f.streams.length, 2);
  f.winEvents.pagehide();
  assert.equal(f.streams[1].closed, true);
  f.winEvents.pageshow({ persisted: true });
  assert.equal(f.streams.length, 3);
});

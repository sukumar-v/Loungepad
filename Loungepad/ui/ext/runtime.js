/*
 * The extension side of the extension API (docs/ADDONS.md).
 *
 * This page is served on an extension's own origin (https://<id>.loungepad.ext) by
 * ExtensionHost, which also serves the extension's folder. It has no bridge to the launcher and
 * no network of its own: everything goes through chrome.webview.postMessage to that one host,
 * which checks each call against the extension's manifest before doing anything.
 *
 *   host → here   { t: "init", id, version, name, settings, launcher, api }
 *                 { t: "call", id, fn, args }        one of the module's hooks
 *                 { t: "reply", id, ok, result | error }
 *                 { t: "event", name, data }
 *   here → host   { t: "hello" }                     first thing, to be told who we are
 *                 { t: "ready", hooks }              the module loaded and activated
 *                 { t: "error", error }              it did not
 *                 { t: "call", id, fn, args }        an API call: fetch, storage.*, games.list
 *                 { t: "reply", id, ok, result | error }
 *                 { t: "log", msg }
 */
(() => {
  "use strict";
  const wv = window.chrome && window.chrome.webview;
  if (!wv) return;

  let nextId = 1;
  const pending = new Map();
  const listeners = {};
  let config = { id: "", version: "", launcher: "", api: 1 };
  let settingsValue = {};
  let mod = null;

  const post = (m) => wv.postMessage(m);

  function call(fn, ...args) {
    return new Promise((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });
      post({ t: "call", id, fn, args });
    });
  }

  const api = {
    get id() { return config.id; },
    get version() { return config.version; },
    get name() { return config.name; },
    get launcher() { return config.launcher; },
    get api() { return config.api; },
    get settings() { return settingsValue; },
    on(name, fn) {
      (listeners[name] || (listeners[name] = [])).push(fn);
      return () => { listeners[name] = (listeners[name] || []).filter(f => f !== fn); };
    },
    async fetch(url, init) {
      const r = await call("fetch", String(url), init || {});
      r.text = () => r.body;
      r.json = () => JSON.parse(r.body);
      return r;
    },
    storage: {
      get: (key) => call("storage.get", key),
      set: (key, value) => call("storage.set", key, value === undefined ? null : value),
      remove: (key) => call("storage.remove", key),
      keys: () => call("storage.keys"),
    },
    games: { list: () => call("games.list") },
    log: (msg) => post({ t: "log", msg: String(msg) }),
  };
  Object.defineProperty(globalThis, "loungepad", { value: Object.freeze(api), writable: false, configurable: false });

  async function load() {
    const meta = document.querySelector('meta[name="loungepad-main"]');
    const main = (meta && meta.content) || "/main.js";
    try {
      mod = await import(main);
      if (typeof mod.activate === "function")
        await mod.activate({ settings: settingsValue, launcher: config.launcher, api: config.api, name: config.name });
      post({ t: "ready", hooks: Object.keys(mod).filter(k => typeof mod[k] === "function") });
    } catch (err) {
      post({ t: "error", error: String((err && err.stack) || err) });
    }
  }

  wv.addEventListener("message", async (e) => {
    const m = e.data;
    if (!m || typeof m !== "object") return;
    switch (m.t) {
      case "init":
        config = m;
        settingsValue = m.settings || {};
        await load();
        break;
      case "reply": {
        const p = pending.get(m.id);
        if (!p) return;
        pending.delete(m.id);
        if (m.ok) p.resolve(m.result); else p.reject(new Error(m.error || "failed"));
        break;
      }
      case "call": {
        let reply;
        try {
          const fn = mod && mod[m.fn];
          if (typeof fn !== "function") throw new Error(`the extension has no ${m.fn} hook`);
          const result = await fn(...(m.args || []));
          reply = { t: "reply", id: m.id, ok: true, result: result === undefined ? null : result };
        } catch (err) {
          reply = { t: "reply", id: m.id, ok: false, error: String((err && err.message) || err) };
        }
        post(reply);
        break;
      }
      case "event":
        if (m.name === "settings") settingsValue = m.data || {};
        (listeners[m.name] || []).forEach(fn => {
          try { fn(m.data); } catch (err) { api.log(`${m.name} listener failed: ${err && err.message}`); }
        });
        break;
    }
  });

  window.addEventListener("error", (e) => post({ t: "log", msg: `uncaught: ${e.message}` }));
  window.addEventListener("unhandledrejection", (e) =>
    post({ t: "log", msg: `unhandled rejection: ${(e.reason && (e.reason.stack || e.reason.message)) || e.reason}` }));

  post({ t: "hello" });
})();

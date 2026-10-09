/*
 * HowLongToBeat under Node, against the live site: `node test.mjs` in this folder.
 *
 * A stand-in `loungepad` whose fetch is Node's own, so the module runs exactly as the launcher
 * runs it (the launcher's fetch answers in the same shape). The matching rules are checked
 * without the network first; the searches need it.
 */
import assert from "node:assert/strict";

const storage = new Map();
globalThis.loungepad = {
  id: "howlongtobeat", version: "test", launcher: "test", api: 1,
  settings: {},
  on() { return () => {}; },
  log: (m) => console.log("  [log]", m),
  storage: {
    get: async (k) => storage.get(k) ?? null,
    set: async (k, v) => { storage.set(k, v); },
    remove: async (k) => { storage.delete(k); },
    keys: async () => [...storage.keys()],
  },
  games: { list: async () => [] },
  async fetch(url, init = {}) {
    const res = await fetch(url, { method: init.method || "GET", headers: init.headers || {}, body: init.body, redirect: "manual" });
    const body = await res.text();
    const headers = {};
    res.headers.forEach((v, k) => { headers[k] = v; });
    return { status: res.status, ok: res.ok, url: res.url, headers, body, text: () => body, json: () => JSON.parse(body) };
  },
};

const mod = await import("./main.js");
let checks = 0;
const check = (cond, what) => { assert(cond, what); checks++; console.log("  ok  " + what); };

console.log("titleKey");
check(mod.titleKey("Hollow Knight") === "hollow knight", "plain");
check(mod.titleKey("The Witcher 3: Wild Hunt - Game of the Year Edition") === mod.titleKey("The Witcher 3: Wild Hunt"), "one edition suffix is discounted");
check(mod.titleKey("Pokémon Emerald") === "pokemon emerald", "accents fold");
check(mod.titleKey("Ori & the Blind Forest") === mod.titleKey("Ori and the Blind Forest"), "& is and");
check(mod.titleKey("Portal") !== mod.titleKey("Portal 2"), "a number is part of the name");
check(mod.titleKey("DOOM™") === "doom", "trademark marks go");

console.log("pickMatch");
const doom93 = { game_id: 1, game_name: "DOOM", release_world: 1993, profile_popular: 50, game_type: "game" };
const doom16 = { game_id: 2, game_name: "DOOM", release_world: 2016, profile_popular: 900, game_type: "game" };
check(mod.pickMatch([doom93, doom16], { title: "DOOM", year: 1993 }, {}) === doom93, "the same year wins over popularity");
check(mod.pickMatch([doom93, doom16], { title: "DOOM", year: null }, {}) === doom16, "no year: the popular one");
check(mod.pickMatch([doom93, doom16], { title: "Doom 3", year: 2004 }, {}) === null, "no exact name, no match");
check(mod.pickMatch([{ game_id: 3, game_name: "Hollow Knight", game_alias: "Hollow Knight: Voidheart Edition", release_world: 2017, game_type: "game" }],
  { title: "Hollow Knight: Voidheart Edition", year: 2018 }, {}) !== null, "an alias matches");
check(mod.pickMatch([doom16], { title: "DOOM", year: 2004 }, { "match-year": true }) === null, "match-year refuses a far year");
check(mod.pickMatch([doom16], { title: "DOOM", year: 2004 }, {}) === doom16, "off, a far year is still the game");
check(mod.hours(97102) === 27, "97102 s is 27 h");
check(mod.hours(149837) === 41.5, "149837 s is 41½ h");
check(mod.hours(0) === null, "0 is no data");

console.log("live");
await mod.activate({ settings: {} });
const hk = await mod.enrich({ id: "steam:367520", title: "Hollow Knight", store: "steam", year: 2017 });
check(hk && hk.id === 26286, "Hollow Knight is #26286");
check(hk.main >= 20 && hk.main <= 40, `main story about a day (${hk.main} h)`);
check(hk.completionist > hk.main, "completionist takes longer");
const portal = await mod.enrich({ id: "steam:400", title: "Portal", store: "steam", year: 2007 });
check(portal && portal.name === "Portal", "Portal is Portal, not Portal 2");
const none = await mod.enrich({ id: "manual:x", title: "Zzyzx Quuxwort Nonexistent 9000", store: "manual" });
check(none === null, "an unknown title is null");
const celeste = await mod.enrich({ id: "steam:504230", title: "Celeste", store: "steam", year: 2018 });
check(celeste && celeste.year === 2018, "Celeste, 2018");
const witcher = await mod.enrich({ id: "steam:292030", title: "The Witcher 3: Wild Hunt — Remastered", store: "steam", year: 2015 });
check(witcher && /witcher 3/i.test(witcher.name), `a dashed, suffixed title still finds its game (${witcher && witcher.name})`);
console.log(`PASS: ${checks} checks`);

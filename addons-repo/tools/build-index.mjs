#!/usr/bin/env node
/*
 * Rebuild index.json from the add-on folders: `node tools/build-index.mjs`.
 *
 *   --check          compare with the committed index.json and fail if it is out of step
 *   --base <url>     where the files will be fetched from (default: this repository's main
 *                    branch on raw.githubusercontent.com); a local server for testing
 *   --out <file>     write somewhere other than index.json (with --base, for a local test)
 *
 * Every theme under themes/ and every extension under extensions/ becomes one entry: what its
 * manifest says, plus every file in the folder with its size and SHA-256. The launcher downloads
 * exactly these files and checks each hash before installing (docs/ADDONS.md in Loungepad).
 */
import { createHash } from "node:crypto";
import { readdirSync, readFileSync, statSync, writeFileSync, existsSync } from "node:fs";
import { join, relative, extname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(fileURLToPath(import.meta.url), "..", "..");
const args = process.argv.slice(2);
const check = args.includes("--check");
const baseArg = args.includes("--base") ? args[args.indexOf("--base") + 1] : null;
const outArg = args.includes("--out") ? args[args.indexOf("--out") + 1] : null;
const BASE = (baseArg || "https://raw.githubusercontent.com/sukumar-v/loungepad-addons/main/").replace(/\/?$/, "/");

const ID = /^[a-z][a-z0-9-]{0,39}$/;
const VERSION = /^\d+\.\d+\.\d+$/;
const HOST = /^(\*\.)?[a-z0-9-]+(\.[a-z0-9-]+)+$/;
const SKIP_FILES = /^(\..*|.*\.md|test.*\.mjs|package(-lock)?\.json|tsconfig\.json)$/i;
const SKIP_DIRS = new Set(["node_modules", ".git", "test", "tests"]);
const problems = [];

function files(dir, rel = "") {
  const out = [];
  for (const name of readdirSync(dir).sort()) {
    const full = join(dir, name);
    const st = statSync(full);
    if (st.isDirectory()) { if (!SKIP_DIRS.has(name) && !name.startsWith(".")) out.push(...files(full, rel + name + "/")); continue; }
    if (SKIP_FILES.test(name)) continue;
    const bytes = readFileSync(full);
    out.push({ path: rel + name, sha256: createHash("sha256").update(bytes).digest("hex"), size: bytes.length });
  }
  return out;
}

function readManifest(dir, kind, id) {
  const file = join(dir, kind === "theme" ? "theme.json" : "manifest.json");
  if (!existsSync(file)) {
    if (kind === "theme") return {};
    problems.push(`${kind}s/${id}: no manifest.json`);
    return null;
  }
  try { return JSON.parse(readFileSync(file, "utf8")); }
  catch (e) { problems.push(`${kind}s/${id}: ${file} is not valid JSON: ${e.message}`); return null; }
}

function entry(kind, id) {
  const dir = join(root, kind + "s", id);
  if (!ID.test(id)) { problems.push(`${kind}s/${id}: the folder name is not a valid id (letters, digits, hyphens, starting with a letter)`); return null; }
  const m = readManifest(dir, kind, id);
  if (m === null) return null;
  if (kind === "extension") {
    if (m.id !== id) problems.push(`extensions/${id}: manifest.json says id "${m.id}"`);
    if (m.kind !== "extension") problems.push(`extensions/${id}: manifest.json's kind must be "extension"`);
    if (!existsSync(join(dir, m.main || "main.js"))) problems.push(`extensions/${id}: the module ${m.main || "main.js"} is missing`);
    for (const h of (m.permissions && m.permissions.hosts) || [])
      if (!HOST.test(String(h))) problems.push(`extensions/${id}: "${h}" is not a host name`);
  } else if (!existsSync(join(dir, "theme.css")) && !existsSync(join(dir, "theme.html"))) {
    problems.push(`themes/${id}: no theme.css`);
  }
  const version = String(m.version || "");
  if (!VERSION.test(version)) problems.push(`${kind}s/${id}: version "${version}" is not major.minor.patch`);
  if (m.icon && !existsSync(join(dir, m.icon))) problems.push(`${kind}s/${id}: the icon ${m.icon} is missing`);
  const list = files(dir);
  if (!list.length) problems.push(`${kind}s/${id}: no files`);
  return {
    id, kind,
    name: String(m.name || id),
    version,
    summary: m.summary || (m.description ? String(m.description).split(/(?<=\.)\s/)[0].slice(0, 200) : undefined),
    author: m.author || undefined,
    homepage: m.homepage || undefined,
    minLauncher: m.minLauncher || undefined,
    permissions: kind === "extension" ? { hosts: ((m.permissions && m.permissions.hosts) || []).map(String) } : undefined,
    icon: m.icon || undefined,
    base: `${BASE}${kind}s/${id}/`,
    files: list,
  };
}

const addons = [];
for (const kind of ["theme", "extension"]) {
  const dir = join(root, kind + "s");
  if (!existsSync(dir)) continue;
  for (const id of readdirSync(dir).sort()) {
    if (!statSync(join(dir, id)).isDirectory() || id.startsWith(".")) continue;
    const e = entry(kind, id);
    if (e) addons.push(e);
  }
}

if (problems.length) {
  console.error("index not built:\n  " + problems.join("\n  "));
  process.exit(1);
}

const index = { schema: 1, generated: new Date().toISOString(), addons };
const text = JSON.stringify(index, null, 2) + "\n";
const file = outArg || join(root, "index.json");
// The generated stamp alone must not count as a change.
const strip = (s) => s.replace(/"generated": "[^"]*"/, '"generated": ""');
if (check) {
  const current = existsSync(file) ? readFileSync(file, "utf8") : "";
  if (strip(current) !== strip(text)) {
    console.error("index.json is out of step with the folders: run `node tools/build-index.mjs` and commit it");
    process.exit(1);
  }
  console.log(`index.json is in step: ${addons.length} add-on(s)`);
} else {
  writeFileSync(file, text);
  console.log(`wrote index.json: ${addons.map(a => `${a.kind} ${a.id} ${a.version} (${a.files.length} files)`).join(", ")}`);
}

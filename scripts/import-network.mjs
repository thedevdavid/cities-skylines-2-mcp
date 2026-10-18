#!/usr/bin/env node
// Posts a network import file (IMPORT_SCHEMA.md) to the CS2MCP bridge.
//
//   node scripts/import-network.mjs fixtures/ten-highway-segments.json --at 0,0
//   node scripts/import-network.mjs <file> --dry-run       validate only, build nothing
//   node scripts/import-network.mjs --status                progress / last report
//   node scripts/import-network.mjs --undo [--id <importId>] remove the newest import
//   node scripts/import-network.mjs --report [failed|merged|created] [--prefab P] [--offset N --limit N]
//
// Options: --at X,Z (translate all x/z, including control points), --batch N,
// --no-nodes-first, --settle N, --full (ask the bridge for every per-edge result).
// Bridge URL: CS2_BRIDGE_URL (default http://127.0.0.1:8642). Node 18+.
import { readFile } from "node:fs/promises";

const BRIDGE = (process.env.CS2_BRIDGE_URL ?? "http://127.0.0.1:8642").replace(/\/+$/, "");
const TIMEOUT_MS = 16 * 60_000;

function parseArgs(argv) {
  const args = { file: null, at: null, dryRun: false, status: false, report: null, prefab: null, offset: null, limit: null, undo: false, id: null, batch: null, nodesFirst: null, settle: null, full: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--at") {
      const [x, z] = argv[++i].split(",").map(Number);
      if (!Number.isFinite(x) || !Number.isFinite(z)) throw new Error("--at needs X,Z");
      args.at = { x, z };
    } else if (a === "--dry-run") args.dryRun = true;
    else if (a === "--status") args.status = true;
    else if (a === "--undo") args.undo = true;
    else if (a === "--report") args.report = argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[++i] : "";
    else if (a === "--prefab") args.prefab = argv[++i];
    else if (a === "--offset") args.offset = Number(argv[++i]);
    else if (a === "--limit") args.limit = Number(argv[++i]);
    else if (a === "--id") args.id = argv[++i];
    else if (a === "--batch") args.batch = Number(argv[++i]);
    else if (a === "--no-nodes-first") args.nodesFirst = false;
    else if (a === "--settle") args.settle = Number(argv[++i]);
    else if (a === "--full") args.full = true;
    else if (a.startsWith("--")) throw new Error(`unknown option ${a}`);
    else args.file = a;
  }
  return args;
}

async function call(method, path, body) {
  let res;
  try {
    res = await fetch(`${BRIDGE}${path}`, {
      method,
      headers: body ? { "Content-Type": "application/json" } : undefined,
      body: body ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
  } catch (err) {
    throw new Error(`cannot reach the bridge at ${BRIDGE} (${err.message}); is the game running with CS2MCP enabled?`);
  }
  const text = await res.text();
  let payload;
  try {
    payload = JSON.parse(text);
  } catch {
    payload = { raw: text };
  }
  return { status: res.status, payload };
}

function translate(doc, at) {
  for (const n of doc.nodes) {
    n.x += at.x;
    n.z += at.z;
  }
  for (const e of doc.edges) {
    for (const p of e.control ?? []) {
      p[0] += at.x;
      p[2] += at.z;
    }
  }
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  let out;
  if (args.report !== null) {
    const params = new URLSearchParams();
    if (args.report) params.set("status", args.report);
    if (args.prefab) params.set("prefab", args.prefab);
    if (args.offset !== null) params.set("offset", String(args.offset));
    if (args.limit !== null) params.set("limit", String(args.limit));
    out = await call("GET", `/import/report?${params}`);
  } else if (args.status) {
    out = await call("GET", "/import/status");
  } else if (args.undo) {
    out = await call("POST", `/import/undo${args.id ? `?id=${encodeURIComponent(args.id)}` : ""}`);
  } else {
    if (!args.file) throw new Error("usage: import-network.mjs <file.json> [--at X,Z] [--dry-run] | --status | --undo");
    const doc = JSON.parse(await readFile(args.file, "utf8"));
    if (args.at) translate(doc, args.at);
    doc.options = { ...doc.options };
    if (args.dryRun) doc.options.dryRun = true;
    if (args.batch) doc.options.batchSize = args.batch;
    if (args.nodesFirst === false) doc.options.nodesFirst = false;
    if (args.settle) doc.options.settleFrames = args.settle;
    if (args.full) doc.options.report = "full";
    console.error(`posting ${doc.nodes.length} nodes / ${doc.edges.length} edges to ${BRIDGE}/import/network ...`);
    out = await call("POST", "/import/network", doc);
  }
  console.log(JSON.stringify(out.payload, null, 2));
  if (out.status !== 200) process.exitCode = 1;
}

main().catch((err) => {
  console.error(err.message);
  process.exitCode = 1;
});

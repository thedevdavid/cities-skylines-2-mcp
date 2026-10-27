#!/usr/bin/env node
// Turns the map's TM:PE data (cs1-offline/out/tmpe-offline.json) into a request for the bridge's
// POST /import/tmpe, which writes Traffic (PDX 80095) lane connections onto the live CS2 network.
//
//   node scripts/import-tmpe.mjs --dry-run      map everything, write nothing in the game; prints the report
//   node scripts/import-tmpe.mjs                import
//   node scripts/import-tmpe.mjs --undo         remove what the last import (this game session) wrote
//   node scripts/import-tmpe.mjs --status       last report
//   node scripts/import-tmpe.mjs --write-only   only write the request file (--out), no bridge call
//
// Inputs (defaults relative to this repo's parent folder, a workspace holding this repo next to the converter):
//   --tmpe  cs1-offline/out/tmpe-offline.json
//   --dump  newest map-*.json in $CS1_DUMP_DIR (the CS1 exporter's output folder: CS1 net prefabs, segment geometry)
//   --body  net-converter/out/import-all.json (the imported network: CS1 node n<id> -> CS2 position,
//           CS1 segment e<segment>_k -> CS2 course). Nodes and segments missing there fall back to the dump.
//   --out   out/tmpe-import-request.json, --report-out out/tmpe-import-report.json (under scripts/..)
// Options: --node-radius M (default 12), --edge-tolerance M (default 8), --replace (overwrite Traffic data
// already on a node), --force-version (run against a Traffic build other than the pinned one).
// Bridge URL: CS2_BRIDGE_URL (default http://127.0.0.1:8642). Node 18+.
import { readFile, writeFile, readdir, mkdir } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(HERE, "..");
const ROOT = resolve(REPO, "..");
const BRIDGE = (process.env.CS2_BRIDGE_URL ?? "http://127.0.0.1:8642").replace(/\/+$/, "");
const PROBE_M = 20;

function parseArgs(argv) {
  const a = {
    tmpe: join(ROOT, "cs1-offline/out/tmpe-offline.json"),
    dump: null,
    body: join(ROOT, "net-converter/out/import-all.json"),
    out: join(REPO, "out/tmpe-import-request.json"),
    reportOut: join(REPO, "out/tmpe-import-report.json"),
    dryRun: false, undo: false, status: false, writeOnly: false, replace: false, forceVersion: false,
    nodeRadius: 12, edgeTolerance: 8,
  };
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    if (k === "--tmpe") a.tmpe = argv[++i];
    else if (k === "--dump") a.dump = argv[++i];
    else if (k === "--body") a.body = argv[++i];
    else if (k === "--out") a.out = argv[++i];
    else if (k === "--report-out") a.reportOut = argv[++i];
    else if (k === "--dry-run") a.dryRun = true;
    else if (k === "--undo") a.undo = true;
    else if (k === "--status") a.status = true;
    else if (k === "--write-only") a.writeOnly = true;
    else if (k === "--replace") a.replace = true;
    else if (k === "--force-version") a.forceVersion = true;
    else if (k === "--node-radius") a.nodeRadius = Number(argv[++i]);
    else if (k === "--edge-tolerance") a.edgeTolerance = Number(argv[++i]);
    else throw new Error(`unknown option ${k}`);
  }
  return a;
}

async function newestDump() {
  const dir = process.env.CS1_DUMP_DIR;
  if (!dir) throw new Error("set CS1_DUMP_DIR to the CS1 exporter's output folder, or pass --dump");
  const files = (await readdir(dir)).filter((f) => /^map-.*\.json$/.test(f)).sort();
  if (!files.length) throw new Error(`no map-*.json in ${dir}; pass --dump`);
  return join(dir, files[files.length - 1]);
}

// ---- geometry (x east, z north; right of heading (hx,hz) is (hz,-hx)) ----
const sub = (p, q) => [p[0] - q[0], p[1] - q[1]];
const len = (v) => Math.hypot(v[0], v[1]);
const unit = (v) => { const l = len(v) || 1; return [v[0] / l, v[1] / l]; };
const right = (h) => [h[1], -h[0]];
const dot = (u, v) => u[0] * v[0] + u[1] * v[1];
const r2 = (v) => Math.round(v * 100) / 100;

function bez(p, t) {
  const u = 1 - t;
  return [0, 1].map((k) => u * u * u * p[0][k] + 3 * u * u * t * p[1][k] + 3 * u * t * t * p[2][k] + t * t * t * p[3][k]);
}

// Point at arc length `dist` from the given end of a cubic Bézier (xz), plus the unit direction away from that end.
function probeFrom(ctrl, fromStart, dist) {
  const p = fromStart ? ctrl : [...ctrl].reverse();
  let prev = bez(p, 0), acc = 0;
  for (let i = 1; i <= 200; i++) {
    const q = bez(p, i / 200);
    acc += len(sub(q, prev));
    prev = q;
    if (acc >= dist) break;
  }
  const dir = unit(sub(bez(p, 0.02), bez(p, 0)));
  return { probe: prev, probeDist: Math.min(acc, dist), dir };
}

function cubicFromBody(edge, nodes) {
  const a = nodes.get(edge.a), b = nodes.get(edge.b);
  const A = [a.x, a.z], B = [b.x, b.z];
  const c = edge.control ?? [];
  if (c.length === 2) return [A, [c[0][0], c[0][2]], [c[1][0], c[1][2]], B];
  if (c.length === 1) {
    const q = [c[0][0], c[0][2]];
    return [A, [A[0] + (2 / 3) * (q[0] - A[0]), A[1] + (2 / 3) * (q[1] - A[1])], [B[0] + (2 / 3) * (q[0] - B[0]), B[1] + (2 / 3) * (q[1] - B[1])], B];
  }
  return [A, [A[0] + (B[0] - A[0]) / 3, A[1] + (B[1] - A[1]) / 3], [A[0] + (2 * (B[0] - A[0])) / 3, A[1] + (2 * (B[1] - A[1])) / 3], B];
}

// ---- CS1 side ----
function build(tmpe, dump, body, args) {
  const seg = new Map(dump.segments.map((s) => [s.id, s]));
  const node = new Map(dump.nodes.map((n) => [n.id, n]));
  const bodyNodes = new Map(body.nodes.map((n) => [n.id, n]));
  const bodyEdgesBySeg = new Map();
  for (const e of body.edges) {
    if (e.cs1_segment === undefined) continue;
    const k = Number(e.cs1_segment);
    if (!bodyEdgesBySeg.has(k)) bodyEdgesBySeg.set(k, []);
    bodyEdgesBySeg.get(k).push(e);
  }
  // CS2 height factor: body node y / CS1 node y (measured: 2).
  const ratios = [];
  for (const n of body.nodes) {
    const c = node.get(Number(String(n.id).replace(/^n/, "")));
    if (c && Number.isFinite(n.y) && c.position[1] > 1) ratios.push(n.y / c.position[1]);
  }
  ratios.sort((p, q) => p - q);
  const heightFactor = ratios.length ? Math.round(ratios[ratios.length >> 1] * 1000) / 1000 : 1;

  const notes = [];
  const nodePos = (id) => {
    const b = bodyNodes.get(`n${id}`);
    if (b) return { x: b.x, y: b.y, z: b.z, from: "body" };
    const c = node.get(id).position;
    return { x: c[0], y: c[1] * heightFactor, z: c[2], from: "dump" };
  };

  // Direction of travel in CS1: "Forward" runs start->end unless the segment is inverted.
  const towardEnd = (laneDir, s) => (laneDir === "Forward") !== !!s.invert;

  // All car lanes of segment `sid` at node `nid` that flow into (incoming=true) or out of the node,
  // ordered left -> right in the direction of travel, with offsets from their group's centre.
  function cs1Lanes(sid, nid, incoming) {
    const s = seg.get(sid);
    const atStart = s.startNode === nid;
    const sAt = (atStart ? 1 : -1) * (s.invert ? -1 : 1); // lateral (right of "away from node") = sAt * lanePosition
    const pf = dump.netPrefabs[s.prefab];
    const lanes = (pf?.lanes ?? [])
      .map((l, index) => ({ ...l, index }))
      .filter((l) => l.type === "Vehicle" && /Car/.test(l.vehicleType ?? "") && (l.direction === "Forward" || l.direction === "Backward"))
      .filter((l) => towardEnd(l.direction, s) === (atStart ? !incoming : incoming))
      .map((l) => ({ index: l.index, position: l.position, lateral: (incoming ? -1 : 1) * sAt * l.position }));
    const mean = lanes.reduce((m, l) => m + l.lateral, 0) / (lanes.length || 1);
    lanes.forEach((l) => (l.offset = l.lateral - mean));
    lanes.sort((p, q) => p.lateral - q.lateral);
    return { lanes, prefab: s.prefab };
  }

  function segmentRef(sid, nid) {
    const s = seg.get(sid);
    const atStart = s.startNode === nid;
    const bodyEdge = (bodyEdgesBySeg.get(sid) ?? []).find((e) => e.a === `n${nid}` || e.b === `n${nid}`);
    let ctrl, fromStart, from;
    if (bodyEdge && bodyNodes.has(bodyEdge.a) && bodyNodes.has(bodyEdge.b)) {
      ctrl = cubicFromBody(bodyEdge, bodyNodes);
      fromStart = bodyEdge.a === `n${nid}`;
      from = bodyEdge.id;
    } else {
      ctrl = s.bezier.map((p) => [p[0], p[2]]);
      fromStart = atStart;
      from = "dump";
    }
    const { probe, probeDist, dir } = probeFrom(ctrl, fromStart, PROBE_M);
    return { segment: sid, prefab: s.prefab, geometry: from, dir: dir.map(r2), probe: probe.map(r2), probeDist: r2(probeDist) };
  }

  function laneEnd(l, incoming) {
    const { lanes } = cs1Lanes(l.segment, l.node, incoming);
    const s = seg.get(l.segment);
    const flowsIn = towardEnd(l.direction, s) === (s.endNode === l.node);
    if (flowsIn !== incoming) notes.push(`lane ${l.laneId} (segment ${l.segment}) flows ${flowsIn ? "into" : "out of"} node ${l.node}, expected ${incoming ? "into" : "out of"}`);
    const me = lanes.find((x) => x.index === l.laneIndex) ?? lanes.reduce((b, x) => (!b || Math.abs(x.position - l.lanePosition) < Math.abs(b.position - l.lanePosition) ? x : b), null);
    return {
      ...segmentRef(l.segment, l.node),
      cs1Lane: l.laneId,
      cs1LaneIndex: l.laneIndex,
      lanePosition: l.lanePosition,
      laneCount: lanes.length,
      rank: me ? lanes.indexOf(me) : -1,
      offset: me ? r2(me.offset) : 0,
    };
  }

  // TM:PE: IsLeftSegment(from, to) = Cross(fromDir, toDir).y >= 0.5 with both directions pointing away from the node.
  const crossY = (a, b) => a[1] * b[0] - a[0] * b[1]; // Unity Cross(a,b).y for a=(ax,0,az), b=(bx,0,bz)
  function arrowDirection(fromSid, toSid, nid) {
    if (fromSid === toSid) return "Turn";
    const d = (sid) => {
      const s = seg.get(sid);
      const v = s.startNode === nid ? s.startDirection : s.endDirection;
      return unit([v[0], v[2]]);
    };
    const f = d(fromSid), t = d(toSid);
    if (crossY(t, f) >= 0.5) return "Right";
    if (crossY(f, t) >= 0.5) return "Left";
    return "Forward";
  }

  const byNode = new Map();
  const nodeEntry = (nid) => {
    if (!byNode.has(nid)) {
      const p = nodePos(nid);
      byNode.set(nid, { id: `n${nid}`, cs1Node: nid, x: r2(p.x), y: r2(p.y), z: r2(p.z), position: p.from, connections: [], arrows: [] });
    }
    return byNode.get(nid);
  };

  tmpe.laneConnections.forEach((c, i) => {
    nodeEntry(c.node).connections.push({
      id: `c${i}`,
      group: c.group,
      source: laneEnd(c.source, true),
      target: laneEnd(c.target, false),
    });
  });

  const connectedSources = new Set(tmpe.laneConnections.map((c) => c.source.laneId));
  tmpe.laneArrows.forEach((a, i) => {
    const l = a.lane;
    const allowed = new Set(String(a.arrows).split(/[,|\s]+/).filter(Boolean));
    const others = dump.segments.filter((s) => (s.startNode === l.node || s.endNode === l.node));
    const targets = [];
    for (const s of others) {
      const dir = arrowDirection(l.segment, s.id, l.node);
      const outLanes = cs1Lanes(s.id, l.node, false).lanes.length;
      if (outLanes === 0) continue;
      targets.push({ ...segmentRef(s.id, l.node), arrow: dir, allowed: allowed.has(dir), cs1OutLanes: outLanes });
    }
    nodeEntry(l.node).arrows.push({
      id: `a${i}`,
      arrows: a.arrows,
      supersededByConnections: connectedSources.has(l.laneId),
      source: laneEnd(l, true),
      targets,
    });
  });

  return {
    schema: "cs2mcp/tmpe-import/v1",
    generatedFrom: { tmpe: args.tmpe, dump: args.dump, body: args.body, heightFactor },
    traffic: { pinnedVersion: "0.2.13", pinnedCommit: "ce1551f", forceVersion: args.forceVersion },
    options: { dryRun: args.dryRun, replace: args.replace, nodeRadius: args.nodeRadius, edgeTolerance: args.edgeTolerance },
    notes,
    nodes: [...byNode.values()].sort((p, q) => p.cs1Node - q.cs1Node),
  };
}

function markdown(report, request) {
  const lines = [];
  const s = report.summary;
  lines.push(`# TM:PE -> Traffic import ${report.dryRun ? "(dry run)" : report.importId}`, "");
  lines.push(`Traffic ${report.trafficVersion} (pinned ${report.pinnedTrafficVersion}). Nodes matched ${s.nodes.matched}/${s.nodes.requested}.`, "");
  lines.push(`- Connections: ${JSON.stringify(s.connections.byStatus)} of ${s.connections.requested}`);
  lines.push(`- Arrow lanes: ${JSON.stringify(s.arrows.byStatus)} of ${s.arrows.requested}`);
  lines.push(`- Traffic data: ${s.traffic.nodes} nodes, ${s.traffic.modifiedSourceLanes} source lanes, ${s.traffic.generatedConnections} connections. ${s.traffic.note}`, "");
  const cs2 = new Map(report.nodes.map((n) => [n.id, n]));
  const seg = (e) => (e ? `${e.segment}:${e.cs1LaneIndex}${e.cs2Edge ? ` → ${e.cs2Edge}:${e.cs2Lane}` : ""}` : "");
  lines.push("## Lane connections", "", "| node | CS2 node | id | CS1 from → CS2 | CS1 to → CS2 | status | reason |", "|---|---|---|---|---|---|---|");
  for (const c of report.connections) lines.push(`| ${c.node} | ${cs2.get(c.node)?.cs2Node ?? ""} | ${c.id} | ${seg(c.source)} | ${seg(c.target)} | ${c.status} | ${c.reason ?? ""} |`);
  lines.push("", "## Lane arrows", "", "| node | id | arrows | CS1 lane → CS2 | status | kept / CS2 default | reason |", "|---|---|---|---|---|---|---|");
  for (const a of report.arrows) lines.push(`| ${a.node} | ${a.id} | ${a.arrows} | ${seg(a.source)} | ${a.status} | ${a.cs2Kept ? `${a.cs2Kept.join(" ")} / ${a.cs2Default.join(" ")}` : ""} | ${a.reason ?? ""} |`);
  lines.push("", `Request: ${request.nodes.length} nodes, height factor ${request.generatedFrom.heightFactor}. Checklist for the manual fallback: cs1-offline/out/tmpe-summary.md.`, "");
  return lines.join("\n");
}

async function call(method, path, body) {
  let res;
  try {
    res = await fetch(`${BRIDGE}${path}`, {
      method,
      headers: body ? { "Content-Type": "application/json" } : undefined,
      body: body ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(5 * 60_000),
    });
  } catch (err) {
    throw new Error(`cannot reach the bridge at ${BRIDGE} (${err.message}); is the game running with CS2MCP enabled?`);
  }
  const text = await res.text();
  try {
    return { status: res.status, payload: JSON.parse(text) };
  } catch {
    return { status: res.status, payload: { raw: text } };
  }
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  let out;
  if (args.status) out = await call("GET", "/import/tmpe/status");
  else if (args.undo) out = await call("POST", "/import/tmpe/undo");
  else {
    args.dump ??= await newestDump();
    const [tmpe, dump, body] = await Promise.all([args.tmpe, args.dump, args.body].map(async (f) => JSON.parse(await readFile(f, "utf8"))));
    const request = build(tmpe, dump, body, args);
    await mkdir(dirname(args.out), { recursive: true });
    await writeFile(args.out, JSON.stringify(request, null, 1));
    const n = request.nodes;
    console.error(`${n.length} nodes, ${n.reduce((s, x) => s + x.connections.length, 0)} connections, ${n.reduce((s, x) => s + x.arrows.length, 0)} arrow lanes -> ${args.out}`);
    for (const note of request.notes) console.error(`note: ${note}`);
    if (args.writeOnly) return;
    out = await call("POST", "/import/tmpe", request);
    if (out.status === 200) {
      await mkdir(dirname(args.reportOut), { recursive: true });
      await writeFile(args.reportOut, JSON.stringify(out.payload, null, 1));
      const md = args.reportOut.replace(/\.json$/, "") + ".md";
      await writeFile(md, markdown(out.payload, request));
      console.error(`report -> ${args.reportOut}, ${md}`);
    }
  }
  console.log(JSON.stringify(out.payload, null, 2));
  if (out.status !== 200) process.exitCode = 1;
}

main().catch((err) => {
  console.error(err.stack ?? err.message);
  process.exitCode = 1;
});

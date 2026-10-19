# Import formats: networks, water sources, graph export

The bridge builds a whole road/track network from a node-and-edge graph. It works in the **map editor** and in a loaded city. MCP tools: `cs2_import_network`, `cs2_import_status`, `cs2_import_report`, `cs2_undo_import`. CLI: `node scripts/import-network.mjs <file>`.

## Endpoints

| Method and path | Purpose |
|---|---|
| `POST /import/network` | Body below. Blocks until every batch is done (up to 15 min), then returns the report. |
| `GET /import/report?status=&prefab=&offset=&limit=` | Pages through the last import's per-edge results (limit default 200, max 1000). |
| `GET /import/status` | Progress of the running import or undo, or the last report. |
| `POST /import/undo[?id=<importId>]` | Bulldozes what the newest import created. `id`, if given, must be the newest one. |
| `POST /import/water` | Creates water sources (see [Water sources](#water-sources)). |
| `POST /import/water/undo[?id=<importId>]` | Deletes the sources of the newest water import. |
| `GET /graph/export?path=&prefab=&owned=&curves=` | Writes the whole network graph to a file (see [Graph export](#graph-export)). |

While an import runs, every `/build/*` endpoint answers 409.

## Body

```json
{
  "name": "optional label",
  "nodes": [
    { "id": "n0", "x": 0, "y": 0, "z": 0, "elevation": 0 },
    { "id": "n1", "x": 120, "z": 0, "entity": { "index": 1234, "version": 1 } }
  ],
  "edges": [
    { "id": "e1", "a": "n0", "b": "n1", "prefab": "Highway Twoway - 4 lanes",
      "control": [[40, 0, 5], [80, 0, 5]], "elevation": [0, 0] }
  ],
  "options": { "batchSize": 64, "nodesFirst": true, "heightMode": "elevation", "settleFrames": 1, "dryRun": false, "report": "summary" }
}
```

Unknown fields are ignored (a `_comment` is fine). Ids may be strings or numbers. The body may be up to 16 MB; split larger networks into several imports (each one gets its own undo).

### Nodes

| Field | Meaning |
|---|---|
| `id` | Unique within the request. |
| `x`, `z` | CS2 world coordinates in metres (playable area is ±7168). |
| `elevation` | Metres above the terrain: `> 0` bridge or flyover, `< 0` tunnel, `0` on the ground. Default: the first edge's `elevation` value at this end, else 0. |
| `y` | World height. Ignored unless `options.heightMode` is `"absolute"`, in which case `elevation = y − terrain height` when `elevation` is missing. |
| `entity` | Optional `{index, version}` of an existing network node (from `cs2_road_graph`). The edge attaches to it; its own position and height are used and `x`/`z` are ignored. |
| `snapToSegment` | Optional `{radius, prefabContains?}` (radius 0–100 m). The node attaches to the nearest existing segment within `radius` of its `x`/`z`, of a prefab whose name contains `prefabContains` and whose layers can connect to the edge's prefab, by splitting that segment at the closest point (`CoursePos.m_Entity` = segment, `m_SplitPosition`, the net tool's own split path). Closer than 2 % of the segment to an end, it joins that end node instead. Nothing within radius: the node is built as a free node. Meant for ramp tails that end 5–30 m beside a mainline. Such nodes are not created in the nodes-first pass, and their edges run after all others, so a mainline in the same import already exists. |

A node has exactly one height. All edges meeting at a node use it, so conflicting edge `elevation` values at a node are reported as warnings and ignored.

### Edges

| Field | Meaning |
|---|---|
| `id` | Unique within the request. |
| `a`, `b` | Node ids. One-way networks run from `a` to `b`. |
| `prefab` | Exact network prefab name, case-insensitive (`/prefabs?category=net&query=...` or `cs2_find_prefabs`). Any network: roads, highways, tracks, paths, pipes, power lines. |
| `control` | Optional. Two points = the cubic Bézier control points b and c; one point = a quadratic control point. Only x and z are used; heights follow the terrain plus the interpolated end elevations. |
| `elevation` | Optional `[e1, e2]`, used only for nodes that have no `elevation` of their own. |
| `attachTo` | Optional `{a?: {radius, prefabContains?}, b?: {...}}`: `snapToSegment` for that end's node, unless the node has its own. |

End-to-end length must be 4–1500 m. The game may split an edge into several segments where it crosses other networks; all of them are reported.

### Options

| Option | Default | Meaning |
|---|---|---|
| `batchSize` | 64 | Definitions per batch (1–512). |
| `nodesFirst` | true | Create every node as a standalone node entity first, then attach all edges to node entities. Judged per network prefab: if the game rejects or ignores standalone nodes of one prefab (the first one tried fails before any succeeds), that prefab's nodes fall back to free edge ends (see below) and `nodesFirst.refusedPrefabs` says why; other prefabs keep nodes-first. |
| `heightMode` | `"elevation"` | `"absolute"` derives missing `elevation` from `y`. |
| `settleFrames` | 1 | Tool frames with `applyMode = None` between creating definitions and applying (1–30). Raise it if large batches are rejected for no stated reason. |
| `dryRun` | false | Validate ids, references, prefab names and lengths; build nothing. |
| `bypassValidation` | false | Sets `ToolSystem.ignoreErrors` (the game's own ignore-errors switch, which `GetAllowApply` honours) for the duration of the import and restores the previous value afterwards, also when the import is interrupted. Imports then no longer depend on Anarchy's toggle or the open tool. The report's `validation` block records it. |
| `report` | `"summary"` | `"full"` adds every per-edge result to the response. Large imports should keep the summary and page with `GET /import/report`. |

## How it builds

1. **Validate** the whole body. Any problem (unknown prefab, dangling node id, duplicate id, bad length) rejects the request with HTTP 400 and a `problems` list; nothing is built.
2. **Nodes first.** Each node without an entity becomes a zero-length course (`NetCourse` with start = end, prefab of the first edge using it), the single-point branch of `GenerateNodesSystem`. After the batch is applied, the new node entities are found by diffing against a snapshot taken before the batch, and matched to the import nodes by position (within 0.75 m).
3. **Edges.** Each edge is one `CreationDefinition` + `NetCourse` whose ends carry the node entity in `CoursePos.m_Entity` (attached ends keep the node's height and never get `FreeHeight`).
4. **Detection.** An edge counts as built when three points along its course (t = 0.2, 0.5, 0.8) each lie within half the prefab width (+0.5 m) of a segment of the same prefab that is new in the batch or was created earlier by this import. `created` = one segment spans exactly the course. `merged` = the game joined collinear courses of one prefab into a longer segment and dropped the node between them; the covering segment is listed in `segments` and in `error`, and is recorded for undo.
5. **Merged nodes.** A node that the game dropped this way is `absorbed`. A later edge that ends there attaches by splitting the covering segment (`CoursePos.m_Entity` = segment, `m_SplitPosition` = t), as the road tool does when it snaps onto a segment, so T-junctions still connect.
6. **Fallback** when standalone nodes do not work: edges are built with free ends. Within one batch, free ends at bit-identical positions merge into one node (that is how `GenerateNodesSystem` keys new nodes), and every node is resolved to its entity after its first batch, so later batches attach by entity.
7. **Batches** use the vanilla tool handshake through `ToolOutputBarrier`: `Clear` (definitions) → `None` (preview generated and validated, `settleFrames` frames) → `Apply` if `GetAllowApply()` → `None` (resolve). A rejected batch is split in half and retried until single items remain, so a bad edge fails alone, with the game's error names (`ToolErrorData`).

## Report

Default (`report: "summary"`), sized for an MCP client even for thousands of edges:

```json
{
  "importId": "imp-20261018-101500-1",
  "completed": true,
  "abortReason": null,
  "edges": { "requested": 1530, "created": 1205, "merged": 320, "failed": 5, "notRun": 0 },
  "byPrefab": { "Highway Oneway - 2 lanes": { "created": 900, "merged": 250, "failed": 2 } },
  "nodes": { "used": 1600, "resolved": 1420, "absorbedIntoMergedSegments": 180, "unresolved": 0, "standaloneCreated": 1590 },
  "nodesFirst": { "enabled": true, "refusedPrefabs": [] },
  "batches": { "applied": 52, "rejected": 3 },
  "frames": 230, "seconds": 4.3,
  "undo": { "segmentsRecorded": 1300, "nodesRecorded": 1590, "note": "..." },
  "warningCount": 0, "warnings": [],
  "failedEdges": [{ "id": "e17", "prefab": "...", "error": "blocked by game validation (SteepSlope)" }],
  "unresolvedNodes": [],
  "note": "...",
  "results": null
}
```

`failedEdges`, `unresolvedNodes` and `warnings` are capped at 200 entries. Per-edge results (`GET /import/report`, or `results` with `report: "full"`):

```json
{ "id": "e1", "status": "merged", "error": "merged by the game into segment 5120:1 (...)", "prefab": "Highway Twoway - 4 lanes",
  "segments": [{ "index": 5120, "version": 1 }], "nodeA": { "index": 5101, "version": 1 }, "nodeB": null }
```

`status` is `created`, `merged` (built, inside a longer segment), `failed` (with `error`) or `pending` (the import stopped before that edge ran, for example because the user picked another tool). `nodeA`/`nodeB` are null for absorbed nodes. `completed: false` with an `abortReason` means the import stopped early; what was built is still recorded for undo.

## Undo

Each import that created something is recorded in game memory (last 20). `POST /import/undo` bulldozes the newest record's segments through the game's bulldoze pipeline (nodes left without segments disappear with them) and standalone nodes that never got an edge. Records are **not** saved with the map and are lost on game restart. Segments the user later connected stay; an existing road that an imported edge split at a crossing stays split.

## Segment snapping in the report

`snapToSegment` in the import report: `requested`, `snapped`, `fellBack`, and `details` (capped at 200): `{id, splitSegment, distance, t, joinedAtEnd, node}` for a snapped node (`splitSegment` is the segment that was split, `node` the junction made there), `{id, result}` for a node that found nothing within radius. One segment is split at most once per batch; further edges that would split the same segment, or that end at a node just made by a split, wait for the next batch.

## Water sources

`POST /import/water`, MCP `cs2_import_water` (file path or inline), undo with `POST /import/water/undo` / `cs2_undo_water`. Works in the editor and in a city.

```json
{
  "name": "Dakota Falls water",
  "sources": [
    { "id": "lake1", "x": 1200, "z": -800, "type": "natural", "height": 152.5, "radius": 60 },
    { "id": "creek", "x": -300, "z": 2400, "type": "stream", "depth": 2, "rate": 20, "radius": 25 },
    { "id": "river-in", "x": -7100, "z": 900, "type": "river", "height": 40, "radius": 120 }
  ],
  "dryRun": false
}
```

| Field | Meaning |
|---|---|
| `id` | Unique within the request. |
| `x`, `z` | World position; the source sits on the terrain there. |
| `type` | `natural` (alias `lake`): constant water level, the vanilla lake source. `stream`: constant flow. `river`: border river source. `sea`: border sea source, **legacy water system only**; the new water system removes sea sources and uses the editor's sea level instead, so such a source is rejected. |
| `height` | Absolute water level in metres. |
| `depth` | Water level as depth above the terrain at the source (alternative to `height`). |
| `rate` | Flow of a stream in the legacy water system. In the new water system all sources are level-driven, so `depth`/`height` is used and `rate` only stands in for a missing depth (with a note). |
| `radius` | Metres, up to 2500. Sources too small to reach a water cell are enlarged, as Water Features does, with a note. |
| `polluted` | Optional 0–1. |

How `m_Height` is filled depends on `WaterSystem.UseLegacyWaterSources` (reported as `waterSystem`): legacy stream = `rate`; legacy natural/river/sea = absolute level; new system = depth above the terrain at the source (clamped to 250 m). `m_ConstantDepth` is 0 stream, 1 natural, 2 river, 3 sea; `m_Multiplier` is 30 for river and sea, else `WaterSystem.CalculateSourceMultiplier`; `m_Id` comes from `WaterSystem.GetNextSourceId()`. Entities are `WaterSourceData + Game.Objects.Transform + Created`, plus `Updated`, through `ToolOutputBarrier`. Any invalid source rejects the whole request (HTTP 400, `problems`). The response lists each source's `sourceId`, final height, radius, multiplier and terrain height. Undo marks the recorded source ids `Deleted`. Records are in game memory only.

## Graph export

`GET /graph/export?path=<file>&prefab=<substring>&owned=true&curves=true`, MCP `cs2_graph_export`. Writes every top-level network segment (with `owned=true` also segments owned by buildings, e.g. station tracks) and its end nodes to `path` as compact JSON and answers only with the path, size and counts. Without `path` the document is returned inline when at most 2000 segments match.

```json
{
  "exportedAt": "2026-10-18T15:00:00Z", "mode": "editor", "prefabFilter": "Highway", "includeOwned": false,
  "counts": { "segments": 2029, "nodes": 1890, "perPrefab": { "Highway Oneway - 2 lanes": 1100 } },
  "segments": [{ "id": "5120:1", "prefab": "Highway Oneway - 2 lanes", "a": "5101:1", "b": "5102:1", "length": 104.2,
                 "elevation": [0, 0], "curve": [[x,y,z],[x,y,z],[x,y,z],[x,y,z]] }],
  "nodes": [{ "id": "5101:1", "x": 120.5, "y": 101.2, "z": -40.0, "degree": 3 }]
}
```

`elevation` is omitted when both ends are on the ground, `curve` unless `curves=true`. `degree` counts every segment at the node, including ones the prefab filter left out.

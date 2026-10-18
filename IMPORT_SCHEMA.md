# Network import format (`POST /import/network`)

The bridge builds a whole road/track network from a node-and-edge graph. It works in the **map editor** and in a loaded city. MCP tools: `cs2_import_network`, `cs2_import_status`, `cs2_undo_import`. CLI: `node scripts/import-network.mjs <file>`.

## Endpoints

| Method and path | Purpose |
|---|---|
| `POST /import/network` | Body below. Blocks until every batch is done (up to 15 min), then returns the report. |
| `GET /import/status` | Progress of the running import or undo, or the last report. |
| `POST /import/undo[?id=<importId>]` | Bulldozes what the newest import created. `id`, if given, must be the newest one. |

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
  "options": { "batchSize": 64, "nodesFirst": true, "heightMode": "elevation", "settleFrames": 1, "dryRun": false }
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

A node has exactly one height. All edges meeting at a node use it, so conflicting edge `elevation` values at a node are reported as warnings and ignored.

### Edges

| Field | Meaning |
|---|---|
| `id` | Unique within the request. |
| `a`, `b` | Node ids. One-way networks run from `a` to `b`. |
| `prefab` | Exact network prefab name, case-insensitive (`/prefabs?category=net&query=...` or `cs2_find_prefabs`). Any network: roads, highways, tracks, paths, pipes, power lines. |
| `control` | Optional. Two points = the cubic Bézier control points b and c; one point = a quadratic control point. Only x and z are used; heights follow the terrain plus the interpolated end elevations. |
| `elevation` | Optional `[e1, e2]`, used only for nodes that have no `elevation` of their own. |

End-to-end length must be 4–1500 m. The game may split an edge into several segments where it crosses other networks; all of them are reported.

### Options

| Option | Default | Meaning |
|---|---|---|
| `batchSize` | 64 | Definitions per batch (1–512). |
| `nodesFirst` | true | Create every node as a standalone node entity first, then attach all edges to node entities. If the game rejects or ignores standalone nodes, the importer falls back automatically (see below) and says so in `nodesFirst.fallbackReason`. |
| `heightMode` | `"elevation"` | `"absolute"` derives missing `elevation` from `y`. |
| `settleFrames` | 1 | Tool frames with `applyMode = None` between creating definitions and applying (1–30). Raise it if large batches are rejected for no stated reason. |
| `dryRun` | false | Validate ids, references, prefab names and lengths; build nothing. |

## How it builds

1. **Validate** the whole body. Any problem (unknown prefab, dangling node id, duplicate id, bad length) rejects the request with HTTP 400 and a `problems` list; nothing is built.
2. **Nodes first.** Each node without an entity becomes a zero-length course (`NetCourse` with start = end, prefab of the first edge using it), the single-point branch of `GenerateNodesSystem`. After the batch is applied, the new node entities are found by diffing against a snapshot taken before the batch, and matched to the import nodes by position (within 0.75 m).
3. **Edges.** Each edge is one `CreationDefinition` + `NetCourse` whose ends carry the node entity in `CoursePos.m_Entity` (attached ends keep the node's height and never get `FreeHeight`). New segments are found by diffing and matched to the edge's course.
4. **Fallback** when standalone nodes do not work: edges are built with free ends. Within one batch, free ends at bit-identical positions merge into one node (that is how `GenerateNodesSystem` keys new nodes), and every node is resolved to its entity after its first batch, so later batches attach by entity.
5. **Batches** use the vanilla tool handshake through `ToolOutputBarrier`: `Clear` (definitions) → `None` (preview generated and validated, `settleFrames` frames) → `Apply` if `GetAllowApply()` → `None` (resolve). A rejected batch is split in half and retried until single items remain, so a bad edge fails alone, with the game's error names (`ToolErrorData`).

## Report

```json
{
  "importId": "imp-20261018-101500-1",
  "completed": true,
  "abortReason": null,
  "edges": { "requested": 10, "created": 10, "failed": 0, "notRun": 0 },
  "nodes": { "requested": 11, "resolved": 11 },
  "nodesFirst": { "used": true, "fallbackReason": null },
  "batches": { "applied": 2, "rejected": 0 },
  "frames": 9, "seconds": 0.3,
  "undo": { "segmentsRecorded": 10, "nodesRecorded": 11, "note": "..." },
  "warnings": [],
  "unresolvedNodes": [],
  "results": [
    { "id": "e1", "status": "created", "error": null, "prefab": "Highway Twoway - 4 lanes",
      "segments": [{ "index": 5120, "version": 1 }], "nodeA": { "index": 5101, "version": 1 }, "nodeB": { "index": 5102, "version": 1 } }
  ]
}
```

`status` is `created`, `failed` (with `error`) or `pending` (the import was aborted before that edge ran, for example because the user picked another tool). `completed: false` with an `abortReason` means the import stopped early; what was built is still recorded for undo.

## Undo

Each import that created something is recorded in game memory (last 20). `POST /import/undo` bulldozes the newest record's segments through the game's bulldoze pipeline (nodes left without segments disappear with them) and standalone nodes that never got an edge. Records are **not** saved with the map and are lost on game restart. Segments the user later connected stay; an existing road that an imported edge split at a crossing stays split.

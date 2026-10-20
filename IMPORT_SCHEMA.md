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
| `connectTo` | Optional `{index?, version?, voltage?: "high"\|"low", radius?}` (0.9.13): attach this node to a building's electricity marker node, the only way a power line connects to a power plant or transformer. Either name the building, or give only `voltage` and `radius` (default 80 m) to take the nearest matching marker node of any building. The node's own x/z only picks the nearest marker. List a building's markers with `GET /entity/subnets?index=&version=` (MCP `cs2_entity_subnets`). |
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
| `wall` | Optional `left`, `right`, `both` or `none`. Forces a wall on that side whatever the elevation (quays). Left and right are seen travelling from `a` to `b`. The edge definition carries `Game.Net.Upgraded` with `CompositionFlags.Side.Raised` on that side. `GenerateEdgesSystem` copies it onto the new edge, and `CompositionSelectSystem` ORs upgrade flags into the composition, the same mechanism as road upgrades and the mods that add retaining walls. It only shows a wall where the net has a composition for raised sides (quays do; other roads only with such an asset). |
| `noPillars` | Optional `true` (0.9.22). When the import ends, deletes the generic `Game.Objects.Pillar` sub-objects of the edge's segments, and of the nodes that join only those segments. They come back whenever the game updates such a segment, for example when a later edge connects at its node, so set it on the last import that touches the bridge, or re-run `POST /net/pillars/remove`. Bridge-prefab towers and anchors are not `Pillar` objects and stay. **Unverified in game.** |
| `fixedIndex` | Accepted and ignored, with a warning. The game assigns fixed span indices itself (see "Bridge prefabs"). |

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
| `bridgeRules` | `"error"` | Checks on edges whose prefab is a bridge (see "Bridge prefabs"). `"error"` rejects the import (400) and lists the broken rules in `problems`. `"warn"` reports them in `warnings` and builds anyway. `"off"` skips the checks. The dry run and the 400 answer carry a per-edge `bridges` block either way. |

### Bridge prefabs (0.9.22)

From the decompiled game: `NetInitializeSystem` turns `Game.Prefabs.Bridge` into net data, `CourseSplitSystem.TryAddCoursePhase2` splits each course (**one import edge = one course**), and `ValidationHelpers` raises the errors. `GET /prefabs/info?name=…` returns a `bridge` block with the authoring values and the derived ranges. The offline values for the two BXP packs are in `bridges/cs2-bridge-rules.json`.

**Fixed-span prefabs** (`bridge.fixedElements` not empty, e.g. `BXP SuspensionBridge03 - Six Lane Highway`: 168 + 320 + 320 + 168 m):
- **One edge per bridge.** Send ONE edge from abutment to abutment, with no nodes in between and no `fixedIndex`. There is nothing for the converter to number: the vanilla net tool never sets `NetCourse.m_FixedIndex` either. `CourseSplitSystem` cuts the course into the span sequence, sets `m_FixedIndex` 0…n−1 and marks the inner cut points `IsFixed`. `GenerateEdgesSystem` then writes `Game.Net.Fixed{m_Index}`, and the towers come from the prefab's sub-objects (`NodeBetweenFixedSegment` etc.).
- **Span lengths.** When every span length is > 0.1, each span may be 0.6–1.4 × its length. The edge length L (x/z, through `control`) must lie within Σ0.6·len … Σ1.4·len; for SuspensionBridge03 that is 585.6–1366.4 m, nominal 976 m.
  - **L > Σmin:** the game gives each span its minimum plus a share of the excess in proportion to its range, so all spans scale by the same factor. At L = 976 every span is exact.
  - **L < Σmin:** every span shrinks in proportion.
  - **L > Σmax:** the whole sequence is built ceil((L − 0.16) / Σmax) times. That is rule `bridge.fixed.repeat`.
  - **Exact lengths:** if any span length is ≤ 0.1, the spans with a length are exact (min = max) and the zero ones take the edge range.
- **Towers.** They sit at the cumulative span ends: for SuspensionBridge03 at L × 168/976 and L × 808/976 from `a`. The dry run lists them in `bridges[].split.spans[].start`.
- **No tolerance at inner nodes.** No node may be placed between spans. Rule `bridge.fixed.chained` rejects any edge of a fixed-span prefab that shares a node with another edge of a fixed-span prefab.
- **Straight spans.** A span whose `canCurve` is false is laid straight along the end tangent. When all spans are straight, the whole bridge is a straight line from `a` to `b`, so do not give it a `control`.
- **Count ranges.** A span's `countRange` [x, y] lets the game repeat that span x…y times; a component of 0 means unbounded. Each span is cut into ceil((len − 0.16) / max) pieces within that range.
- **End nodes.** The deck nodes at `a` and `b` are ordinary nodes; approaches join there.

**Non-fixed bridge prefabs** (e.g. `BXP Truss Arch Bridge - Highway Oneway - 2 Lanes`, segmentLength 128):
- **Splitting.** Send one edge per straight run. The game cuts an edge longer than the maximum (1.4 × segmentLength = `elevatedLength`) into ceil((L − 0.16) / max) equal pieces.
  - BR005 at 305 m becomes 2 × 152.5 m, which gives the two-arch look from a single edge.
  - Two edges are needed only where the deck changes direction.
- **Minimum.** Edges must be ≥ 0.6 × segmentLength (rule `bridge.length.short`, the game's `ShortDistance`), unless `allowMinimalLength` is set. Validation allows up to 1.1 × max before the split, so the split always lands in range.

**Rules checked (names as reported):**

| Rule | Severity | Check |
|---|---|---|
| `bridge.length.short` | error | non-fixed: L < 0.6 × segmentLength |
| `bridge.straight` | error | the prefab has no `canCurve` and a `control` point lies > 1 m off the a–b chord. |
| `bridge.lowElevation` | error | the highest of the end and middle elevations above the terrain is < 2 × the net's `elevationLimit`, the game's `LowElevation` check (bridges always `RequireElevated`). A deck y from `absolute` / elevation counts. |
| `bridge.fixed.repeat` | error | L > Σmax |
| `bridge.fixed.chained` | error | a fixed-span edge shares a node with another fixed-span edge |
| `bridge.fixed.short` | warning | L < Σmin (the spans shrink) |
| `bridge.waterClearance` | warning | the deck is less than `elevationOnWater` above the water surface at one of 9 points over water ≥ 0.2 m deep. The net tool raises the deck to this height when drawing by hand, but validation does not check it. |

**Other rules:**
- **Symmetry and slopes.** The game also forces symmetrical edges and smooth slopes on bridge prefabs (`SymmetricalEdges`, `SmoothSlopes`), so keep a constant grade over the bridge. These are not pre-checked.
- **`bypassValidation`** lets ShortDistance and LowElevation pass in game. The pre-checks still apply unless `bridgeRules` is `warn` or `off`.

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

## Objects (`POST /import/objects`)

MCP `cs2_import_objects` (`file` or inline `objects`), undo `POST /import/objects/undo[?name=<name>|?id=<importId>]` / `cs2_undo_objects`, progress `GET /import/objects/status` / `cs2_objects_status`. Map editor or city.

```json
{ "name": "trees-01",
  "options": { "dryRun": false, "batchSize": 500, "bypassValidation": true, "report": "compact", "settleFrames": 1 },
  "objects": [
    { "id": "t1", "prefab": "TreeWildRandom01", "x": 0, "z": 0, "kind": "tree", "age": "adult" },
    { "id": "b1", "prefab": "<building prefab>", "x": 0, "z": 0, "y": 212.5, "rotation": 90, "kind": "building" },
    { "id": "oc1", "prefab": "Road Outside Connection - Twoway", "x": 7168, "z": 2744, "kind": "outsideConnection", "net": "road" },
    { "id": "air1", "prefab": "Airplane Outside Connection", "x": 7100, "z": -3000, "kind": "outsideConnection", "net": "air" } ] }
```

| Field | Meaning |
|---|---|
| `id` | Unique within the request. |
| `prefab` | Exact object prefab name, case-insensitive (`PREFAB_NAMES.md`; trees, bushes, rocks, props, buildings, markers). Every distinct name is resolved once; unknown names reject the request (400) with counts per name. |
| `x`, `z` | CS2 metres as in `/import/network`; must lie within ±7168 (+1 m). |
| `y` | Optional absolute height. Omitted: the terrain surface at x,z. Above the terrain it becomes the object's elevation. |
| `rotation` | Yaw in degrees, `quaternion.RotateY(radians)` = Unity `Quaternion.Euler(0, rotation, 0)`: 0 = prefab forward along +z, positive turns clockwise seen from above (90 = facing +x). To be confirmed in-game with an asymmetric building. |
| `kind` | Hint only; the bridge classifies by prefab components (`tree`, `plant`, `building`, `outsideConnection`, `object`) and reports mismatches as warnings. `prop` and `decal` are accepted as aliases of `object`. |
| `age` | Trees and bushes: `seedling` (AgeMask.Sapling, age 0), `sapling` (young/teen, AgeMask.Young, 0.175), `adult` (default, AgeMask.Mature, 0.425), `elderly` (AgeMask.Elderly, 0.775). Honoured in the editor too since 0.9.10 (the vanilla editor path picks a random age, 5 % of trees dead); never dead. Plants without `TreeData` (grass, ferns, flowers) ignore it. |
| `net` | Outside connections: `road` or `rail` (see below); anything else (e.g. `air`, `ship`) places the prefab as a free border object. |

**Grounding (`options.grounding`, 0.9.20).** For objects that have **no `y`** and whose prefab the bridge classifies as `object` (props, rocks, cliffs, decals) or `plant` (grass, ferns, flowers), the bridge computes the height from the prefab's `ObjectGeometryData.m_Bounds`. The `kind` hint plays no part, so `prop`, `decal`, `object` or none all get grounding. Objects with a `y`, as well as trees and buildings, are untouched. Without the option, the pivot sits on the terrain (the game's default). The converter should pass it for prop and object layers.

```json
"options": { "grounding": { "mode": "boundsMin", "sink": 0.15, "slopeSink": true } }
```

| Field | Default | Meaning |
|---|---|---|
| `mode` | `boundsMin` | `boundsMin`: y = terrain(x,z) − bounds.min.y, so the mesh bottom sits on the terrain. `pivot`: y = terrain, so the pivot sits on the terrain and only `sink` and `slopeSink` apply. |
| `sink` | 0 | Then lower y by sink × mesh height (bounds.max.y − bounds.min.y). A per-object `sink` (since 0.9.21, e.g. `{"id":"porock308_0","prefab":"CliffSlim","sink":0.581,…}`) replaces this value for that object only, and only takes effect when `options.grounding` is set and the object has no `y`. |
| `slopeSink` | false | Then lower y by the terrain slope × half the larger footprint side, so the downhill edge does not float. The slope is a central difference over ± max(1 m, half footprint) in x and z. |
| `maxLift` | none | Cap, in metres, on the boundsMin lift (−bounds.min.y). |

A y above the terrain becomes the object's elevation, as with an explicit `y`. The dry-run and the report give the `grounded` count.

**Caveat (bounds in PREFAB_NAMES.md):** min.y is negative for every rock, cliff and ground-cover prefab, so boundsMin always lifts. For vanilla `Rock01–30` this is right: their pivot is at the mesh centre. Pack rocks are modelled with a buried skirt, 0.4–1.9 m for most; `CliffFreeStanding`, `CliffSideTall` and `CliffSlim` reach 105–131 m. boundsMin lifts that skirt into view. For those, use `pivot` mode, a `maxLift` of a few metres, or a separate import per family.

Placement: each object becomes one definition from the bridge's port of the game's LineTool/ObjectTool definition job (`CreateDefinitions`), so buildings get their sub-objects, lots and terraforming, placeholder prefabs (`…Random01`) pick a variant, and trees get their age. Batches go through `ToolOutputBarrier` with the network importer's handshake (Clear → None → Apply, bisection on rejection). New objects are matched to ids by position among the batch's Temp objects (the game turns the Temp entity into the permanent one), so there are no full-map scans. `bypassValidation` sets `ToolSystem.ignoreErrors` for the import.

Outside connections: **road and rail** connections are not objects you place. The game tags a net node on the terrain border with `Game.Net.OutsideConnection` and spawns the marker on it. For `kind: outsideConnection, net: road|rail` the bridge finds the nearest road (or train-track) node within 20 m of x,z. It reports `created` with the node entity if the game has made it an outside connection, or `failed` saying the net must end on the border; nothing is placed. **Airplane** (and, unverified, ship) connections are free objects: the bridge moves them onto the nearest terrain edge and, without `rotation`, faces them outward (+x edge 90°, -x 270°, +z 0°, -z 180°), as the editor's object tool does (`ObjectToolSystem.HandleWorldSize`). The game rejects them off the border (`NotOnBorder`).

Response (compact; `report: "full"` adds `results`, one entry per object):

```json
{ "importId": "obj-20261019-101500-1", "name": "trees-01", "completed": true, "abortReason": null,
  "objects": { "requested": 50000, "created": 49980, "failed": 20, "notRun": 0 },
  "byPrefab": { "TreeWildRandom01": { "created": 49980, "failed": 20 } },
  "batches": { "applied": 100, "rejected": 0 }, "frames": 520, "seconds": 18.2,
  "validation": { "bypassValidation": true, "ignoreErrors": false, "anarchyDetected": true, "anarchyEnabled": false },
  "undo": { "objectsRecorded": 49980, "note": "..." },
  "warningCount": 0, "warnings": [],
  "failedObjects": [{ "id": "t17", "prefab": "TreeWildRandom01", "error": "blocked by game validation (InWater)" }],
  "outsideConnections": [{ "id": "oc1", "status": "created", "node": "5101:1", "entity": "5101:1" }],
  "results": null }
```

Full `results` entries: `{id, status (created|failed|pending), error, prefab, kind, entity, attachedTo}`. Dry run answers `{dryRun, valid, mode, objects, prefabsResolved, perKind, batchSize, warningCount, warnings, outsideConnections}`; road/rail connections are resolved in the dry run too. Requests up to 16 MB (about 50 000 objects per file).

## Natural resources (`POST /import/resources`, `GET /map/resources`)

MCP `cs2_import_resources` (file) and `cs2_map_resources`.

```json
{ "name": "resources", "options": { "dryRun": false, "mode": "replace", "scale": { "ore": 10000 } },
  "grid": { "size": 512, "cellSize": 33.75, "originX": -8640, "originZ": -8640, "rowAxis": "+z" },
  "layers": { "fertility": [ ...size*size ints 0-255... ], "ore": [...], "oil": [...], "groundwater": [...], "forest": [...] } }
```

- **Grid:** values are at cell centres, row-major, columns going +x from `originX`.
  - `rowAxis: "+z"`: row 0 at `originZ`, rows going +z.
  - `rowAxis: "-z"`: row 0 at `originZ`, rows going -z.
- **Game grid:** each game layer is 256×256 cells of 56 m over the playable area, origin -7168,-7168, row 0 at the south edge.
  - The bridge samples the input bilinearly at each game cell centre (0 outside the input grid) and writes `round(v/255 × scale)`.
- **`scale`:** defaults to 10000 per layer. That is the editor brush maximum and full concentration for extractors (`NaturalResourceSystem.MAX_BASE_RESOURCES`, `GroundWaterSystem.kMaxGroundWater`).
- **`mode`:** `replace` (default), `max` or `add`; `m_Used` is reset to 0.
- **Layers in CS2:**
  - `fertility`, `ore` and `oil` are written as given.
  - `groundwater` sets amount and max, as the editor does.
  - `fish` is accepted, but the game recomputes it from surface-water depth every update, so shape fish with water.
  - `forest` is ignored (reported under `ignored`): wood comes from tree entities.
- **After writing:** the bridge marks map features `Updated`, as the editor's own resource import does.
- **Response:** `{name, dryRun, mode, writeMode, gameGrid:{size,cellSize,originX,originZ,rowAxis}, layers:[{layer,scale,cellsWritten,cellsNonZero,mean,max,note}], ignored:[{layer,reason}], note}`.

**Read-back:** `GET /map/resources?layer=fertility|ore|oil|fish|groundwater&size=8..256&field=base|used|available|max`.
- `size` defaults to 128 and `field` to `base`; `max` applies to groundwater.
- Returns `{layer, field, size, cellSize, originX: -7168, originZ: -7168, rowAxis: "+z", max, note, values:[size*size]}` in game units.

## Map settings (`GET` / `POST /map/settings`)

MCP `cs2_map_settings` (pass `set` to write). See `MAP_SETTINGS.md` for each field and the editor panel that owns it.

POST body, all fields optional:
`{theme, climate, latitude, longitude, seaLevel, heightScale, leftHandTraffic, naturalDisasters, startingYear, startingMonth (1-12), startingTime (0-1), currentYearAsStartingYear, mapNameAsCityName, cameraPivot:{x,y,z}, cameraAngle:[x,y], cameraZoom}`

Response (GET and POST): `{mode, applied:[names], problems:[...], settings:{...}, notWritable:[...], note}`.
- `settings` holds: `mapName, theme, climate, latitude, longitude, seaLevel, waterSystem, heightScale, heightOffset, leftHandTraffic, naturalDisasters, unlockAll, unlimitedMoney, unlockMapTiles, time:{startingYear,startingMonth,startingTime}, mapPanel:{...}, camera:{pivot,angle,zoom}, startingTiles`.
- Name, description, preview, thumbnail and starting tiles are set by hand in the editor.

## TM:PE lane connections → Traffic (`/import/tmpe`)

Recreates the CS1 map's TM:PE lane connections and lane arrows as lane connections of the **Traffic** mod (PDX 80095, krzychu124). Requires Traffic in the playset. The bridge is compiled against **Traffic 0.2.13+ce1551f** (`pdx_mods/80095_29/Traffic.dll`, csproj `TrafficDllPath`); another Traffic build is refused (409) unless `traffic.forceVersion` is set, because the importer writes Traffic's component structs directly. Without Traffic the bridge still loads; only these endpoints answer 409.

| Endpoint | |
|---|---|
| `POST /import/tmpe` | Body from `scripts/import-tmpe.mjs` (`cs2mcp/tmpe-import/v1`). Runs at the next ToolUpdate and returns the report. |
| `POST /import/tmpe/undo` | Removes the Traffic data from every node the importer wrote in this game session (memory only). |
| `GET /import/tmpe/status` | The last report. |

MCP: `cs2_import_tmpe` (`file`, `dryRun`, `replace`), `cs2_undo_tmpe`. CLI: `node scripts/import-tmpe.mjs [--dry-run|--undo|--status|--write-only|--replace]`; it builds the body from `cs1-offline/out/tmpe-offline.json`, the newest CS1 dump (net prefab lanes, segment directions) and `net-converter/out/import-all.json` (CS2 position of node `n<id>`, course of `e<segment>_k`), and writes `out/tmpe-import-report.{json,md}`.

**Mapping (all by position; entity ids from an import report do not survive a reload):**
- node: the nearest CS2 net node (3D) to the imported node position, within `nodeRadius` (12 m);
- edge: of the edges starting or ending at that node, the one whose centre line, `probeDist` (20 m) from the node, is nearest the CS1 segment's probe point, within `edgeTolerance` (8 m);
- lane: the CS2 car lane flowing into the node (source) or out of it (target) whose lateral offset from its direction group's centre is nearest the CS1 lane's. CS1 and CS2 must have the same number of car lanes in that direction, otherwise the connection is **skipped** (`lane-count mismatch`). If any connection of a CS1 source lane is skipped, the others of that lane are skipped too (a partial list would remove a connection TM:PE kept).
- arrows: TM:PE ignores arrows on lanes with lane connections (`superseded`). Otherwise the lane keeps only those of CS2's current connections whose target edge matches a CS1 segment in an arrow direction (TM:PE's rule: Left/Right when |cross(from, to)| ≥ 0.5 with both directions pointing away from the node). `unchanged` if CS2's defaults already follow the arrows.

**What is written** (as Traffic's `TLEDataMigrationSystem`): on the node `ModifiedConnections` and a `ModifiedLaneConnections` buffer (one entry per source lane: edge, composition lane index, lane position, carriageway/group); per source lane an entity with `DataOwner(node)`, `PrefabRef(Traffic.FakePrefab)` and a `GeneratedConnection` buffer (`method` Road; lane positions and carriageway/group from Traffic's `NetUtils.GetAdditionalLaneDetails`). Then `Updated` on the node, its edges and their other nodes, so Traffic's lane system rebuilds the node lanes in the same frame. Nodes that already have Traffic data are skipped unless `options.replace`, which rebuilds that node's Traffic data from the import only.

Statuses: connections `created` / `mapped` (dry run) / `skipped`; arrows also `superseded`, `unchanged`. Save the map to keep the result; Traffic resets custom connections when a road's composition changes (or Road Builder touches it).

## Notes from the first full run (0.9.12)

- **A net course inside another net is dropped silently.** If a course runs within the footprint of an existing segment, the game discards it at apply time without a validation error, even with `bypassValidation` (`ignoreErrors`). The standalone nodes made for it vanish too. "Within the footprint" means every sample point is closer to that segment's centre line than the two half-widths. Seen live with CS1 retaining walls (`QuaySmall01`, 3 m wide) running 1–7 m beside highways (8/12/16 m wide).
  - Since 0.9.12 such edges fail with `dropped by the game: the course runs inside the existing '<prefab>' segment …; move it at least N m sideways, or leave it out`.
  - The bare half-widths are not quite enough. In the live test, clearances of half-widths + 1 m still failed for about half the edges, and +4 m or more worked.
  - Walls wedged between parallel carriageways in interchanges cannot be built as nets at all.
- **Dry runs:** `dryRun` is honoured in `options` and, since 0.9.12, also at the top level of the body (network, objects, surfaces). The MCP tools `cs2_import_network`, `cs2_import_objects` and `cs2_import_surfaces` have a top-level `dryRun` parameter that overrides the file's `options.dryRun`. Before 0.9.12, a top-level `dryRun` passed to the MCP tool was silently dropped by its schema, and the import ran for real.
- **Object kinds:** the bridge classifies a prefab as `building` when it carries `BuildingData`, i.e. a `BuildingPrefab`: zoned, service, signature and unique buildings, and the vanilla editor's building tab. Static objects that look like buildings but are `StaticObjectPrefab`s (piers such as `LeisurePier01HeadSmall`, `ElectricalSubstation01`) are `object`. A `kind: "building"` hint on those only produces a warning; placement is the same.
- **Climate and theme names:** `GET /prefabs?category=climate` and `category=theme` list them (0.9.12). An unknown `climate` or `theme` in `POST /map/settings` now lists the available names.

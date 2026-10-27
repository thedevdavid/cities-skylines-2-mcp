# Surfaces in the CS2 map editor, and `POST /import/surfaces`

Implemented in bridge `0.9.8-editor-import` (not yet run in the game). Sources: decompiled `Game.dll` (CS2 1.6.2) and the localisation keys in `Locale.cok`.

## How the editor stores surfaces

Surfaces are **area entities with a polygon**, not texture or mask data:

- **Prefab:** `SurfacePrefab : AreaPrefab`. It adds `SurfaceData` and `AreaGeometryData` (`m_Type = AreaType.Surface`).
- **Instance:** an entity with `Game.Areas.Area`, `Surface` (a tag), `Geometry`, `Expand` and a `DynamicBuffer<Game.Areas.Node>`. Each node is a position plus an elevation.
- **Rendering:** the game triangulates the polygon (`Triangle` buffer) and renders the surface material over the terrain. Surfaces save into the map like any area.
- **Closed ring (0.9.26):** the bridge writes the node buffer closed (first node repeated at the end), as the area tool does. Only then does the game mark the area `Complete`. Before 0.9.26 imported surfaces were incomplete areas, and with `bypassValidation` they deleted the nets and objects under them.
- **Shape rules:** a polygon is a single outer ring. **There are no holes.** `Game.Areas.ValidationHelpers.ValidateArea` rejects self-intersecting rings and rings without triangles as `InvalidShape`. I found no surface-vs-surface overlap check in that code path, but overlap behaviour should be checked in-game.
- **How the editor creates one:** the Areas/Surface tool (`AreaToolSystem`) writes a `CreationDefinition { m_Prefab }` plus a `Node` buffer through `ToolOutputBarrier`. The generate and apply systems turn the Temp area into the permanent one (`ApplyAreasSystem.Create` removes `Temp`, keeping the entity). The bridge already used this path for districts (`/build/district`).

## Prefabs (base game)

| Prefab (use this name) | English UI name | Notes |
|---|---|---|
| `Grass Surface 01`, `Grass Surface 02` | Grass Surface 01 / 02 | |
| `Sand Surface 01`, `Sand Surface 02` | Sand Surface 01 / 02 | closest to CS1 gravel / sand |
| `Pavement Surface 01`, `Pavement Surface 02` | Pavement Surface 01 / 02 | |
| `Concrete Surface 01`, `Concrete Surface 02` | Concrete Surface 01 / 02 | |
| `Tiles Surface 01` … `Tiles Surface 03` | Tiles Surface 01–03 | |
| `Agriculture Surface 01` | Agriculture Surface | industry-area look (fields) |
| `Forestry Surface 01` | Forestry Surface | |
| `Ore Surface 01` | Ore Surface | |
| `Oil Surface 01` | Oil Surface | |
| `Landfill Surface 01` | Landfill Surface | |
| `Clip Surface` | Clip Surface | special: clips (hides) terrain, used by assets; not for painting ground |
| `… Surface Placeholder` | | placeholder variants used inside assets; prefer the numbered prefabs |

There is no vanilla gravel surface; `Sand Surface 02` or `Pavement Surface 02` are the nearest looks. `GET /prefabs` does not list areas yet. A dry run of `/import/surfaces` reports unknown names, and those found in the running game (including content packs) are accepted.

## Contract (`POST /import/surfaces`)

```json
{ "name": "surfaces-01",
  "options": { "dryRun": false, "batchSize": 50, "bypassValidation": true, "report": "compact", "settleFrames": 1 },
  "surfaces": [
    { "id": "s1", "prefab": "Grass Surface 01", "polygon": [[100, 200], [180, 200], [180, 260], [100, 260]] } ] }
```

| Field | Meaning |
|---|---|
| `id` | Unique within the request. |
| `prefab` | Surface prefab name from the table, case-insensitive. Unknown names reject the request (400) with counts. |
| `polygon` | Outer ring `[[x, z], ...]` in CS2 metres (same frame as networks; within ±7168). Either winding order. A closing point equal to the first is dropped, as are consecutive duplicates. 3–2000 points, area ≥ 1 m². Nodes are draped on the terrain (elevation `float.MinValue`, as the editor does). |
| `holes` | **Not supported** (CS2 areas have none). If present and non-empty, the request is rejected. The converter must split polygons with holes into hole-free pieces, e.g. by triangulating and re-merging, or by cutting along a line through each hole into two simple polygons. Keyhole rings with touching edges will likely fail as `InvalidShape`. |

- **Options:** as for `/import/objects`. `batchSize` defaults to 50 surfaces per batch (max 1000); large polygons cost more per batch than trees do. `bypassValidation` sets `ToolSystem.ignoreErrors` for the import. A batch the game rejects is bisected down to the offending surface, whose `error` names the game's validation errors (typically `InvalidShape` for self-intersections).
- **Response:** the object-import report (see `IMPORT_SCHEMA.md`), with counts under `objects` and `failedObjects`, and `kind: "surface"` in full results.
- **Dry run:** answers `{dryRun, valid, mode, surfaces, nodes, prefabsResolved, batchSize}`.
- **Matching:** surfaces are matched to ids by the vertex-average centroid of the Temp area.
- **Undo:** `POST /import/objects/undo?name=<name>` (MCP `cs2_undo_objects`), which bulldozes the recorded areas. It shares the in-memory records with object imports.
- **MCP tool:** `cs2_import_surfaces` (`file` or inline `surfaces`).

## Work estimate and remaining risks

The endpoint took about two hours on top of the object importer: one more definition kind, Temp-area matching and area deletion. Unverified until the game runs:

1. **Overlap:** whether overlapping surfaces of different prefabs are accepted (and which draws on top).
2. **Performance:** how big polygons (1000+ nodes) behave in the editor's triangulation. The converter should simplify (Douglas-Peucker with about 2 m tolerance) and keep rings under about 500 nodes.
3. **Deleting areas:** whether bulldoze definitions (`CreationFlags.Delete` + node copy) remove areas as they remove objects. The vanilla bulldozer does it the same way, but this is untested here.

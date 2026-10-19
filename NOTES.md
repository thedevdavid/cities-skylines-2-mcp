# Editor import: notes and manual verification

Branch `editor-import` (local, not pushed). Bridge version `0.9.10-editor-import` (shown by `cs2_ping`). Built with 0 warnings and deployed to `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2MCP\CS2MCP.dll`. MCP server rebuilt (`mcp-server/dist/index.js`). **Nothing here has run inside the game yet.**

## What changed

### 1. Where the bridge assumed a city

| Place | Assumption | Now |
|---|---|---|
| `RequestHandlers.TryGetCity` | `gameMode == Game` and `CitySystem.City != Null`; used by 59 handlers before this change | Unchanged. New `TryGetCityOrEditor` is the explicit editor branch: `gameMode == Editor` (and not loading) passes without a city, otherwise it falls through to `TryGetCity`. The city error now says when an endpoint is city-only. |
| `/prefabs` (`GetPrefabs`) | city gate; `Locked` flags and staleness warning | editor allowed; `locked` reported false and no staleness warning in the editor |
| `/build/road` (`BuildRoad`), `/build/road/connect` (`ConnectRoad`) | city gate; milestone lock check | editor allowed; lock check skipped in the editor |
| `/city/terrain`, `/city/road-graph`, `/city/roads`, `/entity/inspect`, `/build/demolish` | city gate | editor allowed (no city data is read) |
| `/ping`, `/screenshot`, `/camera`, `/camera/set`, `/prefabs/info` | none | unchanged |
| `/state` (`GetState`) | reads city systems only when `gameMode == Game` | unchanged; reports `gameMode: "Editor"`, `cityLoaded: false` |
| `BridgeToolSystem` / `BridgeTransitToolSystem` | `CityConfigurationSystem.leftHandTraffic`/`defaultTheme`; `m_EditorMode = actionMode.IsEditor()` | unchanged (building placement is still city-gated) |
| `/city/tiles/*` (`PlayerMoney`), economy, demand, services, policies, districts, zoning, transit, traffic, vehicles, `/sim/*`, `/game/save` | need a city | still city-gated (409 in the editor) |
| `/camera` | `CameraUpdateSystem.gamePlayController` | unchanged; may not be the editor's active camera (question 6) |

### 2. Batch import

`POST /import/network`, `GET /import/status`, `POST /import/undo`; format and algorithm in `IMPORT_SCHEMA.md`. Code: `CS2MCP.Bridge/RequestHandlers.Import.cs` (validation, job) and `CS2MCP.Bridge/BridgeImportToolSystem.cs` (batches, nodes-first, bisection on rejection, entity resolution by snapshot diff, in-memory undo records). HTTP: request bodies up to 16 MB; `/import/*` waits up to 15 min instead of 10 s; `/build/*` returns 409 while an import runs. MCP tools: `cs2_import_network` (file path or inline), `cs2_import_status`, `cs2_undo_import`.

### 3. Fixture

`fixtures/ten-highway-segments.json`: 11 nodes, 10 edges of `Highway Twoway - 4 lanes` (name taken from the game's `Assets.NAME[...]` localization keys; the runtime list is the authority). Chain `n0…n9` along +x, 120 m segments, a gentle bend after `n5`, `e7` curved through two control points, and a T-branch `e10` from `n3` going 160 m to −z. Coordinates are relative; `--at X,Z` moves them. Script: `scripts/import-network.mjs`.

## Manual verification (you, in the game)

1. Playset for the editor: CS2MCP (local mod), Anarchy, Scene Explorer. Nothing else needed for this test.
2. Start the game, open the map editor, load or create a map (a flat test map first, then `dktflls`).
3. In a Claude Code session in `D:\work\mods\csl2-editor`: `cs2_ping`. Expect `version: 0.9.4-editor-import`, `gameMode: Editor`. If `/ping` fails, the bridge did not start: check `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2MCP.log`.
4. `cs2_game_state` (needs the main-thread loop): expect a JSON answer, not a 503. A 503 means `BridgeSystem` does not update in the editor (question 1).
5. `cs2_find_prefabs` category `road`, query `Highway`. Confirm `Highway Twoway - 4 lanes` exists; if not, put the listed name into the fixture.
6. `cs2_terrain` resolution 32: heights should match the imported heightmap. Pick a flat, dry spot X,Z.
7. Dry run: `node cs2mcp/scripts/import-network.mjs cs2mcp/fixtures/ten-highway-segments.json --at X,Z --dry-run`. Expect `valid: true`, `mode: "editor"`.
8. Import: the same without `--dry-run` (or `cs2_import_network` with `file`). Expect `edges.created: 10`, `nodesFirst.used: true`. Note `nodesFirst.fallbackReason` if present.
9. Check connectivity: `cs2_road_graph` at X+500,Z radius 800 query `Highway`. Expect 10 segments, 11 nodes; `n3`'s node has 3 segments, `n1…n8` have 2, ends 1. Look at it (Scene Explorer on a node: `ConnectedEdge` buffer; `cs2_screenshot`).
10. Undo test: `--undo`, confirm the highways are gone, `cs2_road_graph` is empty there. Import again.
11. Save the map in the editor, exit to the main menu, reload the map. `cs2_road_graph` again: same 10 segments and junction counts. Look at the junctions to confirm nothing detached.
12. Optional: `--no-nodes-first` on another spot, to test the fallback path; and `cs2_build_road` / `cs2_connect_road` once each in the editor.

Please paste the import report (step 8) and the road graph (steps 9 and 11) back, plus anything in `CS2MCP.log`.

## Open questions about editor behaviour

1. Does `BridgeSystem` (registered at `SystemUpdatePhase.UIUpdate`) update in the editor? `/ping` is answered off-thread, so only step 4 proves the main-thread loop runs.
2. Do custom `ToolBaseSystem`s at `SystemUpdatePhase.ToolUpdate` run in the editor, and does setting `ToolSystem.activeTool` from code work there as in a city?
3. Does the game accept a standalone node (zero-length `NetCourse`) for a highway prefab, and does it survive `Apply` as a permanent node (with `Orphan`)? If not, the importer falls back automatically; the report says which path ran.
4. Do edges attached through `CoursePos.m_Entity` to those standalone nodes keep the same node entity (no duplicate node at the junction)? Step 9 checks this.
5. Is one `settleFrames` frame enough for validation of a 64-edge batch? If batches are rejected without error names, retry with `--settle 3`.
6. Is `CameraUpdateSystem.gamePlayController` the editor's camera? `cs2_get_camera`/`cs2_set_camera` may read or move an inactive controller; `cs2_screenshot` itself should work regardless.
7. Does `GetAllowApply()` in the editor reject on the same errors as in a city (water, steep slope, overlap), or does the editor ignore some (`ToolSystem.ignoreErrors`)?
8. Do imported segments save into the `.cok` and reload with the same connectivity (step 11)? Expected yes, since they are ordinary permanent entities created by the game's own apply systems.
9. Does building a road in the editor cost or need anything (money, unlocks)? The editor is expected to have neither; the import does not check.
10. Highways at grade: does CS2 accept a T-junction on `Highway Twoway - 4 lanes` without an error (the fixture's `e10`)? If it is rejected, `e10` fails alone; switch it to a road prefab and record what the game said.
11. Elevated or tunnel ends (`elevation ≠ 0`): untested here; the fixture is all at ground level. Worth one bridge edge before importing Dakota Falls.
12. Undo records live in memory only. Is that enough, or should imported entities carry a saved tag? A custom component would end up in the map file, so I did not add one.

## Round 2 (after the first Dakota Falls run)

Findings from the live run: highways 1530 → 1205 reported created, rail 402 → 227, roads 1614 → 838, no rejected batches, elevations correct. Changes in `0.9.2-editor-import`:

1. **False "failed" edges.** The game joins collinear courses of one prefab into a longer segment and drops the node between them, so exact-end matching missed them. Detection now checks coverage: three points along each course must lie within half the prefab width of a new (or earlier-imported) segment of that prefab. New status `merged`, with the covering segment in `segments`/`error`; it is recorded for undo.
2. **Dropped nodes** (also the cause of the rail warnings "no node entity found at its position"): such nodes are `absorbed`, and a later edge ending there attaches by splitting the covering segment (`CoursePos.m_Entity` = segment, `m_SplitPosition`), so junctions on merged stretches still connect.
3. **Nodes-first per prefab.** A prefab whose standalone nodes the game refuses falls back alone (`nodesFirst.refusedPrefabs`); other prefabs keep nodes-first.
4. **Compact report** by default (counts per status and per prefab, failed ids, capped lists). `GET /import/report` / `cs2_import_report` pages through per-edge results; `options.report: "full"` restores the old full output.
5. **`cs2_terrain` flat 511.9 m in the editor.** Not reproduced offline: the endpoint and the importer call the same `TerrainSystem.GetHeightData()`. 511.9 m is half of a 1024 m height scale, i.e. flat or stale CPU heights. The endpoint now waits for the pending GPU readback (`GetHeightData(waitForPending: true)`) and returns `minHeight`, `maxHeight` and `diagnostics` (CPU height-copy resolution, height scale, backdrop). If it is still flat, send the `diagnostics` block.

Re-check after restarting the game: re-run the three layers (undo first, or use a fresh map), compare `edges.created + merged` with the earlier counts, confirm `unresolvedNodes` is empty for rail, and run `cs2_terrain` once.

New open question: does joining an edge to a merged segment by split (point 2) ever re-merge or move the junction? Check one T-junction on a merged highway stretch in-game.

## Round 3 (0.9.3)

Verified by the user on 0.9.2: import-all (3546 edges) with Anarchy gave 2029 created, 1509 merged, 8 failed, 0 rejected batches in 10 s; `cs2_terrain` returns real heights.

Added, not yet run in the game:

1. **`snapToSegment`** on nodes and **`attachTo`** on edges: split the nearest matching segment within a radius (ramp tails beside a mainline); report under `snapToSegment`.
2. **Water sources**: `POST /import/water`, `/import/water/undo`, MCP `cs2_import_water` / `cs2_undo_water`.
3. **Graph export**: `GET /graph/export` / `cs2_graph_export` writes the whole graph to a file.

To check: one ramp tail with `snapToSegment` radius 30 (junction made, `splitSegment` reported, mainline still continuous); one natural source with `dryRun` then for real, and whether `waterSystem` says `new` or `legacy` on this map; `cs2_graph_export` to a file and compare counts with the import report.

Open questions: in the new water system, does a `stream` source behave as a flow (we write depth)? Does a source written through `ToolOutputBarrier` while no tool of ours is active get played back in the editor (Water Features writes from its active tool)? Do two snaps onto one mainline in consecutive batches land on the right halves after the first split?

## Round 4 (0.9.4): validation and Anarchy

Reported: 0.9.3 rejected many batches (SteepSlope, OverlapExisting, ...) where 0.9.2 had none. Nothing between f1b03ba and cee951a touches the apply decision (`GetAllowApply`, `ignoreErrors`, `actionMode`, tool requirements); snapping only adds course ends. How Anarchy works (from its Anarchy.dll): `DisableToolErrorsSystem` sets the "disabled in game/editor" flags on the allowed `ToolErrorData` every frame **while Anarchy's toggle is on**, for whatever tool is active; on every tool change `EnableToolErrorsSystem` restores all errors once. So the toggle state, not the mod being enabled, decides, and a run with the toggle off validates in full.

Changes: every import report has `validation {ignoreErrors, anarchyDetected, anarchyEnabled, anarchyEnabledAtEnd}`, and `GET /import/status` shows the live `validation` block (read Anarchy's state there before importing). The import tools register with Anarchy through `Anarchy.Bridge.AnarchyBridge.TryAddToolSystem` (reflection; no hard dependency), and an import waits 3 tool frames after activating its tool before the first batch, so Anarchy's one-frame error restore on tool change cannot reject the first batch.

Round 5 (0.9.5): confirmed not a regression (Anarchy toggle is per tool context and resets on map load). New `options.bypassValidation` (script `--bypass`) sets `ToolSystem.ignoreErrors` for the import and restores it afterwards.

Round 6 (0.9.6): `/import/water` and `/import/water/undo` failed with "Trying to create EntityCommandBuffer when it's not allowed" (the ToolOutputBarrier buffer was created in the request handler, outside a system update). Both now run inside `BridgeImportToolSystem`'s tool update (activate, warm-up, write through the barrier, answer), like the network importer; exceptions there answer HTTP 500 with the message. Requires a game restart to load.

## Round 7 (0.9.7): objects, natural resources, map settings

New, not yet run in the game:
- **Objects:** `POST /import/objects` (+ undo, status).
- **Natural resources:** `POST /import/resources` + `GET /map/resources`.
- **Map settings:** `GET/POST /map/settings`.
- **MCP tools:** `cs2_import_objects`, `cs2_undo_objects`, `cs2_objects_status`, `cs2_import_resources`, `cs2_map_resources`, `cs2_map_settings`.

Shapes are in `IMPORT_SCHEMA.md`, names in `PREFAB_NAMES.md`, editor panels in `MAP_SETTINGS.md`.

Contract deviations:
- Road/rail outside connections are verified, not placed: the game creates them from nets ending on the border.
- `forest` is ignored: there is no such cell layer.
- `fish` is overwritten by the game.
- Map name, description, preview and starting tiles are UI-only.

Verification list for the next game start (dakota-v8):

1. `cs2_ping` shows `version: 0.9.7-editor-import`.
2. One water source: `cs2_import_water` with `dryRun: true`, then for real, then `cs2_undo_water`. It now runs through the tool update. Check whether `waterSystem` says legacy or new, and that water appears after letting the simulation run.
3. Asymmetric rotation test: place one building with an obvious front at `rotation: 0` and another at `rotation: 90`. Confirm that 0 faces +z (north) and 90 faces +x (east, clockwise from above). Record the result here and in IMPORT_SCHEMA.md.
4. 2 000-tree timing: `cs2_import_objects` with 2 000 `TreeWildRandom01` (batch 500, `bypassValidation: true`). Note `seconds`, `frames` and created/failed, then run `cs2_undo_objects`.

## Round 8 (0.9.8): surfaces

Surfaces are area entities: a polygon of `Game.Areas.Node` with the `Surface` tag. They are not texture data. New: `POST /import/surfaces` (MCP `cs2_import_surfaces`), run by the object tool; undo with `/import/objects/undo?name=`. Details, prefab list and contract are in `SURFACES.md`. Not yet run in the game.

Add to the next verification run (after the round-7 list):

5. A grass rectangle and an L-shaped sand polygon on dakota-v8: run `cs2_import_surfaces` with a dry run, then for real. Check that both render, then undo by name and check that they are gone.

## Round 9 (0.9.9): prefab listing for custom assets, ship paths

`GET /prefabs` gains `category=object` (every object prefab: props, decals, rocks, bushes, markers; custom ones included) and `category=surface`, plus `offset` and `limit` up to 1000 for paging (MCP `cs2_find_prefabs`). The ship-path prefab names (`Narrow/Medium/Wide Seaway`, `Narrow/Medium/Wide Boatway`) and the decal answer are in PREFAB_NAMES.md.

Add to the next verification run:

6. Ship paths: import one `Medium Seaway` edge over water that ends on the border. Check whether the border node gets `Ship Outside Connection - Twoway` automatically, as roads do: find the node with `cs2_road_graph` (allNets) and inspect it in Scene Explorer for `Game.Net.OutsideConnection` and the marker sub-object. If not, place `Ship Outside Connection - Twoway` with `/import/objects` (`kind: outsideConnection, net: ship`; snapped to the border) and see whether the game accepts it.
7. Custom asset packs (grass props, rocks, decals from PDX Mods): run `cs2_find_prefabs` with category `object` and a pack-specific query. The names must appear. Then dry-run `/import/objects` with a few of those names (they must resolve), place one decal and one rock for real, and undo.

## Round 10 (0.9.10): audit fixes (audit/cs2-side.md)

- **Finding 1, tree age:** the requested `age` is now applied in the editor. The ported definition job has `m_FixedAge`, and the object importer sets it, so editor mode uses `ToolUtils.GetRandomAge(mask)`: one bit gives one age, never dead. Before this, the vanilla editor path picked `random.NextFloat(1)` and about 5 % of trees came out dead. In-game single placements are unchanged.
- **Finding 12:** `kind: "prop"` (and `"decal"`) count as `object`; no more mismatch warnings.
- **Finding 4:** edge field `wall: left|right|both|none` adds `Upgraded` with `Side.Raised` to the edge definition, forcing quay walls at any height.

Add to the next verification run:

8. Quay wall, one edge. Import one `QuaySmall01` edge on flat dry ground at elevation 0 with `wall: "left"`, and a second with `wall: "both"`. Check that the first shows a wall on the left side (seen from a to b) and the second on both sides. Inspect the edge in Scene Explorer: `Upgraded.m_Flags.m_Left` should contain `Raised`. If no wall shows, the asset has no raised-side composition at that elevation; note it, and fall back to node elevation ≥ 4 m (one side) or ≥ 8 m (both).
9. Seaway border node at (4718.4, −7168): after importing the seaways, find that node (`cs2_road_graph` with allNets near it, or `cs2_graph_export` with prefab Seaway) and inspect it in Scene Explorer. Check for `Game.Net.OutsideConnection` and a `Ship Outside Connection - Twoway` sub-object. If there is a tag but no marker, place the marker with `/import/objects` (`kind: outsideConnection`, `net: ship`) at that point.
10. Does a seaway dig? Read `cs2_terrain` (resolution 256) along one test `Medium Seaway` edge, import that one edge with its nodes at riverbed y (`heightMode: absolute`), and read `cs2_terrain` again. If the bed dropped by the hull depth, the converter should write seaway y at the water surface (sea level or the CS1 water level), or omit y so the bridge uses the terrain.

## Round 11 (0.9.11): TM:PE lane connections → Traffic

New, not yet run in the game: `POST /import/tmpe` (+ undo, status), MCP `cs2_import_tmpe` / `cs2_undo_tmpe`, `scripts/import-tmpe.mjs`. Details in IMPORT_SCHEMA.md. Compile-time reference to Traffic.dll 0.2.13+ce1551f (PDX 80095 build 29), used only by `TrafficLaneImport`; the writes run in `BridgeTmpeImportSystem` at ToolUpdate (Updated tags added from BridgeSystem's UIUpdate would be cleared by CleanUpSystem before Traffic's lane system runs).

Offline (`--write-only`): 27 nodes, 68 connections, 39 arrow lanes (37 superseded by connections, as in TM:PE), 0 direction inconsistencies, all lane ends resolved through `import-all.json` geometry.

Verification (playset: CS2MCP, Traffic 0.2.13, the dktflls map with the imported network):
1. `cs2_ping` shows `0.9.13-editor-import`.
2. `node scripts/import-tmpe.mjs --dry-run`. Expect 27 nodes matched; read `out/tmpe-import-report.md` for skipped connections (lane-count mismatches) and `offsetDelta`/`rankAgrees` per lane.
3. `node scripts/import-tmpe.mjs`. Open Traffic's Lane Connector on 2–3 listed junctions (e.g. CS1 node 11611 near -6461,1078; 9988 near -6198,627): the custom connections should show, and lanes without custom connections keep defaults.
4. Save the map, return to the menu, reload, check the same junctions again (Traffic's loader validates the data and would reset invalid nodes; `Traffic.log` says so).
5. Undo test (optional, before saving): `--undo`, junctions back to defaults.

## Round 12 (0.9.12): findings from run A

- **Walls:** 23 of the 113 edges in `imp-20261019-135208-6` failed. All 23 are `QuaySmall01` running 1–7 m beside a highway; only 6 carry `wall`, and 15 other wall edges built fine. The game drops a net course that lies inside another net's footprint (probe: e1588_0 as given failed, and the same edge moved 8 m away built; undone afterwards).
  - Re-imports:
    - `walls-retry-shifted` (`imp-20261019-140336-9`): each edge moved clear of its highway by half-widths + 1 m, with its own node copies. 11 of 23 built.
    - `walls-retry-shifted-2` (`imp-20261019-140351-10`): the remaining 12 moved another 3 m. 3 built.
  - The last 9 sit between parallel carriageways in interchanges (moving away from one highway reaches the next) and cannot be nets in CS2.
  - The shifted walls are separate pieces, offset 3–11 m from the CS1 line and not joined to their neighbours. Both imports can be undone by id.
- **Bridge:** such failures now say "dropped by the game … runs inside the existing '<prefab>' segment …".
- **Climates:** `/prefabs?category=climate|theme`, and the settings error lists the names (after a game restart).
- **Dry run:** top-level `dryRun` (MCP parameter and request body).

## Round 13: live experiments on dakota-A-2048 (game on 0.9.11), and 0.9.13

All test placements below were undone afterwards. The map is as it was before the tests.

### Dam at the CS1 site (x 1550, z 4690, x1 frame)

- **The CS2 dam has three parts:** the building `HydroelectricPowerPlant01` (48×72 m, placement `Shoreline | SubNetSnap | RoadSide`), the network `Hydroelectric_Power_Plant_01 Dam` (a `RoadPrefab`, 34 m wide, max slope 0, elevation range 0–0, segments 9.6–22.4 m), and the extension `HydroelectricPowerPlant01_End`. `NotreDame` is unrelated.
- **The building alone:** `/import/objects` with `bypassValidation` accepts it off-grid, exactly at (1550, 4690), y 166.7 on the terrain. It brings no dam stub or other sub-net, and the site is dry ground (162–166 m). The water channel runs north–south about 110–170 m to the west (x ≈ 1380–1440, bed 125–150 m).
- **Rotation:** the CS1 dam axis is west–east. The converted quays and path run from (1440, 4673) to (1576, 4694), a heading of about 81°. So **261.7° is the rotation that aligns** the plant's 72 m axis with the dam, front facing west to the river. 98.3° is about 17° off that axis.
- **The dam network alone:** one edge from (1440, 4673) to (1576, 4694) at crest y 172 (`heightMode: absolute`, bypass) built as a level 34 m dam deck with walls, split into 5 segments (screenshot checked). No power plant or end building spawned with it, so the plant/dam pairing is made by the vanilla placement tool, not by the network.
- **Conclusion:** a working dam is a one-off best placed by hand in the editor (Hydroelectric Power Plant tool across the river). The automated pieces stand, but nothing links them, and the CS1 reservoir has no water at the site yet.

### Transformer station and power-line attachment

- **Prefabs:** `TransformerStation01` (48×56 m) and `TransformerStation02` (24×32 m), both `BuildingPrefab` with `HasLowVoltageNode`. For CS1 b39573 "SEC Extra Large Substation" (1594.28, 4549.11, rotation 318.04) I used `TransformerStation01`. It placed fine (y 168.3 on the terrain) and shows error icons (no road connection).
- **Power line ending on coordinates:** a `High-voltage Line` from the existing CS1 line end (node 103851:15, about 45 m away) to the station centre built, but its end was a free node with one segment. **It does not connect:** a line connects only by ending on the building's electricity marker node. `ElectricityBuildingGraphSystem` links a building through orphan sub-net nodes whose prefab carries `ElectricityConnectionData`.
- **What the station had:** its sub-nets included two `Low-voltage Marker` nodes. Its high-voltage marker was not found in the id range I could probe, which needs the new endpoint.
- **New in 0.9.13** (needs a game restart):
  - `GET /entity/subnets?index=&version=` (MCP `cs2_entity_subnets`) lists a building's sub-net nodes and edges, with marker voltage and position.
  - Network import nodes take `connectTo: {index, version, voltage}`, or `{voltage, radius}` for the nearest building marker, and attach through that node entity.
  - To test after the restart: place `TransformerStation01`, read its markers, import one HV edge whose end has `connectTo: {voltage: "high"}`, and check `segmentsConnected` on the marker.
  - No position tolerance applies: the end must be the marker entity itself.
- **Fix:** undo now also records the short pieces of a split course that the coverage samples missed. The dam test left two 8 m segments, which I removed by hand.

### Climate names

They are not readable offline: the climate prefabs are inside compressed asset bundles, and neither `Locale.cok` nor `Game.dll` names them. The current map uses `TemperateArchipelagoHavenClimate`. After a restart, `GET /prefabs?category=climate`, or any unknown climate in `POST /map/settings`, lists all of them.

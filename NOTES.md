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
1. `cs2_ping` shows `0.9.16-editor-import`.
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

## Round 14 (0.9.14): power-line attachment, still to verify

- On 0.9.13, `connectTo {voltage: high}` found no high-voltage marker on `TransformerStation01` or `HydroelectricPowerPlant01`. The station's own sub-nets are two `Low-voltage Marker` nodes.
- `ConnectionWarningSystem` also counts the sub-nets of a building's **sub-objects** and installed upgrades. The high-voltage connector is most likely there, e.g. on the station's gantry object.
- What 0.9.14 adds:
  - `/entity/subnets` and `connectTo` walk sub-objects and upgrades (2 levels deep) and read the voltage also from the net's power layers. Each entry names its `via`.
  - `/city/buildings/near` works in the editor, so building ids can be found.
  - `/entity/inspect` shows the owner, node positions with segment counts, and an edge's end nodes.
  - Edges of rejected nodes now say "skipped because its node X was rejected".
- To do after the restart:
  1. Find both buildings with `/city/buildings/near`.
  2. List their markers with `/entity/subnets`.
  3. Bulldoze the superseded `e5094_0` / `e32855_0` segments, run `next-run/import-power-ends-x1.json`, and check `segmentsConnected` on each marker.

## Round 15: power ends finished live (game on 0.9.13, Anarchy on)

**Deleted:** the old CS1 stubs 613931:1 and 613979:1, and the tangled pieces of the earlier dam end, 1662725–1662727. Their nodes 599497, 619346, 1662722 and 1662708 went with them.

**Station connector.** Node 599497 (a `High-voltage Line` node owned by the station) turned out to be the station's high-voltage connection. The game had merged it into the CS1 end node at that spot, so it disappeared with the stubs. Re-placing the station regenerated its sub-nets:
- the old station 599367:1 was demolished;
- the new `TransformerStation01` is 639618:5 at (1594.28, 4549.11), rotation 318.04, import `transformer-replaced`;
- its sub-nets are `High-voltage Marker` 639619:5 at (1607.05, 4522.94) and `Low-voltage Marker`s 642613:3 and 642614:3.

**Station end:** segment 548750:5 runs 611632 → 639619:5. The marker has 1 connected segment, so the line is attached through `connectTo {index, version, voltage: high}`.

**Dam end:** a free end, because the saved `HydroelectricPowerPlant01` could not be found by id on 0.9.13 (id scans over 400k–900k and 1.655M–1.67M found nothing). A freshly placed plant has a direct `High-voltage Marker` sub-net 23 m from its centre, so the saved one has lost it the same way the station did. The dam end is segments 598981:9 and 599152:9 (the game split it at its maximum span, pylon 548768:9), from 611610 to free end 598980:9 at (1554.19, 4683.52).

**Still to do (0.9.15):**
1. Find the plant with `/city/buildings/near?x=1550&z=4690`.
2. Re-place it, or just read its sub-nets.
3. Bulldoze 599152:9 and rebuild the last piece from 548768:9 with `connectTo {voltage: high}`.

**Lesson:** never delete the last segment into a building's power connector. When a CS1 line end and the connector share a position, the connector node is that line node, and it goes with the segment.

**Remaining one-segment HV nodes:** only the intended dam free end 598980:9.

## Round 16 (0.9.16): checklist, settings persistence, bridge styles, run order

1. **`GET /map/checklist`** (MCP `cs2_map_checklist`), described in MAP_SETTINGS.md. Not yet run in the game.
2. **Latitude/longitude cannot persist per map.** `ClimateSystem.PostDeserialize` resets `PlanetarySystem.latitude/longitude` from the climate prefab on every load. `/map/settings` now labels them "this session only". Choose the climate for the coordinates you want.
3. **Bridge styles:** CS2 has no per-segment bridge style. `CompositionFlags` hold no bridge-style flag; the general flags are Elevated, Tunnel, Lighting, Pavement/Gravel/Tiles, StyleBreak and similar. An elevated segment's look comes from its net prefab's elevated composition (pillars, deck). The only way to vary it is a different prefab per edge, which `/import/network` already takes.
   - **Candidate bridge prefabs from the localisation keys** (types unverified; check with `/prefabs?category=net&query=Bridge`):
     - cable-stayed roads: `Cable Stayed Bridge - 4 lanes`, `Cable-Stayed Bridge - Highway Oneway - 3 Lanes`, `Cable-Stayed Bridge - Small Road - 2 lanes`, `Cable-stayed Bridge - XL Road Divided - 8 Lanes`;
     - other roads: `Suspension Bridge - Medium Road`, `Golden Gate Bridge`, `Grand Bridge`;
     - truss arch and extradosed: `TrussArchBridge01Net` (and `TrussArchBridge01`), `ExtradosedBridge01`;
     - bike and pedestrian: `Arc Pedestrian Bridge`, `Arc Bike Bridge`, `Covered Pedestrian Bridge`, `Covered Bike Bridge`, `Cable Stayed Pedestrian Bridge`, `Cable Stayed Bike Bridge`.
   - **Mapping:** the converter can map CS1 Network Skins truss arches to `TrussArchBridge01Net` where lanes and span allow, and leave deck viaducts as the normal elevated road.
   - **No bridge code was added.**
4. **Back-to-back imports (tools/import-run.mjs):**
   - **One at a time:** each POST answers only after its tool has finished and handed the active tool back, so awaiting each request is enough. No extra delay is needed: every import starts with 3 warm-up frames, and `settleFrames` is per batch. Sending a second import while one runs returns 409.
   - **Separate tools, one guard:** network/water (import tool) and objects/surfaces (object tool) are separate tools, but one busy check covers both.
   - **Resources:** they write synchronously.
   - **Order:**
     1. sea level / map settings;
     2. networks, including power and ship paths, so border connections and power nodes exist;
     3. outside-connection check;
     4. surfaces;
     5. objects and trees (trees last, by far the longest);
     6. water;
     7. resources (any time);
     8. checklist.
   - **Avoid:** never delete the last segment into a building's power connector (round 15).

## Round 17: A-v3 power ends finished live (0.9.17)

**Found:** the station connector was merged into the CS1 line end node 98045:47 (owner `TransformerStation01` 192673:1). So the old station stub 104122:29 (97954 → 98045) already **is** the station end, connected, and was kept. A duplicate `TransformerStation01` (166492:1, no high-voltage connector) sat on the same spot and was deleted. The dam line ran through the station connector to the plant marker.

**Deleted (`force=true`):** 104320:27 (old dam stub), 2219720:1 and 2219721:1. Deleting 2219721 removed the plant's `High-voltage Marker` 192772:3 as well: even a real marker prefab goes with its last segment.

**Plant re-placed:** `HydroelectricPowerPlant01` 192771:3 → **52058:17** at (1549.5, 4690), rotation 261.7 (facing −98°). Its new marker is **52059:17** at (1531.05, 4664.07).

**Dam end:** import `imp-20261019-170401-10`, 98501 → `connectTo` the plant. The game routed it through the station connector by itself: segments **52051:19** (98501 → 98045) and **52052:19** (98045 → 52059). That is why the report said "2 of 3 points". A power line passing near a building's high-voltage connector attaches to it, as with the vanilla tool.

**Result:**
- station connector 98045 has 3 segments (104122, 52051, 52052);
- plant marker 52059 has 1 segment;
- no high-voltage node is left with one segment;
- both circuits now meet at the station. That is electrically valid. Routing the dam line through a waypoint more than about 45 m from the station would keep them separate.

**Checklist** (`GET /map/checklist`):
- **Required:** starting tiles ✔ (9), water ✔, road connection ✔ (22 border nodes), map name ✘ (enter one by hand).
- **Optional:** all ✔ (train 4, air, electricity, oil, ore, forest, fertile).
- **Publishing:** description ✘; preview and thumbnail ✔.
- `playable` is false only because of the map name.

## Round 18 (0.9.18): EditorHierarchyUISystem ArgumentOutOfRangeException on A-v4

**Symptom:** `[CRITICAL] System update error during UIUpdate->EditorHierarchyUISystem: ArgumentOutOfRangeException` in `PrefabSystem.GetPrefab<T>(PrefabData)` ← `EditorHierarchyUISystem.UpdateFilteredHierarchy`.
- **When:** first at 14:58:06 local (`SceneFlow.log`). That is the start of `import-trees-01` (`obj-20261019-215806-9`; the import ids are UTC, the logs are UTC−7). It has repeated every frame since: 18 000+ times, still at 15:09, long after the run ended at about 15:04.

**Mechanism** (decompiled):
- **Cache:** every time objects change, the Workspace panel's `UpdateHierarchy` collects the `PrefabData` of **every top-level object** (`Object + PrefabRef`, no `Owner`) into `m_PrefabsCache`, in a job.
- **Lookup:** `UpdateFilteredHierarchy` then names each entry with the unchecked `PrefabSystem.GetPrefab(PrefabData)`, which is `m_Prefabs[m_Index]`. The game's other paths use `TryGetPrefab`, which tolerates negative indices.
- **Bad index:** a prefab removed at runtime (`PrefabSystem.RemovePrefab`) gets `m_Index = -1000000000`.
- **Stuck state:** the exception aborts `OnUpdate` before the cache is rebuilt, so the panel is stuck and rethrows every frame.

**Cause:** some top-level object references a prefab that is no longer in the `PrefabSystem`. Possible sources of a runtime removal:
- the game's asset-pack loader (a pack not in the active playset, unloaded or replaced);
- `MenuUISystem` on an asset-deleted event;
- Water Features removing its own water-source tool prefabs (placed objects do not reference these).

The logs show no pack activity during the run, so the source is unproven.

**Ruled out:**
- a missing component on our placements, since the editor's own placement adds nothing extra here;
- object count alone, since the stuck state persists after the import;
- water sources, surfaces and outside connections, which are not hierarchy objects or carry no `PrefabRef`.

**Save risk:**
- **Panel:** UI only.
- **Save path:** serialisation tolerates negative prefab indices.
- **The affected objects:** they point to a prefab entity that is deleted, and are likely dropped, or shown as missing, on reload.
- **Recommendation:** clean them before the final save.

**0.9.18:** `GET /diagnostics/hierarchy` groups top-level objects by prefab and lists every group whose prefab index is invalid, with the object count and sample entities and positions. `POST /diagnostics/hierarchy?removeInvalid=true` deletes those objects (raw `Deleted`, since the bulldoze pipeline needs the missing prefab data) and resets the panel's caches by reflection. `POST` without `removeInvalid` only resets the caches.

**Workaround on 0.9.17:** none from the UI. Closing or collapsing the Workspace panel does not stop `OnUpdate`. The map is otherwise fine, but every frame logs a stack trace (FPS cost) and the Workspace tree no longer updates.

**Plan:**
1. Save A-v4 under a backup name.
2. Restart on 0.9.18 with every asset pack used by the import enabled in the editor playset.
3. Load the map and run `GET /diagnostics/hierarchy`.
4. If there are invalid groups, `POST ?removeInvalid=true`, then save.

Do not change the playset while the editor is open.

## Round 19 (0.9.19): hierarchy error, revised cause

**Evidence:**
- On A-v4 the first error was at 14:58:06 (`SceneFlow.log`, timestamped), 6.5 minutes after the editor opened the new map and 29 s into the import run, at the first tree file. `Player.log` has no timestamps, so its line 331 cannot place the error in time.
- After restarting on 0.9.18, with the same playset and an **empty** map loaded, there were **0** hierarchy errors and `GET /diagnostics/hierarchy` found 0 objects and 49 857 prefabs, all valid. So the error is not present from the start.

**Duplicate prefab IDs are harmless.** The warnings cover the Cliffs pack's CliffSide2 meshes and the climate/render-settings copies carried by old saves. `PrefabSystem.AddPrefab` only logs them; the duplicate still gets its own index (`m_Index = m_Prefabs.Count`) and is appended, so no index shifts. Dropping the pack is not needed for this.

**Revised mechanism** (`EditorHierarchyUISystem`): a race that gets stuck, not a broken object.
1. When objects change, `UpdateHierarchy` schedules `ObjectHierarchyJob`. It writes every top-level object's `PrefabData` into `m_PrefabsCache` and runs over all objects; it also takes the Prefabs node's entries.
2. On the next frame, `UpdateFilteredHierarchy` enumerates that `NativeParallelHashSet` on the main thread, before anything waits for the job. Each entry is named with the unchecked `PrefabSystem.GetPrefab` (`m_Prefabs[m_Index]`).
3. During a big import the job can still be writing. One read of a half-written or stale entry throws `ArgumentOutOfRangeException`.
4. The throw happens before `UpdateHierarchy` in the same `OnUpdate`, so the cache is never rebuilt. The panel rethrows on the same stale entry every frame, even after the import ends, and probably until the game restarts.

**Effect on the map:**
- **Harmless to the saved map.** Only the Workspace panel is affected; the save path does not use it and handles negative prefab indices itself. The panel stops updating, and the stack trace logged every frame costs FPS.
- **Hidden-node workaround:** none. Collapsing or closing the panel does not stop its `OnUpdate`.

**Fixes:**
- **Restart:** restarting the game clears it, since the cache lives only in memory.
- **Without a restart:** `POST /diagnostics/hierarchy` (no `removeInvalid`) clears the panel's caches and marks it dirty, so it rebuilds. Available since 0.9.18.
- **0.9.19 diagnostic:** `GET /diagnostics/hierarchy` also reports `panelCache {entries, invalidEntries, stuck}` straight from the cache. So a stuck panel (invalid cache entries, all objects valid) is told apart from real broken objects (`invalidGroups`, removable with `removeInvalid=true`).
- **Runner:** `tools/import-run.mjs` should end with `POST /diagnostics/hierarchy`. It is harmless when nothing is stuck. Call it only once the imports are done, so the panel's job is idle.

## Round 20: " dakota-A4-2048" hangs on load (diagnosis from logs and process sampling, no code change)

**Observed 2026-10-20:**
- `SceneFlow.log` shows `Starting game from ' dakota-A4-2048 (… %20dakota-A4-2048.cok@%20dakota-A4-2048.MapMetadata)'` at 04:35:47, then the version and format tags at 04:35:49, then nothing.
- Sampling the `Cities2` process every 20 s from 04:36:33 to 04:38:15: CPU time grew by exactly 20 s per 20 s, so **one core was pinned**. Memory stayed flat at about 6.99 GB and the window was "not responding".
- That pattern is a single-threaded loop on the main thread during deserialisation or post-load, not slow I/O or a slow load: a slow load would grow memory and log progress. The game was closed at about 04:38.
- The earlier attempt (04:23) looked the same.

**Ruled out or unlikely:**
- **The leading space:** the asset database resolved the URL-encoded path, found the MapMetadata and read the file header and format tags. A renamed copy (`dakota-A4b-2048.cok`, already made) is unlikely to behave differently. It is cheap to try once, but not a priority.
- **Climate set through `/map/settings`:** `ClimatePanelSystem.currentClimate`'s setter does exactly what the bridge does (`ClimateSystem.currentClimate = prefabEntity`), plus a UI refresh. `ClimateSystem.currentClimate`'s own setter runs `EnsureSeasonsOrder` and the season/weather update either way. `EnsureSeasonsOrder` is a plain LINQ sort with no loop that can spin.
- **The "References were null when checking if prefab ContinentalGreatHighlandsClimate was previously saved" warnings at save:** these come from `PrefabSystem.WasPreviouslySaved`. They only mean the map had no earlier embedded copies (first save with that climate); the climate, water render settings and terrain render settings are still embedded (`SaveClimate` and friends). They are benign.
- **The bridge:** at load it only resets a frame counter (`BridgeSystem.OnGameLoadingComplete`). Its tool systems are idle and the undo records are in memory only, not in the file.

**Most likely:** a mod's load or deserialisation hook looping over content that only A4 has.
- **What is new in A4:** the first full save with about 500 k trees, 727 surfaces, 62 water sources, the TM:PE/Traffic data if the run imported it, and the climate change. A4 is the first map with all of these together.
- **Mods that run code at load:** Traffic (TM:PE lane connections), Water Features, Lumina, Anarchy, Image Overlay, I18NEverywhere and the PDX packs.

**To isolate (owner):**
1. Load `dakota-A3-2048` (50 MB, saved 10:04 on 2026-10-19). If it loads, the hang is specific to A4's content or settings.
2. Load A4 with a **minimal playset**: CS2MCP plus the asset packs only, with Traffic, Water Features and Lumina disabled. If it loads, re-enable mods one at a time; Traffic and Water Features first, since both act on load and A4 is the first map with TM:PE data and 62 bridge-made sources.
3. If it still hangs with the minimal playset, the cause is in vanilla content: object count, or the embedded climate/render-settings prefabs. Then re-run the import into a fresh map, save after networks, objects and trees separately, and test-load each save to bisect.

The bridge cannot repair a saved `.cok`, and nothing about how it sets the climate needs to change.

### Round 20b: ranking the A4 differences for the load spin (code review, offline)

**Ruled out:**
- **Leading space in the name:** the asset database resolved `%20dakota-A4-2048.cok`, and the loader read the MapMetadata, the serialized version and the format tags. The spin starts after that.
- **Climate set through `/map/settings`:** the Climate panel's setter does the identical assignment (`ClimateSystem.currentClimate = entity`). At save, `PrefabSystem.SaveClimate` → `SavePrefab` returns early for a read-only (vanilla) climate, so nothing is embedded and the map stores the prefab id like any vanilla map. The "References were null…" warning is benign.

**Not ruled out:**
- **Medium: 96 `Arc Pedestrian Bridge` edges (decor arches).** These are a structural first for us. The Arc bridges are very likely fixed-span bridge prefabs (`FixedNetElement`), which the net tool builds with fixed element indices; our importer writes plain courses with `m_FixedIndex = -1`. Load-time geometry and composition for such edges is untested.
- **Medium-low: power ends on building connectors.** A line ending on a building's marker node is the normal state that the vanilla tool also saves. A4 may be non-vanilla in one way: a CS1 line node adopted as the station's connector (a `High-voltage Line` node owned by the building) carried 2–3 segments, with the dam line passing through it. No load-time loop over that was found in `ElectricityBuildingGraphSystem`.
- **Medium-low: 239 po-rocks with absolute y up to about 150 m under the terrain.** No load-time object system loops on height; placement validation does not run on load. Their `Elevation` is 0, so they are simply objects below the surface.
- **Low: lakes, fort, po-structures, po-walls, rock scatter.** These are of the same kinds as content in map A, which loads.

**Reading the `.cok`:** not feasible. It is zstd-compressed ECS streams with no public schema, and the spin is after the header, probably in post-load systems or a mod's load hook, so a reader position would not show it.

**Fastest safe path to a loadable map:**
1. **One cheap test first:** load A4 with a minimal playset (CS2MCP plus the asset packs; no Traffic, Water Features or Lumina). If it loads, it is a mod: bisect the mods and keep the map.
2. **Otherwise a staged rebuild with test-loads,** riskiest items last and alone. Save under a new name after each stage, reload it (about 2 min each), then continue:
   - **S1:** A-equivalent content plus lakes, fort, rock scatter, po-structures, po-walls.
   - **S2:** + po-rocks. If S2 hangs, re-import them with no `y`, i.e. on the terrain.
   - **S3:** + Arc Pedestrian Bridge edges. If S3 hangs, drop them or map them to a non-fixed path prefab.
   - **S4:** + power ends.
   - **S5:** map settings (climate, theme).

   Every stage before its save can be undone, so a hanging stage costs one reload and the previous save survives.

### Round 20c: resolved, the leading space was the cause (my ranking was wrong)

The owner made a copy of the save with the leading space removed from the file name and from the 15 internal occurrences of the map name: a same-length byte replace, with no `.cid`. It loads normally as `dakota-A4b-2048`. So the name caused the hang, not the content.

I had ruled the name out because the loader resolved `%20dakota-A4-2048.cok`, found the metadata and read the format tags. Evidently something later in the load path breaks on the encoded or leading-space name, while the early steps tolerate it. The sunk rocks, power attachments and Arc bridge edges need no action.

**Rules:**
- Never start a map or save name with a space, and avoid spaces in map names altogether.
- When a load spins silently after the format tags, try a renamed copy before any content bisect.

**Hierarchy-panel error:** this is not the Cliffs and Rocks pack, as round 19 says.
- **Duplicate prefab IDs:** they only log a warning in `PrefabSystem.AddPrefab`; the duplicate still gets its own index and shifts nothing.
- **Empty map:** with the pack loaded and an empty map, no errors occur.
- **Cause:** the error starts during large imports (a race in the Workspace panel's cache, stuck afterwards).
- **Fix:** a restart or `POST /diagnostics/hierarchy`.

## Round 21 (0.9.20): dam deck, Arc bridges, prefab bounds, `/debug/prefab-refs`

**What the dam deck became (live on dakota-A4b):** the deck course is `Hydroelectric_Power_Plant_01 Dam`, the dam *net*. It has 5 segments (1631625–1631629, nodes 1631044–1631620) from (1440, 4673) to (1577, 4694), with per-end elevations of 8–11 m and 25 m at the east end. The splits at x ≈ 1489/1496 and 1520/1527 are where the two 8.6 m pieces join. A `Pavement Path` runs along the same line (631449–631451 and 1629322/1629325, x 1443 → 1585). `/graph/export?prefab=Arc` counts **87** `Arc Pedestrian Bridge` segments in the save.

**Decision (orchestrator):** both layers, the dam deck (`Hydroelectric_Power_Plant_01 Dam`) and the decor `Arc Pedestrian Bridge` edges, are **dropped from the runner**. The dam becomes a hand-placed item in the editor; the runner no longer imports either layer.

**Prefab bounds:** `/prefabs/info` returns `geometry.boundsMin/boundsMax` since 0.9.19. The live table for every rock, cliff and ground-cover prefab in `cs2-playset/ASSET_PACKS.md`, plus vanilla `Rock01–30` and `Boulder01–08`, is in PREFAB_NAMES.md. **min.y is negative everywhere.**
- **Pack meshes:** the pivot is above the mesh bottom, so `y = terrain − min.y` lifts the buried skirt into view. The tall cliffs reach 105–131 m below the pivot.
- **Vanilla rocks:** the pivot is at the mesh centre.
- **Converter:** apply the lift per family, not globally.

**Hierarchy error, A4b (loaded without imports):** the errors began during the map load, not during an import.
- **Objects:** `GET /diagnostics/hierarchy` found every top-level object valid.
- **Panel cache:** it held two entries with index −1. `POST` cleared it, but one −1 came back on the rebuild, so something current feeds it.
- **Where a −1 comes from:** in `EditorHierarchyUISystem` the cache is filled only by `ObjectHierarchyJob` (the PrefabData of top-level objects) and `CollectPrefabs` (the Workspace "Prefabs" list `m_Prefabs`, unchecked).
- **Why −1 is odd:** in `PrefabSystem`, −1 is the first obsolete prefab (`AddObsoleteID`, index −1−j: a save referencing a prefab that is not loaded). But this Player.log has **no "Unknown prefab ID" line**.
- **Duplicate IDs logged at startup:**
  - user-save-embedded `TemperateArchipelagoHavenClimate (copy)`, `BarrierIslandWaterRenderSettings`, `DefaultTerrainRenderSettings`, `Canadian Rockies` and `Banff National Park`, from 8 saves in `Saves/76561198039632782`;
  - `CliffSide2 Mesh` / `CliffSide2_LOD1 Mesh` (one hash, Cliffs and Rocks pack).

  Duplicates only warn, so none of them makes a −1 by itself.

`GET /debug/prefab-refs` (0.9.20, needs the restart) names the source. It reports:
- every prefab entity whose index is outside the list, with its name (or the obsolete ID's name), the holders by PrefabRef (top-level ones counted apart) and samples;
- PrefabRefs to missing entities;
- every duplicate PrefabID group, with type, asset path and which member the ID resolves to;
- the save's unresolved IDs (`m_LoadedObsoleteIDs`) and the obsolete entities;
- the Workspace "Prefabs" list with each entry's index;
- the panel cache.

`POST /debug/prefab-refs?fixWorkspace=true` drops Workspace entries with an invalid index and resets the panel.

**Root cause (offline, decompiled Game.dll and Unity.Collections.dll), superseding rounds 19 and 20c: a vanilla capacity overflow.**
- `EditorHierarchyUISystem.m_PrefabsCache` is a `NativeParallelHashSet<PrefabData>` created once with capacity **128** (`Allocator.Persistent`, never grown).
- `UpdateHierarchy` clears it, then `CollectPrefabs` (Workspace list, main thread) and `ObjectHierarchyJob` add to it. The job adds the PrefabData of every top-level object's prefab through a **ParallelWriter, which cannot grow**.
- With more than about 128 distinct prefabs, `UnsafeParallelHashMapBase.AllocEntry` finds no free slot. `ThrowFull` is `[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]` and is stripped in the release player. So AllocEntry returns −1 and executes `next[-1] = -1`. In the map's single allocation (values `bool`, then keys, then next), `next[-1]` is the **last key slot**, which becomes `PrefabData{m_Index = -1}`.
- `UpdateFilteredHierarchy` then iterates the cache with the unchecked `GetPrefab<PrefabBase>(PrefabData)` (`m_Prefabs[-1]`) and throws `ArgumentOutOfRangeException`. The filter task never starts, so the error repeats every frame.
- **This matches everything seen:**
  - A4b has **136** distinct top-level object prefabs, and the cache reported exactly 128 entries with a −1.
  - The error starts at map load, or mid-import on a map that crosses about 128 prefabs. An empty map never shows it.
  - A cache reset is undone by the next rebuild.
  - No object has a bad prefab.
- **Duplicate IDs are not involved.** `AddPrefab` gives a duplicate its own index and only skips the `m_PrefabIndices` entry. The Cliffs pack's `CliffSide2 Mesh` / `CliffSide2_LOD1 Mesh` are RenderPrefabs, never top-level objects, so they never enter this cache. **Dropping the pack does not fix the error;** it only removes 10 distinct prefabs from the count. (A swap-removed prefab, `RemovePrefab`, can also leave a stale index past the end in a by-value cache, but nothing removes prefabs here.)
- **Fix (0.9.20):** `BridgeSystem` checks about once a second. When the editor's cache capacity is below 8192, it completes the tracked jobs, clears the set, sets `Capacity = 8192` (which reallocates through the shared buffer pointer) and resets the panel. The log line is "Workspace hierarchy prefab cache grown from 128 to 8192". The set is persistent, so this happens once per game session, before or after a map load. `POST /diagnostics/hierarchy` and `POST /debug/prefab-refs?fixWorkspace=true` force it.
- **Unverified in game:** confirm that the error stops on the next A4b load, and that `/diagnostics/hierarchy` reports `capacity: 8192, invalidEntries: 0`.
- `/debug/prefab-refs` now also scans prefab entities with `PrefabData` disabled (obsolete prefabs).


**What `Hydroelectric_Power_Plant_01 Dam` is:** a `RoadPrefab`, not a `PathwayPrefab` (round 13 `/prefabs/info`: 34 m wide, max slope 0, elevation range 0–0). On its own it builds as a level 34 m deck with walls. Why the A4b deck looked like a footbridge:
- the converter imported it with per-end elevations of 8–25 m above the valley floor, so the game drew elevated segments;
- a `Pavement Path` (a pathway) runs along the same axis at 167–177 m.

The visible walkway is that path, not the dam net. Its lane set (whether it carries pedestrian lanes) sits in the asset bundle and cannot be read offline; `/prefabs/info?name=Hydroelectric_Power_Plant_01%20Dam` shows it next run. Moot now: the layer is dropped and the dam is placed by hand.

**`/import/objects` `options.grounding` (0.9.20):** for kinds object and plant without `y`, the y comes from the prefab bounds: `boundsMin` = terrain − min.y − sink × height − slope × half footprint, or `pivot` mode, with an optional `maxLift`. See IMPORT_SCHEMA.md. Not exercised in game yet.
- **Watch:** pack rocks and cliffs have buried skirts (PREFAB_NAMES.md), so plain `boundsMin` can float them.
- **Safe first run:** `mode: "pivot"` for pack rocks and cliffs, `boundsMin` for vanilla `Rock`/`Boulder`; or `boundsMin` with `maxLift` of about 1.5 m.

## Round 22 (0.9.22, offline): bridge prefabs, fixed spans, pillars

**Fixed spans need no `fixedIndex`.**
- **Who numbers the spans:** the vanilla net tool never sets `NetCourse.m_FixedIndex` for a new course; it copies it only from existing edges, as our replace and upgrade paths already do. `CourseSplitSystem.TryAddCoursePhase2` sees the prefab's `FixedNetElement` buffer and does the work: it cuts the single course into the span sequence, numbers the spans and scales them to the course length.
- **What this means for the importer:** a fixed-span bridge must be ONE import edge, abutment to abutment. Splitting it into 4 edges would have built 4 complete scaled-down bridges.
- **Contract:** see IMPORT_SCHEMA.md "Bridge prefabs". BXP SuspensionBridge03 takes 585.6–1366.4 m; 976 m gives exact spans. Its towers sit at 168/976 and 808/976 of L.
- **Edge field:** `fixedIndex` is accepted and ignored, with a warning.

**Non-fixed bridge prefabs:** the game cuts an edge longer than 1.4 × segmentLength into equal pieces. So one edge per straight run is enough, for example BR005 at 305 m becomes 2 × 152.5 m.

**Pre-validation (`options.bridgeRules`, default `error`):**
- **Rules:** `bridge.length.short`, `bridge.straight`, `bridge.lowElevation`, `bridge.fixed.repeat` and `bridge.fixed.chained` are errors. `bridge.fixed.short` and `bridge.waterClearance` are warnings.
- **Output:** the dry run returns a per-edge `bridges` block with the predicted split, span starts and tower positions.
- **`m_ElevationOnWater`** is only a placement aid in the net tool, so a deck below it is a warning.

**`/prefabs/info` `bridge` block:**
- the `Bridge` authoring values (segmentLength, hanging, elevationOnWater, canCurve, allowMinimalLength, waterFlow, buildStyle, fixedSegments);
- the derived `FixedNetElement` spans;
- the edge length range, elevationLimit and geometry flags.

The prefab class was already reported as `type`.

**Offline rules:** `bridges/cs2-bridge-rules.json` holds the 74 bridge prefabs of BXP (92245) and BXP B&P (124160), parsed from the binary `.Prefab` files in the mods' `.cok` zips. The fields are serialised with their UTF-16 names; in a fixed span, `m_CountRange` comes before `m_Length`.
- **Vanilla and DLC prefabs:** not on disk (resources.assets); read them with `/prefabs/info` next run.
- **Count ranges:** a component of 0 means unbounded (componentwise `math.select`). For example, `BXP Double Deck Suspension Bridge - Highway` has 320 m × 1…∞.

**`noPillars`:** pillars are `Game.Objects.Pillar` sub-objects in the edge's or node's `SubObject` buffer. SubObjectSystem rebuilds them only for owners that carry `Updated` or `Deleted`, so deleting them sticks until the owner is touched again.
- **The edge flag:** an edge with `noPillars: true` gets its pillars deleted with a raw `Deleted` when the import finishes. This covers the edge's segments and the inner nodes joined only by them; end nodes are left alone.
- **Re-running it:** `POST /net/pillars/remove {segments:["i:v",...]}` does the same for existing segments.
- **Unverified:**
  - that a raw `Deleted` on a pillar does not mark its owner `Updated`, which would respawn the pillar at once;
  - that the deletion survives a save and load.

  If it fails, Pillar Spacing (162558) is the global alternative.

## Round 23 (0.9.23, offline): automatic dam, map look settings

**Dam:** the game has no dam tool. `Hydroelectric_Power_Plant_01 Dam` is a bridge-type `RoadPrefab`: `Bridge.m_WaterFlow` sets `FlowLeft`/`FlowRight`. Its `CourseStart`/`CourseEnd` sub-objects become course objects.
- **What the net tool does:** it spawns the `MakeOwner` one (the plant) at a course end and makes the dam edges the plant's sub-nets. `WaterPowered` then sums those edges.
- **What the importer now does:** `/import/network` does the same, along with the tool's flow orientation. The contract is in IMPORT_SCHEMA.md "Dams": one level edge, abutments on the ground, `flow`.
- **Pre-checks:** `dam.single`, `dam.level`, `dam.elevation` and `dam.flow`.
- **Unverified in game:** which end the plant takes, the FlowLeft/Right flag, and whether HydroelectricPowerPlant01_End is the other course object. The dry run shows all three.
- **Undo** does not remove the plant yet.

**Look:** climate, water render settings and terrain render settings can be listed, selected and edited field by field (MAP_SETTINGS.md "Look"). An edit clones a read-only prefab, as the editor's Edit buttons do. A map-specific latitude/longitude is therefore possible through a climate copy, which is saved into the map.

## Round 24 (0.9.24, offline): the 309 lost segments on A-v4

**Anarchy (decompiled Anarchy.dll, mod 74604) does not protect net segments.**
- **What it adds:** `Anarchy.Components.PreventOverride`, an empty tag. `AnarchyPlopSystem` adds it to entities that are `Applied`, have `Static`, `Object` or `EditorContainer`, and have no `Owner`, i.e. top-level objects. Only while the toggle is on and Object/Line/Net Tool is active.
- **What it does:** `PreventOverrideSystem` strips the game's `Overridden` flag from tagged entities and marks them `Updated`. `Overridden` is what hides an object that a net or area overlaps.
- **On load:** `AnarchyPlopSystem.OnGameLoadingComplete` removes `PreventOverride` from everything except certain static non-building objects.
- **Nets:** no net edge is ever tagged. In the game, `Overridden` on net entities is set only on fence utility lanes (`Game.Net.OverrideSystem`), never on edges.
- **Validation:** the game validates only `Temp` entities while a tool runs. No game system deletes a permanent segment for carrying an error.

So "segments built without Anarchy's protection get removed later" has no mechanism in either code base.

**What the lost segments have in common** (`out/x1/live-lost-edges.json` against `import-all` + bridges + gaps, analysis script in the session scratchpad):
- **Chains, not scattered edges.** 284 matched edges form 57 connected chains. An edge sharing a node with a lost edge was lost in 59 % of cases, against 0.5 % for the rest.
- **The chains sit on the imported surfaces.** 44 of the 57 chains, holding **260 of the 284 edges**, touch (≤ 10 m) a polygon from the surfaces step: Agriculture Surface 01 fields 189, Sand Surface 01 78, PO Pavement Surface 01 22.
- **Fields are the clearest case:** of the import edges whose midpoint lies **inside a field polygon, 47 were lost and 4 kept (92 %)**.
- **Prefabs and shape:** Gravel Road 207 (farm tracks inside fields), highway 2-lane 44 (near sand), Pavement Path 10, Small/Medium Road 15. 92 % are ground-level. Slope, length and node degree do not stand out.
- **The run-A comparison does not apply.** Run A's graph (`out/live-graph.json`) was pulled on 2026-10-19 at 03:03 UTC, before any surface file existed (first commit 2026-10-19 06:24 PDT). "Live in run A" therefore only shows the segments survived the network step; it says nothing about bypass against Anarchy.
- **Prime suspect:** the surfaces step, or something done after it, removes road chains lying under the surface polygons. The decompile gives no mechanism: our area definitions are plain (no `Delete`/`ClearArea`), and `ClearArea` only works inside building sub-areas. So it must be caught live.

**0.9.24:**
- **`netGuard` in every `/import/objects` and `/import/surfaces` report.** Every top-level and owned segment is snapshotted before each batch and compared after it. The report gives `segmentsRemoved`, `geometryGone` (no segment left within 6 m: not a split or merge) and, per batch that removed any, the item ids and up to 50 lost segments with position and prefab. A deletion that lands a frame late shows up under the next batch.
- **`GET /net/count`:** top-level segments per prefab, plus owned segments.
- **Anarchy protection for objects:** with `bypassValidation`, every created top-level static object (not buildings, not surfaces) gets `PreventOverride` through reflection when Anarchy is loaded, as with the toggle on. The report shows it under `validation.anarchyPreventOverride {available, added}`. Nets and areas get nothing, because no such protection exists.

**Verification for the next run** (it must pass before an A-v5 is accepted):
1. **Right after the network steps:** `GET /net/count`, then save the numbers.
2. **After each later step:** check that step's report `netGuard.segmentsRemoved`; it must be 0. The runner should stop at the first non-zero value. If the surfaces step is the culprit, `netGuard.batches[].items` names the surface ids.
3. **After 2 minutes of editor time** with no input: `GET /net/count` must equal step 1, per prefab.
4. **After save, then load (a name without a leading space):** `GET /net/count` must equal step 1 again.
5. **On a mismatch at 3 or 4:** pull `/graph/export` and diff it against the import set with `tools/live-analysis.mjs`.

## Round 25 (0.9.25, live on A-v5): dam check, level-course fix, suspension bridge

**The A-v5 dam did build:** `imp-20261020-140910-6`, reported "merged 1".
- **Owner:** plant `HydroelectricPowerPlant01` 57553:11 at (1484.1, 173.6, 4675.8), facing −99°, owns 11 `Hydroelectric_Power_Plant_01 Dam` segments from (1440, 4673) to (1741, 4720).
- **Why it looked missing:** owned segments are absent from `/graph/export` (use `?owned=true`), and `/net/count` lists them only under `ownedSegments`.
- **Plant sub-nets:** a High-voltage Marker 57556:11 at (1465.9, 4649.7), plus the dam nodes.
- **Spans:** the course was cut exactly as the prefab defines and as the dry run predicted. The prefab's `/prefabs/info` `bridge` block shows them: abutment 64 m (straight), spillway 9.6–22.4 m × 1…∞, centre 32 m, spillway again, abutment 64 m (straight). At 300 m that is 64 + 4×18 + 32 + 4×18 + 64.
- **The plant** is the course-start object at local (4, 0, 44). `HydroelectricPowerPlant01_End` is the course-end object at (4, 0, −44).
- **Water flow:** `FlowLeft`, buildStyle Elevated, segmentLength 16, elevationOnWater 10, flags include `SubOwner`, `NoCurveSplit`, `FlattenTerrain`, `ClipTerrain` and `ExclusiveGround`.

**Defect: the spans did not share nodes.**
- **Symptom:** each of the 4 span boundaries had two nodes at the same x/z, 1.8 m apart in height.
- **Elevation is not the cause.** A test dam on flat ground at (−3150…−2850, −6200), with elevation 0 at both ends, showed the same gaps. It was undone and its plant demolished; the counts are back to 3254 top-level and 15 owned.
- **Cause:** the importer bends every course to the terrain (`NetUtils.AdjustPosition`) plus its elevations. `CourseSplitSystem` lays the straight fixed spans along that curve, but samples the curvable spans from the course ends, so the boundary heights differ and `GenerateNodesSystem` makes two nodes.
- **What the net tool does:** for a max-slope-0 prefab it clamps the elevation to the prefab range (0..0) and sets every control point to the start height, giving a level straight course.
- **0.9.25** does the same for every prefab with `m_MaxSlopeSteepness == 0`: end y = start y (unless the end is attached), a straight line, elevation 0 at both ends, and control points ignored with a warning. This needs a game restart. **Unverified:** the next build must show the 10 inner span nodes as degree-2.

**Two problems with the site itself:**
- **No water flows across the dam line** (flow weight 0). The abutments stand 7.2 m and 3.5 m above the terrain, on dry ground east of the river channel (x ≈ 1380–1440). A dam that spans no flowing water produces no power.
- **The `auto` orientation is wrong for this river.** With no flow, `auto` kept a → b (west → east), so the plant sits at the west end. For a north → south river (+z = north) and a `FlowLeft` prefab, the course must start at the east abutment, i.e. `flow: "invert"` or a/b swapped. Otherwise `WaterPowered` counts the flow as negative.

**BXP SuspensionBridge03 on A-v5:** 4 segments of 168.1 + 320.1 + 320.1 + 167.9 m, connected through degree-2 nodes.
- **Tower nodes:** (1339, 153.6, −3752.9) and (1726, 153.6, −3243.0), 168 m in from each end, where the prefab's towers stand.
- **Middle node:** (1532.5, 157.8, −3498.0), mid-span.
- **End nodes:** the 3-way splits (1237.4, −3886.8) and (1827.5, −3109.4).
- **Towers:** they are prefab sub-objects and cannot be listed in the editor (`/city/objects` is city-only), so they still need a look in game.

**Rebuilding the A-v5 dam after the restart:**
1. `POST /build/demolish?index=57553&version=11&force=true` removes the plant and its owned dam segments. Undo cannot be used, because later network imports exist.
2. Move the abutments so the line crosses the river, at the crest height on both valley sides (`dam.elevation` ≈ 0).
3. Import one edge with `flow: "invert"` (or `auto` once water flows).
4. Check `/entity/subnets?index=<plant>` for degree-2 inner dam nodes.

**Side finding:** two `TransformerStation01` (163022:1 and 135364:17) stand at the same spot (1594.28, 4549.11), probably the substation imported twice. Delete one.

## Round 26 (0.9.26, offline): TM:PE import against Traffic's validation

**What happened on A5** (`tmpe-20261020-082957`, Traffic 0.2.13): "couldn't load data from 7 of 9 intersections".
- **The validation ran during our import.** `TrafficDataMigrationSystem` has a `_loaded` flag that every map load sets. Its `OnUpdate` runs once, and clears the flag, only when a node with `ModifiedLaneConnections` exists. A5 was loaded at 07:41 without Traffic data, so the flag stayed armed. Our import wrote the first such nodes at 08:29:57, and the system validated them in the same frame (Logs/Traffic.Mod.log).
- **The same validation runs after every save and load,** so the data has to pass it in any case.

**What `ValidateLoadedDataJob` rejects.** It removes **all** of a node's data when any `ModifiedLaneConnections` item fails:
- the `modifiedConnections` entity is null or missing;
- `edgeEntity` is not an `Edge`;
- any connected edge lacks `Composition`, or the node's composition is a **roundabout**;
- `edgeEntity` is not a road, track or bicycle edge of the node;
- `laneIndex` is not a key of that edge composition's `NetCompositionLane` buffer, among the lanes flagged Slave, Road, Track or BicyclesOnly;
- no `GeneratedConnection` buffer;
- a connection whose `sourceEntity` ≠ `edgeEntity`, whose target is not such an edge, or whose `laneIndexMap.y` is not a valid lane of the target;
- `method` Road onto a target without `RoadComposition`, Bicycle without a bike lane, or Track without track types (unless unsafe).

`ValidateLoadedReferencesJob` then deletes any `DataOwner` entity whose owner node lacks the buffer or does not reference it.

**Comparison with our writer.** It writes the same structure (`DataOwner`, `PrefabRef` FakePrefab, `GeneratedConnection`, node `ModifiedConnections`), and lane indices come from `Lane.m_StartNode.GetLaneIndex() & 0xFF`, exactly as Traffic's own `GenerateConnectorsSystem` computes them. So the rejected items are per-node conditions above, such as a roundabout node, a connected edge with no road/track composition, or a lane index outside the flagged composition lanes. They cannot be read back: Traffic deleted the data, and its log gives only counts.

**0.9.26:**
- **Pre-validation:** the importer runs a port of Traffic's checks before writing (`ValidateLikeTraffic`, dry run included). A source lane that would fail is skipped with `reason: "Traffic validation: <rule>"`, so Traffic never sees invalid data and never drops a whole node.
- **Node matching in x/z only:** previously it was 3D, and CS1 and CS2 heights differ (n27789 "nearest 167 m"). Every node within `options.nodeSearchRadius` (default 60 m) is scored by how well its edges pass the probe points of the CS1 segments the node's data uses. The node matching the most segments wins, ties broken by mean probe distance + 0.1 × x/z distance. A node farther than `nodeRadius` (12 m) is accepted only if its edges carry every segment. `nodes[].match` reports "k/n CS1 segments matched".
- **Next run:** after the restart, a **dry run** of the same body names the rule each of the former 7 nodes breaks and shows whether the 8 skipped nodes now match. Then import for real and check that Traffic shows no "couldn't load" dialog after save and reload.

### Round 26b (0.9.26): why imported surfaces deleted roads, found and fixed

**Reproduced live on A5**, in the empty test area at (−3000, −6200):
- **Road first, then surface:** a Gravel Road (206 m), then a 60 m `Sand Surface 01` square over it.
  - With `bypassValidation` false, the surface is refused as `OverlapExisting`.
  - With `bypassValidation` true, the surface is applied and the road entity is destroyed (netGuard `segmentsRemoved` 1, `geometryGone` 1, entity gone).
- **Surface first, then road:** both survive.
- **`PDO23 Sand Surface 01`** behaves like the vanilla prefab (`OverlapExisting`).

All test entities were removed afterwards.

**Cause (decompiled):**
- **Our areas were never `Complete`.** `GenerateAreasSystem` sets `AreaFlags.Complete` on a new area only when its node buffer has at least 4 nodes and the **last node equals the first**, which is how `AreaToolSystem` (and its GenerateAreas helper) write them. `BridgeObjectToolSystem.DefineArea` wrote the polygon open (the handler even strips a closing point), so every imported surface was an *incomplete* area.
- **Overlaps became Warnings.** `ValidationSystem` validates an incomplete area with `noErrors = true`, so `Game.Areas.ValidationHelpers.ValidateTriangle` reports its overlaps with **`ErrorSeverity.Warning`**. `AddError` puts the `Warning` component on the overlapped permanent entity (the road, or an object).
- **Apply deletes Warning entities.** **`ToolApplySystem` adds `Deleted` to every entity with `Warning`.** This step is meant for incomplete previews, which the vanilla tool never applies. With `ignoreErrors`, the overlap Warning (plus the Error on the area itself) no longer blocks the apply, so every surface batch deleted the nets **and objects** it overlapped.
- **This explains A-v4:** the lost chains were the gravel tracks under the fields, plus highways and paths under the sand and pavement.

**Fix (0.9.26):**
- **Surfaces:** `DefineArea` closes the ring, so the area is `Complete`. Overlaps are then `Error` severity: bypass places the surface over the net, and the road keeps only a transient `Error` tag.
- **Districts:** the district tool's area definition is closed the same way.
- **Unverified in game.** After the restart, the same test (road, then surface with bypass) must show netGuard `segmentsRemoved: 0`.

**Consequences for existing maps:**
- **Objects too:** trees, props and rocks under every surface imported before 0.9.26 were also deleted. netGuard tracks only nets.
- **Incomplete areas remain:** surfaces already imported are stored as incomplete areas. Re-import them with 0.9.26 (undo and import, or rebuild the map) so they are `Complete` like hand-painted ones.
- **Order:** importing surfaces before networks also avoided the loss, because nets placed over a surface keep both. 0.9.26 makes the order irrelevant.

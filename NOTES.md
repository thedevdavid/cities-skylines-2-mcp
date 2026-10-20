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

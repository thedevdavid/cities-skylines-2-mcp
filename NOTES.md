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

## Round 27 (0.9.27, offline against A-v6): TM:PE node and edge matching

**A-v6 dry run (0.9.26):** 20 of 27 nodes matched, but only 9 of 68 connections mapped. Two causes, both visible in `live-graph-20261020-193334-end.json`:
- **Node position versus curves.** The game's node reduction leaves `Node.m_Position` where the import put it, but the edge curves meet elsewhere. At CS2 node 54722 (CS1 n4378) the curves meet at (−439.6, 3924.8), 25.8 m from the node position. Node 55295 (n12156) is 24 m off. The matcher measured node distance from `m_Position`, and matched edges by the point **20 m along the curve from the node** (probeDist). That point was 14–24 m off the CS1 probe, beyond the 8 m tolerance, so it reported "0/2 CS1 segments matched".
- **No node at all.** Six CS1 nodes (n527, n1884, n4801, n6802, n10611, n15971) join two collinear segments of one prefab. The game merged those segments, so no CS2 node exists there. Their TM:PE connections are all lane rank i → rank i with equal lane counts, which is the default straight-through behaviour.

**0.9.27 fixes:**
- **Edges by shape:** an edge matches by the **perpendicular distance from the CS1 probe to the whole edge curve**, provided the edge leaves the node in the CS1 segment's direction (dot ≥ 0.5). Distance along the curve no longer matters.
- **Node position:** candidate distance is measured from the **point where the node's edge curves meet**. That point is also used for lane-side detection.
- **Acceptance:** within `nodeRadius` (12 m); or anywhere within `nodeSearchRadius` if the node's edges carry every segment; or within 2 × `nodeRadius` if all but one segment (at least 2) match.
- **Merged-away nodes:** a node with no CS2 counterpart whose connections are all straight identity mappings is reported as **`notNeeded`**, not skipped.
- **Offline replay** of the A-v6 graph with a JS port of the matcher: 21 matched, 6 notNeeded, 0 skipped (A-v5 had 19 matched). The lane mapping per connection still has to be shown by the next dry run.
- **Nodes already holding Traffic data:** the 0.9.26 import already wrote data at some nodes, which are now skipped as "already has Traffic lane connections". Rerun with `--replace` to rebuild them with the new matcher.

## Round 28 (0.9.28, offline): TM:PE root cause

**Root cause: the wrong lane index.**
- **Proof:** the A-v6 dry run with Traffic's validation (`out/tmpe-import-report.json`, `net-converter/out/x1/tmpe-a6-dry.json`). The 15 connections that went from "created" on A-v5 to "skipped" on A-v6 all fail one rule, e.g. "source lane index 0 is not a road/track lane of edge 65169:11's composition (valid: 2,3,6)". So **the A-v5 import wrote invalid data**, and that is exactly why Traffic dropped 7 of its 9 nodes.
- **What our writer did:** it took a CS2 lane's index from `Lane.m_StartNode.GetLaneIndex() & 0xFF` of the edge sub-lane, whichever way the lane runs. Traffic needs the edge composition lane index (`NetCompositionLane.m_Index`). The game encodes that index in the edge lane's path node **on the node side**: `PathNode(edge, m_Index, segment)` in `LaneSystem`, which is what Traffic's own `GenerateConnectorsSystem` reads.
- **Where the wrong value came from:** for an incoming lane, `m_StartNode` is the far end. On split lanes, and on connection lanes whose far end belongs to another entity (`LaneSystem` 3903–3911), it decodes to 0 or 1 instead of 2, 3 or 6.
- **The same scan miscounted lanes.** It also counted every lane piece of a long edge, giving "CS1 has 2 car lanes, CS2 edge has 4".

**A-v5 against A-v6 is not a regression.** A-v5's 23 "created" connections were mostly invalid and were removed by Traffic. 0.9.26 runs Traffic's checks before writing and reports them as skipped.
- **Remaining A-v6 losses:** 6 nodes are merged away (straight-through identity data; `notNeeded` since 0.9.27). 3 nodes have edge curves meeting 24–26 m from `Node.m_Position` (fixed in 0.9.27 by matching on curve geometry).
- **Unexplained:** why the game leaves `m_Position` there is not explained offline; the 0.9.28 self-check reports both points per node.

**0.9.28:**
- **Lane pieces:** only the edge-lane piece that touches the node counts (`EdgeLane.m_EdgeDelta` at the node's end of the edge).
- **Lane index:** taken from that piece's node-side path node (`m_EndNode` for incoming, `m_StartNode` for outgoing). The node must be owned by the edge itself, otherwise the lane is skipped.
- **Traffic pre-validation** (0.9.26) stays as the guard.
- **Dry-run `selfCheck[]`,** per matched node:
  - `cs2Node`, `curveMeetingPoint` against `cs1Position`, and `connectedEdges`;
  - for each CS1 segment: the matched `cs2Edge`, the perpendicular probe `distance`, the CS1 lane count, and the CS2 composition lane indices into and out of the node.

**Next run (A-v7):**
1. Dry-run the TM:PE import. `selfCheck` should show composition-style lane indices (e.g. 2, 3 rather than 0, 1) with lane counts equal to CS1's.
2. Check that no connection is skipped by "Traffic validation".
3. Import, save, reload: Traffic must show no "couldn't load" dialog.

## Round 29 (0.9.29, offline): terrain material painting; folded network courses

**Terrain material painting:**
- **Endpoints:** `GET /map/terrain-materials`; `POST /import/terrain-paint` with polygons or strokes, a dry run, and netGuard; `POST /import/terrain-paint/undo`. Details in IMPORT_SCHEMA.md "Terrain material painting".
- **Materials** come from the editor's Materials row: `Terrain Material Extra 1–4` (Dry, Lush, Dirt, Sand; the en-US Locale descriptions) and `Terrain Material Clear`. Each paints a slot of the terrain render settings.
- **No rock material:** rock on slopes is automatic in the terrain shader.
- **Painting** is `TerrainMaterialSystem.ApplyBrush` per stamp, writing the user splatmaps that are saved with the map.

**`/import/network` folded courses:**
- **The problem:** when the game folds a course into an **older** neighbour segment, and re-splits it on its grid, the segment keeps its entity. It is then neither new in the batch nor recorded by this import, so the course was reported failed although it is there. Examples: A-v6 gravel `e23168_0`, `e31265_0`, `e31665_0`, `e36006_0`.
- **The fix:** a second pass checks the course's sample points against **all** live segments of the prefab. If they are covered, the edge is `merged` with "folded by the game into existing segment …". Those older segments are not added to this import's undo.

## Round 30 (0.9.30, offline after A-v7)

**A-v7 TM:PE:** 40 created; after reload, Traffic found "0 affected nodes of 14". The 0.9.28 lane-index fix is confirmed. Remaining skips, classified against `out/tmpe-import-report.json` and `live-graph-20261020-211001-end.json`:
- **Lane-count mismatch "CS2 has 0" (8 connections: n7849, n12950, n13892 on CS1 segments 20430, 22842, 35734).**
  - **Data:** degree-2 straight nodes whose TM:PE data is pure lane i → i, i.e. the default.
  - **0 lanes:** the node-side path nodes there belong to the neighbouring edge.
  - **Matcher fixed (bridge):** these nodes are now `notNeeded`, and the lane index falls back to the far end of the piece.
- **"No edge … nearest 20 m off" (6 connections: n11611/34976, n14407/12566, n18884/18923). Converter topology.** In each case the edge leaving the CS2 node in the CS1 direction is a **4–8 m stub of the neighbouring prefab**, and the real segment starts at the next node:
  - 65213 Highway 2-lane 4.8 m → Medium Road 96404;
  - 64403 1-lane 8.3 m → 2-lane 64890;
  - 62606 1-lane 4.2 m → BXP Truss Arch 56973.

  The prefab change lands a few metres off the CS1 node, which also makes a short lane pinch in game. Fix in the converter: put the change node on the CS1 node. The matcher now looks through stubs under 15 m (selfCheck `viaStubTo`), so the edge is found; the lane counts then come from the stub.

**Interchange at (2995, 820) (owner report): never imported.**
- **CS1 export** (`DakotaExport/…20261019-063123.json`): 48 segments within 350 m.
- **Import files** (import-all, bridges, bridges-fixed, gaps): **34 of them have no import edge**, by `cs1_segment` or within 3 m. They are 3L, 2L and 4L interstate and 1L ramps.
- **A-v7 graph:** all 15 import edges there are live.

So the converter dropped them; it is not a game removal. Check its stub and dead-end removal (round 12, `--keep-highway-stubs`).

**Terrain paint:**
- **Feather:** new `feather` option, strength ramping from 0 at the outline (A-v7's hard edges came from full-strength stamps reaching the outline).
- **Probe:** `POST /debug/terrain-paint/probe` measures how stamps combine and how materials are encoded, for the next run.

**`GET/POST /graph/diff`:** import set against the live graph, giving missing and extra segments per 500 m cell.

## Round 31 (0.9.31, offline)

**Terrain textures:** code **can** do what the image picker does. `POST /map/terrain-textures` imports an image as a `TextureAsset` into the user asset database and assigns it to an editable copy of the terrain render settings (MAP_SETTINGS.md). The bridge now references `Colossal.AssetPipeline(.Native)` and `Colossal.IO`. Not yet run in game.

**`/map/time`:** the editor's time-of-day, date and cloudiness preview, for dated season screenshots.

**Snap drift (A-v7 gravel gaps `gap_e25579_0`, `gap_e36563_0`):** the T ends landed 56 m and 64 m from the requested points, while their other ends sit exactly at the requested points.
- **The through road's segments are now 64/128/160 m,** multiples of 32 m: Gravel Road is a `SnapCellSize` prefab.
- **Only `NodeReductionSystem` moves nodes like that,** to balance lengths: it joins or moves **degree-2** Temp nodes that have an original (`CanMove`). So the split node was degree 2 at some moment, before the gap edge connected or when a later course re-touched the road.
- **Not the cause:** `CourseSplitSystem.SnapCoursePositions` shifts at most 2 m (4 m snap).
- **Evidence for A-v8:** the import report's `snapToSegment.details[]` gains `driftAfterBuild` (metres from the requested point to the live junction node) and `nodeDegree`.
- **Test on A-v8:** a snapped T on a long straight gravel road, alone in one import. If the drift appears there, the split node is being reduced in the same tool cycle, and the fix is to pin it (a `Fixed`/elevated-like `ForbidMove`, or adding the edge in the split's own definition). If not, a later import moves it, and that import is the place to fix.

## Round 32 (0.9.32)

**A-v8 `POST /map/terrain-textures` (14 files):**
- **What happened:** the bridge timed out ("simulation thread did not respond within 10s") and the window went black for about 2 min.
- **The imports did complete:** `ImportedData/TextureAssets` holds all 14 `.Texture` and `.cid` files, written 15:42:53–15:44:41. Player.log has no exception. Each field is assigned right after its file's `Save`, and `ApplyRenderSettings` runs after the loop.
- **Why GET looked broken:** it printed `AssetReference<TextureAsset>.ToString()`, which is the type name.

**Fix:**
- POST is now a background job: a worker-thread import, one file at a time, plus `GET /map/terrain-textures/status` with the per-file log.
- GET prints each slot's guid and asset name, or `default`.

## Round 33 (0.9.33): A-v8 native crash in `detroit-test`

**Crash:**
- Player.log 16:11 shows "Native Crash Reporting … UNKNOWN while executing native code" with an empty managed stack.
- There is no Crashes folder or dump under `%TEMP%\Colossal Order\Cities Skylines II`.
- CS2MCP.log has nothing after 15:44.
- So the cause is not proven: the crash came inside a Burst job or native code, during the step that built the combined option-2 and option-3 body.

**Prefab evidence:** read from the BXP pack, `pdx_mods/92245_36/BridgeExpansionAssetPack.cok`.
- **`BXP Double Deck Suspension Bridge - Highway`** has an `AuxiliaryNets` component. Its `m_Prefab` is CID `77f110c8a62ccb512739d5d58ae9803c`, which is `Double Deck Suspension Bridge - Lower Highway.Prefab.cid`.
  - So the lower deck is spawned by the upper deck.
  - The Lower Highway prefab has no `UIObject`, so it is not in the menu; its locale name is "… Lower Highway Lanes".
  - The import placed it on its own, as a separate dead-end edge in the air, and also placed the upper deck, which spawns its own lower deck at the same spot. That overlap is the prime suspect.
- **The same pattern, with an auxiliary-only partner,** exists in the Double Deck Subway and Train, the Golden Gate Subway and Train, and the ExtradosedBridge01 PedsBikes, Public Transport, Subway and Tram.
- **`BXP Suspension Bridge - Highway Twoway - 6 Lanes`** (option 2) has no `AuxiliaryNets`. It is a plain non-fixed net, 192–448 m per edge.

**Bridge:** `/import/network` now refuses auxiliary nets (`options.allowAuxiliaryNets` overrides).

**Dry run at 16:2x:** the editor terrain was flat at 511.9 m because the rebuild had not loaded the heightmap yet. Both bodies therefore failed only on `bridge.lowElevation` (deck 148 m).
- **Option 2:** each 362 m edge is one piece within 192–448.
- **Option 3:** the upper deck splits into 3 × 325 m fixed spans.
- **Rerun** both dry runs on the rebuilt terrain.

## Round 34 (0.9.34): A-v8 save failure and native crashes, analysis

**Sequence at save (Player.log 16:55):**
- **`SerializerSystem` threw** a `NullReferenceException` in `EntityManager.HighestEntityIndex`, at IL_0053, `ChunkIndex.get_Count()`. An archetype lists a chunk whose memory is gone: the ECS chunk store was already corrupt before the save.
- **That left the MapData stream open.** `MapPanelSystem.SaveMap` passes `mapData.GetWriteStream()` to `SaveGameSystem.RunOnce()`; after the throw it is never closed, and MapData stays at 0 bytes.
- **The package writer then failed.** `PackageAsset.SaveNoUnload` reopens MapData for reading, which raises the IOException (sharing violation). Continuing then crashed natively.
- **The 14 terrain textures had already been copied into the save bundle by that point.** So neither their handles nor their size broke the save.

**Texture import, checked against the decompiled game:**
- **`DefaultTextureImporter.Import`:** it uses `LongFile.Open` inside a `using` block, `Allocator.Persistent` buffers and native `NativeTextures` calls, and no Unity objects. The game itself runs it on pool threads, in `AssetImportPipeline` (`Parallel.ForEach`).
- **`TextureAsset.SetData(ITexture)`:** it copies the mips into its own buffer.
- **The bridge sequence is the editor picker's** (`DualImagePickerPopupField.OnSelectFile`), line for line. No stream stays open, and no Unity object is created off the main thread.
- **The first crash (16:11) was on 0.9.31,** where the import ran synchronously on the main thread.

**Bridge audit (subagent):**
- **Clean:**
  - no `DynamicBuffer` is used after a structural change;
  - there is no ECS access off the main thread;
  - there are no unsafe writes into game memory.
- **Hierarchy cache resize:** it completes tracked jobs first, and the game keeps that job's handle in the system's `Dependency`. It runs once per session.
- **Remaining indirect risks:**
  - raw `Deleted` from the UI phase, only through `?removeInvalid` on the hierarchy diagnostics endpoint;
  - direct pillar deletes;
  - the city `/save` has no busy guard (the editor saves through the UI).

**Root cause: not proven.**

**0.9.34 changes:**
- The texture job reuses an existing asset at the same path (same file and hash) instead of calling `AddAsset` over a texture that is already loaded and in use. A-v8 job 2 had re-imported all 14 files.
- CS2MCP.log records every non-GET and every `/import*` request at its start, with the frame number, so the last line before a native crash names the bridge call in flight.

## Round 35 (0.9.35): idempotent look edits

**Owner data:**
- **A8a/A8b:** a texture job followed by a save works on a loaded map: A8b was 159.8 MB, with no error.
- **The failure needs this sequence in one session:**
  1. a settings swap and texture job 1;
  2. the look step (vanilla terrain settings selected again);
  3. a second settings swap and texture job 2;
  4. the save.

**Mechanism found:** in `PrefabSystem.AddPrefab`, a second `Clone()` of the vanilla terrain settings gets the same name, "DefaultTerrainRenderSettings (copy)", and its own prefab entity. But `m_PrefabIndices` (by PrefabID) keeps pointing to the first copy, which triggers the "Duplicate prefab ID" warning.
- The terrain then uses copy 2.
- The save's prefab references and every name lookup resolve to copy 1, along with its own asset references.
- So two prefab entities share one ID at save. This doesn't explain the corrupted chunk store, but it is the one state that differs between the failing session and A8b.

**Fix:** `EditLook` first looks up an editable "<name> (copy)" of the same type by PrefabID and edits that instead of cloning again. This covers `/map/settings`, the texture job and climate/water/terrain.

**No crash dumps:** neither `%LOCALAPPDATA%\Temp\Colossal Order\Cities Skylines II\Crashes`, LocalLow `Crashes`, nor `%LOCALAPPDATA%\CrashDumps` has a Cities2 dump.

**Owner note:** three saves under the name of the crashed 16:55 save were silently dropped. A bridge `/map/save` that reports the real outcome is a candidate follow-up.

## Round 36 (0.9.36): aux-net owners refused

**Reproduced by the owner (17:44, 0.9.33):**
- **The body:** a single `BXP Double Deck Suspension Bridge - Highway` edge, 976 m at deck Y 148, with nodesFirst, bypass and batchSize 1, and no Lower Highway edge. The dry run said valid, 3 × 325 m.
- **Outcome:** an instant native crash with no managed stack, the same as 16:11.
- **Control:** option 2 (`BXP Suspension Bridge - Highway Twoway - 6 Lanes`) imported fine 3 minutes earlier.

**Why it can die natively (from the code, not proven by a dump):**
- **Where the aux nets are built:** the upper deck's `AuxiliaryNets` (lower deck at `m_Position.y` below it) is expanded inside the Burst jobs of `CourseSplitSystem`: `CheckCourseIntersectionsJob` with `m_AuxIndex`, `MergeAuxPositions` and `auxIntersectionEntities`. A release build has no bounds checks there, so an index error is a native crash.
- **Our input differs from the vanilla tool's in three ways:**
  1. nodesFirst attaches the course to standalone nodes built beforehand. The aux positions reset `m_CoursePos.m_Entity` to null, so aux end nodes must be created inside the same course.
  2. The single course covers 3 fixed segments.
  3. It is a dead end at both ends, in the air.
- **The vanilla tool treats these prefabs specially:** `NetToolSystem` refuses replace/upgrade into any prefab with aux nets (`AllowReplace` requires an empty `AuxiliaryNet` buffer).

**Bridge:** `/import/network` refuses any edge whose prefab has `AuxiliaryNets`, naming its aux nets; `options.allowAuxiliaryNets` overrides. Together with 0.9.33's aux-only check, the whole family is covered: the BXP Double Deck Highway, Subway and Train, Golden Gate Bridge Subway and Train, and the ExtradosedBridge01 PedsBikes, Public Transport, Subway and Tram.

**Still unexplained:** the 16:55 save failure, the `SerializerSystem` NRE in the chunk store. Candidates are the duplicate "(copy)" prefab ID (fixed in 0.9.35), and leftover damage from the 16:11-type aux-course crash path if a partial aux build happened without killing the process.

## Round 38 (0.9.38): absolute heights resolved at build time

**Converter report (A-v8 interchange 8, fidelity pass):** CS1 segments 14574 and 35648 ask for y 139.8–140.7, but the live segments 64661:3 and 64743:3 sit at y 168.7–178.3. 17897 and 31688 are 13 m off, and curved elevated pieces sag 5–9 m mid-span.

**Cause, from the live graph:**
- **The nodes themselves were built too high:** 64661:3 ends at y 174–178, with elevation 112.
- **Elevation was frozen at parse time.** In heightMode absolute, `/import/network` turned y into `elevation = y − ground` when it parsed the body. The node was later built at `ground_at_build + elevation`.
- **The terrain had changed in between.** The ground under those nodes read ~28 m at parse and ~62 m at build time (+34 m), so the nodes went up by exactly that much.
- **Mid-span:** `NetUtils.AdjustPosition` put the control points on the terrain under them plus the interpolated elevation. Over uneven ground this gives the sag or hump.

**Fix:**
- **Nodes keep `AbsoluteY`.** `NodePosition` resolves the elevation against the terrain of the batch being built, so the node lands exactly at the requested y.
- **Linear y mid-span:** an edge whose two ends are absolute and more than 2 m above the ground keeps the straight y line between its ends (`bezier.b/c` = lerp of the end y). Ground-level edges still follow the terrain.

**Report:** the import report has a new block, `heights`:
- `checkedEdges`, `offMoreThan1m` and `worstDeviation`;
- the worst 200, each with requested and live y at a / mid / b.

**Converter bug found on the way (not the cause):**
- **Doubled control y:** every control point in import-all, roads, rail, power and canals has y ≈ 2 × the node y (ratio 2.000; e.g. `e15_0aa` nodes 125/126, controls 250). bridges-fixed has 0.
- **No effect today:** the bridge ignores control y (only x/z are used), so this never reached the game. It should be fixed in the converter anyway.

**Test body:** `net-converter/out/x1/test-heights-ic08.json` (14 edges, CS1 segments 14574, 35648, 17897, 31688).

## Round 39 (0.9.39): pillars vs retaining walls, `elevated: true`

**The owner's "messed up interchange" (A-v8 shots 12/15):** CS1's flyover ramps on pillars appear as walled embankments, even though the heights match within 4 m.

**The rule, from the game code (all roads and highways, including ramps):**
- **Elevation limit:** `NetInitializeSystem` sets `NetGeometryData.m_ElevationLimit` to 4 m for every road and highway; only pathways get 2 m.
  - `RaisedIsElevated`, which would put 4 m pieces on pillars, is set only for pathways, airplane paths, power lines and pipelines.
- **Composition:** `CompositionSelectSystem` → `NetCompositionHelpers.GetElevationFlags(start node, edge, end node)` decides, per side, from max(min elevation at the start, min at the end, middle elevation):
  - ≥ 8 m (2 × limit) on both sides → `General.Elevated`: a deck on pillars, no walls (`RequireElevated` prefabs always).
  - ≥ 4 m → `Side.Raised`: retaining walls, the "raised road" pieces.
  - ≤ −4 m → `Side.Lowered`. Tunnel needs ≤ −8 / −12 m.
  - Otherwise → the ground composition.
  - The result is ORed with `Upgraded` flags and fixed-span states.
- **Pillars:** `SubObjectSystem` places `RequireElevated` sub-objects (pillars) from the same stored `Elevation`, against limit × (1, 2) + max(0, `elevationRange.min`):
  - mid-span pillars need the edge's middle at ≥ 8 m;
  - node pillars need the node at ≥ 8 m, or ≥ 4 m for edge-placed pillars when the edge's middle is ≥ 8 m.
- **Where the stored `Elevation` comes from:**
  1. `CourseSplitSystem` splits each course where the height above terrain changes type: ≥ 2, ≥ 4 m, ≤ −2, −4 or −12 m.
  2. `GenerateEdgesSystem` writes the piece's elevation.
  3. Absolute heights become height above the terrain at that point.
- **So a CS1 ramp 5 m up is Raised in CS2**, i.e. retaining walls.
- **Terrain:** for ground compositions (< 4 m), `FlattenTerrain` makes `TerrainSystem` fill and cut the terrain to the road, which gives the earth embankment. For Raised sides it does not raise the terrain (`float7 = −1e6`). For Elevated it only cuts below the deck. So the walls come from the composition, not from the terrain. Earth slopes come from the terrain flattening.

**Option (0.9.39):**
- **What it does:** per-edge `elevated: true`, or `options.elevatedFromStructure` for edges tagged `"structure": "elevated"`. After the import, the bridge raises the stored Elevation of the segments and inner nodes to 2 × the limit + 0.05, adds the component where missing, and marks them `Updated`. The composition then becomes Elevated and pillars are placed.
- **What it leaves alone:** the geometry, and shared end nodes.
- **When it reverts:** a later net-tool edit of such a segment recomputes the elevation from the terrain.

**For the converter:** the alternative is to lift the ramps to ≥ 8.05 m above the terrain at every point, with no flag. That changes heights and is not what CS1 had.

## Round 40 (0.9.40): absolute heights over water; season readout

**Interchange 8, still too high in A-v9:** heights were identical to A-v8 to the centimetre (e.g. 174.06 / 178.00), so round 38's "terrain changed between parse and build" explanation was wrong.
- **Real cause:** `CourseSplitSystem.InitializeCoursePos` places a `FreeHeight` course end at `max(terrain, water surface + 2 × ElevationLimit) + elevation`. Below an elevation of −1 it uses the terrain alone. Water counts where its depth is ≥ 0.2 m, and 2 × the limit is 8 m for roads.
- **How the bridge triggered it:** it sets `FreeHeight` on every unattached end with a nonzero elevation (standalone nodes from nodesFirst, and free edge ends), but measured the elevation from the terrain only. Over water each node therefore rose by water surface + 8 − bed height. That accounts for the 34–38 m.
- **Fix:**
  - For absolute nodes, the elevation is measured from the same base as `InitializeCoursePos`: water surface data is read once per batch and `WaterUtils.SampleHeight` gives the terrain, water and depth.
  - Between the two bases (no elevation reproduces y), the end is built without `FreeHeight` at the given y (`KeepY`).
  - This applies to `DefineNode` and to the edge ends.
  - Round 38's per-batch resolution and linear mid-span y stay.
- **Verify:** `report.heights` on the next import.

**Season test 31 °C on every date:**
- **What `/map/time` read:** `sampleAtDate` applies the climate overrides, and `temperature.overrideState` was on.
- **Who set it:** Time and Weather Anarchy (PDX 88893) is loaded with the selected profile "desert" (`ModsSettings/TimeWeatherAnarchy`). That profile has a custom temperature of 31, custom precipitation of 0, TimeOption 3, WeatherOption 2 and day of the year 193. The mod re-applies this every frame, so the season stays summer.
- **Curve rebuild:** `climateEdit` already rebuilds the copy's curves (`AfterLookChange`: `EnsureSeasonsOrder` + `RebuildCurves`).
- **`/map/time` now also returns:**
  - `curveAtDate`: the raw curve at the date, without overrides;
  - `overrides`: the state of date, temperature (with its value), precipitation, cloudiness, fog and aurora;
  - `timeWeatherAnarchy`: loaded or not, the selected profile and its custom settings.
- **Action for the owner:** select the mod's "Main" (default) profile, or disable the mod, for season tests.

**`dropped by the game: the course runs inside the existing … segment`:** this is a post-build diagnostic (0.9.12) that explains a course the game discarded. The bridge does not pre-reject overlaps.

## Round 41 (0.9.41): climate overrides cleared

**Theme session's decompile of TimeWeatherAnarchy 88893_17:**
- **What the mod does:** `TimeAndWeatherControlSystem` writes `overrideValue`/`overrideState` for temperature, precipitation, cloudiness, fog and aurora, plus time, date, latitude and longitude, from the selected profile.
- **Why it sticks in the editor:** the mod stops updating there (`_isEditor`) but never clears the overrides.

**Bridge changes:**
- **Editor load:** clears the five weather overrides and logs a warning that names the likely source.
- **`/map/time`:**
  - `clearOverrides: true` clears them on demand, plus the date override unless the same call sets a date;
  - GET lists the value of every override;
  - `month` + `hour` without `day` sets date = (month − 1 + hour/24)/12, because a CS2 day is one month unit of the climate curves.

## Round 42 (0.9.42): pillar objects

**Rock-shed colonnade question:** the vanilla pillar names are now listed in PREFAB_NAMES.md.

**Can they be placed standalone?** `/import/objects` takes them; there is no flag and no refusal. As standalone objects they are not stretched by `AlignSystem`, which only handles owned objects.
- **Stack pillars:** they get their range from `GenerateObjectsSystem`. With `StackDirection.Up` they reach down by the object's elevation.
- **Placeholders:** they pick a random variant.

**`/prefabs/info`:** now reports `geometry.pillar` (type, offsetRange), `geometry.stack` (direction, first/middle/last bounds) and `geometry.placeholder` (variant count).

## Round 43 (0.9.43): A-v10 native crash after the look step, `/map/save`

**Crash:** a fresh map. The last bridge requests (CS2MCP.log 05:44:16) were four `/map/settings` posts within 220 ms: lat/long/theme, climate.json, water.json, and terrain-cs1.json (editable copy, legacy format, tiling). Player.log then shows "GPU instance buffer size changing to: 80 MB" and the native crash at 05:44:26, with an empty managed stack. No dump.

**What the terrain post does natively (decompiled `TerrainMaterialSystem`):**
- **The setter swaps the live textures:** `currentTerrainRenderSettings` calls `ApplyRenderSettings()`. That starts with `ReleaseTextures()`, which `Unload()`s every terrain `TextureAsset` it loaded. It then loads the new set synchronously (`TextureAsset.Load(mipBias)`) and binds it with `Shader.SetGlobalTexture`.
- **Twice per post:** the bridge's `AfterLookChange` calls `ApplyRenderSettings()` again in the same frame.
- **Legacy format:** it switches the normal-map fallback (`Texture2D.normalTexture` vs the system's) and the `_TerrainLegacy*` shader globals.
- **Hypothesis:** freeing and reloading GPU textures that the frame in flight (and the VT/terrain passes) still reference, on a map whose terrain and materials were just built.
- **Not proven:** there is no dump, and A-v9 survived the same posts on a fresh map.

**Duplicate prefab IDs:**
- **Cause:** the "(copy)" reuse of 0.9.35 only finds an editable copy. A copy from a map loaded earlier in the game session is asset-backed, so the bridge cloned again under the same name and produced a duplicate ID.
- **Fix:** clones now get a free name ("… (copy 2)" etc.).

**Changes:**
- **`POST /map/save?name=…[&overwrite=true]`:** the editor's own save path with the real outcome in `/map/save/status`. Existing names get a 409 unless overwrite is set. See MAP_SETTINGS.md.
- **Never-saved maps:** terrain render edits and terrain textures are refused there (409, `?force=true`).
- **Safe sequence:** build → `/map/save` → look posts, one per request, after the previous answer → save.

**"CSS parsing error … 5.000000e-02":** not the bridge's. CS2MCP has no UI module or CSS. Another mod's UI writes a float in exponent form.

## Round 44 (0.9.44): raster terrain paint, object import timing

**Owner: ~18 min of bridge time per build.** A-v10 terrain paint took 673 s: 5,406 polygons became ~1.6 M stamps, plus 88,983 strokes, each one `ApplyBrush`. Trees took 368 s for 505 k (0.73 ms each).

**Raster paint (`options.method`, default `"raster"`; `"stamp"` = 0.9.29 behaviour):**
- **Grouping:** polygons and strokes are grouped by (material, strength).
- **Rasterising:** each group becomes one CPU coverage grid at splatmap resolution (3.5 m, ≤ 4096²).
  - Polygons use scanline inside, with distance bands for feather (inward ramp) and softEdge (outward ramp, only without feather).
  - Strokes are the brush prefab's own texture (read back once, 64²), scaled to the disc.
  - Overlaps combine as 1 − Π(1 − vᵢ·s); the uploaded mask is that divided by s. This equals repeated stamps if the kernel moves a texel toward the target by brush × strength.
- **Dispatch:** the grid is uploaded as an RGBA32 linear texture and passed as the brush texture of one square brush over the group bounds. It goes to the game's private `TerrainMaterialSystem.ApplyToSplatmap`, once for each splatmap. The paint/erase kernel, the channel and the strength handling are the game's.
- **Calibration:** the first raster paint of a session paints a 16² test mask (one quadrant 255, a neighbouring one 128) at the playable centre, reads 4 probes and 4 corners, and restores both splatmaps.
  - It finds the uv → x/z flips and transpose, and whether the kernel clips to the inscribed circle (then the brush square is enlarged by √2).
  - `calibration` in the response shows the raw changes. The 128 quadrant's change should be about half the 255 one if the kernel is linear.
  - If the result is inconclusive, the import falls back to stamps.
- **Differences from stamping:**
  - The quadtree stamps overlapped (radius 0.75 × cell), so stamped interiors with strength < 1 got more than the requested strength. The raster interior gets exactly the strength.
  - Edges follow the feather/softEdge ramps instead of the brush falloff of edge stamps.
- **Timing:** the response gives `seconds` and, per group, `rasterMs`, `uploadMs` and `dispatchMs`.

**Objects:**
- **Batched protection:** Anarchy's `PreventOverride` is added once per batch (`EntityManager.AddComponent(NativeArray, type)`) instead of per entity.
- **New `timing` block in the report:** defineMs, matchMs, applyFrameMs (the game's work from Apply to resolve), resolveMs, protectMs, netGuardMs and settleFrames. It is there to find where the 0.73 ms per tree goes before replacing the tool pipeline.
- **Not built yet:** direct bulk entity creation, i.e. reproducing `GenerateObjectsSystem` + `ApplyObjectsSystem`. That waits until the timing shows the game's apply frame dominates.

**Perf test bodies** (root repo, `net-converter/out/perf/`):
- `perf-paint-400.json`: 400 polygons + 2,000 strokes, raster.
- `perf-paint-400-stamp.json`: the same body with `method: "stamp"`.
- `perf-trees-100k.json`: batchSize 5000, bypassValidation.

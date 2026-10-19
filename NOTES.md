# Editor import: notes and manual verification

Branch `editor-import` (local, not pushed). Bridge version `0.9.8-editor-import` (shown by `cs2_ping`). Built with 0 warnings and deployed to `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2MCP\CS2MCP.dll`. MCP server rebuilt (`mcp-server/dist/index.js`). **Nothing here has run inside the game yet.**

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

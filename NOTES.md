# Editor import: notes and manual verification

Branch `editor-import` (local, not pushed). Bridge version `0.9.2-editor-import` (shown by `cs2_ping`). Built with 0 warnings and deployed to `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CS2MCP\CS2MCP.dll`. MCP server rebuilt (`mcp-server/dist/index.js`). **Nothing here has run inside the game yet.**

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
3. In a Claude Code session in `D:\work\mods\csl2-editor`: `cs2_ping`. Expect `version: 0.9.2-editor-import`, `gameMode: Editor`. If `/ping` fails, the bridge did not start: check `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\CS2MCP.log`.
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

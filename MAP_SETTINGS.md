# Map settings: what the bridge can set, and where the rest is in the editor

`GET /map/settings` reads, `POST /map/settings` writes (MCP `cs2_map_settings`). Panel names below are the editor's inspector sections (`Editor.INSPECTOR_SECTION_TITLE[...]`) and field labels (`Editor.*` localisation ids) from the decompiled `Game.UI.Editor` panels of CS2 1.6.2.

| Setting | Bridge | Lives in | Editor panel → field |
|---|---|---|---|
| Theme | read/write (`theme`: prefab name, e.g. `European`, `North American`) | `CityConfigurationSystem.defaultTheme` | Map Settings → Theme |
| Climate | read/write (`climate`: climate prefab name, `climateEdit`: any field; see "Look" below) | `ClimateSystem.currentClimate` | Climate Settings → Selected climate |
| Latitude / longitude | `latitude`/`longitude`: this session only. **Persistent via `climateEdit`: {latitude, longitude}** (0.9.23) | `PlanetarySystem.latitude/longitude`, reset from the climate prefab | `ClimateSystem.PostDeserialize` and the climate panel copy the **current climate prefab's** `m_Latitude`/`m_Longitude` over the planetary values on every load and every change. So the sun position is a climate property. `climateEdit` edits a copy of the climate, as the editor's "Edit climate" does; the copy is saved into the map, and its coordinates come back on load. |
| Sea level | read/write (0–2000) | `WaterSystem.SeaLevel` | Water Settings → Sea level (and "Apply sea level") |
| Height scale | read/write (200–10000) | `TerrainSystem.SetTerrainProperties` | Terrain (Heightmaps) → Height scale |
| Left-hand traffic, natural disasters | read/write | `CityConfigurationSystem` | no editor field (saved into the map; a player's own new-game options override them) |
| Unlock all, unlimited money, unlock map tiles | read only | `CityConfigurationSystem` | no editor field |
| Starting year / month / time, "current year as starting year" | write in the editor only | `MapPanelSystem` fields + `TimeData` singleton | Map Settings → Starting year / Starting month / Starting time |
| "Map name as city name" | write in the editor only | `MapPanelSystem.m_MapNameAsCityName` | Map Settings → Use map name as default city name |
| Camera start (pivot, angle, zoom) | read/write | `CityConfigurationSystem.m_CameraPivot/Angle/Zoom` | Map Settings → Camera starting position |
| Map name | read only (`mapName`) | `MapPanelSystem` private localisation field; becomes `Maps.MAP_TITLE[...]` on save | **by hand:** Map Settings → Map name |
| Description | not readable | same, `Maps.MAP_DESCRIPTION[...]` | **by hand:** Map Settings → Description |
| Preview, thumbnail | no | `MapPanelSystem` private | **by hand:** Map Settings → Preview / Thumbnail |
| Starting tiles | count only (`startingTiles`) | `MapTileSystem.GetStartTiles()` | **by hand:** Map Settings → "Select starting tiles" button, then click tiles |
| Content prerequisites | no | `CityConfigurationSystem.requiredContent` | Map Settings → Content prerequisites |

Saving is always by hand: the editor's Save Map dialog builds the map metadata (name, preview, playability checklist) at save time.

Things the playability checklist (`MapRequirementSystem`) wants before a map can be published: a starting area, water in it, a road outside connection and a map name (details below).

## Playability checklist (`GET /map/checklist`, MCP `cs2_map_checklist`)

The same items as the Map Settings checklist, from `MapRequirementSystem` and `MapPanelSystem.MapIsPlayable`.

- **Required:** starting tiles; water (surface or groundwater) in a starting tile; a road outside connection (any road node on the border); a map name (valid in the fallback language).
- **Optional:** train, air and electricity connections; oil, ore, forest and fertile land anywhere on the map.
- **Publishing extras:** description, preview, thumbnail.

The response is `{playable, required:[{item, pass, detail}], optional:[…], publishing:[…]}`; `detail` says what is missing. The editor checks only that some road node lies on the border; it has no separate inbound/outbound check, so a two-way road connection satisfies it.

## Look: climate, water rendering, terrain rendering (0.9.23)

Decompiled from `Game.UI.Editor.ClimatePanelSystem` (inspector section **Climate Settings**) and `EnvironmentPanelSystem` (sections **Water Rendering Settings** and **Terrain Rendering Settings**).

**How the editor handles these three.** Each panel has a prefab picker ("Selected climate / water / terrain prefab"). It writes `ClimateSystem.currentClimate`, `WaterRenderSystem.currentWaterRenderSettings` or `TerrainMaterialSystem.currentTerrainRenderSettings`.
- **Read-only prefabs:** vanilla, DLC and mod prefabs are shown greyed out with an **Edit …** button. The button clones the prefab (`PrefabBase.Clone()`, name "<name> (copy)"), registers it and makes the clone current.
- **Editing:** after that the inspector shows **every public field** of the prefab (`EditorGenerator.BuildMembers`).
- **Saving:** `PrefabSystem.SaveClimate`, `SaveWaterRenderSettings` and `SaveTerrainRenderSettings` embed the edited copies into the map. That is why saves carry prefabs such as `TemperateArchipelagoHavenClimate (copy)` (the "Duplicate prefab ID" lines in Player.log).
- **Refresh after a change:** the climate panel calls `RebuildCurves()` and copies latitude, longitude and the sun limits to `PlanetarySystem`. The environment panel calls `TerrainMaterialSystem.ApplyRenderSettings()` and `WaterRenderSystem.ApplyMaterialParams()`.

The bridge does the same:

| Call | What it does |
|---|---|
| `GET /prefabs?category=climate|waterRenderSettings|terrainRenderSettings[&detail=1][&query=]` (MCP `cs2_find_prefabs`, `detail: true`) | Every prefab of that kind: vanilla, DLC, mods and copies embedded in maps (all prefabs in `PrefabSystem`). Each comes with `current`, `readOnly`, `asset` and a `summary`, plus all fields with `detail`. |
| `GET /map/look/schema` | Every field of the three prefabs (and of seasons and the nested terrain settings): type, range, default, tooltip, shownInEditor, writable. |
| `GET /map/settings[?detail=1]` | `look.climate`, `look.waterRenderSettings`, `look.terrainRenderSettings`: the current prefab, its summary and, with `detail`, all fields. |
| `POST /map/settings {climate, waterRenderSettings, terrainRenderSettings}` | Selects the prefab by name, as the picker does. |
| `POST /map/settings {climateEdit, waterEdit, terrainEdit}` | Field edits on the current prefab. A read-only one is cloned first, as the Edit button does, and `applied` says so. Then the panel's refresh runs. |

**Field edits:**
- **Keys:** keys are field names with or without `m_`, case-insensitive (`latitude` = `m_Latitude`).
- **Values:**
  - numbers, bools, strings and enums by name;
  - `float2`/`double2` as `[x, y]`;
  - colours as `[r, g, b(, a)]` (0–1) or `"#rrggbb"`;
  - nested objects as objects;
  - `Vector2`/`Vector4` as arrays;
  - prefab references (`m_DefaultWeather`, a season's `m_Prefab`) by prefab name;
  - nested objects as objects;
  - array elements as `[{index, field: value}]`, where an index past the end appends and `{index, remove: true}` deletes.
- **Not settable:** `AnimationCurve` fields (generated from the seasons) and the texture references (`AssetReference`; use the panel's image picker).

### Full field reference (0.9.23)

Read from the decompiled `ClimatePrefab`, `ClimateSystem.SeasonInfo`, `WaterRenderSettingsPrefab`, `TerrainRenderSettingsPrefab`, `TerrainBlurDepthData`, `TerrainDepthScaleSetting` and `TerrainTilingData` (game 1.6.2). Ranges are the editor's `[Range]`/`[RangeN]` slider limits. The game does not clamp values written from code, so stay inside them. `GET /map/look/schema` returns the same list from the running game: type, range, default (from a fresh instance), tooltip, `shownInEditor` and `writable`.

**How each value is set:**
- **Per-map copies:** every value below is per map, but only through a **map-specific copy** of the prefab. The game has no other per-map override; the map stores which climate, water and terrain prefab it uses.
- **Editing a shared prefab:** `climateEdit` / `waterEdit` / `terrainEdit` on a read-only (vanilla, DLC or mod) prefab first clones it to "<name> (copy)", exactly like the editor's Edit button. The copy is embedded in the map on save. Picking a prefab by name (`climate` / `waterRenderSettings` / `terrainRenderSettings`) uses it as it is.
- **Panel paths:** Editor → right panel. Climate: "Climate Settings". Water and terrain: "Environment" → "Water Rendering Settings" / "Terrain Rendering Settings". In both panels: pick a prefab, then "Edit …", then the fields.

#### Climate (`climateEdit`), panel "Climate Settings"

| Field | Type | Range | Default | Meaning / writable |
|---|---|---|---|---|
| `m_Latitude` | float | −90…90 | 61.498 | Sun path and day length (`PlanetarySystem`). Writable. Persistent only through the copy. |
| `m_Longitude` | float | −180…180 | 23.767 | Sun timing. Writable. |
| `m_MaxSunElevationAngle` | double | 45…90 | 90 | "The Sun will be limited to not rise higher above the horizon than this". Writable. |
| `m_SunElevationClampStart` | double | 0…90 | 45 | Angle where the clamp starts to deviate. Writable. |
| `m_FreezingTemperature` | float | – | 0 | °C below which precipitation is snow and snow cover builds up. Writable. |
| `m_RandomSeed` | int | editor 1–99 ("Pick new seed") | 1 | Seed for the generated weather curves. Writable. |
| `m_DefaultWeather` | WeatherPrefab | – | – | Default weather. Writable by WeatherPrefab name. |
| `m_DefaultWeathers` | WeatherPrefab[] | – | [] | Weather set. Writable as a list of names. |
| `m_Seasons` | SeasonInfo[] | – | [] | Seasons in year order; see below. Edit as `[{index, …}]`: an index past the end appends, `{index, remove: true}` deletes. |
| `m_Temperature`, `m_Precipitation`, `m_Cloudiness`, `m_Aurora`, `m_Fog` | AnimationCurve | – | generated | Hidden in the editor (`[HideInEditor]`). `RebuildCurves()` regenerates them from the seasons and the seed after every edit; they are the panel's five graphs. **Not writable.** Change the seasons instead. |

**Season fields** (`ClimateSystem.SeasonInfo`, `climateEdit.seasons[i]`):

| Field | Type | Range | Default | Meaning |
|---|---|---|---|---|
| `m_Prefab` | SeasonPrefab | – | – | Which season this is (spring/summer/autumn/winter variants); it drives the vegetation look. Writable by SeasonPrefab name. Required for an appended season. |
| `m_StartTime` | float | 0…1 | 0 | Start as a fraction of the year. **Season lengths** are the gaps between consecutive start times (`EnsureSeasonsOrder` sorts them). |
| `m_TempNightDay` | float2 [night, day] | −70…70 | [5, 20] | Mean night and day temperature, °C. |
| `m_TempDeviationNightDay` | float2 | 0.5…40 | [4, 7] | Spread of night and day temperature. |
| `m_CloudChance` | float | 0…100 | 50 | Chance of clouds, %. |
| `m_CloudAmount` | float | 0…100 | 40 | Cloud cover, %. |
| `m_CloudAmountDeviation` | float | 1…100 | 20 | Spread of cloud cover. |
| `m_PrecipitationChance` | float | 0…100 | 30 | Chance of rain or snow, %. |
| `m_PrecipitationAmount` | float | 0…100 | 40 | Rain or snow amount, %. |
| `m_PrecipitationAmountDeviation` | float | 1…100 | 30 | Spread of the amount. |
| `m_Turbulence` | float | 0…1 | 0.2 | Wind turbulence. |
| `m_AuroraAmount` | float | 0…1 | 1 | Aurora strength. |
| `m_AuroraChance` | float | 0…100 | 10 | Chance of aurora, %. |

**Climate items without a field of their own:**
- **Fog:** generated from cloudiness, precipitation and temperature (`RebuildFogCurves`).
- **Snow:** precipitation below `m_FreezingTemperature`.
- **Day and night:** latitude plus the sun clamp.
- **Year length:** fixed at 12 (`kYearDuration`).
- **Wind direction:** the panel's wind control writes `WindSimulationSystem`, not the climate, and is not saved with the climate. Not exposed by the bridge.

#### Water rendering (`waterEdit`), panel "Environment → Water Rendering Settings"

| Field | Type | Range | Default | Look aspect |
|---|---|---|---|---|
| `m_WaterColor` | Color | – | (0.14, 0.62, 0.62) | Base water colour (absorption tint) |
| `m_AbsorptionDistance` | float | 0…100 | 8 | Transparency / depth falloff: metres until the water is opaque |
| `m_MaxRefractionDistance` | float | 0…4 | 3 | Refraction distortion depth |
| `m_WaterScatteringColor` | Color | – | (0.19, 0.33, 0.39) | Scattering colour |
| `m_AmbientScattering` | float | 0…1 | 0.2 | Scattering from ambient light |
| `m_HeightScattering` | float | 0…1 | 0.2 | Scattering by wave height |
| `m_DisplacementScattering` | float | 0…1 | 0.1 | Scattering by displacement |
| `m_DirectLightTipScattering` | float | 0…1 | 0.6 | Sunlight through wave tips |
| `m_DirectLightBodyScattering` | float | 0…1 | 0.4 | Sunlight through the wave body |
| `m_FoamAmount` | float | 0…2 | 0.3 | Foam |
| `m_FoamFadeRange` | float2 [start, distance] | 0…1000, 500…1500 | [500, 1500] | Foam fade with camera distance |
| `m_WavesMultiplier` | float | 0…2 | 1 | Sea waves |
| `m_LakeWavesMultiplier` | float | 0…1 | 1 | Lake waves |
| `m_MinWaterAmountForWaves` | float | 5…200 | 10 | Water depth before waves appear (shallow and shoreline calm) |
| `m_SeaWindAffectWavesDirection` | bool | – | false | Waves follow the sea wind |
| `m_RipplesWindSpeed` | float | 0…15 | 5 | Ripples |
| `m_RipplesChaos` | float | 0…1 | 1 | Ripple chaos |
| `m_SmoothnessClose` | float | 0…0.99 | 0.95 | Reflection sharpness up close |
| `m_SmoothnessDistant` | float | 0…0.99 | 0.85 | Reflection sharpness far away |
| `m_SmoothnessRange` | float2 [start, distance] | 0…1000, 500…1500 | [100, 500] | Blend range between the two |
| `m_CausticsPlaneDistance` | float | 0…10 | 4 | Caustics depth |
| `m_CausticsIntensity` | float | 0…3 | 1 | Caustics strength |

**Water items with no map setting:**
- **Pollution tint:** the game has none per map. Polluted water is drawn by the water shader from the simulation's pollution, and the pollution infoview uses `WaterRenderSystem.overlayPollutionMask`, a runtime overlay.
- **Shoreline:** no separate parameters beyond `m_MinWaterAmountForWaves` and the foam. Neither can be set from code or in the editor.

#### Terrain rendering (`terrainEdit`), panel "Environment → Terrain Rendering Settings"

| Field | Type | Range | Default | Look aspect |
|---|---|---|---|---|
| `m_RockDiffuse` / `m_RockNormal`, `m_DirtDiffuse` / `m_DirtNormal`, `m_GrassDiffuse` / `m_GrassNormal` | texture | – | – | Base terrain textures. **Not writable from code.** Set them in the panel's image picker; it imports a texture. |
| `m_Extra1…4Diffuse` / `m_Extra1…4Normal` | texture | – | – | Extra layers. **Not writable from code;** same panel. |
| `m_ExtraNDirtOverride` (N = 1…4) | float | 0…1 | 0 | How much extra layer N replaces dirt |
| `m_ExtraNSplatRange` | float | 0.1…1 | 1 | Splat range of layer N |
| `m_ExtraNBlurDepth` | float | 0.01…0.3 | 0.1 | Blend blur of layer N |
| `m_ExtraNHeightOffset` | float | −0.4…0.4 | 0 | Height-blend offset of layer N |
| `m_UseLegacyTextureFormat` | bool | – | false | Legacy texture layout (no extra layers) |
| `m_BlurDepth` {`m_NearBlurDepth` 0.01…2, `m_FarBlurDepth` 0.01…2, `m_NearDistance` 0…250, `m_FarDistance` 250…5000} | object | per field | {0.1, 0.25, 50, 500} | Texture blending blur by distance |
| `m_TriplanarHeightMapBlending` | float | – | 0.5 | Cliff (triplanar) blending |
| `m_TriplanarBlendStrengthY` | float | 0.01…1 | 0.2 | Vertical triplanar strength |
| `m_SplatMultiplier` | float | – | 2 | Splat-map contrast |
| `m_SplatPower` | float | – | 1 | Splat-map curve |
| `m_DepthScaleSettings` {`m_Rock`, `m_Dirt`, `m_Grass`: Vector2 [x, y]} | object | – | [1, 1] each | Height/depth scale per material |
| `m_TerrainRockTiling` / `m_TerrainGrassTiling` / `m_TerrainDirtTiling` {`m_FarTiling`, `m_MidTiling`, `m_NearTiling` 1…20000} | object | 1…20000 | {40, 1600, 2400} | Texture tiling at three distances |

**Other climate-panel items:** the editor's wind-direction control (`WindSimulationSystem.SetWind`) is not part of the climate prefab and is not exposed here.

**Example:**

```json
POST /map/settings
{ "climate": "TemperateClimate",
  "climateEdit": { "latitude": 46.9, "longitude": -96.8, "seasons": [ { "index": 0, "tempNightDay": [-14, -4] } ] },
  "waterRenderSettings": "DefaultWaterRenderSettings",
  "waterEdit": { "waterColor": "#2f5d57", "absorptionDistance": 6 } }
```

The prefab names here are examples; list the real ones with `/prefabs?category=…`. **Unverified in game:** that a clone made through the bridge is saved into the map exactly like an editor clone. The code path is the same (`Clone` + `AddPrefab` + current), so it is expected to be.

## Terrain textures (`GET` / `POST /map/terrain-textures`, 0.9.31; background job 0.9.32)

This does what the Environment panel's image picker does (`Game.UI.Editor.DualImagePickerPopupField.OnSelectFile`):
1. `DefaultTextureImporter.Import`, with the slot's `[TextureImport]` settings (albedo, or mask map for the normals, wrap Repeat, BC compression);
2. `AssetDatabase.user.AddAsset(AssetImportPipeline.GetPath(…, DataType.RawTexture, user), texture)` and `Save()`;
3. the new `TextureAsset` guid written into the slot's `AssetReference<TextureAsset>` on the current terrain render settings. A read-only prefab is cloned first, as with the panel's "Edit terrain rendering settings" button.
4. `TerrainMaterialSystem.ApplyRenderSettings()`.

The texture lives in the user asset database. The map references it the same way a hand-picked texture is referenced.

```json
POST /map/terrain-textures
{ "textures": [ { "slot": "Grass", "diffuse": "D:/.../grass.png" },
                { "slot": "Rock", "diffuse": "D:/.../rock.png", "normal": "D:/.../rock_n.png" },
                { "slot": "Extra1", "diffuse": "...", "normal": "..." } ] }
```

- **Slots:** `Rock`, `Dirt`, `Grass`, `Extra1`–`Extra4`. Each has `diffuse` and `normal`, and either may be omitted.
- **Formats:** the importer's (`ImageAsset.kExtensions`, png/jpg/tga…).
- **Background job (0.9.32):** POST validates, queues the files and answers `202 {job, queued}` at once. Each frame the bridge advances the job by one file. Decoding and BC compression run on a worker thread, one file at a time; `AddAsset`, `Save` and the field assignment run on the main thread. `ApplyRenderSettings` runs once, after the last file. `"worker": false` imports on the main thread, one file per frame; each such frame still stalls for that file's import (0.9.31 measured about 2 min for 14 files, two of them 4096²). A second POST while a job runs answers 409.
- **`GET /map/terrain-textures/status`:** state (`running`, `done`, `finished with failures`), done/failed counts and a log per file: field, file, result, importSeconds, saveSeconds, guid, asset name and error.
- **GET** lists each slot as `{guid, name, imported}`, or `"default"` when the slot is unset; `imported` is the source file of an import made this game session. It also includes the last job's status.
- **Unverified in game:** whether the native importer is safe off the main thread. If worker-thread items fail, rerun with `"worker": false`.
- **Settings and tiling:** legacy texture format, tiling and the ExtraN numbers stay in `/map/settings terrainEdit`.
- **Unverified in game:** that the imported asset is packed with the saved map like a picker import. The same code path suggests it is.

## Time-of-day and date preview (`GET` / `POST /map/time`, 0.9.31)

This is the editor bottom bar (`EditorBottomBarUISystem`): `PlanetarySystem.overrideTime` + `normalizedTime`, `ClimateSystem.currentDate` override (0–1 through the year), and `cloudiness` override.

```json
POST /map/time { "month": 1, "day": 15, "hour": 13.5, "cloudiness": 0.2 }
POST /map/time { "date": 0.54 }
POST /map/time { "reset": true }
```

It is a preview only and is not saved with the map. The season look follows the date through the climate's seasons.

**0.9.37, climate readout:**
- **Why the HUD looks stuck:** `ClimateSystem.OnUpdate` is the only writer of temperature, precipitation, cloudiness, aurora, fog, season and weather. It runs in the `EditorSimulation` phase, which runs only in simulation steps, i.e. while the clock runs. Paused, the HUD (`ClimateUISystem` binds `ClimateSystem.temperature`) keeps the last sample. `SnowSystem` likewise needs running steps.
- **`POST {..., "settle": seconds, "speed": 1-3}`:** runs the clock for that many real seconds, at most 600, then pauses it again. BridgeSystem enforces the deadline every frame. Snow needs simulated hours to build up.
- **GET (and the POST response):**
  - `live`: ClimateSystem's current values, override state, rain/snow flags, season, freezingTemperature, averageTemperature, temperatureBaseHeight and classification.
  - `sampleAtDate`: `ClimateSystem.SampleClimate(currentDate)`, computed now and valid while paused, with overrides applied.
  - `climate`: the current climate prefab's name, `daysPerYear`, `normalizedDate`, the temperature curve's key count and span, and 12 monthly samples (`temperatureNoon` at t = (m + 0.5)/12, `temperatureMidnight` at t = m/12, plus precipitation, cloudiness and fog).
- **Why those sample points:** `RebuildTemperatureCurves` writes 288 keys, 24 per month unit, so each month unit is one day cycle (a CS2 day is a month).
- **`POST /map/settings` with `climateEdit`:** the response adds the same `climateSample`, so a copy whose curves were not rebuilt shows at once.

**0.9.40–0.9.41, climate overrides:**
- **Where they come from:** Time and Weather Anarchy (PDX 88893) applies its selected profile's overrides in the main menu: temperature, precipitation, cloudiness, fog, aurora and date. In the editor it stops updating them but never clears them. Its "desert" profile (31 °C, no precipitation, lat 40.77 / lon 116.05) pinned A-v9's readings.
- **Editor load:** since 0.9.41 the bridge clears the five weather overrides when an editor map loads, and logs which were on. It leaves the date alone.
- **`POST {"clearOverrides": true}`:** clears the five weather overrides at any time. It also clears the date override unless the same call sets a date. The response lists the overrides that were on.
- **GET:** `overrides` shows the state and value of every override, and `timeWeatherAnarchy` shows the mod's selected profile.
- **Date from `month` + `hour`:**
  - **With `day`:** day-of-month positions the date within the month, as before.
  - **Without `day`:** the date is (month − 1 + hour/24)/12, because a CS2 day is one month unit of the climate curves. So `hour` sets both the sun and the climate's hour of day.
  - **`date`:** sets the curve position directly.

## Saving the editor map (`POST /map/save?name=…[&overwrite=true]`, 0.9.43)

- **What it does:** runs the editor's own save path (`MapPanelSystem.SaveMap` on the game's `SaveLoadGame` task queue, as the Save Map panel does) and answers `202` at once.
- **Status:** `GET /map/save/status` reports `state` (queued, saving, written, done, failed), `error`, the metadata guid, the package path under `Maps` with its size in bytes, and `seconds`. `done` means the save callback ran and a non-empty package exists. A save that throws, such as A-v8's MapData sharing violation, comes back as `failed` with the exception.
- **Names:** a name that already exists answers 409 unless `overwrite=true`. In that case the bridge follows the panel's overwrite branch.
- **`mapEverSaved`:** true when the map came from a save, was saved through `/map/save`, or was saved in the editor UI since it loaded. The UI case is detected through `MapPanelSystem`'s source guid changing.

**Terrain render edits are refused on never-saved maps (0.9.43):**
- **What is refused:** `terrainEdit` or `terrainRenderSettings` in `/map/settings`, and `POST /map/terrain-textures`, on a map whose `mapEverSaved` is false. They answer 409 unless `?force=true`.
- **Why:** the A-v8 and A-v10 native crashes both followed these posts on fresh maps; loaded saves never crashed.
- **Sequence for the runner:** build, then `POST /map/save?name=…` and wait for `done`, then the look posts, then save again.

## Loading an editor map (`POST /map/load?name=…`, 0.9.46)

- **What it does:** loads through the editor's own path, `MapPanelSystem.LoadMap` → `GameManager.Load(Editor, LoadMap, asset)`, without the "progress loss" dialog. It answers `202`.
- **Name:** the name as listed in the Load Map panel. An unknown name answers `404` with some of the known names.
- **Status:** `GET /map/load/status` reports `loading`, then `loaded` once `OnGameLoadingComplete` has run, or `failed` with the error. While the game loads, the bridge answers nothing.
- **Refused:** while a `/map/save` or another load is still running.

**`GET /debug/editor-ui`:** a diagnostic snapshot of the active tool, the action mode, the game state, the map panel's sub-panel with its asset picker items and selection, the editor panel systems' enabled/running state, and the last save and load jobs.

## Starting tiles and starting camera (0.9.60)

- **`GET /map/starting-tiles`:** lists `MapTileSystem`'s start tiles (entity, centre) and the total tile count.
- **`POST /map/starting-tiles`:** `{tiles:[{index,version}]}`, or `{center:[x,z], size:3}` for a size × size block around the tile nearest the point (`dryRun` supported). It replaces the list the Map panel's "Select Starting Tiles" edits; the list is saved with the map. Editor only.
- **`GET/POST /map/camera-start`:** `{x,y,z, angleX, angleY, zoom}` or `{capture: true}`. It writes `CityConfigurationSystem.m_CameraPivot / m_CameraAngle / m_CameraZoom`, exactly what "Capture Camera Properties" writes from the current camera.
- **What "Select Starting Tiles" does:**
  - It sets `MapTilePurchaseSystem.selecting = true`, which makes `SelectionToolSystem` (`SelectionType.MapTiles`) the active tool. Tiles are toggled by clicking or dragging, and the tool copies the start-tile list in and out.
  - Selecting ends on the button again ("Stop selecting…"), on the panel's back/Esc (`OnClose`), or whenever any other code sets a different active tool. That includes the bridge's default-tool switch before imports, `/map/load`, settles and demolish/strip, which then shows Default Tool and no grid.

## Publish prep without clicks (0.9.61)

The Map inspector's buttons do not fire from synthetic clicks, so the bridge calls the same code.
- **`GET /screenshot?ui=false&width=&height=[&save=<path>]`:** renders `Camera.main` through the game's save-preview capture (`ScreenCaptureHelper.CaptureScreenshot` with `SaveGamePreviewSettings`) into a render texture, without the editor UI. `save` writes the PNG to a file and answers JSON; otherwise the PNG is returned.
- **`POST /map/preview {preview?: path, thumbnail?: path}`:** the Map panel's private `OnSelectPreview(string)` / `OnSelectThumbnail(string)`, i.e. `ImageAsset.ImportImageFromPath` into the fields the Save Map dialog embeds. They are written on the next `/map/save`.
- **`POST /debug/invoke {system, method}`:** invokes a parameterless method of a `Game.UI.Editor` system, e.g. `{"system":"MapPanelSystem","method":"CaptureCameraProperties"}` or `"ToggleMapTileSelection"`, what the buttons' actions call. An unknown method answers 404 with the list of available ones.
- **Map name and description:** stay typed by hand. The description lives in the panel's `LocalizationField` per locale; the name is `MapMetadataSystem.mapName`.

## Saving the look prefabs for other maps (`POST /map/look/save-prefabs`, 0.9.62)

- **Request:** `{kinds?: ["climate","water","terrain"], name: "Dakota Falls"}`.
- **What it writes:** renamed clones of the current look prefabs, `<name> Climate` / `<name> Water` / `<name> Terrain`, saved as standalone user prefabs under `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ImportedData\<name>\<clone>.Prefab`. That is the path the editor's Save asset action (`InspectorPanelSystem.SaveAsset`) uses.
- **Pickable at once:** the clones are added to the `PrefabSystem`.
- **Result:** the response lists each clone's guid and path. A name already taken answers a problem for that kind.
- **The map's own copies stay untouched on purpose:** the map save embeds only look prefabs without a persistent asset (`PrefabSystem.SavePrefab`), so saving the map's own copy standalone would drop it from the published map.
- **In another map:** pick them in the Climate / Water / Terrain panels or with `/map/settings {climate | waterRenderSettings | terrainRenderSettings: "<name> Climate"}`. The terrain clone references the user TextureAssets (`ImportedData/TextureAssets`), so that works on the same PC.

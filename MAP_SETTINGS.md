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
  - array elements as `[{index, field: value}]`.
- **Not settable:** `AnimationCurve` fields, the texture references (`AssetReference`) and prefab references. The curves are generated from the seasons.

**ClimatePrefab fields** (summary in the listing):
- **Sun position:** `m_Latitude`, `m_Longitude`, `m_MaxSunElevationAngle`, `m_SunElevationClampStart`.
- **Climate values:** `m_FreezingTemperature` and `m_RandomSeed` (the editor's "Pick new seed" sets 1–99).
- **Weather:** `m_DefaultWeather` and `m_DefaultWeathers` (prefab references, read-only here).
- **Seasons:** `m_Seasons[]` (`ClimateSystem.SeasonInfo`), each with:
  - `m_Prefab` (the season prefab, read-only) and `m_StartTime` (month 0–12);
  - `m_TempNightDay` [night, day] °C and `m_TempDeviationNightDay`;
  - `m_CloudChance`, `m_CloudAmount`, `m_CloudAmountDeviation` (%);
  - `m_PrecipitationChance`, `m_PrecipitationAmount`, `m_PrecipitationAmountDeviation` (%);
  - `m_Turbulence`, `m_AuroraAmount`, `m_AuroraChance`.
- **Generated curves:** `RebuildCurves()` regenerates the temperature, precipitation, cloudiness, aurora and fog curves (the panel's five graphs) from the seasons and the seed. **There are no separate fog, sun or aurora overrides:** fog is derived from cloudiness, precipitation and temperature.
- **Summary values:** the listing's summary adds `temperatureRange`, `averageCloudiness` and `averagePrecipitation`.
- **Day length:** follows from the latitude (`PlanetarySystem`).
- **Snow:** follows from temperature against `m_FreezingTemperature`.

**WaterRenderSettingsPrefab fields:**
- **Colour:** `m_WaterColor`, `m_WaterScatteringColor`.
- **Light through the water:** `m_AbsorptionDistance`, `m_MaxRefractionDistance`, `m_AmbientScattering`, `m_HeightScattering`, `m_DisplacementScattering`, `m_DirectLightTipScattering`, `m_DirectLightBodyScattering`.
- **Surface:** `m_FoamAmount`, `m_FoamFadeRange`, `m_WavesMultiplier`, `m_LakeWavesMultiplier`, `m_MinWaterAmountForWaves`, `m_SeaWindAffectWavesDirection`.
- **Caustics, ripples and smoothness:** `m_CausticsPlaneDistance`, `m_CausticsIntensity`, `m_RipplesWindSpeed`, `m_RipplesChaos`, `m_SmoothnessClose`, `m_SmoothnessDistant`, `m_SmoothnessRange`.
- **CS1 Theme Mixer mapping:** the water colour maps to `m_WaterColor` and `m_WaterScatteringColor`; clarity maps to `m_AbsorptionDistance`.

**TerrainRenderSettingsPrefab fields:**
- **Textures (read-only here):** rock, dirt and grass diffuse/normal, and the `Extra1…4` diffuse/normal textures.
- **Settable per extra layer:** `m_ExtraNDirtOverride`, `m_ExtraNSplatRange`, `m_ExtraNBlurDepth`, `m_ExtraNHeightOffset`.
- **Blending:** `m_BlurDepth` {…}, `m_TriplanarHeightMapBlending`, `m_TriplanarBlendStrengthY`, `m_SplatMultiplier`, `m_SplatPower`.
- **Depth and tiling:** `m_DepthScaleSettings` {…}, `m_TerrainRockTiling` / `m_TerrainGrassTiling` {…}.
- **Format:** `m_UseLegacyTextureFormat`.
- **Texture swaps:** these need a terrain render settings prefab that already carries the textures, selected by name.

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

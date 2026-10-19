# Map settings: what the bridge can set, and where the rest is in the editor

`GET /map/settings` reads, `POST /map/settings` writes (MCP `cs2_map_settings`). Panel names below are the editor's inspector sections (`Editor.INSPECTOR_SECTION_TITLE[...]`) and field labels (`Editor.*` localisation ids) from the decompiled `Game.UI.Editor` panels of CS2 1.6.2.

| Setting | Bridge | Lives in | Editor panel → field |
|---|---|---|---|
| Theme | read/write (`theme`: prefab name, e.g. `European`, `North American`) | `CityConfigurationSystem.defaultTheme` | Map Settings → Theme |
| Climate | read/write (`climate`: climate prefab name) | `ClimateSystem.currentClimate` | Climate Settings → Selected climate |
| Latitude / longitude | read/write, **this session only** | `PlanetarySystem.latitude/longitude` | Not map-specific. `PlanetarySystem` serialises them, but `ClimateSystem.PostDeserialize` overwrites them on every load with the current climate prefab's `m_Latitude`/`m_Longitude`. The climate panel does the same on every change. So a saved map always comes back with the climate's coordinates (seen live: 40.77 / 116.05). A map-specific sun position needs a climate prefab carrying those coordinates, or a mod that re-applies them after load; it is not a map setting. |
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

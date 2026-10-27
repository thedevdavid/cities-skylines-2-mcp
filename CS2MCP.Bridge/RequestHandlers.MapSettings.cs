using System;
using System.Collections.Generic;
using Game.Areas;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.Prefabs.Climate;
using Game.Simulation;
using Game.UI;
using Game.UI.Editor;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// GET/POST /map/settings: the map-level settings that live on ECS systems and
    /// can be read and written from code while the editor is open. Name,
    /// description, preview and thumbnail are private fields of MapPanelSystem and
    /// are set by hand (MAP_SETTINGS.md).
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class Vec3Dto
        {
            public float x;
            public float y;
            public float z;
        }

        public sealed class MapSettingsDto
        {
            public string theme;
            public string climate;
            public string waterRenderSettings;
            public string terrainRenderSettings;
            /// <summary>Field edits on the current climate (cloned first when read-only), e.g. {"latitude": 47.5, "seasons": [{"index": 0, "tempNightDay": [1, 10]}]}.</summary>
            public Newtonsoft.Json.Linq.JObject climateEdit;
            public Newtonsoft.Json.Linq.JObject waterEdit;
            public Newtonsoft.Json.Linq.JObject terrainEdit;
            public float? latitude;
            public float? longitude;
            public float? seaLevel;
            public float? heightScale;
            public bool? leftHandTraffic;
            public bool? naturalDisasters;
            public int? startingYear;
            public int? startingMonth;
            public float? startingTime;
            public bool? currentYearAsStartingYear;
            public bool? mapNameAsCityName;
            public Vec3Dto cameraPivot;
            public float[] cameraAngle;
            public float? cameraZoom;
        }

        private EntityQuery m_ClimatePrefabQuery;
        private bool m_ClimatePrefabQueryCreated;
        private EntityQuery m_ThemePrefabQuery;
        private bool m_ThemePrefabQueryCreated;

        private EntityQuery ClimatePrefabQuery
        {
            get
            {
                if (!m_ClimatePrefabQueryCreated)
                {
                    m_ClimatePrefabQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<ClimateData>());
                    m_ClimatePrefabQueryCreated = true;
                }
                return m_ClimatePrefabQuery;
            }
        }

        private EntityQuery ThemePrefabQuery
        {
            get
            {
                if (!m_ThemePrefabQueryCreated)
                {
                    m_ThemePrefabQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<ThemeData>());
                    m_ThemePrefabQueryCreated = true;
                }
                return m_ThemePrefabQuery;
            }
        }

        /// <summary>Names of the prefabs a query matches, for error messages.</summary>
        private string PrefabNames(EntityQuery query)
        {
            PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            var names = new List<string>();
            using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (prefabs.TryGetPrefab(entity, out PrefabBase prefab))
                    {
                        names.Add(prefab.name);
                    }
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(", ", names);
        }

/// <summary>0.9.43: terrain render edits are refused on a map that was never saved (see MapEverSaved).</summary>
        private BridgeResponse TerrainRenderGuard(BridgeRequest request, bool editor)
        {
            if (!editor || SavedSinceLoad() || (request.TryGetBool("force", out bool force) && force))
            {
                return null;
            }
            return BridgeResponse.Json(new
            {
                error = "terrain render edits (terrainEdit, terrainRenderSettings, terrain textures) are refused on a map that has never been saved",
                why = "Two test sessions crashed natively right after terrain-cs1 on fresh, unsaved maps; on loaded saves the same posts never crashed",
                fix = "POST /map/save?name=<name> first (or load a saved map), then repeat; ?force=true overrides",
            }, 409);
        }

        private BridgeResponse MapSettings(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            var applied = new List<string>();
            var problems = new List<string>();
            if (request.Method == "POST")
            {
                MapSettingsDto body;
                try
                {
                    body = JsonConvert.DeserializeObject<MapSettingsDto>(request.Body ?? "{}");
                }
                catch (Exception e)
                {
                    return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
                }
                if (body != null && (body.terrainEdit != null || !string.IsNullOrEmpty(body.terrainRenderSettings)))
                {
                    BridgeResponse refused = TerrainRenderGuard(request, editor);
                    if (refused != null)
                    {
                        return refused;
                    }
                }
                ApplyMapSettings(body ?? new MapSettingsDto(), editor, applied, problems);
                if (problems.Count > 0 && applied.Count == 0)
                {
                    return BridgeResponse.Json(new { error = "nothing was changed", problems }, 400);
                }
            }
            object settings = ReadMapSettings(editor);
            bool detail = request.Query.TryGetValue("detail", out string rawDetail) && (rawDetail == "1" || rawDetail == "true");
            return BridgeResponse.Json(new
            {
                mode = editor ? "editor" : "game",
                applied = request.Method == "POST" ? applied : null,
                problems = request.Method == "POST" ? problems : null,
                settings,
                look = ReadLook(detail),
                climateSample = request.Method == "POST" && (request.Body ?? "").Contains("climateEdit") ? ClimateMonthly() : null,
                notWritable = new[]
                {
                    "name", "description", "preview", "thumbnail", "startingTiles (count is reported; select them in the editor)",
                },
                note = "see MAP_SETTINGS.md for the editor panel of each setting",
            });
        }

        private object ReadMapSettings(bool editor)
        {
            PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            CityConfigurationSystem city = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            ClimateSystem climate = World.GetOrCreateSystemManaged<ClimateSystem>();
            PlanetarySystem planet = World.GetOrCreateSystemManaged<PlanetarySystem>();
            WaterSystem water = World.GetOrCreateSystemManaged<WaterSystem>();
            TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            MapMetadataSystem metadata = World.GetOrCreateSystemManaged<MapMetadataSystem>();
            MapPanelSystem panel = editor ? World.GetOrCreateSystemManaged<MapPanelSystem>() : null;
            int startTiles = 0;
            try
            {
                startTiles = World.GetOrCreateSystemManaged<MapTileSystem>().GetStartTiles().Length;
            }
            catch
            {
                // not available outside the editor/game world
            }
            object time = null;
            using (EntityQuery timeQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<TimeData>()))
            {
                if (timeQuery.CalculateEntityCount() == 1)
                {
                    TimeData data = timeQuery.GetSingleton<TimeData>();
                    time = new { startingYear = data.m_StartingYear, startingMonth = data.m_StartingMonth + 1, startingTime = data.TimeOffset };
                }
            }
            return new
            {
                mapName = metadata.mapName,
                theme = PrefabNameOf(prefabs, city.defaultTheme),
                climate = PrefabNameOf(prefabs, climate.currentClimate),
                latitude = planet.latitude,
                longitude = planet.longitude,
                seaLevel = water.SeaLevel,
                waterSystem = water.UseLegacyWaterSources ? "legacy" : "new",
                heightScale = terrain.heightScaleOffset.x,
                heightOffset = terrain.heightScaleOffset.y,
                leftHandTraffic = city.leftHandTraffic,
                naturalDisasters = city.naturalDisasters,
                unlockAll = city.unlockAll,
                unlimitedMoney = city.unlimitedMoney,
                unlockMapTiles = city.unlockMapTiles,
                time,
                mapPanel = panel == null ? null : new
                {
                    startingYear = panel.m_StartingYear,
                    startingMonth = panel.m_StartingMonth,
                    startingTime = panel.m_StartingTime,
                    currentYearAsStartingYear = panel.m_CurrentYearAsStartingYear,
                    mapNameAsCityName = panel.m_MapNameAsCityName,
                },
                camera = new
                {
                    pivot = new { x = city.m_CameraPivot.x, y = city.m_CameraPivot.y, z = city.m_CameraPivot.z },
                    angle = new[] { city.m_CameraAngle.x, city.m_CameraAngle.y },
                    zoom = city.m_CameraZoom,
                },
                startingTiles = startTiles,
            };
        }

        private void ApplyMapSettings(MapSettingsDto body, bool editor, List<string> applied, List<string> problems)
        {
            PrefabSystem prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            CityConfigurationSystem city = World.GetOrCreateSystemManaged<CityConfigurationSystem>();

            if (!string.IsNullOrEmpty(body.theme))
            {
                if (prefabs.TryGetPrefab(new PrefabID(nameof(ThemePrefab), body.theme), out PrefabBase theme)
                    && prefabs.TryGetEntity(theme, out Entity themeEntity))
                {
                    city.defaultTheme = themeEntity;
                    applied.Add("theme");
                }
                else
                {
                    problems.Add($"unknown theme prefab '{body.theme}'; available: {PrefabNames(ThemePrefabQuery)}");
                }
            }
            // Climate before latitude/longitude: the climate prefab carries its own
            // coordinates and the climate panel copies them over on its next change.
            if (!string.IsNullOrEmpty(body.climate))
            {
                SelectLook(typeof(ClimatePrefab), body.climate, applied, problems, "climate");
            }
            if (!string.IsNullOrEmpty(body.waterRenderSettings))
            {
                SelectLook(typeof(Game.Prefabs.Water.WaterRenderSettingsPrefab), body.waterRenderSettings, applied, problems, "waterRenderSettings");
            }
            if (!string.IsNullOrEmpty(body.terrainRenderSettings))
            {
                SelectLook(typeof(Game.Prefabs.Terrain.TerrainRenderSettingsPrefab), body.terrainRenderSettings, applied, problems, "terrainRenderSettings");
            }
            if (body.climateEdit != null)
            {
                EditLook(typeof(ClimatePrefab), body.climateEdit, applied, problems, "climateEdit");
            }
            if (body.waterEdit != null)
            {
                EditLook(typeof(Game.Prefabs.Water.WaterRenderSettingsPrefab), body.waterEdit, applied, problems, "waterEdit");
            }
            if (body.terrainEdit != null)
            {
                EditLook(typeof(Game.Prefabs.Terrain.TerrainRenderSettingsPrefab), body.terrainEdit, applied, problems, "terrainEdit");
            }
            PlanetarySystem planet = World.GetOrCreateSystemManaged<PlanetarySystem>();
            if (body.latitude.HasValue)
            {
                planet.latitude = math.clamp(body.latitude.Value, -90f, 90f);
                applied.Add("latitude (this session only: reset from the climate prefab on every load)");
            }
            if (body.longitude.HasValue)
            {
                planet.longitude = math.clamp(body.longitude.Value, -180f, 180f);
                applied.Add("longitude (this session only: reset from the climate prefab on every load)");
            }
            if (body.seaLevel.HasValue)
            {
                World.GetOrCreateSystemManaged<WaterSystem>().SeaLevel = math.clamp(body.seaLevel.Value, 0f, 2000f);
                applied.Add("seaLevel");
            }
            if (body.heightScale.HasValue)
            {
                TerrainSystem terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
                float2 scaleOffset = terrain.heightScaleOffset;
                scaleOffset.x = math.clamp(body.heightScale.Value, 200f, 10000f);
                terrain.SetTerrainProperties(scaleOffset);
                applied.Add("heightScale");
            }
            if (body.leftHandTraffic.HasValue)
            {
                city.leftHandTraffic = body.leftHandTraffic.Value;
                applied.Add("leftHandTraffic");
            }
            if (body.naturalDisasters.HasValue)
            {
                city.naturalDisasters = body.naturalDisasters.Value;
                applied.Add("naturalDisasters");
            }
            if (body.cameraPivot != null)
            {
                city.m_CameraPivot = new float3(body.cameraPivot.x, body.cameraPivot.y, body.cameraPivot.z);
                applied.Add("cameraPivot");
            }
            if (body.cameraAngle != null && body.cameraAngle.Length == 2)
            {
                city.m_CameraAngle = new float2(body.cameraAngle[0], body.cameraAngle[1]);
                applied.Add("cameraAngle");
            }
            if (body.cameraZoom.HasValue)
            {
                city.m_CameraZoom = body.cameraZoom.Value;
                applied.Add("cameraZoom");
            }

            bool timeChanged = body.startingYear.HasValue || body.startingMonth.HasValue || body.startingTime.HasValue;
            if (editor)
            {
                MapPanelSystem panel = World.GetOrCreateSystemManaged<MapPanelSystem>();
                if (body.startingYear.HasValue)
                {
                    panel.m_StartingYear = body.startingYear.Value;
                }
                if (body.startingMonth.HasValue)
                {
                    panel.m_StartingMonth = math.clamp(body.startingMonth.Value, 1, 12);
                }
                if (body.startingTime.HasValue)
                {
                    panel.m_StartingTime = math.clamp(body.startingTime.Value, 0f, 1f);
                }
                if (body.currentYearAsStartingYear.HasValue)
                {
                    panel.m_CurrentYearAsStartingYear = body.currentYearAsStartingYear.Value;
                    applied.Add("currentYearAsStartingYear");
                }
                if (body.mapNameAsCityName.HasValue)
                {
                    panel.m_MapNameAsCityName = body.mapNameAsCityName.Value;
                    applied.Add("mapNameAsCityName");
                }
                if (timeChanged)
                {
                    // As MapPanelSystem.ApplyTime, but directly: the TimeData singleton.
                    using (EntityQuery timeQuery = EntityManager.CreateEntityQuery(ComponentType.ReadWrite<TimeData>()))
                    {
                        if (timeQuery.CalculateEntityCount() == 1)
                        {
                            Entity entity = timeQuery.GetSingletonEntity();
                            TimeData data = EntityManager.GetComponentData<TimeData>(entity);
                            data.m_StartingYear = panel.m_StartingYear;
                            data.m_StartingMonth = (byte)(panel.m_StartingMonth - 1);
                            data.TimeOffset = panel.m_StartingTime;
                            EntityManager.SetComponentData(entity, data);
                            applied.Add("startingYear/Month/Time");
                        }
                        else
                        {
                            problems.Add("no TimeData singleton; starting time not applied");
                        }
                    }
                }
            }
            else if (timeChanged || body.currentYearAsStartingYear.HasValue || body.mapNameAsCityName.HasValue)
            {
                problems.Add("starting year/month/time and the map-panel flags can only be set in the editor");
            }
        }

        private static string PrefabNameOf(PrefabSystem prefabs, Entity entity)
        {
            if (entity == Entity.Null)
            {
                return null;
            }
            return prefabs.TryGetPrefab(entity, out PrefabBase prefab) ? prefab.name : null;
        }
    }
}

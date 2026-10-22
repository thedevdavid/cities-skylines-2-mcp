using System;
using System.Collections.Generic;
using Game.Areas;
using Game.City;
using Game.Rendering;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.60: publish prep without clicks.
    /// GET/POST /map/starting-tiles: the editor's start tiles are MapTileSystem.m_StartTiles (the list
    /// GetStartTiles() returns, serialized with the map; the Map panel's "Select Starting Tiles" is the
    /// SelectionToolSystem in MapTiles mode copying that list in and out). POST replaces the list with
    /// the given tile entities or a size x size block around a point.
    /// GET/POST /map/camera-start: CityConfigurationSystem.m_CameraPivot / m_CameraAngle / m_CameraZoom,
    /// what the Map panel's "Capture Camera Properties" writes from the CameraController.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class StartTilesDto
        {
            public List<EntityRefDto> tiles;
            public float[] center;
            public int? size;
            public bool? dryRun;
        }

        public sealed class EntityRefDto
        {
            public int index;
            public int version;
        }

        private BridgeResponse StartingTiles(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            MapTileSystem tileSystem = World.GetOrCreateSystemManaged<MapTileSystem>();
            NativeList<Entity> start = tileSystem.GetStartTiles();
            var all = new List<(Entity e, float2 c)>();
            using (NativeArray<Entity> tiles = MapTileAreaQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity t in tiles)
                {
                    all.Add((t, EntityManager.GetComponentData<Geometry>(t).m_CenterPosition.xz));
                }
            }
            object Describe(Entity t) => new
            {
                index = t.Index,
                version = t.Version,
                center = EntityManager.Exists(t) && EntityManager.HasComponent<Geometry>(t)
                    ? new { x = Math.Round(EntityManager.GetComponentData<Geometry>(t).m_CenterPosition.x, 1), z = Math.Round(EntityManager.GetComponentData<Geometry>(t).m_CenterPosition.z, 1) }
                    : null,
            };
            if (request.Method == "POST")
            {
                if (!editor)
                {
                    return BridgeResponse.Error(409, "starting tiles are set in the editor");
                }
                StartTilesDto body;
                try
                {
                    body = JsonConvert.DeserializeObject<StartTilesDto>(request.Body ?? "{}") ?? new StartTilesDto();
                }
                catch (Exception e)
                {
                    return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
                }
                var chosen = new List<Entity>();
                if (body.tiles != null && body.tiles.Count > 0)
                {
                    foreach (EntityRefDto r in body.tiles)
                    {
                        var e = new Entity { Index = r.index, Version = r.version };
                        if (!all.Exists(a => a.e == e))
                        {
                            return BridgeResponse.Error(400, $"{r.index}:{r.version} is not a map tile");
                        }
                        chosen.Add(e);
                    }
                }
                else if (body.center != null && body.center.Length == 2 && all.Count > 0)
                {
                    int size = math.clamp(body.size ?? 3, 1, 23);
                    var c = new float2(body.center[0], body.center[1]);
                    (Entity e, float2 c) nearest = all[0];
                    foreach (var t in all)
                    {
                        if (math.distance(t.c, c) < math.distance(nearest.c, c))
                        {
                            nearest = t;
                        }
                    }
                    // Grid pitch: the nearest other tile centre.
                    float pitch = float.MaxValue;
                    foreach (var t in all)
                    {
                        float d = math.distance(t.c, nearest.c);
                        if (d > 1f && d < pitch)
                        {
                            pitch = d;
                        }
                    }
                    float reach = (size - 1) * 0.5f * pitch + pitch * 0.25f;
                    // Even sizes: shift the block toward the requested point.
                    float2 blockCentre = nearest.c;
                    if (size % 2 == 0)
                    {
                        blockCentre += math.sign(c - nearest.c) * pitch * 0.5f;
                    }
                    foreach (var t in all)
                    {
                        float2 d = math.abs(t.c - blockCentre);
                        if (d.x <= reach && d.y <= reach)
                        {
                            chosen.Add(t.e);
                        }
                    }
                }
                else
                {
                    return BridgeResponse.Error(400, "POST {tiles:[{index,version}]} or {center:[x,z], size:3}");
                }
                if (body.dryRun != true)
                {
                    start.Clear();
                    foreach (Entity e in chosen)
                    {
                        start.Add(e);
                    }
                    Mod.Log.Info($"starting tiles set: {chosen.Count}");
                }
                return BridgeResponse.Json(new
                {
                    dryRun = body.dryRun == true,
                    count = chosen.Count,
                    tiles = chosen.ConvertAll(Describe),
                    note = "MapTileSystem start tiles (saved with the map); the Map panel's Select Starting Tiles shows them",
                });
            }
            var current = new List<object>();
            foreach (Entity e in start)
            {
                current.Add(Describe(e));
            }
            return BridgeResponse.Json(new { count = start.Length, totalTiles = all.Count, tiles = current });
        }

        public sealed class CameraStartDto
        {
            public float? x;
            public float? y;
            public float? z;
            public float? angleX;
            public float? angleY;
            public float? zoom;
            public bool? capture;
        }

        private BridgeResponse CameraStart(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            CityConfigurationSystem config = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            var applied = new List<string>();
            if (request.Method == "POST")
            {
                CameraStartDto body;
                try
                {
                    body = JsonConvert.DeserializeObject<CameraStartDto>(request.Body ?? "{}") ?? new CameraStartDto();
                }
                catch (Exception e)
                {
                    return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
                }
                if (body.capture == true)
                {
                    // As the Map panel's "Capture Camera Properties".
                    if (Game.CameraController.TryGet(out Game.CameraController controller))
                    {
                        config.m_CameraPivot = controller.pivot;
                        config.m_CameraAngle = controller.angle;
                        config.m_CameraZoom = controller.zoom;
                        applied.Add("captured from the current camera");
                    }
                    else
                    {
                        return BridgeResponse.Error(409, "no camera controller");
                    }
                }
                if (body.x.HasValue || body.y.HasValue || body.z.HasValue)
                {
                    float3 p = config.m_CameraPivot;
                    config.m_CameraPivot = new float3(body.x ?? p.x, body.y ?? p.y, body.z ?? p.z);
                    applied.Add("pivot");
                }
                if (body.angleX.HasValue || body.angleY.HasValue)
                {
                    float2 a = config.m_CameraAngle;
                    config.m_CameraAngle = new float2(body.angleX ?? a.x, body.angleY ?? a.y);
                    applied.Add("angle");
                }
                if (body.zoom.HasValue)
                {
                    config.m_CameraZoom = body.zoom.Value;
                    applied.Add("zoom");
                }
            }
            return BridgeResponse.Json(new
            {
                applied = request.Method == "POST" ? applied : null,
                pivot = new { x = config.m_CameraPivot.x, y = config.m_CameraPivot.y, z = config.m_CameraPivot.z },
                angle = new { x = config.m_CameraAngle.x, y = config.m_CameraAngle.y },
                zoom = config.m_CameraZoom,
                note = "the map's starting camera (CityConfigurationSystem, saved with the map); capture:true takes the current view like the Map panel button",
            });
        }
    }
}

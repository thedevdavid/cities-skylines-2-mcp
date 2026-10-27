using System;
using System.Collections.Generic;
using System.IO;
using Colossal.IO.AssetDatabase;
using Game.AssetPipeline;
using Game.Prefabs;
using Game.Prefabs.Climate;
using Game.Prefabs.Terrain;
using Game.Prefabs.Water;
using Newtonsoft.Json;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.62: POST /map/look/save-prefabs {kinds?: [climate, water, terrain], name}. Saves renamed
    /// clones of the current look prefabs ("&lt;name&gt; Climate" / "Water" / "Terrain") as standalone user
    /// assets (AssetImportPipeline.GetPath(null, name, clone, Prefab, user) = ImportedData/&lt;name&gt;/),
    /// as the editor's Save asset action (InspectorPanelSystem.SaveAsset) writes them, and registers them
    /// with the PrefabSystem so they are pickable at once. The map's own copies stay untouched: a look
    /// prefab that already has a persistent asset is no longer embedded by the map save
    /// (PrefabSystem.SavePrefab skips it), so players would lose it.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class SaveLookPrefabsDto
        {
            public List<string> kinds;
            public string name;
        }

        private BridgeResponse SaveLookPrefabs(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST")
            {
                return BridgeResponse.Error(405, "POST /map/look/save-prefabs {kinds?: [climate, water, terrain], name: \"My Map\"}");
            }
            SaveLookPrefabsDto body;
            try
            {
                body = JsonConvert.DeserializeObject<SaveLookPrefabsDto>(request.Body ?? "{}") ?? new SaveLookPrefabsDto();
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            if (string.IsNullOrWhiteSpace(body.name) || body.name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return BridgeResponse.Error(400, "name: a file-name-safe base name, e.g. \"My Map\"");
            }
            string baseName = body.name.Trim();
            var kinds = body.kinds != null && body.kinds.Count > 0 ? body.kinds : new List<string> { "climate", "water", "terrain" };
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var saved = new List<object>();
            var problems = new List<string>();
            foreach (string kind in kinds)
            {
                Type type;
                string suffix;
                switch ((kind ?? "").ToLowerInvariant())
                {
                    case "climate":
                        type = typeof(ClimatePrefab);
                        suffix = "Climate";
                        break;
                    case "water":
                        type = typeof(WaterRenderSettingsPrefab);
                        suffix = "Water";
                        break;
                    case "terrain":
                        type = typeof(TerrainRenderSettingsPrefab);
                        suffix = "Terrain";
                        break;
                    default:
                        problems.Add($"unknown kind '{kind}' (climate, water, terrain)");
                        continue;
                }
                PrefabBase current = CurrentLookPrefab(type);
                if (current == null)
                {
                    problems.Add($"{kind}: no current prefab");
                    continue;
                }
                string cloneName = $"{baseName} {suffix}";
                if (prefabSystem.TryGetPrefab(new PrefabID(type.Name, cloneName), out PrefabBase existing) && existing != null)
                {
                    problems.Add($"{kind}: a prefab named '{cloneName}' already exists{(existing.asset != null ? $" ({existing.asset.id.guid})" : "")}; pick another name");
                    continue;
                }
                try
                {
                    PrefabBase clone = current.Clone(cloneName);
                    AssetDataPath path = AssetImportPipeline.GetPath(null, baseName, cloneName, DataType.Prefab, AssetDatabase.user);
                    PrefabAsset asset = AssetDatabase.user.AddAsset(path, clone);
                    asset.Save();
                    prefabSystem.AddPrefab(clone);
                    if (clone is ClimatePrefab climate)
                    {
                        climate.RebuildCurves();
                    }
                    saved.Add(new
                    {
                        kind,
                        source = current.name,
                        prefab = cloneName,
                        guid = asset.id.guid.ToString(),
                        path = Path.Combine(UnityEngine.Application.persistentDataPath, "ImportedData", baseName, cloneName + ".Prefab"),
                    });
                    Mod.Log.Info($"look prefab saved: {cloneName} ({asset.id.guid}) from {current.name}");
                }
                catch (Exception e)
                {
                    problems.Add($"{kind}: {e.GetType().Name}: {e.Message}");
                }
            }
            return BridgeResponse.Json(new
            {
                saved,
                problems,
                note = "standalone user prefabs (ImportedData/<name>/); the map's own copies are unchanged and stay embedded on save. In another map pick them in the Climate / Water / Terrain panels or with /map/settings {climate|waterRenderSettings|terrainRenderSettings: name}. The terrain prefab references the user TextureAssets in ImportedData/TextureAssets (same PC).",
            }, saved.Count == 0 && problems.Count > 0 ? 400 : 200);
        }
    }
}

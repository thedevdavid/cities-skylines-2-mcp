using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Colossal;
using Colossal.AssetPipeline;
using Colossal.AssetPipeline.Importers;
using Colossal.IO.AssetDatabase;
using Game.AssetPipeline;
using Game.Prefabs.Terrain;
using Game.Rendering;
using Game.Simulation;
using Game.UI.Widgets;
using Newtonsoft.Json;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.31: terrain render textures and the editor's time/date preview.
    ///
    /// Textures do what the editor's image picker does (Game.UI.Editor.DualImagePickerPopupField
    /// .OnSelectFile): DefaultTextureImporter.Import with the field's [TextureImport] settings,
    /// AssetDatabase.user.AddAsset(AssetImportPipeline.GetPath(...), texture), Save(), and the
    /// TextureAsset guid into the AssetReference&lt;TextureAsset&gt; field of the (editable copy of the)
    /// current TerrainRenderSettingsPrefab, then TerrainMaterialSystem.ApplyRenderSettings.
    ///
    /// Time is the editor bottom bar (EditorBottomBarUISystem): PlanetarySystem.overrideTime +
    /// normalizedTime, ClimateSystem.currentDate and cloudiness override values.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class TerrainTextureItemDto
        {
            public string slot;
            public string diffuse;
            public string normal;
        }

        public sealed class TerrainTexturesDto
        {
            public List<TerrainTextureItemDto> textures;
            public bool? dryRun;
        }

        private static readonly string[] kTextureSlots = { "Rock", "Dirt", "Grass", "Extra1", "Extra2", "Extra3", "Extra4" };

        /// <summary>GET / POST /map/terrain-textures</summary>
        private BridgeResponse TerrainTextures(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            var applied = new List<string>();
            var problems = new List<string>();
            if (request.Method == "POST")
            {
                TerrainTexturesDto body;
                try
                {
                    body = JsonConvert.DeserializeObject<TerrainTexturesDto>(request.Body ?? "");
                }
                catch (Exception e)
                {
                    return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
                }
                if (body?.textures == null || body.textures.Count == 0)
                {
                    return BridgeResponse.Error(400, "POST {textures:[{slot: Rock|Dirt|Grass|Extra1..Extra4, diffuse: <image path>, normal: <image path>}]}");
                }
                // Validate first.
                foreach (TerrainTextureItemDto t in body.textures)
                {
                    if (t == null || Array.IndexOf(kTextureSlots, t.slot) < 0)
                    {
                        problems.Add($"slot '{t?.slot}': must be one of {string.Join(", ", kTextureSlots)}");
                        continue;
                    }
                    foreach (string p in new[] { t.diffuse, t.normal })
                    {
                        if (!string.IsNullOrEmpty(p) && !File.Exists(p))
                        {
                            problems.Add($"slot {t.slot}: file not found: {p}");
                        }
                    }
                }
                if (problems.Count > 0)
                {
                    return BridgeResponse.Json(new { error = "nothing was changed", problems }, 400);
                }
                if (body.dryRun != true)
                {
                    // An editable copy, as the Environment panel's "Edit terrain rendering settings" button.
                    var noEdits = new Newtonsoft.Json.Linq.JObject();
                    EditLook(typeof(TerrainRenderSettingsPrefab), noEdits, applied, problems, "terrainTextures");
                    var settings = CurrentLookPrefab(typeof(TerrainRenderSettingsPrefab)) as TerrainRenderSettingsPrefab;
                    if (settings == null)
                    {
                        return BridgeResponse.Json(new { error = "no current terrain render settings", problems }, 409);
                    }
                    foreach (TerrainTextureItemDto t in body.textures)
                    {
                        if (!string.IsNullOrEmpty(t.diffuse))
                        {
                            ImportIntoField(settings, $"m_{t.slot}Diffuse", t.diffuse, applied, problems);
                        }
                        if (!string.IsNullOrEmpty(t.normal))
                        {
                            ImportIntoField(settings, $"m_{t.slot}Normal", t.normal, applied, problems);
                        }
                    }
                    World.GetOrCreateSystemManaged<TerrainMaterialSystem>().ApplyRenderSettings();
                }
            }
            var current = CurrentLookPrefab(typeof(TerrainRenderSettingsPrefab)) as TerrainRenderSettingsPrefab;
            var slots = new List<object>();
            foreach (string slot in kTextureSlots)
            {
                slots.Add(new
                {
                    slot,
                    diffuse = current == null ? null : FieldTexture(current, $"m_{slot}Diffuse"),
                    normal = current == null ? null : FieldTexture(current, $"m_{slot}Normal"),
                });
            }
            return BridgeResponse.Json(new
            {
                terrainRenderSettings = current?.name,
                readOnly = current != null && (current.isReadOnly || (current.asset != null && current.asset.isPersistent)),
                applied = request.Method == "POST" ? applied : null,
                problems = request.Method == "POST" ? problems : null,
                slots,
                note = "POST imports each image as a TextureAsset into the user asset database (as the editor's image picker does) " +
                       "and assigns it to an editable copy of the current terrain render settings; save the map to keep it. " +
                       "Extra1..4 are the four material-paint slots (Dry, Lush, Dirt, Sand by default); the m_ExtraN* numbers and tiling stay settable via /map/settings terrainEdit.",
            });
        }

        private static string FieldTexture(TerrainRenderSettingsPrefab settings, string field)
        {
            object value = typeof(TerrainRenderSettingsPrefab).GetField(field)?.GetValue(settings);
            return value?.ToString();
        }

        private void ImportIntoField(TerrainRenderSettingsPrefab settings, string fieldName, string file, List<string> applied, List<string> problems)
        {
            FieldInfo field = typeof(TerrainRenderSettingsPrefab).GetField(fieldName);
            if (field == null)
            {
                problems.Add($"{fieldName}: no such field");
                return;
            }
            var attribute = field.GetCustomAttribute<TextureImportAttribute>();
            try
            {
                var importer = new DefaultTextureImporter();
                DefaultTextureImporter.ImportSettings importSettings = DefaultTextureImporter.ImportSettings.GetDefault();
                if (attribute != null)
                {
                    importSettings.alphaIsTransparency = attribute.alphaIsTransparency;
                    importSettings.compressBC = attribute.bcCompress;
                    importSettings.linearTexture = attribute.isLinear;
                    importSettings.normalMap = attribute.isNormalMap;
                    if (attribute.isNormalMap)
                    {
                        importSettings.overrideCompressionFormat = Colossal.AssetPipeline.Native.NativeTextures.BlockCompressionFormat.BC7;
                    }
                    importSettings.wrapMode = attribute.wrapMode;
                }
                ILocalAssetDatabase user = AssetDatabase.user;
                using (TextureImporter.Texture texture = importer.Import(importSettings, file))
                {
                    AssetDataPath path = AssetImportPipeline.GetPath(null, UncheckedPath.GetLastPathSegmentOrRoot(file, out _),
                        $"{texture.name}_{HashUtils.GetHash(texture, file)}", DataType.RawTexture, user);
                    using (Colossal.IO.AssetDatabase.TextureAsset asset = user.AddAsset(path, texture))
                    {
                        asset.Save();
                        field.SetValue(settings, (AssetReference<Colossal.IO.AssetDatabase.TextureAsset>)asset.id.guid);
                        applied.Add($"{fieldName} <- {Path.GetFileName(file)} ({asset.id.guid})");
                    }
                }
            }
            catch (Exception e)
            {
                problems.Add($"{fieldName}: {e.GetType().Name}: {e.Message}");
            }
        }

        public sealed class MapTimeDto
        {
            public int? month;
            public int? day;
            public float? hour;
            public float? date;
            public float? cloudiness;
            public bool? reset;
        }

        /// <summary>
        /// GET/POST /map/time: the editor bottom bar's time-of-day, date and cloudiness preview.
        /// month 1-12 and day 1-31 (or date 0..1 through the year), hour 0..24, cloudiness 0..1;
        /// reset:true returns to the simulated values. Preview only: not saved with the map.
        /// </summary>
        private BridgeResponse MapTime(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            PlanetarySystem planet = World.GetOrCreateSystemManaged<PlanetarySystem>();
            ClimateSystem climate = World.GetOrCreateSystemManaged<ClimateSystem>();
            var applied = new List<string>();
            if (request.Method == "POST")
            {
                MapTimeDto body;
                try
                {
                    body = JsonConvert.DeserializeObject<MapTimeDto>(request.Body ?? "{}") ?? new MapTimeDto();
                }
                catch (Exception e)
                {
                    return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
                }
                if (body.reset == true)
                {
                    planet.overrideTime = false;
                    climate.currentDate.overrideState = false;
                    climate.cloudiness.overrideState = false;
                    applied.Add("reset");
                }
                if (body.hour.HasValue)
                {
                    planet.overrideTime = true;
                    planet.normalizedTime = math.clamp(body.hour.Value / 24f, 0f, 0.9999f);
                    applied.Add($"time of day {body.hour.Value:F2} h");
                }
                float? date = body.date;
                if (!date.HasValue && body.month.HasValue)
                {
                    int month = math.clamp(body.month.Value, 1, 12);
                    int daysInMonth = DateTime.DaysInMonth(2025, month);
                    int day = math.clamp(body.day ?? 15, 1, daysInMonth);
                    date = ((month - 1) + (day - 1f) / daysInMonth) / 12f;
                }
                if (date.HasValue)
                {
                    climate.currentDate.overrideValue = math.clamp(date.Value, 0f, 0.9999f);
                    climate.currentDate.overrideState = true;
                    applied.Add($"date {date.Value:F4} of the year");
                }
                if (body.cloudiness.HasValue)
                {
                    climate.cloudiness.overrideValue = math.clamp(body.cloudiness.Value, 0f, 1f);
                    climate.cloudiness.overrideState = true;
                    applied.Add($"cloudiness {body.cloudiness.Value:F2}");
                }
            }
            return BridgeResponse.Json(new
            {
                applied = request.Method == "POST" ? applied : null,
                overrideTime = planet.overrideTime,
                normalizedTime = planet.normalizedTime,
                hour = planet.normalizedTime * 24f,
                date = (float)climate.currentDate,
                dateOverridden = climate.currentDate.overrideState,
                cloudiness = (float)climate.cloudiness,
                cloudinessOverridden = climate.cloudiness.overrideState,
                temperature = (float)climate.temperature,
                note = "editor preview (bottom bar): not saved with the map; season visuals follow the date through the climate's seasons",
            });
        }
    }
}

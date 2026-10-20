using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Colossal.Mathematics;
using Game.Prefabs;
using Game.Prefabs.Climate;
using Game.Prefabs.Terrain;
using Game.Prefabs.Water;
using Game.Rendering;
using Game.Simulation;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>
    /// Map look (0.9.23): climate, water render settings and terrain render settings.
    /// Mirrors the editor's Climate panel (ClimatePanelSystem) and Environment panel
    /// (EnvironmentPanelSystem): a prefab picker per kind, and an "Edit" button that clones a
    /// read-only prefab (PrefabBase.Clone + PrefabSystem.AddPrefab) and makes the clone
    /// current, after which every public field of the prefab is editable (EditorGenerator
    /// .BuildMembers). Clones are saved into the map (PrefabSystem.SaveClimate /
    /// SaveWaterRenderSettings / SaveTerrainRenderSettings). After a change the panels call
    /// ClimatePrefab.RebuildCurves + PlanetarySystem latitude/longitude/sunLimit, and
    /// TerrainMaterialSystem.ApplyRenderSettings + WaterRenderSystem.ApplyMaterialParams.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private List<PrefabBase> AllPrefabsOf(Type type)
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var list = new List<PrefabBase>();
            if (typeof(PrefabSystem).GetField("m_Prefabs", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(prefabSystem) is List<PrefabBase> all)
            {
                foreach (PrefabBase p in all)
                {
                    if (p != null && type.IsInstanceOfType(p))
                    {
                        list.Add(p);
                    }
                }
            }
            return list;
        }

        private static Type LookType(string category)
        {
            switch ((category ?? "").ToLowerInvariant())
            {
                case "climate":
                    return typeof(ClimatePrefab);
                case "waterrendersettings":
                case "water":
                    return typeof(WaterRenderSettingsPrefab);
                case "terrainrendersettings":
                case "terrain":
                    return typeof(TerrainRenderSettingsPrefab);
                default:
                    return null;
            }
        }

        /// <summary>GET /prefabs?category=climate|waterRenderSettings|terrainRenderSettings[&amp;detail=1][&amp;query=]</summary>
        private BridgeResponse LookPrefabs(BridgeRequest request, Type type)
        {
            bool detail = request.Query.TryGetValue("detail", out string d) && (d == "1" || d == "true");
            request.Query.TryGetValue("query", out string search);
            Entity current = CurrentLookEntity(type);
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            var results = new List<object>();
            foreach (PrefabBase p in AllPrefabsOf(type))
            {
                if (!string.IsNullOrEmpty(search) && p.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                prefabSystem.TryGetEntity(p, out Entity entity);
                results.Add(new
                {
                    name = p.name,
                    current = entity != Entity.Null && entity == current,
                    readOnly = p.isReadOnly || (p.asset != null && p.asset.isPersistent),
                    asset = p.asset?.ToString(),
                    summary = LookSummary(p),
                    fields = detail ? DumpFields(p, 0) : null,
                });
            }
            return BridgeResponse.Json(new
            {
                category = type.Name,
                count = results.Count,
                note = "names for POST /map/settings {climate | waterRenderSettings | terrainRenderSettings}; edits via climateEdit / waterEdit / terrainEdit (MAP_SETTINGS.md)",
                prefabs = results,
            });
        }

        private static object LookSummary(PrefabBase p)
        {
            if (p is ClimatePrefab c)
            {
                var seasons = new List<object>();
                foreach (ClimateSystem.SeasonInfo s in c.m_Seasons ?? Array.Empty<ClimateSystem.SeasonInfo>())
                {
                    if (s == null)
                    {
                        continue;
                    }
                    seasons.Add(new
                    {
                        season = s.m_Prefab != null ? s.m_Prefab.name : null,
                        startTime = s.m_StartTime,
                        tempNightDay = new[] { s.m_TempNightDay.x, s.m_TempNightDay.y },
                        tempDeviationNightDay = new[] { s.m_TempDeviationNightDay.x, s.m_TempDeviationNightDay.y },
                        cloudChance = s.m_CloudChance,
                        cloudAmount = s.m_CloudAmount,
                        precipitationChance = s.m_PrecipitationChance,
                        precipitationAmount = s.m_PrecipitationAmount,
                        turbulence = s.m_Turbulence,
                        auroraChance = s.m_AuroraChance,
                        auroraAmount = s.m_AuroraAmount,
                    });
                }
                Bounds1 temperature = default;
                float cloud = 0f, precip = 0f;
                try
                {
                    temperature = c.temperatureRange;
                    cloud = c.averageCloudiness;
                    precip = c.averagePrecipitation;
                }
                catch (Exception)
                {
                }
                return new
                {
                    latitude = c.m_Latitude,
                    longitude = c.m_Longitude,
                    maxSunElevationAngle = c.m_MaxSunElevationAngle,
                    sunElevationClampStart = c.m_SunElevationClampStart,
                    freezingTemperature = c.m_FreezingTemperature,
                    randomSeed = c.m_RandomSeed,
                    temperatureRange = new[] { temperature.min, temperature.max },
                    averageCloudiness = cloud,
                    averagePrecipitation = precip,
                    seasons,
                };
            }
            if (p is WaterRenderSettingsPrefab w)
            {
                return new
                {
                    waterColor = ColorArray(w.m_WaterColor),
                    scatteringColor = ColorArray(w.m_WaterScatteringColor),
                    absorptionDistance = w.m_AbsorptionDistance,
                    maxRefractionDistance = w.m_MaxRefractionDistance,
                    foamAmount = w.m_FoamAmount,
                    wavesMultiplier = w.m_WavesMultiplier,
                    lakeWavesMultiplier = w.m_LakeWavesMultiplier,
                };
            }
            return null;
        }

        private static float[] ColorArray(Color c) => new[] { c.r, c.g, c.b, c.a };

        private Entity CurrentLookEntity(Type type)
        {
            if (type == typeof(ClimatePrefab))
            {
                return World.GetOrCreateSystemManaged<ClimateSystem>().currentClimate;
            }
            if (type == typeof(WaterRenderSettingsPrefab))
            {
                return World.GetOrCreateSystemManaged<WaterRenderSystem>().currentWaterRenderSettings;
            }
            if (type == typeof(TerrainRenderSettingsPrefab))
            {
                return World.GetOrCreateSystemManaged<TerrainMaterialSystem>().currentTerrainRenderSettings;
            }
            return Entity.Null;
        }

        private void SetCurrentLookEntity(Type type, Entity entity)
        {
            if (type == typeof(ClimatePrefab))
            {
                World.GetOrCreateSystemManaged<ClimateSystem>().currentClimate = entity;
            }
            else if (type == typeof(WaterRenderSettingsPrefab))
            {
                World.GetOrCreateSystemManaged<WaterRenderSystem>().currentWaterRenderSettings = entity;
            }
            else if (type == typeof(TerrainRenderSettingsPrefab))
            {
                World.GetOrCreateSystemManaged<TerrainMaterialSystem>().currentTerrainRenderSettings = entity;
            }
        }

        private PrefabBase CurrentLookPrefab(Type type)
        {
            Entity e = CurrentLookEntity(type);
            if (e == Entity.Null || !EntityManager.Exists(e))
            {
                return null;
            }
            return World.GetOrCreateSystemManaged<PrefabSystem>().TryGetPrefab(EntityManager.GetComponentData<PrefabData>(e), out PrefabBase p) ? p : null;
        }

        /// <summary>The current look settings for GET /map/settings.</summary>
        private object ReadLook(bool detail)
        {
            var result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, Type> kind in new[]
            {
                new KeyValuePair<string, Type>("climate", typeof(ClimatePrefab)),
                new KeyValuePair<string, Type>("waterRenderSettings", typeof(WaterRenderSettingsPrefab)),
                new KeyValuePair<string, Type>("terrainRenderSettings", typeof(TerrainRenderSettingsPrefab)),
            })
            {
                PrefabBase p = CurrentLookPrefab(kind.Value);
                result[kind.Key] = p == null ? null : new
                {
                    name = p.name,
                    readOnly = p.isReadOnly || (p.asset != null && p.asset.isPersistent),
                    summary = LookSummary(p),
                    fields = detail ? DumpFields(p, 0) : null,
                };
            }
            return result;
        }

        /// <summary>Select by name (any of the three kinds); returns false with a problem if unknown.</summary>
        private bool SelectLook(Type type, string name, List<string> applied, List<string> problems, string label)
        {
            PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            foreach (PrefabBase p in AllPrefabsOf(type))
            {
                if (string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase) && prefabSystem.TryGetEntity(p, out Entity e))
                {
                    SetCurrentLookEntity(type, e);
                    AfterLookChange(p);
                    applied.Add(label);
                    return true;
                }
            }
            var names = new List<string>();
            foreach (PrefabBase p in AllPrefabsOf(type))
            {
                names.Add(p.name);
            }
            problems.Add($"unknown {label} '{name}'; available: {string.Join(", ", names)}");
            return false;
        }

        /// <summary>
        /// Field edits on the current prefab of that kind. A read-only prefab is cloned first,
        /// exactly as the editor's "Edit climate" / "Edit water/terrain rendering settings" buttons do.
        /// </summary>
        private void EditLook(Type type, JObject edits, List<string> applied, List<string> problems, string label)
        {
            PrefabBase current = CurrentLookPrefab(type);
            if (current == null)
            {
                problems.Add($"{label}: no current prefab to edit");
                return;
            }
            if (current.isReadOnly || (current.asset != null && current.asset.isPersistent))
            {
                PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
                PrefabBase clone = current.Clone();
                prefabSystem.AddPrefab(clone);
                if (!prefabSystem.TryGetEntity(clone, out Entity cloneEntity))
                {
                    problems.Add($"{label}: could not register the editable copy of '{current.name}'");
                    return;
                }
                SetCurrentLookEntity(type, cloneEntity);
                applied.Add($"{label}: '{current.name}' is read-only; editing the copy '{clone.name}' (saved into the map, as the editor's Edit button does)");
                current = clone;
            }
            int before = applied.Count;
            AssignFields(current, edits, label, applied, problems);
            if (applied.Count > before)
            {
                AfterLookChange(current);
            }
        }

        private void AfterLookChange(PrefabBase p)
        {
            if (p is ClimatePrefab climate)
            {
                typeof(ClimatePrefab).GetMethod("EnsureSeasonsOrder", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(climate, new object[] { true });
                climate.RebuildCurves();
                PlanetarySystem planet = World.GetOrCreateSystemManaged<PlanetarySystem>();
                planet.latitude = climate.m_Latitude;
                planet.longitude = climate.m_Longitude;
                planet.sunLimit = climate.sunLimitRadians;
            }
            else if (p is WaterRenderSettingsPrefab)
            {
                World.GetOrCreateSystemManaged<WaterRenderSystem>().ApplyMaterialParams();
            }
            else if (p is TerrainRenderSettingsPrefab)
            {
                World.GetOrCreateSystemManaged<TerrainMaterialSystem>().ApplyRenderSettings();
            }
        }

        // ---- reflection: dump and assign public fields ----

        private static FieldInfo FindField(Type type, string key)
        {
            string k = key.StartsWith("m_", StringComparison.Ordinal) ? key.Substring(2) : key;
            foreach (FieldInfo f in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                string n = f.Name.StartsWith("m_", StringComparison.Ordinal) ? f.Name.Substring(2) : f.Name;
                if (string.Equals(n, k, StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
            return null;
        }

        private static object DumpFields(object obj, int depth)
        {
            if (obj == null)
            {
                return null;
            }
            var result = new Dictionary<string, object>();
            foreach (FieldInfo f in obj.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (f.DeclaringType == typeof(PrefabBase) || f.DeclaringType == typeof(UnityEngine.Object) || f.DeclaringType == typeof(ScriptableObject))
                {
                    continue;
                }
                result[f.Name] = DumpValue(f.GetValue(obj), depth + 1);
            }
            return result;
        }

        private static object DumpValue(object v, int depth)
        {
            switch (v)
            {
                case null:
                    return null;
                case float _:
                case double _:
                case int _:
                case bool _:
                case string _:
                    return v;
                case Enum e:
                    return e.ToString();
                case float2 f2:
                    return new[] { f2.x, f2.y };
                case float3 f3:
                    return new[] { f3.x, f3.y, f3.z };
                case double2 d2:
                    return new[] { d2.x, d2.y };
                case Vector2 v2:
                    return new[] { v2.x, v2.y };
                case Vector4 v4:
                    return new[] { v4.x, v4.y, v4.z, v4.w };
                case Color c:
                    return ColorArray(c);
                case AnimationCurve curve:
                    return new { curve = true, keys = curve.length, note = "generated from the seasons (RebuildCurves); edit the seasons" };
                case PrefabBase p:
                    return p.name;
                case UnityEngine.Object o:
                    return o.name;
            }
            Type t = v.GetType();
            if (t.Name.StartsWith("AssetReference", StringComparison.Ordinal))
            {
                return v.ToString();
            }
            if (v is IList list)
            {
                var items = new List<object>();
                for (int i = 0; i < list.Count; i++)
                {
                    items.Add(depth < 4 ? DumpValue(list[i], depth + 1) : "…");
                }
                return items;
            }
            if (depth < 4 && (t.IsClass || (t.IsValueType && !t.IsPrimitive)))
            {
                return DumpFields(v, depth);
            }
            return v.ToString();
        }

        private void AssignFields(object target, JObject edits, string path, List<string> applied, List<string> problems)
        {
            foreach (KeyValuePair<string, JToken> edit in edits)
            {
                FieldInfo field = FindField(target.GetType(), edit.Key);
                string where = $"{path}.{edit.Key}";
                if (field == null)
                {
                    problems.Add($"{where}: no such field on {target.GetType().Name}");
                    continue;
                }
                try
                {
                    object current = field.GetValue(target);
                    Type ft = field.FieldType;
                    Type elementType = ft.IsArray ? ft.GetElementType() : null;
                    if (elementType != null && !elementType.IsPrimitive && !typeof(PrefabBase).IsAssignableFrom(elementType)
                        && edit.Value is JArray array && array.Count > 0 && array[0] is JObject)
                    {
                        // Element edits: [{index, field: value, ...}]; index defaults to the position.
                        // An index at or past the end appends a new element (class defaults);
                        // {index, remove: true} deletes one.
                        var items = new List<object>();
                        if (current is Array existing)
                        {
                            foreach (object o in existing)
                            {
                                items.Add(o);
                            }
                        }
                        var removed = new HashSet<int>();
                        for (int i = 0; i < array.Count; i++)
                        {
                            if (!(array[i] is JObject element))
                            {
                                continue;
                            }
                            int index = element.Value<int?>("index") ?? i;
                            element.Remove("index");
                            if (element.Value<bool?>("remove") == true)
                            {
                                if (index >= 0 && index < items.Count)
                                {
                                    removed.Add(index);
                                    applied.Add($"{where}[{index}] removed");
                                }
                                else
                                {
                                    problems.Add($"{where}[{index}]: nothing to remove (0..{items.Count - 1})");
                                }
                                continue;
                            }
                            if (index < 0)
                            {
                                problems.Add($"{where}[{index}]: negative index");
                                continue;
                            }
                            while (index >= items.Count)
                            {
                                items.Add(elementType.IsValueType || elementType.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(elementType) : null);
                                applied.Add($"{where}[{items.Count - 1}] added");
                            }
                            object item = items[index] ?? Activator.CreateInstance(elementType);
                            AssignFields(item, element, $"{where}[{index}]", applied, problems);
                            items[index] = item;
                        }
                        var result = new List<object>();
                        for (int i = 0; i < items.Count; i++)
                        {
                            if (!removed.Contains(i))
                            {
                                result.Add(items[i]);
                            }
                        }
                        Array updated = Array.CreateInstance(elementType, result.Count);
                        for (int i = 0; i < result.Count; i++)
                        {
                            updated.SetValue(result[i], i);
                        }
                        field.SetValue(target, updated);
                        continue;
                    }
                    if (elementType != null && typeof(PrefabBase).IsAssignableFrom(elementType) && edit.Value is JArray names)
                    {
                        Array refs = Array.CreateInstance(elementType, names.Count);
                        bool ok = true;
                        for (int i = 0; i < names.Count; i++)
                        {
                            PrefabBase found = FindPrefabByName(elementType, names[i].Value<string>());
                            if (found == null)
                            {
                                problems.Add($"{where}[{i}]: no {elementType.Name} named '{names[i]}'");
                                ok = false;
                                break;
                            }
                            refs.SetValue(found, i);
                        }
                        if (ok)
                        {
                            field.SetValue(target, refs);
                            applied.Add(where);
                        }
                        continue;
                    }
                    if (typeof(PrefabBase).IsAssignableFrom(ft) && edit.Value.Type == JTokenType.String)
                    {
                        PrefabBase found = FindPrefabByName(ft, edit.Value.Value<string>());
                        if (found == null)
                        {
                            var available = new List<string>();
                            foreach (PrefabBase p in AllPrefabsOf(ft))
                            {
                                available.Add(p.name);
                            }
                            problems.Add($"{where}: no {ft.Name} named '{edit.Value}'; available: {string.Join(", ", available)}");
                            continue;
                        }
                        field.SetValue(target, found);
                        applied.Add(where);
                        continue;
                    }
                    if (edit.Value is JObject nested && current != null && !(current is Color) && !ft.IsPrimitive
                        && !(current is float2) && !(current is float3) && !(current is double2)
                        && !(current is Vector2) && !(current is Vector4))
                    {
                        AssignFields(current, nested, where, applied, problems);
                        if (ft.IsValueType)
                        {
                            field.SetValue(target, current);
                        }
                        continue;
                    }
                    if (!TryConvert(edit.Value, ft, out object value))
                    {
                        problems.Add($"{where}: cannot set a {ft.Name} from {edit.Value.Type} (supported: numbers, bools, strings, enums, [x,y] float2/double2, [x,y,z] float3, colours as [r,g,b(,a)] or \"#rrggbb\")");
                        continue;
                    }
                    field.SetValue(target, value);
                    applied.Add(where);
                }
                catch (Exception e)
                {
                    problems.Add($"{where}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        private static bool TryConvert(JToken token, Type type, out object value)
        {
            value = null;
            try
            {
                if (type == typeof(float))
                {
                    value = token.Value<float>();
                }
                else if (type == typeof(double))
                {
                    value = token.Value<double>();
                }
                else if (type == typeof(int))
                {
                    value = token.Value<int>();
                }
                else if (type == typeof(bool))
                {
                    value = token.Value<bool>();
                }
                else if (type == typeof(string))
                {
                    value = token.Value<string>();
                }
                else if (type.IsEnum)
                {
                    value = Enum.Parse(type, token.Value<string>(), true);
                }
                else if (type == typeof(float2) && token is JArray a2 && a2.Count == 2)
                {
                    value = new float2(a2[0].Value<float>(), a2[1].Value<float>());
                }
                else if (type == typeof(double2) && token is JArray d2 && d2.Count == 2)
                {
                    value = new double2(d2[0].Value<double>(), d2[1].Value<double>());
                }
                else if (type == typeof(Vector2) && token is JArray v2 && v2.Count == 2)
                {
                    value = new Vector2(v2[0].Value<float>(), v2[1].Value<float>());
                }
                else if (type == typeof(Vector4) && token is JArray v4 && v4.Count == 4)
                {
                    value = new Vector4(v4[0].Value<float>(), v4[1].Value<float>(), v4[2].Value<float>(), v4[3].Value<float>());
                }
                else if (type == typeof(float3) && token is JArray a3 && a3.Count == 3)
                {
                    value = new float3(a3[0].Value<float>(), a3[1].Value<float>(), a3[2].Value<float>());
                }
                else if (type == typeof(Color))
                {
                    if (token is JArray c && (c.Count == 3 || c.Count == 4))
                    {
                        value = new Color(c[0].Value<float>(), c[1].Value<float>(), c[2].Value<float>(), c.Count == 4 ? c[3].Value<float>() : 1f);
                    }
                    else if (token.Type == JTokenType.String && ColorUtility.TryParseHtmlString(token.Value<string>(), out Color parsed))
                    {
                        value = parsed;
                    }
                }
            }
            catch (Exception)
            {
                value = null;
            }
            return value != null;
        }
        private PrefabBase FindPrefabByName(Type type, string name)
        {
            foreach (PrefabBase p in AllPrefabsOf(type))
            {
                if (string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }
            return null;
        }

        /// <summary>
        /// GET /map/look/schema: every field of ClimatePrefab (+ SeasonInfo), WaterRenderSettingsPrefab and
        /// TerrainRenderSettingsPrefab (+ nested settings) with type, editor range ([Range]/[RangeN]),
        /// default (from a fresh instance), tooltip, whether the editor shows it and whether the bridge writes it.
        /// </summary>
        private BridgeResponse LookSchema()
        {
            return BridgeResponse.Json(new
            {
                climate = SchemaOf(typeof(ClimatePrefab)),
                season = SchemaOf(typeof(ClimateSystem.SeasonInfo)),
                waterRenderSettings = SchemaOf(typeof(WaterRenderSettingsPrefab)),
                terrainRenderSettings = SchemaOf(typeof(TerrainRenderSettingsPrefab)),
                terrainBlurDepth = SchemaOf(typeof(TerrainBlurDepthData)),
                terrainDepthScale = SchemaOf(typeof(TerrainDepthScaleSetting)),
                terrainTiling = SchemaOf(typeof(TerrainTilingData)),
                note = "write with POST /map/settings {climateEdit | waterEdit | terrainEdit}; keys with or without m_; seasons as [{index, ...}] (append past the end, {index, remove:true} deletes)",
            });
        }

        private static List<object> SchemaOf(Type type)
        {
            object defaults = null;
            try
            {
                defaults = typeof(ScriptableObject).IsAssignableFrom(type) ? ScriptableObject.CreateInstance(type) : Activator.CreateInstance(type);
            }
            catch (Exception)
            {
            }
            var fields = new List<object>();
            foreach (FieldInfo f in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (f.DeclaringType == typeof(PrefabBase) || f.DeclaringType == typeof(UnityEngine.Object) || f.DeclaringType == typeof(ScriptableObject))
                {
                    continue;
                }
                object range = null;
                var r = f.GetCustomAttribute<RangeAttribute>();
                if (r != null)
                {
                    range = new[] { r.min, r.max };
                }
                var rn = f.GetCustomAttribute<Game.UI.Widgets.RangeNAttribute>();
                if (rn != null)
                {
                    range = new { min = new[] { rn.min.x, rn.min.y }, max = new[] { rn.max.x, rn.max.y } };
                }
                Type ft = f.FieldType;
                bool writable = ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft == typeof(float2) || ft == typeof(double2) || ft == typeof(float3)
                    || ft == typeof(Color) || ft == typeof(Vector2) || ft == typeof(Vector4) || typeof(PrefabBase).IsAssignableFrom(ft)
                    || (ft.IsArray && ft.GetElementType() != null && !ft.GetElementType().IsPrimitive)
                    || (ft.IsClass && ft.Namespace != null && ft.Namespace.StartsWith("Game", StringComparison.Ordinal));
                if (ft == typeof(AnimationCurve) || ft.Name.StartsWith("AssetReference", StringComparison.Ordinal))
                {
                    writable = false;
                }
                fields.Add(new
                {
                    field = f.Name,
                    type = ft.IsArray ? ft.GetElementType()?.Name + "[]" : ft.Name,
                    range,
                    @default = defaults == null ? null : DumpValue(f.GetValue(defaults), 3),
                    tooltip = f.GetCustomAttribute<TooltipAttribute>()?.tooltip,
                    shownInEditor = f.GetCustomAttribute<Game.UI.Widgets.HideInEditorAttribute>() == null,
                    writable,
                });
            }
            if (defaults is UnityEngine.Object o)
            {
                UnityEngine.Object.DestroyImmediate(o);
            }
            return fields;
        }
    }
}

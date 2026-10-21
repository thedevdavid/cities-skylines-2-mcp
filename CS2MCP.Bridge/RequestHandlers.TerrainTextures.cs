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
            public bool? worker;
        }

        private static readonly string[] kTextureSlots = { "Rock", "Dirt", "Grass", "Extra1", "Extra2", "Extra3", "Extra4" };

        /// <summary>One file of a terrain texture job (0.9.32).</summary>
        private sealed class TextureImportItem
        {
            public string Field;
            public string File;
            public string State = "queued"; // queued, importing, done, failed
            public string Guid;
            public string AssetName;
            public string Error;
            public double ImportSeconds;
            public double SaveSeconds;
            public System.Threading.Tasks.Task<TextureImporter.Texture> Task;
            public System.Diagnostics.Stopwatch Clock;
        }

        private sealed class TextureImportJob
        {
            public int Id;
            public bool Worker;
            public DateTime Started;
            public DateTime? Finished;
            public List<TextureImportItem> Items = new List<TextureImportItem>();
            public List<string> Problems = new List<string>();
            public bool Applied;
            public bool Done => Finished.HasValue;
        }

        private static int s_TextureJobCounter;
        private TextureImportJob m_TextureJob;
        // guid -> "file (seconds, result)" for the GET's names; survives jobs, not game restarts.
        private readonly Dictionary<string, string> m_ImportedTextureNames = new Dictionary<string, string>();

        /// <summary>
        /// GET / POST /map/terrain-textures. 0.9.32: POST queues the files and returns a job id at once;
        /// BridgeSystem advances the job every frame (PumpTerrainTextureJob). Decoding/compressing runs on a
        /// worker thread one file at a time (worker:false = on the main thread, one file per frame);
        /// AddAsset/Save/field assignment stay on the main thread. GET /map/terrain-textures/status.
        /// </summary>
        private BridgeResponse TerrainTextures(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool texturesEditor, out BridgeResponse error))
            {
                return error;
            }
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
                    return BridgeResponse.Error(400, "POST {textures:[{slot: Rock|Dirt|Grass|Extra1..Extra4, diffuse: <image path>, normal: <image path>}], worker?: true}");
                }
                BridgeResponse refused = TerrainRenderGuard(request, texturesEditor);
                if (refused != null)
                {
                    return refused;
                }
                if (m_TextureJob != null && !m_TextureJob.Done)
                {
                    return BridgeResponse.Json(new { error = "a terrain texture job is still running", job = TextureJobStatus(m_TextureJob) }, 409);
                }
                var problems = new List<string>();
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
                var job = new TextureImportJob { Id = ++s_TextureJobCounter, Worker = body.worker != false, Started = DateTime.Now };
                foreach (TerrainTextureItemDto t in body.textures)
                {
                    if (!string.IsNullOrEmpty(t.diffuse))
                    {
                        job.Items.Add(new TextureImportItem { Field = $"m_{t.slot}Diffuse", File = t.diffuse });
                    }
                    if (!string.IsNullOrEmpty(t.normal))
                    {
                        job.Items.Add(new TextureImportItem { Field = $"m_{t.slot}Normal", File = t.normal });
                    }
                }
                if (body.dryRun == true)
                {
                    return BridgeResponse.Json(new { dryRun = true, files = job.Items.ConvertAll(i => new { field = i.Field, file = i.File }) });
                }
                // An editable copy, as the Environment panel's "Edit terrain rendering settings" button.
                var applied = new List<string>();
                EditLook(typeof(TerrainRenderSettingsPrefab), new Newtonsoft.Json.Linq.JObject(), applied, job.Problems, "terrainTextures");
                if (!(CurrentLookPrefab(typeof(TerrainRenderSettingsPrefab)) is TerrainRenderSettingsPrefab))
                {
                    return BridgeResponse.Json(new { error = "no current terrain render settings", problems = job.Problems }, 409);
                }
                m_TextureJob = job;
                Mod.Log.Info($"terrain texture job {job.Id}: {job.Items.Count} files queued ({(job.Worker ? "worker thread" : "main thread, one per frame")})");
                return BridgeResponse.Json(new
                {
                    job = job.Id,
                    queued = job.Items.Count,
                    status = "/map/terrain-textures/status",
                    note = "the files import one at a time in the background; poll GET /map/terrain-textures/status until done, then save the map",
                }, 202);
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
                slots,
                lastJob = m_TextureJob == null ? null : TextureJobStatus(m_TextureJob),
                note = "POST imports each image as a TextureAsset into the user asset database (as the editor's image picker does) " +
                       "and assigns it to an editable copy of the current terrain render settings; save the map to keep it. " +
                       "Extra1..4 are the four material-paint slots (Dry, Lush, Dirt, Sand by default); the m_ExtraN* numbers and tiling stay settable via /map/settings terrainEdit.",
            });
        }

        /// <summary>GET /map/terrain-textures/status</summary>
        private BridgeResponse TerrainTexturesStatus(BridgeRequest request)
        {
            if (m_TextureJob == null)
            {
                return BridgeResponse.Json(new { job = (int?)null, note = "no terrain texture job since the game started" });
            }
            return BridgeResponse.Json(TextureJobStatus(m_TextureJob));
        }

        private static object TextureJobStatus(TextureImportJob job)
        {
            int done = job.Items.FindAll(i => i.State == "done").Count;
            int failed = job.Items.FindAll(i => i.State == "failed").Count;
            return new
            {
                job = job.Id,
                state = job.Done ? (failed > 0 ? "finished with failures" : "done") : "running",
                worker = job.Worker,
                total = job.Items.Count,
                done,
                failed,
                renderSettingsApplied = job.Applied,
                seconds = Math.Round(((job.Finished ?? DateTime.Now) - job.Started).TotalSeconds, 1),
                log = job.Items.ConvertAll(i => new
                {
                    field = i.Field,
                    file = i.File,
                    result = i.State,
                    importSeconds = Math.Round(i.ImportSeconds, 2),
                    saveSeconds = Math.Round(i.SaveSeconds, 2),
                    guid = i.Guid,
                    asset = i.AssetName,
                    error = i.Error,
                }),
                problems = job.Problems,
            };
        }

        /// <summary>
        /// Called by BridgeSystem every frame: advances the terrain texture job by at most one file.
        /// </summary>
        internal void PumpTerrainTextureJob()
        {
            TextureImportJob job = m_TextureJob;
            if (job == null || job.Done)
            {
                return;
            }
            var settings = CurrentLookPrefab(typeof(TerrainRenderSettingsPrefab)) as TerrainRenderSettingsPrefab;
            TextureImportItem item = job.Items.Find(i => i.State == "importing") ?? job.Items.Find(i => i.State == "queued");
            if (item == null || settings == null)
            {
                if (settings == null)
                {
                    job.Problems.Add("the terrain render settings went away (map unloaded?); remaining files skipped");
                    foreach (TextureImportItem i in job.Items)
                    {
                        if (i.State == "queued" || i.State == "importing")
                        {
                            i.State = "failed";
                            i.Error = "skipped";
                        }
                    }
                }
                else
                {
                    try
                    {
                        World.GetOrCreateSystemManaged<TerrainMaterialSystem>().ApplyRenderSettings();
                        job.Applied = true;
                    }
                    catch (Exception e)
                    {
                        job.Problems.Add($"ApplyRenderSettings: {e.GetType().Name}: {e.Message}");
                    }
                }
                job.Finished = DateTime.Now;
                Mod.Log.Info($"terrain texture job {job.Id} finished in {(job.Finished.Value - job.Started).TotalSeconds:F1} s");
                return;
            }
            FieldInfo field = typeof(TerrainRenderSettingsPrefab).GetField(item.Field);
            if (field == null)
            {
                item.State = "failed";
                item.Error = "no such field";
                return;
            }
            if (item.State == "queued")
            {
                DefaultTextureImporter.ImportSettings importSettings = ImportSettingsFor(field);
                string file = item.File;
                item.Clock = System.Diagnostics.Stopwatch.StartNew();
                item.State = "importing";
                if (job.Worker)
                {
                    item.Task = System.Threading.Tasks.Task.Run(() => new DefaultTextureImporter().Import(importSettings, file));
                    return;
                }
                try
                {
                    item.Task = System.Threading.Tasks.Task.FromResult(new DefaultTextureImporter().Import(importSettings, file));
                }
                catch (Exception e)
                {
                    Fail(item, e);
                    return;
                }
            }
            if (!item.Task.IsCompleted)
            {
                return;
            }
            item.ImportSeconds = item.Clock.Elapsed.TotalSeconds;
            if (item.Task.IsFaulted || item.Task.IsCanceled)
            {
                Fail(item, item.Task.Exception?.GetBaseException() ?? new OperationCanceledException());
                return;
            }
            var save = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                ILocalAssetDatabase user = AssetDatabase.user;
                using (TextureImporter.Texture texture = item.Task.Result)
                {
                    AssetDataPath path = AssetImportPipeline.GetPath(null, UncheckedPath.GetLastPathSegmentOrRoot(item.File, out _),
                        $"{texture.name}_{HashUtils.GetHash(texture, item.File)}", DataType.RawTexture, user);
                    // 0.9.34: the same file gives the same path; never AddAsset over a texture asset
                    // that is already loaded and in use by the terrain (A-v8 job 2 re-imported all 14).
                    if (user.Exists(path, out Colossal.IO.AssetDatabase.TextureAsset existing))
                    {
                        field.SetValue(settings, (AssetReference<Colossal.IO.AssetDatabase.TextureAsset>)existing.id.guid);
                        item.Guid = existing.id.guid.ToString();
                        item.AssetName = existing.name;
                        item.Error = "reused the existing asset (same file and hash)";
                        m_ImportedTextureNames[item.Guid] = $"{existing.name} <- {Path.GetFileName(item.File)}";
                        item.State = "done";
                        return;
                    }
                    using (Colossal.IO.AssetDatabase.TextureAsset asset = user.AddAsset(path, texture))
                    {
                        asset.Save();
                        field.SetValue(settings, (AssetReference<Colossal.IO.AssetDatabase.TextureAsset>)asset.id.guid);
                        item.Guid = asset.id.guid.ToString();
                        item.AssetName = asset.name;
                        m_ImportedTextureNames[item.Guid] = $"{asset.name} <- {Path.GetFileName(item.File)}";
                    }
                }
                item.State = "done";
            }
            catch (Exception e)
            {
                Fail(item, e);
            }
            finally
            {
                item.Task = null;
                item.SaveSeconds = save.Elapsed.TotalSeconds;
            }
            Mod.Log.Info($"terrain texture job {job.Id}: {item.Field} <- {Path.GetFileName(item.File)} {item.State} (import {item.ImportSeconds:F1} s, save {item.SaveSeconds:F1} s){(item.Error != null ? ": " + item.Error : "")}");
        }

        private static void Fail(TextureImportItem item, Exception e)
        {
            item.State = "failed";
            item.Error = $"{e.GetType().Name}: {e.Message}";
            item.Task = null;
            if (item.Clock != null && item.ImportSeconds == 0)
            {
                item.ImportSeconds = item.Clock.Elapsed.TotalSeconds;
            }
        }

        private static DefaultTextureImporter.ImportSettings ImportSettingsFor(FieldInfo field)
        {
            DefaultTextureImporter.ImportSettings importSettings = DefaultTextureImporter.ImportSettings.GetDefault();
            var attribute = field.GetCustomAttribute<TextureImportAttribute>();
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
            return importSettings;
        }

        /// <summary>guid and asset name of an AssetReference&lt;TextureAsset&gt; field, or "default" when unset.</summary>
        private object FieldTexture(TerrainRenderSettingsPrefab settings, string field)
        {
            object value = typeof(TerrainRenderSettingsPrefab).GetField(field)?.GetValue(settings);
            if (!(value is AssetReference reference) || !reference.guid.isValid)
            {
                return "default";
            }
            string guid = reference.guid.ToString();
            string name = null;
            if (AssetDatabase.global.TryGetAsset(reference.guid, out Colossal.IO.AssetDatabase.TextureAsset asset))
            {
                name = asset.name;
            }
            m_ImportedTextureNames.TryGetValue(guid, out string imported);
            return new { guid, name, imported };
        }


        /// <summary>Switches the climate overrides off; returns the names of those that were on.</summary>
        internal static List<string> ClearClimateOverrides(ClimateSystem climate, bool includeDate)
        {
            var cleared = new List<string>();
            void Clear(string name, Game.OverridableProperty<float> property)
            {
                if (property.overrideState)
                {
                    cleared.Add($"{name} {property.overrideValue:0.##}");
                    property.overrideState = false;
                }
            }
            Clear("temperature", climate.temperature);
            Clear("precipitation", climate.precipitation);
            Clear("cloudiness", climate.cloudiness);
            Clear("fog", climate.fog);
            Clear("aurora", climate.aurora);
            if (includeDate)
            {
                Clear("date", climate.currentDate);
            }
            return cleared;
        }

        private object CurveAtDate(ClimateSystem climate)
        {
            if (climate.currentClimate == Unity.Entities.Entity.Null)
            {
                return null;
            }
            var prefab = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>().GetPrefab<Game.Prefabs.Climate.ClimatePrefab>(climate.currentClimate);
            return SampleObject(climate.SampleClimate(prefab, (float)climate.currentDate));
        }

        /// <summary>
        /// 0.9.40: Time and Weather Anarchy (PDX 88893) re-applies its selected profile's custom
        /// temperature / precipitation / date every frame; A-v9's season test read its "desert"
        /// profile (31 °C, day 193) on every date.
        /// </summary>
        private static object TimeWeatherAnarchyProfile()
        {
            bool loaded = false;
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name.IndexOf("TimeWeatherAnarchy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    loaded = true;
                    break;
                }
            }
            if (!loaded)
            {
                return null;
            }
            try
            {
                string root = Path.Combine(UnityEngine.Application.persistentDataPath, "ModsSettings");
                var settings = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(root, "TimeWeatherAnarchy.coc")).Substring(
                    File.ReadAllText(Path.Combine(root, "TimeWeatherAnarchy.coc")).IndexOf('{')));
                string id = (string)settings["SelectedProfile"];
                string profilePath = Path.Combine(root, "TimeWeatherAnarchy", "Profiles", id + ".json");
                var profile = File.Exists(profilePath) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(profilePath)) : null;
                return new
                {
                    loaded = true,
                    selectedProfile = (string)profile?["Name"] ?? id,
                    customTemperature = (bool?)profile?["EnableCustomTemperature"],
                    temperature = (float?)profile?["Temperature"],
                    customPrecipitation = (bool?)profile?["EnableCustomPrecipitation"],
                    timeOption = (int?)profile?["TimeOption"],
                    weatherOption = (int?)profile?["WeatherOption"],
                    dayOfTheYear = (int?)profile?["DayOfTheYear"],
                    note = "settings file as saved; select the 'Main' (default) profile in the mod's options to stop its overrides",
                };
            }
            catch (Exception e)
            {
                return new { loaded = true, error = e.Message };
            }
        }

        private static object SampleObject(ClimateSystem.ClimateSample c)
        {
            return new
            {
                temperature = Math.Round(c.temperature, 2),
                precipitation = Math.Round(c.precipitation, 3),
                cloudiness = Math.Round(c.cloudiness, 3),
                aurora = Math.Round(c.aurora, 3),
                fog = Math.Round(c.fog, 3),
            };
        }

        /// <summary>
        /// The current climate prefab and its curves sampled at mid-month (raw curves, no overrides),
        /// to see whether an edited "(copy)" was rebuilt.
        /// </summary>
        private object ClimateMonthly()
        {
            ClimateSystem climate = World.GetOrCreateSystemManaged<ClimateSystem>();
            TimeSystem time = World.GetOrCreateSystemManaged<TimeSystem>();
            if (climate.currentClimate == Unity.Entities.Entity.Null)
            {
                return new { error = "no current climate" };
            }
            var prefab = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>().GetPrefab<Game.Prefabs.Climate.ClimatePrefab>(climate.currentClimate);
            var months = new List<object>();
            for (int m = 0; m < 12; m++)
            {
                float t = (m + 0.5f) / 12f;
                ClimateSystem.ClimateSample c = climate.SampleClimate(prefab, t);
                ClimateSystem.ClimateSample night = climate.SampleClimate(prefab, m / 12f);
                months.Add(new
                {
                    month = m + 1,
                    t = Math.Round(t, 4),
                    temperatureNoon = Math.Round(c.temperature, 1),
                    temperatureMidnight = Math.Round(night.temperature, 1),
                    precipitation = Math.Round(c.precipitation, 2),
                    cloudiness = Math.Round(c.cloudiness, 2),
                    fog = Math.Round(c.fog, 2),
                });
            }
            return new
            {
                prefab = prefab.name,
                daysPerYear = time.daysPerYear,
                normalizedDate = time.normalizedDate,
                temperatureKeys = prefab.m_Temperature?.length ?? 0,
                curveSpan = prefab.m_Temperature != null && prefab.m_Temperature.length > 0
                    ? new[] { prefab.m_Temperature[0].time, prefab.m_Temperature[prefab.m_Temperature.length - 1].time }
                    : null,
                monthly = months,
                note = "curves sampled at t*daysPerYear (ClimateSystem.SampleClimate). RebuildTemperatureCurves writes 288 keys, 24 per month unit, one day cycle per month (a CS2 day is a month): t=(m+0.5)/12 is that month's noon, t=m/12 its midnight",
            };
        }

        public sealed class MapTimeDto
        {
            public int? month;
            public int? day;
            public float? hour;
            public float? date;
            public float? cloudiness;
            public bool? reset;
            /// <summary>0.9.41: switch off the temperature / precipitation / cloudiness / fog / aurora overrides (and the date override unless a date is given).</summary>
            public bool? clearOverrides;
            /// <summary>0.9.37: run the clock this many real seconds, then pause again.</summary>
            public float? settle;
            /// <summary>Simulation speed for settle (default 1).</summary>
            public float? speed;
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
            if (request.Method == "POST" && BridgeSystem.Instance.AutoPauseAt.HasValue)
            {
                // 0.9.47: one thing at a time; the previous settle is still running the clock.
                return BridgeResponse.Json(new
                {
                    error = "a settle is still running; wait until it ends (settleUntil) and the clock is paused",
                    settleUntil = BridgeSystem.Instance.AutoPauseAt.Value.ToString("HH:mm:ss"),
                }, 409);
            }
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
                if (body.clearOverrides == true)
                {
                    bool keepDate = body.date.HasValue || body.month.HasValue;
                    List<string> cleared = ClearClimateOverrides(climate, !keepDate);
                    applied.Add(cleared.Count > 0 ? $"overrides cleared (were on: {string.Join(", ", cleared)})" : "no climate override was on");
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
                    if (!body.day.HasValue && body.hour.HasValue)
                    {
                        // 0.9.41: a CS2 day is one month unit of the climate curves (24 keys per
                        // month), so the climate's hour of day is the position inside the month.
                        date = ((month - 1) + math.clamp(body.hour.Value, 0f, 23.99f) / 24f) / 12f;
                    }
                    else
                    {
                        int day = math.clamp(body.day ?? 15, 1, daysInMonth);
                        date = ((month - 1) + (day - 1f) / daysInMonth) / 12f;
                    }
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
                if (body.settle.HasValue && body.settle.Value > 0f)
                {
                    // ClimateSystem (EditorSimulation phase) and SnowSystem only update in simulation
                    // steps, i.e. while the clock runs; paused, the HUD keeps a stale sample.
                    float seconds = math.min(body.settle.Value, 600f);
                    float speed = math.clamp(body.speed ?? 1f, 1f, 3f);
                    World.GetOrCreateSystemManaged<SimulationSystem>().selectedSpeed = speed;
                    BridgeSystem.Instance.AutoPauseAt = DateTime.Now.AddSeconds(seconds);
                    applied.Add($"clock running at speed {speed} for {seconds:F0} s, then paused");
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
                settleUntil = BridgeSystem.Instance.AutoPauseAt?.ToString("HH:mm:ss"),
                running = World.GetOrCreateSystemManaged<SimulationSystem>().selectedSpeed > 0f,
                live = new
                {
                    temperature = climate.temperature.value,
                    temperatureOverridden = climate.temperature.overrideState,
                    precipitation = (float)climate.precipitation,
                    fog = (float)climate.fog,
                    aurora = (float)climate.aurora,
                    climate.isRaining,
                    climate.isSnowing,
                    climate.isPrecipitating,
                    season = climate.currentSeasonName,
                    climate.freezingTemperature,
                    climate.averageTemperature,
                    climate.temperatureBaseHeight,
                    classification = climate.classification.ToString(),
                },
                sampleAtDate = SampleObject(climate.SampleClimate((float)climate.currentDate)),
                curveAtDate = CurveAtDate(climate),
                overrides = new
                {
                    date = climate.currentDate.overrideState,
                    temperature = climate.temperature.overrideState,
                    temperatureValue = climate.temperature.overrideValue,
                    precipitation = climate.precipitation.overrideState,
                    precipitationValue = climate.precipitation.overrideValue,
                    cloudiness = climate.cloudiness.overrideState,
                    cloudinessValue = climate.cloudiness.overrideValue,
                    fog = climate.fog.overrideState,
                    fogValue = climate.fog.overrideValue,
                    aurora = climate.aurora.overrideState,
                    auroraValue = climate.aurora.overrideValue,
                    dateValue = climate.currentDate.overrideValue,
                    timeWeatherAnarchy = TimeWeatherAnarchyProfile(),
                    note = "a value whose override is on reads the override everywhere (HUD, sampleAtDate, weather): the editor bottom bar or a mod such as Time and Weather Anarchy sets them",
                },
                climate = ClimateMonthly(),
                note = "editor preview (bottom bar): not saved with the map. live values only update while the clock runs (ClimateSystem runs in EditorSimulation steps); sampleAtDate is computed now from the current climate curves (overrides applied); use settle:<seconds> to let weather and snow catch up",
            });
        }
    }
}

using System;
using System.IO;
using System.Threading.Tasks;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Assets;
using Game.UI;
using Game.UI.Editor;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.43: POST /map/save?name=&lt;name&gt;[&amp;overwrite=true] saves the editor map through the
    /// editor's own path (MapPanelSystem.OnSaveMap → SaveMap on the "SaveLoadGame" task queue)
    /// and reports the real outcome in GET /map/save/status. A name that already exists in the
    /// user database answers 409 unless overwrite=true (a test session lost three saves silently under a
    /// name still registered to a failed save).
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private sealed class MapSaveJob
        {
            public string Name;
            public bool Overwrite;
            public DateTime Started;
            public DateTime? Finished;
            public string State = "queued"; // queued, saving, done, failed
            public string Error;
            public string MetadataGuid;
            public string PackagePath;
            public long? PackageBytes;
        }

        private MapSaveJob m_MapSave;

        private BridgeResponse MapSave(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST")
            {
                return MapSaveStatus();
            }
            if (!editor)
            {
                return BridgeResponse.Error(409, "/map/save saves an editor map; in a city use /save");
            }
            if (m_MapSave != null && !m_MapSave.Finished.HasValue)
            {
                return BridgeResponse.Json(new { error = "a map save is still running", save = MapSaveObject(m_MapSave) }, 409);
            }
            if (!request.Query.TryGetValue("name", out string name) || string.IsNullOrWhiteSpace(name))
            {
                return BridgeResponse.Error(400, "POST /map/save?name=<map name>[&overwrite=true]");
            }
            name = name.Trim();
            bool overwrite = request.TryGetBool("overwrite", out bool o) && o;
            MapPanelSystem panel = World.GetExistingSystemManaged<MapPanelSystem>();
            if (panel == null)
            {
                return BridgeResponse.Error(503, "MapPanelSystem not available");
            }
            MapMetadata existing = null;
            foreach (MapMetadata meta in AssetDatabase.user.GetAssets<MapMetadata>())
            {
                if (string.Equals(meta.GetMeta().displayName, name, StringComparison.OrdinalIgnoreCase))
                {
                    existing = meta;
                    break;
                }
            }
            if (existing != null && !overwrite)
            {
                return BridgeResponse.Json(new
                {
                    error = $"a map named '{name}' already exists ({existing.id.guid}); pass overwrite=true or pick another name",
                }, 409);
            }

            var job = new MapSaveJob { Name = name, Overwrite = existing != null, Started = DateTime.Now };
            m_MapSave = job;
            World.GetOrCreateSystemManaged<MapMetadataSystem>().Update();
            Func<Task> save;
            if (existing != null)
            {
                SourceMeta meta = existing.GetMeta();
                Colossal.Hash128 guid = existing.id.guid;
                save = () => panel.SaveMap(meta.displayName, guid, existing.target, existing.database,
                    AssetDataPath.Create(meta.subPath, meta.fileName, EscapeStrategy.None),
                    !(existing.database is AssetDatabase<Colossal.IO.AssetDatabase.Game>), m => OnMapSaved(job, m));
            }
            else
            {
                save = () => panel.SaveMap(name, Colossal.Hash128.Empty, null, AssetDatabase.user,
                    Game.SceneFlow.SaveHelpers.GetAssetDataPath<MapMetadata>(AssetDatabase.user, name), true, m => OnMapSaved(job, m));
            }
            TaskManager.instance.EnqueueTask("SaveLoadGame", async () =>
            {
                job.State = "saving";
                try
                {
                    await save();
                    if (job.State == "saving")
                    {
                        // SaveMap finished without throwing; the callback runs on the main thread next.
                        job.State = "written";
                    }
                }
                catch (Exception e)
                {
                    job.State = "failed";
                    job.Error = $"{e.GetType().Name}: {e.Message}";
                    job.Finished = DateTime.Now;
                    Mod.Log.Warn($"map save '{job.Name}' failed: {e}");
                }
            }, 1);
            Mod.Log.Info($"map save '{name}' queued{(job.Overwrite ? " (overwrite)" : "")}");
            return BridgeResponse.Json(new
            {
                save = MapSaveObject(job),
                status = "/map/save/status",
                note = "the save runs on the game's SaveLoadGame task queue; poll GET /map/save/status until state is done or failed",
            }, 202);
        }

        private void OnMapSaved(MapSaveJob job, MapMetadata meta)
        {
            job.MetadataGuid = meta?.id.guid.ToString();
            try
            {
                string root = Path.Combine(UnityEngine.Application.persistentDataPath, "Maps");
                string file = null;
                if (Directory.Exists(root))
                {
                    foreach (string candidate in Directory.GetFiles(root, job.Name + ".cok", SearchOption.AllDirectories))
                    {
                        if (file == null || File.GetLastWriteTime(candidate) > File.GetLastWriteTime(file))
                        {
                            file = candidate;
                        }
                    }
                }
                job.PackagePath = file;
                job.PackageBytes = file != null ? new FileInfo(file).Length : (long?)null;
            }
            catch (Exception e)
            {
                job.Error = $"saved, but the package file could not be checked: {e.Message}";
            }
            job.State = job.PackageBytes > 0 ? "done" : "failed";
            if (job.State == "failed" && job.Error == null)
            {
                job.Error = "the save callback ran but no package file was found under Maps";
            }
            job.Finished = DateTime.Now;
            MapEverSaved = job.State == "done" || MapEverSaved;
            Mod.Log.Info($"map save '{job.Name}': {job.State}, {job.PackageBytes} bytes, {job.PackagePath}");
        }

        private BridgeResponse MapSaveStatus()
        {
            return BridgeResponse.Json(new
            {
                save = m_MapSave == null ? null : MapSaveObject(m_MapSave),
                mapEverSaved = SavedSinceLoad(),
                note = "mapEverSaved: the current map was loaded from a save or saved through /map/save in this session",
            });
        }

        private static object MapSaveObject(MapSaveJob job)
        {
            return new
            {
                name = job.Name,
                overwrite = job.Overwrite,
                state = job.State,
                error = job.Error,
                metadata = job.MetadataGuid,
                package = job.PackagePath,
                packageBytes = job.PackageBytes,
                seconds = Math.Round(((job.Finished ?? DateTime.Now) - job.Started).TotalSeconds, 1),
            };
        }

        /// <summary>
        /// 0.9.43: true when the current editor map came from a save or was saved by /map/save.
        /// Terrain render edits on a never-saved map are refused (two test sessions crashed natively
        /// right after them on fresh maps; loaded saves never did).
        /// </summary>
        internal static bool MapEverSaved { get; set; }

        /// <summary>MapPanelSystem.m_CurrentSourceDataGuid when the map finished loading.</summary>
        internal static string SourceGuidAtLoad { get; set; }

        /// <summary>
        /// MapPanelSystem.m_CurrentSourceDataGuid: set on load (the template or saved map) and by
        /// every editor save, the UI's included. A change since load means the map was saved.
        /// </summary>
        internal static string EditorSourceGuid(Unity.Entities.World world)
        {
            MapPanelSystem panel = world.GetExistingSystemManaged<MapPanelSystem>();
            object value = typeof(MapPanelSystem).GetField("m_CurrentSourceDataGuid", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(panel);
            return value?.ToString();
        }

        private bool SavedSinceLoad()
        {
            return MapEverSaved || (SourceGuidAtLoad != null && EditorSourceGuid(World) != SourceGuidAtLoad);
        }
    }
}

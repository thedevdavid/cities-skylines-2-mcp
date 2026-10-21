using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Colossal.IO.AssetDatabase;
using Game.Assets;
using Game.SceneFlow;
using Game.Tools;
using Game.UI.Editor;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.46: POST /map/load?name=&lt;map&gt; loads an editor map through the editor's own path
    /// (MapPanelSystem.LoadMap: GameManager.Load(Editor, LoadMap, asset)) without the confirmation
    /// dialog; GET /map/load/status reports queued / loading / loaded / failed. GET /debug/editor-ui
    /// dumps the state behind the editor panels (active tool, map panel sub-panel, asset picker
    /// selection, game state) for UI problems such as the Load Map panel ignoring clicks.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private sealed class MapLoadJob
        {
            public string Name;
            public string Guid;
            public DateTime Started;
            public DateTime? Finished;
            public string State = "queued";
            public string Error;
        }

        private static MapLoadJob s_MapLoad;

        /// <summary>Called by BridgeSystem.OnGameLoadingComplete.</summary>
        internal static void MapLoadCompleted()
        {
            if (s_MapLoad != null && !s_MapLoad.Finished.HasValue && s_MapLoad.State == "loading")
            {
                s_MapLoad.State = "loaded";
                s_MapLoad.Finished = DateTime.Now;
            }
        }

        private BridgeResponse MapLoad(BridgeRequest request)
        {
            if (request.Method != "POST")
            {
                return BridgeResponse.Json(new
                {
                    load = s_MapLoad == null ? null : MapLoadObject(s_MapLoad),
                    note = "state: queued, loading, loaded (OnGameLoadingComplete ran), failed",
                });
            }
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            if (!editor)
            {
                return BridgeResponse.Error(409, "/map/load loads an editor map; open the editor first");
            }
            if (s_MapLoad != null && !s_MapLoad.Finished.HasValue)
            {
                return BridgeResponse.Json(new { error = "a map load is still running", load = MapLoadObject(s_MapLoad) }, 409);
            }
            if (m_MapSave != null && !m_MapSave.Finished.HasValue)
            {
                return BridgeResponse.Json(new { error = "a map save is still running", save = MapSaveObject(m_MapSave) }, 409);
            }
            if (!request.Query.TryGetValue("name", out string name) || string.IsNullOrWhiteSpace(name))
            {
                return BridgeResponse.Error(400, "POST /map/load?name=<map name> (as listed in the editor's Load Map panel)");
            }
            name = name.Trim();
            MapMetadata found = null;
            var names = new List<string>();
            foreach (MapMetadata meta in AssetDatabase.global.GetAssets<MapMetadata>())
            {
                if (meta.database is AssetDatabase<Colossal.IO.AssetDatabase.Game>)
                {
                    continue;
                }
                string displayName = meta.GetMeta().displayName;
                if (displayName == null)
                {
                    continue;
                }
                if (names.Count < 50)
                {
                    names.Add(displayName);
                }
                if (string.Equals(displayName, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = meta;
                }
            }
            if (found == null)
            {
                return BridgeResponse.Json(new { error = $"no map named '{name}'", some = names }, 404);
            }
            MapPanelSystem panel = World.GetExistingSystemManaged<MapPanelSystem>();
            if (panel == null)
            {
                return BridgeResponse.Error(503, "MapPanelSystem not available");
            }
            var job = new MapLoadJob { Name = name, Guid = found.id.guid.ToString(), Started = DateTime.Now, State = "loading" };
            s_MapLoad = job;
            Colossal.Hash128 guid = found.id.guid;
            Mod.Log.Info($"map load '{name}' ({job.Guid}) started");
            Task task = panel.LoadMap(guid);
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    job.State = "failed";
                    job.Error = t.Exception?.GetBaseException().Message;
                    job.Finished = DateTime.Now;
                    Mod.Log.Warn($"map load '{job.Name}' failed: {t.Exception?.GetBaseException()}");
                }
            });
            return BridgeResponse.Json(new
            {
                load = MapLoadObject(job),
                status = "/map/load/status",
                note = "the bridge answers again once the map has loaded; poll GET /map/load/status (state loaded) or /ping",
            }, 202);
        }

        private static object MapLoadObject(MapLoadJob job)
        {
            return new
            {
                name = job.Name,
                guid = job.Guid,
                state = job.State,
                error = job.Error,
                seconds = Math.Round(((job.Finished ?? DateTime.Now) - job.Started).TotalSeconds, 1),
            };
        }

        /// <summary>GET /debug/editor-ui</summary>
        private BridgeResponse EditorUiDebug()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            object Safe(Func<object> f)
            {
                try
                {
                    return f();
                }
                catch (Exception e)
                {
                    return $"{e.GetType().Name}: {e.Message}";
                }
            }
            MapPanelSystem panel = World.GetExistingSystemManaged<MapPanelSystem>();
            object subPanel = Safe(() => panel == null ? null : typeof(MapPanelSystem).GetProperty("activeSubPanel", flags)?.GetValue(panel)
                ?? typeof(MapPanelSystem).BaseType?.GetProperty("activeSubPanel", flags)?.GetValue(panel));
            object adapter = Safe(() => subPanel?.GetType().GetField("m_Adapter", flags)?.GetValue(subPanel));
            var editorPanels = new List<object>();
            foreach (Type type in new[] { typeof(MapPanelSystem), typeof(EditorHierarchyUISystem), typeof(InspectorPanelSystem) })
            {
                var system = World.GetExistingSystemManaged(type) as Unity.Entities.ComponentSystemBase;
                editorPanels.Add(new { system = type.Name, exists = system != null, enabled = system?.Enabled, running = Safe(() => system?.ShouldRunSystem()) });
            }
            var editorTool = World.GetExistingSystemManaged<EditorToolUISystem>();
            return BridgeResponse.Json(new
            {
                activeTool = Safe(() => World.GetOrCreateSystemManaged<ToolSystem>().activeTool?.toolID),
                activeToolType = Safe(() => World.GetOrCreateSystemManaged<ToolSystem>().activeTool?.GetType().FullName),
                actionMode = Safe(() => World.GetOrCreateSystemManaged<ToolSystem>().actionMode.ToString()),
                gameState = Safe(() => GameManager.instance.state.ToString()),
                isLoading = Safe(() => GameManager.instance.isGameLoading),
                mapPanel = new
                {
                    subPanel = Safe(() => subPanel?.GetType().FullName),
                    adapterItems = Safe(() => (adapter?.GetType().GetField("m_FilteredItems", flags)?.GetValue(adapter) as System.Collections.ICollection)?.Count),
                    adapterSelected = Safe(() => adapter?.GetType().GetField("m_SelectedItem", flags)?.GetValue(adapter)?.ToString()),
                },
                editorTool = Safe(() => editorTool == null ? null : new { enabled = editorTool.Enabled }),
                systems = editorPanels,
                mapSave = m_MapSave == null ? null : MapSaveObject(m_MapSave),
                mapLoad = s_MapLoad == null ? null : MapLoadObject(s_MapLoad),
                note = "diagnostic snapshot; reflection reads are guarded (a failed read shows the exception)",
            });
        }
    }
}

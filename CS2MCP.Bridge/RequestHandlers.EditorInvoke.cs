using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.UI.Editor;
using Newtonsoft.Json;
using Unity.Entities;

namespace CS2MCP
{
    /// <summary>
    /// 0.9.61: publish prep without clicks (synthetic clicks do not fire the Map inspector's buttons).
    /// POST /map/preview {preview?: path, thumbnail?: path} — the Map panel's own private
    /// OnSelectPreview(string) / OnSelectThumbnail(string) (ImageAsset.ImportImageFromPath, then the
    /// field the Save Map dialog embeds).
    /// POST /debug/invoke {system, method} — a parameterless method of an editor UI system (namespace
    /// Game.UI.Editor), e.g. MapPanelSystem.CaptureCameraProperties / ToggleMapTileSelection, exactly
    /// what the inspector buttons' actions call.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        public sealed class MapPreviewDto
        {
            public string preview;
            public string thumbnail;
        }

        public sealed class InvokeDto
        {
            public string system;
            public string method;
        }

        private BridgeResponse MapPreview(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out bool editor, out BridgeResponse error))
            {
                return error;
            }
            if (!editor || request.Method != "POST")
            {
                return BridgeResponse.Error(400, "POST /map/preview {preview?: image path, thumbnail?: image path} in the editor");
            }
            MapPreviewDto body;
            try
            {
                body = JsonConvert.DeserializeObject<MapPreviewDto>(request.Body ?? "{}") ?? new MapPreviewDto();
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            MapPanelSystem panel = World.GetExistingSystemManaged<MapPanelSystem>();
            if (panel == null)
            {
                return BridgeResponse.Error(503, "MapPanelSystem not available");
            }
            var applied = new List<string>();
            var problems = new List<string>();
            foreach ((string path, string method) in new[] { (body.preview, "OnSelectPreview"), (body.thumbnail, "OnSelectThumbnail") })
            {
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                if (!File.Exists(path))
                {
                    problems.Add($"{method}: file not found: {path}");
                    continue;
                }
                MethodInfo m = typeof(MapPanelSystem).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (m == null)
                {
                    problems.Add($"{method}(string) not found (game update?)");
                    continue;
                }
                try
                {
                    m.Invoke(panel, new object[] { path });
                    applied.Add($"{method} <- {path}");
                }
                catch (Exception e)
                {
                    problems.Add($"{method}: {(e.InnerException ?? e).Message}");
                }
            }
            return BridgeResponse.Json(new
            {
                applied,
                problems,
                note = "set in the Map panel; written into the map on the next save (/map/save)",
            }, problems.Count > 0 && applied.Count == 0 ? 400 : 200);
        }

        private BridgeResponse DebugInvoke(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST")
            {
                return BridgeResponse.Error(405, "POST /debug/invoke {system: e.g. MapPanelSystem, method: e.g. CaptureCameraProperties}");
            }
            InvokeDto body;
            try
            {
                body = JsonConvert.DeserializeObject<InvokeDto>(request.Body ?? "{}") ?? new InvokeDto();
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid JSON: {e.Message}");
            }
            if (string.IsNullOrEmpty(body.system) || string.IsNullOrEmpty(body.method))
            {
                return BridgeResponse.Error(400, "system and method are required");
            }
            ComponentSystemBase target = null;
            foreach (ComponentSystemBase system in World.Systems)
            {
                Type t = system.GetType();
                if (t.Namespace == "Game.UI.Editor" && string.Equals(t.Name, body.system, StringComparison.Ordinal))
                {
                    target = system;
                    break;
                }
            }
            if (target == null)
            {
                return BridgeResponse.Error(404, $"no editor UI system '{body.system}' (namespace Game.UI.Editor)");
            }
            MethodInfo method = target.GetType().GetMethod(body.method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method == null)
            {
                var names = new List<string>();
                foreach (MethodInfo m in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.GetParameters().Length == 0 && !m.IsSpecialName && m.ReturnType == typeof(void))
                    {
                        names.Add(m.Name);
                    }
                }
                names.Sort(StringComparer.Ordinal);
                return BridgeResponse.Json(new { error = $"{body.system} has no parameterless method '{body.method}'", available = names }, 404);
            }
            try
            {
                object result = method.Invoke(target, null);
                Mod.Log.Info($"debug/invoke {body.system}.{body.method}");
                return BridgeResponse.Json(new { invoked = $"{body.system}.{body.method}", result = result?.ToString() });
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(500, $"{body.system}.{body.method}: {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}");
            }
        }
    }
}

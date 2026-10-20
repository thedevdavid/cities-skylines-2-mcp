using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace CS2MCP
{
    /// <summary>
    /// TM:PE (CS1) lane connections and lane arrows -> Traffic (PDX 80095) lane connections.
    /// Body: scripts/import-tmpe.mjs (cs2mcp/tmpe-import/v1). The work runs in BridgeTmpeImportSystem
    /// (ToolUpdate), so the Updated tags it adds reach Traffic's lane system in the same frame.
    /// Nothing in this file touches Traffic types: the bridge still loads without Traffic.
    /// </summary>
    public sealed partial class RequestHandlers
    {
        /// <summary>The Traffic build the importer is compiled against (Traffic.dll 0.2.13+ce1551f, PDX 80095 build 29).</summary>
        public const string PinnedTrafficVersion = "0.2.13+ce1551f";

        private BridgeResponse ImportTmpe(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (request.Method != "POST" || string.IsNullOrEmpty(request.Body))
            {
                return BridgeResponse.Error(400, "POST a cs2mcp/tmpe-import/v1 body (scripts/import-tmpe.mjs)");
            }
            TmpeImportDto body;
            try
            {
                body = JsonConvert.DeserializeObject<TmpeImportDto>(request.Body);
            }
            catch (Exception e)
            {
                return BridgeResponse.Error(400, $"invalid body: {e.Message}");
            }
            if (body?.nodes == null || body.nodes.Count == 0)
            {
                return BridgeResponse.Error(400, "body has no nodes");
            }
            if (!TryCheckTraffic(body.traffic?.forceVersion == true, out string trafficVersion, out error))
            {
                return error;
            }
            BridgeTmpeImportSystem system = World.GetOrCreateSystemManaged<BridgeTmpeImportSystem>();
            if (!system.TryQueue(new BridgeTmpeImportSystem.TmpeJob { Request = request, Body = body, TrafficVersion = trafficVersion }))
            {
                return BridgeResponse.Error(409, "a TM:PE import or undo is already queued");
            }
            return null;
        }

        private BridgeResponse UndoTmpe(BridgeRequest request)
        {
            if (!TryGetCityOrEditor(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!TryCheckTraffic(true, out string trafficVersion, out error))
            {
                return error;
            }
            BridgeTmpeImportSystem system = World.GetOrCreateSystemManaged<BridgeTmpeImportSystem>();
            if (!system.TryQueue(new BridgeTmpeImportSystem.TmpeJob { Request = request, Undo = true, TrafficVersion = trafficVersion }))
            {
                return BridgeResponse.Error(409, "a TM:PE import or undo is already queued");
            }
            return null;
        }

        private BridgeResponse TmpeStatus()
        {
            object last = World.GetOrCreateSystemManaged<BridgeTmpeImportSystem>().LastReport;
            return BridgeResponse.Json(last ?? new { status = "no TM:PE import in this game session" });
        }

        private static bool s_TrafficResolverInstalled;

        /// <summary>
        /// Finds the loaded Traffic assembly and checks its version against the pin. Installs an
        /// AssemblyResolve hook so the bridge's compile-time reference binds to the copy the game loaded.
        /// </summary>
        private static bool TryCheckTraffic(bool force, out string version, out BridgeResponse error)
        {
            version = null;
            error = null;
            Assembly traffic = FindTrafficAssembly();
            if (traffic == null)
            {
                error = BridgeResponse.Error(409, "the Traffic mod (PDX 80095) is not loaded; enable it in the playset");
                return false;
            }
            version = traffic.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? traffic.GetName().Version.ToString();
            if (!s_TrafficResolverInstalled)
            {
                AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                    new AssemblyName(args.Name).Name == "Traffic" ? FindTrafficAssembly() : null;
                s_TrafficResolverInstalled = true;
            }
            if (!version.StartsWith(PinnedTrafficVersion, StringComparison.Ordinal) && !force)
            {
                error = BridgeResponse.Error(409,
                    $"Traffic {version} is loaded; the importer is built against {PinnedTrafficVersion}. " +
                    "Its component layouts may differ: rebuild the bridge against the new Traffic.dll, or pass traffic.forceVersion (--force-version) at your own risk.");
                return false;
            }
            return true;
        }

        private static Assembly FindTrafficAssembly()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Traffic" && a.GetType("Traffic.Components.LaneConnections.GeneratedConnection", false) != null);
        }
    }

    /// <summary>Nodes the TM:PE importer wrote in this game session (memory only).</summary>
    public sealed class TmpeUndoLog
    {
        public readonly List<Unity.Entities.Entity> Nodes = new List<Unity.Entities.Entity>();
        public string ImportId;
    }

    // ---- request body (scripts/import-tmpe.mjs) ----

    public sealed class TmpeImportDto
    {
        public string schema;
        public TmpeTrafficDto traffic;
        public TmpeOptionsDto options;
        public List<TmpeNodeDto> nodes;
    }

    public sealed class TmpeTrafficDto
    {
        public string pinnedVersion;
        public bool forceVersion;
    }

    public sealed class TmpeOptionsDto
    {
        public bool dryRun;
        public bool replace;
        public float nodeRadius = 12f;
        /// <summary>0.9.26: x/z radius within which nodes are scored by their edges against the CS1 segment probes.</summary>
        public float nodeSearchRadius = 60f;
        public float edgeTolerance = 8f;
    }

    public sealed class TmpeNodeDto
    {
        public string id;
        public int cs1Node;
        public float x, y, z;
        public List<TmpeConnectionDto> connections = new List<TmpeConnectionDto>();
        public List<TmpeArrowDto> arrows = new List<TmpeArrowDto>();
    }

    public sealed class TmpeConnectionDto
    {
        public string id;
        public TmpeLaneEndDto source;
        public TmpeLaneEndDto target;
    }

    public sealed class TmpeArrowDto
    {
        public string id;
        public string arrows;
        public bool supersededByConnections;
        public TmpeLaneEndDto source;
        public List<TmpeArrowTargetDto> targets = new List<TmpeArrowTargetDto>();
    }

    public class TmpeSegmentRefDto
    {
        public int segment;
        public string prefab;
        public float[] dir;
        public float[] probe;
        public float probeDist;
    }

    public sealed class TmpeLaneEndDto : TmpeSegmentRefDto
    {
        public long cs1Lane;
        public int cs1LaneIndex;
        public int laneCount;
        public int rank;
        public float offset;
    }

    public sealed class TmpeArrowTargetDto : TmpeSegmentRefDto
    {
        public string arrow;
        public bool allowed;
    }
}

using System;
using System.IO;
using System.Reflection;
using Game;
using UnityEngine.Scripting;

namespace CS2MCP
{
    /// <summary>
    /// Runs queued TM:PE imports (/import/tmpe) and undos at ToolUpdate. BridgeSystem answers at UIUpdate,
    /// after the modification phases; Updated tags added there would be cleared by CleanUpSystem before
    /// Traffic's lane system (Modification4) sees them. From here they are seen in the same frame.
    /// No Traffic types appear in this assembly (0.9.52): TrafficLaneImport lives in the satellite
    /// CS2MCP.Traffic, deployed as CS2MCP.Traffic.bin and loaded here only when Traffic is present.
    /// </summary>
    public sealed partial class BridgeTmpeImportSystem : GameSystemBase
    {
        public sealed class TmpeJob
        {
            public BridgeRequest Request;
            public TmpeImportDto Body;
            public bool Undo;
            public string TrafficVersion;
        }

        private TmpeJob m_Pending;
        private readonly TmpeUndoLog m_UndoLog = new TmpeUndoLog();

        public object LastReport { get; private set; }

        /// <summary>Must be called on the simulation thread.</summary>
        public bool TryQueue(TmpeJob job)
        {
            if (m_Pending != null)
            {
                return false;
            }
            m_Pending = job;
            return true;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            OverrideSampler.Sample(EntityManager);
            TmpeJob job = m_Pending;
            if (job == null)
            {
                return;
            }
            m_Pending = null;
            try
            {
                Type importer = TrafficImporter(out string why);
                if (importer == null)
                {
                    job.Request.Complete(BridgeResponse.Error(503, why));
                    return;
                }
                object report = job.Undo
                    ? importer.GetMethod("Undo").Invoke(null, new object[] { EntityManager, m_UndoLog, job.TrafficVersion })
                    : importer.GetMethod("Run").Invoke(null, new object[] { this, job.Body, m_UndoLog, job.TrafficVersion });
                LastReport = report;
                job.Request.Complete(BridgeResponse.Json(report));
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException && e.InnerException != null)
                {
                    e = e.InnerException;
                }
                Mod.Log.Warn($"TM:PE import failed: {e}");
                job.Request.Complete(BridgeResponse.Error(500, $"{e.GetType().Name}: {e.Message}"));
            }
        }

        private static Type s_Importer;

        /// <summary>
        /// The satellite's TrafficLaneImport type: loaded from CS2MCP.Traffic.bin next to CS2MCP.dll,
        /// once Traffic itself is loaded (its types then resolve from the AppDomain).
        /// </summary>
        private static Type TrafficImporter(out string why)
        {
            why = null;
            if (s_Importer != null)
            {
                return s_Importer;
            }
            bool traffic = false;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == "Traffic")
                {
                    traffic = true;
                    break;
                }
            }
            if (!traffic)
            {
                why = "Traffic (PDX 80095) is not loaded; enable it in the playset and restart";
                return null;
            }
            string dir = null;
            try
            {
                dir = Path.GetDirectoryName(typeof(Mod).Assembly.Location);
            }
            catch (Exception)
            {
            }
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "CS2MCP.Traffic.bin")))
            {
                dir = Path.Combine(UnityEngine.Application.persistentDataPath, "Mods", "CS2MCP");
            }
            string file = Path.Combine(dir, "CS2MCP.Traffic.bin");
            if (!File.Exists(file))
            {
                why = $"{file} is missing (deploy the CS2MCP.Traffic project)";
                return null;
            }
            try
            {
                Assembly satellite = Assembly.Load(File.ReadAllBytes(file));
                s_Importer = satellite.GetType("CS2MCP.TrafficLaneImport", true);
                Mod.Log.Info($"TM:PE importer loaded from {file}");
                return s_Importer;
            }
            catch (Exception e)
            {
                why = $"loading {file} failed: {e.GetType().Name}: {e.Message}";
                return null;
            }
        }
    }
}

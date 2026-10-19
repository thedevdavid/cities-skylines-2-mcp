using System;
using Game;
using UnityEngine.Scripting;

namespace CS2MCP
{
    /// <summary>
    /// Runs queued TM:PE imports (/import/tmpe) and undos at ToolUpdate. BridgeSystem answers at UIUpdate,
    /// after the modification phases; Updated tags added there would be cleared by CleanUpSystem before
    /// Traffic's lane system (Modification4) sees them. From here they are seen in the same frame.
    /// No Traffic types appear in this class: TrafficLaneImport holds them, so the bridge loads without Traffic.
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
            TmpeJob job = m_Pending;
            if (job == null)
            {
                return;
            }
            m_Pending = null;
            try
            {
                object report = job.Undo
                    ? TrafficLaneImport.Undo(EntityManager, m_UndoLog, job.TrafficVersion)
                    : TrafficLaneImport.Run(this, job.Body, m_UndoLog, job.TrafficVersion);
                LastReport = report;
                job.Request.Complete(BridgeResponse.Json(report));
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"TM:PE import failed: {e}");
                job.Request.Complete(BridgeResponse.Error(500, $"{e.GetType().Name}: {e.Message}"));
            }
        }
    }
}

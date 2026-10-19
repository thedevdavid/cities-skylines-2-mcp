using Colossal.Logging;
using Game;
using Game.Modding;

namespace CS2MCP
{
    public sealed class Mod : IMod
    {
        public const string Name = "CS2MCP";
        public const string Version = "0.9.11-editor-import";

        public static readonly ILog Log = LogManager.GetLogger(Name).SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info($"{Name} {Version} loading, registering bridge systems");
            updateSystem.UpdateAt<BridgeSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<BridgeToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeTransitToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeTransitRenameSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<BridgeMapTileRefreshSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeRoadToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeImportToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeObjectToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<BridgeTmpeImportSystem>(SystemUpdatePhase.ToolUpdate);
        }

        public void OnDispose()
        {
            Log.Info($"{Name} disposing");
            BridgeSystem.Instance?.StopServer();
        }
    }
}

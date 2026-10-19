using System;
using System.Reflection;
using Game.Tools;

namespace CS2MCP
{
    /// <summary>
    /// Optional, reflection-only link to yenyang's Anarchy (no compile-time
    /// reference). Anarchy suppresses tool errors globally while its toggle is
    /// on (DisableToolErrorsSystem rewrites ToolErrorData flags every frame);
    /// on every tool change its EnableToolErrorsSystem restores them for a frame.
    /// Its public Anarchy.Bridge.AnarchyBridge exposes the toggle state and lets
    /// other mods register their tools (tool icon, tooltip).
    /// </summary>
    public static class AnarchyInterop
    {
        private static bool s_Resolved;
        private static MethodInfo s_GetToggleState;
        private static MethodInfo s_TryAddToolSystem;

        private static void Resolve()
        {
            if (s_Resolved)
            {
                return;
            }
            s_Resolved = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type bridge;
                try
                {
                    bridge = assembly.GetType("Anarchy.Bridge.AnarchyBridge", throwOnError: false);
                }
                catch
                {
                    continue;
                }
                if (bridge == null)
                {
                    continue;
                }
                s_GetToggleState = bridge.GetMethod("GetAnarchyToggleState", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                s_TryAddToolSystem = bridge.GetMethod("TryAddToolSystem", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ToolBaseSystem) }, null);
                return;
            }
        }

        public static bool Detected
        {
            get
            {
                Resolve();
                return s_GetToggleState != null;
            }
        }

        /// <summary>The Anarchy toggle, or null when Anarchy is not loaded or the call fails.</summary>
        public static bool? ToggleState()
        {
            Resolve();
            if (s_GetToggleState == null)
            {
                return null;
            }
            try
            {
                return (bool)s_GetToggleState.Invoke(null, null);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Anarchy toggle state unreadable: {e.Message}");
                return null;
            }
        }

        /// <summary>Adds the tool to Anarchy's list of supported tools (idempotent). False when Anarchy is absent.</summary>
        public static bool RegisterTool(ToolBaseSystem tool)
        {
            Resolve();
            if (s_TryAddToolSystem == null)
            {
                return false;
            }
            try
            {
                s_TryAddToolSystem.Invoke(null, new object[] { tool });
                return true;
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Anarchy tool registration failed: {e.Message}");
                return false;
            }
        }

        public static object Describe(ToolSystem toolSystem)
        {
            return new
            {
                anarchyDetected = Detected,
                anarchyEnabled = ToggleState(),
                ignoreErrors = toolSystem != null && toolSystem.ignoreErrors,
            };
        }
    }
}

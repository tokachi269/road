using System;
using System.Reflection;
using CitiesHarmony.API;
using HarmonyLib;

namespace RoadRuntimeHost.Runtime
{
    /// <summary>
    /// Notifies the runtime immediately after IMT has rebuilt its node and
    /// segment entrance topology.  This is deliberately narrower than a
    /// simulation-tick poll: callers only receive a notification when IMT's
    /// own Update() has completed.
    /// </summary>
    internal static class ImtTopologyUpdateHook
    {
        private const string PatchTypeName =
            "RoadRuntimeHost.Runtime.ImtTopologyUpdatePatch";
        private static readonly object Sync = new object();
        private static Action _updatedHandler;
        private static bool _startAttempted;

        public static void Start(Action updatedHandler)
        {
            lock (Sync) _updatedHandler = updatedHandler;
            if (_startAttempted) return;
            _startAttempted = true;
            InvokePatch("Start");
        }

        public static void Stop()
        {
            lock (Sync) _updatedHandler = null;
            if (_startAttempted) InvokePatch("Stop");
            _startAttempted = false;
        }

        internal static void NotifyUpdated()
        {
            Action handler;
            lock (Sync) handler = _updatedHandler;
            if (handler != null) handler();
        }

        private static void InvokePatch(string methodName)
        {
            try
            {
                Type patchType = typeof(ImtTopologyUpdateHook).Assembly.GetType(
                    PatchTypeName, true);
                MethodInfo method = patchType.GetMethod(
                    methodName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null) throw new MissingMethodException(PatchTypeName, methodName);
                method.Invoke(null, null);
            }
            catch (TargetInvocationException error)
            {
                ReportLoadFailure(error.InnerException ?? error);
            }
            catch (Exception error)
            {
                ReportLoadFailure(error);
            }
        }

        private static void ReportLoadFailure(Exception error)
        {
            DiagnosticLog.Error(
                "MOD_DEPENDENCY",
                "imt_topology_update_hook_load_failed",
                "Runtime marking updates cannot synchronize to IMT topology completion because the IMT update hook could not be loaded",
                error);
        }
    }

    internal static class ImtTopologyUpdatePatch
    {
        private static readonly string PatchId = "RoadRuntimeHost.ImtTopologyUpdate."
            + typeof(ImtTopologyUpdatePatch).Module.ModuleVersionId.ToString("N");
        private static Harmony _harmony;
        private static MethodInfo _updateOriginal;
        private static bool _stopped = true;

        public static void Start()
        {
            _stopped = false;
            HarmonyHelper.DoOnHarmonyReady(Install);
        }

        public static void Stop()
        {
            _stopped = true;
            if (_harmony != null && _updateOriginal != null)
            {
                _harmony.Unpatch(_updateOriginal, HarmonyPatchType.All, PatchId);
                DiagnosticLog.Info(
                    "MOD",
                    "imt_topology_update_hook_removed",
                    "Removed the IMT topology completion hook");
            }
            _harmony = null;
            _updateOriginal = null;
        }

        private static void Install()
        {
            if (_stopped) return;
            try
            {
                Type markingManagerType = FindMarkingManagerType();
                if (markingManagerType == null)
                    throw new TypeLoadException("IMT.Manager.MarkingManager");
                _updateOriginal = markingManagerType.GetMethod(
                    "Update",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (_updateOriginal == null)
                    throw new MissingMethodException(markingManagerType.FullName, "Update()");
                MethodInfo postfix = typeof(ImtTopologyUpdatePatch).GetMethod(
                    "UpdatePostfix", BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null)
                    throw new MissingMethodException(typeof(ImtTopologyUpdatePatch).FullName, "UpdatePostfix");

                _harmony = new Harmony(PatchId);
                _harmony.Unpatch(_updateOriginal, HarmonyPatchType.All, PatchId);
                _harmony.Patch(_updateOriginal, null, new HarmonyMethod(postfix));
                DiagnosticLog.Info(
                    "SUCCESS",
                    "imt_topology_update_hook_installed",
                    "Installed the IMT topology completion hook",
                    "target", markingManagerType.FullName + ".Update()");
            }
            catch (Exception error)
            {
                if (_harmony != null && _updateOriginal != null)
                    _harmony.Unpatch(_updateOriginal, HarmonyPatchType.All, PatchId);
                _harmony = null;
                _updateOriginal = null;
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "imt_topology_update_hook_install_failed",
                    "Runtime marking updates will not be synchronized to IMT topology completion",
                    error);
            }
        }

        private static Type FindMarkingManagerType()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType("IMT.Manager.MarkingManager", false);
                if (type != null) return type;
            }
            return null;
        }

        private static void UpdatePostfix()
        {
            if (!_stopped) ImtTopologyUpdateHook.NotifyUpdated();
        }
    }
}

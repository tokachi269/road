using System;
using System.Reflection;
using CitiesHarmony.API;
using HarmonyLib;

namespace RoadRuntimeHost.Runtime
{
    internal static class NetSegmentCreationHook
    {
        private const string PatchTypeName = "RoadRuntimeHost.Runtime.NetSegmentCreationPatch";
        private static readonly object Sync = new object();
        private static Action<ushort, NetInfo> _handler;
        private static bool _startAttempted;

        public static void Start(Action<ushort, NetInfo> handler)
        {
            lock (Sync) _handler = handler;
            if (_startAttempted) return;
            _startAttempted = true;
            InvokePatch("Start");
        }

        public static void Stop()
        {
            lock (Sync) _handler = null;
            if (_startAttempted) InvokePatch("Stop");
            _startAttempted = false;
        }

        internal static void Notify(ushort segmentId, NetInfo info)
        {
            Action<ushort, NetInfo> handler;
            lock (Sync) handler = _handler;
            if (handler != null) handler(segmentId, info);
        }

        private static void InvokePatch(string methodName)
        {
            try
            {
                Type patchType = typeof(NetSegmentCreationHook).Assembly.GetType(PatchTypeName, true);
                MethodInfo method = patchType.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
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
                "net_segment_creation_hook_load_failed",
                "Newly created roads will not receive event-driven IMT preview markings because the creation hook could not be loaded",
                error);
        }
    }

    // Optional Harmony references remain behind this reflection boundary so
    // offline validation and shutdown can load Runtime without game-side mods.
    internal static class NetSegmentCreationPatch
    {
        private static readonly string PatchId = "RoadRuntimeHost.NetSegmentCreation."
            + typeof(NetSegmentCreationPatch).Module.ModuleVersionId.ToString("N");
        private static Harmony _harmony;
        private static MethodInfo _original;
        private static bool _stopped = true;

        public static void Start()
        {
            _stopped = false;
            HarmonyHelper.DoOnHarmonyReady(Install);
        }

        public static void Stop()
        {
            _stopped = true;
            if (_harmony != null && _original != null)
            {
                _harmony.Unpatch(_original, HarmonyPatchType.All, PatchId);
                DiagnosticLog.Info(
                    "MOD",
                    "net_segment_creation_hook_removed",
                    "Net segment creation hook was removed");
            }
            _harmony = null;
            _original = null;
        }

        private static void Install()
        {
            if (_stopped) return;
            try
            {
                _original = typeof(NetManager).GetMethod(
                    "CreateSegment",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new Type[]
                    {
                        typeof(ushort).MakeByRefType(),
                        typeof(ColossalFramework.Math.Randomizer).MakeByRefType(),
                        typeof(NetInfo),
                        typeof(TreeInfo),
                        typeof(ushort),
                        typeof(ushort),
                        typeof(UnityEngine.Vector3),
                        typeof(UnityEngine.Vector3),
                        typeof(uint),
                        typeof(uint),
                        typeof(bool)
                    },
                    null);
                if (_original == null)
                    throw new MissingMethodException(typeof(NetManager).FullName, "CreateSegment(TreeInfo overload)");

                MethodInfo postfix = typeof(NetSegmentCreationPatch).GetMethod(
                    "Postfix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null)
                    throw new MissingMethodException(typeof(NetSegmentCreationPatch).FullName, "Postfix");

                _harmony = new Harmony(PatchId);
                _harmony.Unpatch(_original, HarmonyPatchType.All, PatchId);
                _harmony.Patch(_original, null, new HarmonyMethod(postfix));
                DiagnosticLog.Info(
                    "SUCCESS",
                    "net_segment_creation_hook_installed",
                    "Installed one post-create hook; IMT work is queued outside NetManager.CreateSegment",
                    "hook_method", "NetManager.CreateSegment(TreeInfo overload)");
            }
            catch (Exception error)
            {
                _harmony = null;
                _original = null;
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "net_segment_creation_hook_install_failed",
                    "Newly created roads will not receive event-driven IMT preview markings until the hook can be installed",
                    error);
            }
        }

        private static void Postfix(bool __result, ushort segment, NetInfo info)
        {
            if (__result && segment != 0) NetSegmentCreationHook.Notify(segment, info);
        }
    }
}

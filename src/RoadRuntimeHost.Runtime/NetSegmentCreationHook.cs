using System;
using System.Reflection;
using CitiesHarmony.API;
using HarmonyLib;

namespace RoadRuntimeHost.Runtime
{
    internal static class NetSegmentCreationHook
    {
        private const string PatchTypeName = "RoadRuntimeHost.Runtime.NetSegmentMutationPatch";
        private static readonly object Sync = new object();
        private static Action<ushort, NetInfo> _createdHandler;
        private static Action<ushort, ushort, ushort, NetInfo> _releasedHandler;
        private static bool _startAttempted;

        public static void Start(
            Action<ushort, NetInfo> createdHandler,
            Action<ushort, ushort, ushort, NetInfo> releasedHandler)
        {
            lock (Sync)
            {
                _createdHandler = createdHandler;
                _releasedHandler = releasedHandler;
            }
            if (_startAttempted) return;
            _startAttempted = true;
            InvokePatch("Start");
        }

        public static void Stop()
        {
            lock (Sync)
            {
                _createdHandler = null;
                _releasedHandler = null;
            }
            if (_startAttempted) InvokePatch("Stop");
            _startAttempted = false;
        }

        internal static void NotifyCreated(ushort segmentId, NetInfo info)
        {
            Action<ushort, NetInfo> handler;
            lock (Sync) handler = _createdHandler;
            if (handler != null) handler(segmentId, info);
        }

        internal static void NotifyReleased(
            ushort segmentId,
            ushort startNode,
            ushort endNode,
            NetInfo info)
        {
            Action<ushort, ushort, ushort, NetInfo> handler;
            lock (Sync) handler = _releasedHandler;
            if (handler != null) handler(segmentId, startNode, endNode, info);
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
                "net_segment_mutation_hook_load_failed",
                "Changed road nodes will not receive event-driven IMT updates because the segment mutation hook could not be loaded",
                error);
        }
    }

    // Optional Harmony references remain behind this reflection boundary so
    // offline validation and shutdown can load Runtime without game-side mods.
    internal static class NetSegmentMutationPatch
    {
        private sealed class ReleaseState
        {
            public ushort SegmentId;
            public ushort StartNode;
            public ushort EndNode;
            public NetInfo Info;
        }

        private static readonly string PatchId = "RoadRuntimeHost.NetSegmentMutation."
            + typeof(NetSegmentMutationPatch).Module.ModuleVersionId.ToString("N");
        private static Harmony _harmony;
        private static MethodInfo _createOriginal;
        private static MethodInfo _releaseOriginal;
        private static bool _stopped = true;

        public static void Start()
        {
            _stopped = false;
            HarmonyHelper.DoOnHarmonyReady(Install);
        }

        public static void Stop()
        {
            _stopped = true;
            if (_harmony != null)
            {
                if (_createOriginal != null)
                    _harmony.Unpatch(_createOriginal, HarmonyPatchType.All, PatchId);
                if (_releaseOriginal != null)
                    _harmony.Unpatch(_releaseOriginal, HarmonyPatchType.All, PatchId);
                DiagnosticLog.Info(
                    "MOD",
                    "net_segment_mutation_hook_removed",
                    "Net segment create and release hooks were removed");
            }
            _harmony = null;
            _createOriginal = null;
            _releaseOriginal = null;
        }

        private static void Install()
        {
            if (_stopped) return;
            try
            {
                _createOriginal = typeof(NetManager).GetMethod(
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
                _releaseOriginal = typeof(NetManager).GetMethod(
                    "ReleaseSegment",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(ushort), typeof(bool) },
                    null);
                if (_createOriginal == null)
                    throw new MissingMethodException(typeof(NetManager).FullName, "CreateSegment(TreeInfo overload)");
                if (_releaseOriginal == null)
                    throw new MissingMethodException(typeof(NetManager).FullName, "ReleaseSegment(ushort, bool)");

                MethodInfo createPostfix = typeof(NetSegmentMutationPatch).GetMethod(
                    "CreatePostfix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo releasePrefix = typeof(NetSegmentMutationPatch).GetMethod(
                    "ReleasePrefix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo releasePostfix = typeof(NetSegmentMutationPatch).GetMethod(
                    "ReleasePostfix", BindingFlags.Static | BindingFlags.NonPublic);
                if (createPostfix == null || releasePrefix == null || releasePostfix == null)
                    throw new MissingMethodException(typeof(NetSegmentMutationPatch).FullName, "Harmony callbacks");

                _harmony = new Harmony(PatchId);
                _harmony.Unpatch(_createOriginal, HarmonyPatchType.All, PatchId);
                _harmony.Unpatch(_releaseOriginal, HarmonyPatchType.All, PatchId);
                _harmony.Patch(_createOriginal, null, new HarmonyMethod(createPostfix));
                _harmony.Patch(
                    _releaseOriginal,
                    new HarmonyMethod(releasePrefix),
                    new HarmonyMethod(releasePostfix));
                DiagnosticLog.Info(
                    "SUCCESS",
                    "net_segment_mutation_hook_installed",
                    "Installed create and release hooks; only changed segment endpoints are queued for IMT updates",
                    "create_hook", "NetManager.CreateSegment(TreeInfo overload)",
                    "release_hook", "NetManager.ReleaseSegment(ushort, bool)");
            }
            catch (Exception error)
            {
                if (_harmony != null)
                {
                    if (_createOriginal != null)
                        _harmony.Unpatch(_createOriginal, HarmonyPatchType.All, PatchId);
                    if (_releaseOriginal != null)
                        _harmony.Unpatch(_releaseOriginal, HarmonyPatchType.All, PatchId);
                }
                _harmony = null;
                _createOriginal = null;
                _releaseOriginal = null;
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "net_segment_mutation_hook_install_failed",
                    "Changed road nodes will not receive event-driven IMT updates until both hooks can be installed",
                    error);
            }
        }

        private static void CreatePostfix(bool __result, ushort segment, NetInfo info)
        {
            if (__result && segment != 0)
                NetSegmentCreationHook.NotifyCreated(segment, info);
        }

        private static void ReleasePrefix(ushort segment, out ReleaseState __state)
        {
            __state = new ReleaseState { SegmentId = segment };
            NetManager manager = NetManager.instance;
            if (manager == null || segment == 0 || segment >= manager.m_segments.m_size)
                return;
            ref NetSegment current = ref manager.m_segments.m_buffer[segment];
            __state.StartNode = current.m_startNode;
            __state.EndNode = current.m_endNode;
            __state.Info = current.Info;
        }

        private static void ReleasePostfix(ReleaseState __state)
        {
            if (__state == null || __state.SegmentId == 0) return;
            NetSegmentCreationHook.NotifyReleased(
                __state.SegmentId,
                __state.StartNode,
                __state.EndNode,
                __state.Info);
        }
    }
}

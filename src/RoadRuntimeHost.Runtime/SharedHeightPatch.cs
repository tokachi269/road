using System;
using System.Reflection;
using CitiesHarmony.API;
using HarmonyLib;
using ColossalFramework.Math;

namespace RoadRuntimeHost.Runtime
{
    internal static class SharedHeightPatch
    {
        private static readonly string Id = "RoadRuntimeHost.SharedHeight." + typeof(SharedHeightPatch).Module.ModuleVersionId.ToString("N");
        private static Harmony _harmony;
        private static MethodInfo _flags, _preview, _nodePreview, _bezier;
        private static bool _stopped;
        public static void Start() { _stopped = false; HarmonyHelper.DoOnHarmonyReady(Install); }
        public static void Stop()
        {
            _stopped = true; SharedHeightHook.Ready = false;
            if (_harmony != null) {
                if (_flags != null) _harmony.Unpatch(_flags, HarmonyPatchType.All, Id);
                if (_preview != null) _harmony.Unpatch(_preview, HarmonyPatchType.All, Id);
                if (_nodePreview != null) _harmony.Unpatch(_nodePreview, HarmonyPatchType.All, Id);
                if (_bezier != null) _harmony.Unpatch(_bezier, HarmonyPatchType.All, Id);
            }
            _harmony = null; _flags = null; _preview = null; _nodePreview = null; _bezier = null;
        }
        private static void Install()
        {
            if (_stopped || SharedHeightHook.Bridge == null || _harmony != null) {
                SharedHeightHook.ActivationPending = false; return;
            }
            try {
                Assembly ar = SharedHeightHook.Bridge.Assembly;
                foreach (Type native in new Type[] { typeof(NetSegment), typeof(NetNode) })
                    foreach (string name in new string[] { "RenderInstance", "CalculateGroupData", "PopulateGroupData" })
                        RequireArRenderer(native, name, ar);
                _flags = ar.GetType("AdaptiveRoads.Manager.NetSegmentExt", true).GetMethod("UpdateAllFlags", Type.EmptyTypes);
                _preview = ar.GetType("AdaptiveRoads.Patches.Segment.NetTool_RenderSegmentPatch", true)
                    .GetMethod("CheckFlags", BindingFlags.Static | BindingFlags.NonPublic);
                _nodePreview = ar.GetType("AdaptiveRoads.Patches.Node.NetTool_RenderNodePatch", true)
                    .GetMethod("CheckFlags", BindingFlags.Static | BindingFlags.NonPublic);
                _bezier = typeof(NetSegment).GetMethod("GenerateBezier", new Type[] { typeof(ushort), typeof(ushort),
                    typeof(Bezier3).MakeByRefType(), typeof(Bezier3).MakeByRefType() });
                if (_flags == null || _preview == null || _nodePreview == null || _bezier == null) throw new MissingMethodException("Shared-height consumer hooks are incompatible");
                _harmony = new Harmony(Id);
                _harmony.Patch(_flags, null, new HarmonyMethod(typeof(SharedHeightPatch).GetMethod("FlagsPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                _harmony.Patch(_preview, new HarmonyMethod(typeof(SharedHeightPatch).GetMethod("PreviewPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                _harmony.Patch(_nodePreview, new HarmonyMethod(typeof(SharedHeightPatch).GetMethod("NodePreviewPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                _harmony.Patch(_bezier, null, new HarmonyMethod(typeof(SharedHeightPatch).GetMethod("BezierPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                SharedHeightHook.Ready = true;
                DiagnosticLog.Info("SUCCESS", "shared_height_hooks_installed", "AR individual state and shared height consumers are connected");
            } catch (Exception error) { Stop(); DiagnosticLog.Error("MOD_COMPATIBILITY", "shared_height_hooks_failed", "Height sharing was not activated", error); }
            finally { SharedHeightHook.ActivationPending = false; }
        }
        private static void RequireArRenderer(Type native, string name, Assembly ar)
        {
            foreach (MethodInfo method in native.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                if (method.Name != name) continue;
                Patches patches = Harmony.GetPatchInfo(method);
                if (patches == null) continue;
                foreach (Patch patch in patches.Transpilers)
                    if (patch.PatchMethod.DeclaringType != null && patch.PatchMethod.DeclaringType.Assembly == ar) return;
            }
            throw new InvalidOperationException("Adaptive Roads renderer is not active: " + native.Name + "." + name);
        }
        private static bool NodePreviewPrefix(NetInfo info, NetInfo.Node nodeInfo, ref bool __result)
        {
            bool visible;
            if (!SharedHeightHook.PreviewNode(info, nodeInfo, out visible)) return true;
            __result = visible; return false;
        }
        // Harmony injects the public field directly. No boxing of the AR
        // NetSegmentExt struct or per-call reflection is needed.
        private static void FlagsPostfix(ushort ___SegmentID)
        {
            try { SharedHeightHook.Rebuild(___SegmentID); }
            catch (Exception error) { DiagnosticLog.Error("MOD_CONTRACT", "shared_height_rebuild_failed", "Individual height could not be reapplied", error); }
        }
        private static bool PreviewPrefix(NetInfo info, NetInfo.Segment segmentInfo, ref bool __result)
        {
            bool visible;
            if (!SharedHeightHook.PreviewSegment(info, segmentInfo, out visible)) return true;
            __result = visible; return false;
        }
        private static void BezierPostfix(ushort segmentID, ushort startNodeID, ref NetSegment __instance,
            ref Bezier3 leftBezier, ref Bezier3 rightBezier, ref Bezier3 __result)
        {
            SharedHeightHook.Data data = SharedHeightHook.Find(__instance.Info);
            if (data == null) return;
            float start, end;
            bool invert = (__instance.m_flags & NetSegment.Flags.Invert) != 0;
            if (!SharedHeightHook.TryLevel(__instance.Info, segmentID, true, invert, out start)
                || !SharedHeightHook.TryLevel(__instance.Info, segmentID, false, invert, out end)) return;
            bool reverse = __instance.m_startNode != startNodeID;
            float native = __instance.Info.m_surfaceLevel;
            leftBezier = HeightTransitionKernel.OffsetCurve(leftBezier, start, end, native, reverse);
            rightBezier = HeightTransitionKernel.OffsetCurve(rightBezier, start, end, native, reverse);
            __result = HeightTransitionKernel.OffsetCurve(__result, start, end, native, reverse);
        }
    }
}

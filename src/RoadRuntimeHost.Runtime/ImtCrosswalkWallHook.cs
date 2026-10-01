using System;
using System.Collections.Generic;
using System.Reflection;
using CitiesHarmony.API;
using HarmonyLib;
using IMT.Manager;
using ModsCommon.Utilities;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal static class ImtCrosswalkWallHook
    {
        private const string PatchTypeName =
            "RoadRuntimeHost.Runtime.ImtCrosswalkWallPatch";
        private static readonly object TargetSync = new object();
        private static volatile HashSet<NetInfo> _targets = new HashSet<NetInfo>();
        private static bool _startAttempted;

        public static bool HasTargets { get { return _targets.Count != 0; } }

        public static bool ContainsTarget(NetInfo info)
        {
            return info != null && _targets.Contains(info);
        }

        public static void RegisterTarget(NetInfo info)
        {
            if (info == null) return;
            lock (TargetSync)
            {
                HashSet<NetInfo> targets = new HashSet<NetInfo>(_targets);
                targets.Add(info);
                _targets = targets;
            }
            if (_startAttempted) return;
            _startAttempted = true;
            InvokePatch("Start");
        }

        public static void Stop()
        {
            lock (TargetSync) _targets = new HashSet<NetInfo>();
            if (_startAttempted) InvokePatch("Stop");
            _startAttempted = false;
        }

        private static void InvokePatch(string methodName)
        {
            try
            {
                Type patchType = typeof(ImtCrosswalkWallHook).Assembly.GetType(
                    PatchTypeName, true);
                MethodInfo method = patchType.GetMethod(
                    methodName,
                    BindingFlags.Static | BindingFlags.Public
                        | BindingFlags.NonPublic);
                if (method == null)
                    throw new MissingMethodException(PatchTypeName, methodName);
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
                "imt_crosswalk_wall_hook_load_failed",
                "Crosswalks remain on native IMT bounds because the wall-alignment hook could not be loaded",
                error);
        }
    }

    // IMT's public API can create a crosswalk, but cannot give that crosswalk
    // independent lateral limits. Patch the private boundary-construction
    // method so the crosswalk line, side borders, clipping contour, and later
    // dash generation all use one wall-aligned extent. Never rewrite the
    // resulting per-dash decals.
    internal static class ImtCrosswalkWallPatch
    {
        private static readonly string PatchId =
            "RoadRuntimeHost.ImtCrosswalkWall."
            + typeof(ImtCrosswalkWallPatch).Module.ModuleVersionId.ToString("N");
        private static Harmony _harmony;
        private static MethodInfo _boundaryOriginal;
        private static MethodInfo _zebraDashesOriginal;
        private static MethodInfo _rightBorderTrajectorySetter;
        private static MethodInfo _leftBorderTrajectorySetter;
        private static bool _stopped = true;
        private static bool _applyFailureReported;

        public static void Start()
        {
            _stopped = false;
            HarmonyHelper.DoOnHarmonyReady(Install);
        }

        public static void Stop()
        {
            _stopped = true;
            if (_harmony != null && _boundaryOriginal != null)
            {
                _harmony.Unpatch(
                    _boundaryOriginal, HarmonyPatchType.All, PatchId);
                if (_zebraDashesOriginal != null)
                    _harmony.Unpatch(
                        _zebraDashesOriginal, HarmonyPatchType.All, PatchId);
                DiagnosticLog.Info(
                    "MOD",
                    "imt_crosswalk_wall_hook_removed",
                    "IMT crosswalk wall-alignment hook was removed");
            }
            _harmony = null;
            _boundaryOriginal = null;
            _zebraDashesOriginal = null;
            _rightBorderTrajectorySetter = null;
            _leftBorderTrajectorySetter = null;
            _applyFailureReported = false;
        }

        private static void Install()
        {
            if (_stopped || !ImtCrosswalkWallHook.HasTargets) return;
            try
            {
                Assembly imtAssembly = typeof(MarkingCrosswalk).Assembly;
                Version version = imtAssembly.GetName().Version;
                if (version == null || version.Major != 1 || version.Minor != 15)
                    throw new NotSupportedException(
                        "IMT crosswalk wall alignment supports 1.15.x only; loaded "
                        + (version == null ? "<unknown>" : version.ToString()));

                _boundaryOriginal = typeof(MarkingCrosswalk).GetMethod(
                    "GetTrajectory",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (_boundaryOriginal == null)
                    throw new MissingMethodException(
                        typeof(MarkingCrosswalk).FullName,
                        "GetTrajectory");

                _rightBorderTrajectorySetter = RequirePrivateSetter(
                    typeof(MarkingCrosswalk), "RightBorderTrajectory");
                _leftBorderTrajectorySetter = RequirePrivateSetter(
                    typeof(MarkingCrosswalk), "LeftBorderTrajectory");

                _zebraDashesOriginal = typeof(ZebraCrosswalkStyle).GetMethod(
                    "GetDashes",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new Type[]
                    {
                        typeof(MarkingCrosswalk),
                        typeof(StraightTrajectory)
                    },
                    null);
                if (_zebraDashesOriginal == null)
                    throw new MissingMethodException(
                        typeof(ZebraCrosswalkStyle).FullName,
                        "GetDashes");

                MethodInfo prefix = typeof(ImtCrosswalkWallPatch).GetMethod(
                    "Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo zebraDashesPrefix =
                    typeof(ImtCrosswalkWallPatch).GetMethod(
                        "ZebraDashesPrefix",
                        BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null)
                    throw new MissingMethodException(
                        typeof(ImtCrosswalkWallPatch).FullName, "Prefix");
                if (zebraDashesPrefix == null)
                    throw new MissingMethodException(
                        typeof(ImtCrosswalkWallPatch).FullName,
                        "ZebraDashesPrefix");

                _harmony = new Harmony(PatchId);
                _harmony.Unpatch(
                    _boundaryOriginal, HarmonyPatchType.All, PatchId);
                _harmony.Unpatch(
                    _zebraDashesOriginal, HarmonyPatchType.All, PatchId);
                _harmony.Patch(
                    _boundaryOriginal, new HarmonyMethod(prefix));
                _harmony.Patch(
                    _zebraDashesOriginal,
                    new HarmonyMethod(zebraDashesPrefix));
                _applyFailureReported = false;
                DiagnosticLog.Info(
                    "SUCCESS",
                    "imt_crosswalk_wall_hook_installed",
                    "Installed IMT crosswalk-boundary and stable dash-count hooks for target roads",
                    "imt_version", version.ToString(),
                    "hook_methods",
                    "MarkingCrosswalk.GetTrajectory,ZebraCrosswalkStyle.GetDashes");
            }
            catch (Exception error)
            {
                if (_harmony != null)
                {
                    if (_boundaryOriginal != null)
                        _harmony.Unpatch(
                            _boundaryOriginal,
                            HarmonyPatchType.All,
                            PatchId);
                    if (_zebraDashesOriginal != null)
                        _harmony.Unpatch(
                            _zebraDashesOriginal,
                            HarmonyPatchType.All,
                            PatchId);
                }
                _harmony = null;
                _boundaryOriginal = null;
                _zebraDashesOriginal = null;
                _rightBorderTrajectorySetter = null;
                _leftBorderTrajectorySetter = null;
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "imt_crosswalk_wall_hook_install_failed",
                    "Crosswalks use native IMT bounds and spacing because the version-gated hooks could not be installed",
                    error);
            }
        }

        private static MethodInfo RequirePrivateSetter(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic);
            MethodInfo setter = property == null ? null : property.GetSetMethod(true);
            if (setter == null)
                throw new MissingMethodException(type.FullName, "set_" + name);
            return setter;
        }

        private static bool Prefix(
            MarkingCrosswalk __instance,
            ref StraightTrajectory __result)
        {
            try
            {
                Entrance entrance = __instance.EnterLine.Start.Enter;
                ref NetSegment segment = ref entrance.GetSegment();
                if (!ImtCrosswalkWallHook.ContainsTarget(segment.Info)) return true;
                if (__instance.RightBorder.Value != null
                    || __instance.LeftBorder.Value != null)
                    return true;

                Vector3 normal = __instance.NormalDir;
                float totalWidth = __instance.TotalWidth;
                StraightTrajectory native = __instance.GetOffsetTrajectory(totalWidth);
                float startT;
                float endT;
                if (!CrosswalkWallGeometry.TryGetSpan(
                        native.StartPosition,
                        native.EndPosition,
                        entrance.FirstPointSide,
                        entrance.LastPointSide,
                        normal,
                        out startT,
                        out endT))
                    return true;

                StraightTrajectory rightBorder = new StraightTrajectory(
                    entrance.FirstPointSide,
                    entrance.FirstPointSide + normal * totalWidth,
                    false);
                StraightTrajectory leftBorder = new StraightTrajectory(
                    entrance.LastPointSide,
                    entrance.LastPointSide + normal * totalWidth,
                    false);
                _rightBorderTrajectorySetter.Invoke(
                    __instance, new object[] { rightBorder });
                _leftBorderTrajectorySetter.Invoke(
                    __instance, new object[] { leftBorder });
                __result = native.Cut(startT, endT);
                return false;
            }
            catch (Exception error)
            {
                if (!_applyFailureReported)
                {
                    _applyFailureReported = true;
                    DiagnosticLog.Error(
                        "MOD_COMPATIBILITY",
                        "imt_crosswalk_wall_hook_apply_failed",
                        "IMT used its native crosswalk bounds because wall-aligned boundary construction failed; repeated failures are suppressed",
                        error);
                }
                return true;
            }
        }

        private static bool ZebraDashesPrefix(
            ZebraCrosswalkStyle __instance,
            MarkingCrosswalk crosswalk,
            StraightTrajectory trajectory,
            ref List<StyleHelper.PartT> __result)
        {
            try
            {
                Entrance entrance = crosswalk.EnterLine.Start.Enter;
                ref NetSegment segment = ref entrance.GetSegment();
                if (!ImtCrosswalkWallHook.ContainsTarget(segment.Info)
                    || crosswalk.RightBorder.Value != null
                    || crosswalk.LeftBorder.Value != null
                    || __instance.UseGap.Value
                    || __instance.DashType.Value
                        == ZebraCrosswalkStyle.DashEnd.NotParallel)
                    return true;

                float angleScale = Mathf.Sin(
                    crosswalk.CornerAndNormalAngle);
                if (angleScale <= 0.00001f) return true;
                float dashLength = __instance.DashLength.Value / angleScale;
                float spaceLength = __instance.SpaceLength.Value / angleScale;
                int partCount = CrosswalkWallGeometry.GetStableDashCount(
                    entrance.RoadHalfWidth * 2f,
                    __instance.DashLength.Value,
                    __instance.SpaceLength.Value);
                if (partCount <= 0 || trajectory.Length <= 0f)
                {
                    __result = new List<StyleHelper.PartT>();
                    return false;
                }

                float startSpace = (
                    trajectory.Length + spaceLength
                    - (dashLength + spaceLength) * partCount) * 0.5f;
                float startT = startSpace / trajectory.Length;
                float partT = dashLength / trajectory.Length;
                float spaceT = spaceLength / trajectory.Length;
                List<StyleHelper.PartT> parts =
                    new List<StyleHelper.PartT>(partCount);
                for (int i = 0; i < partCount; ++i)
                {
                    float partStart = startT + (partT + spaceT) * i;
                    parts.Add(new StyleHelper.PartT(
                        partStart, partStart + partT));
                }
                __result = parts;
                return false;
            }
            catch (Exception error)
            {
                if (!_applyFailureReported)
                {
                    _applyFailureReported = true;
                    DiagnosticLog.Error(
                        "MOD_COMPATIBILITY",
                        "imt_crosswalk_wall_hook_apply_failed",
                        "IMT used its native zebra spacing because stable wall-width dash calculation failed; repeated failures are suppressed",
                        error);
                }
                return true;
            }
        }
    }
}

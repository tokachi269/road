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
    // independent lateral limits. Patch the public IMT implementation method
    // before dash generation; never rewrite the resulting per-dash decals.
    internal static class ImtCrosswalkWallPatch
    {
        private static readonly string PatchId =
            "RoadRuntimeHost.ImtCrosswalkWall."
            + typeof(ImtCrosswalkWallPatch).Module.ModuleVersionId.ToString("N");
        private static Harmony _harmony;
        private static MethodInfo _original;
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
            if (_harmony != null && _original != null)
            {
                _harmony.Unpatch(_original, HarmonyPatchType.All, PatchId);
                DiagnosticLog.Info(
                    "MOD",
                    "imt_crosswalk_wall_hook_removed",
                    "IMT crosswalk wall-alignment hook was removed");
            }
            _harmony = null;
            _original = null;
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

                _original = typeof(MarkingCrosswalk).GetMethod(
                    "GetFullTrajectory",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new Type[] { typeof(float), typeof(Vector3) },
                    null);
                if (_original == null)
                    throw new MissingMethodException(
                        typeof(MarkingCrosswalk).FullName,
                        "GetFullTrajectory");

                MethodInfo prefix = typeof(ImtCrosswalkWallPatch).GetMethod(
                    "Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null)
                    throw new MissingMethodException(
                        typeof(ImtCrosswalkWallPatch).FullName, "Prefix");

                _harmony = new Harmony(PatchId);
                _harmony.Unpatch(_original, HarmonyPatchType.All, PatchId);
                _harmony.Patch(_original, new HarmonyMethod(prefix));
                _applyFailureReported = false;
                DiagnosticLog.Info(
                    "SUCCESS",
                    "imt_crosswalk_wall_hook_installed",
                    "Installed one pre-dash IMT trajectory hook for target-road crosswalk wall alignment",
                    "imt_version", version.ToString(),
                    "hook_method", "MarkingCrosswalk.GetFullTrajectory");
            }
            catch (Exception error)
            {
                _harmony = null;
                _original = null;
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "imt_crosswalk_wall_hook_install_failed",
                    "Crosswalks remain on native IMT bounds because the version-gated trajectory hook could not be installed",
                    error);
            }
        }

        private static bool Prefix(
            MarkingCrosswalk __instance,
            float offset,
            Vector3 normal,
            ref StraightTrajectory __result)
        {
            try
            {
                Entrance entrance = __instance.EnterLine.Start.Enter;
                ref NetSegment segment = ref entrance.GetSegment();
                if (!ImtCrosswalkWallHook.ContainsTarget(segment.Info)) return true;

                StraightTrajectory native = __instance.GetOffsetTrajectory(offset);
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
                        "IMT used its native crosswalk bounds because wall-alignment trajectory calculation failed; repeated failures are suppressed",
                        error);
                }
                return true;
            }
        }
    }
}

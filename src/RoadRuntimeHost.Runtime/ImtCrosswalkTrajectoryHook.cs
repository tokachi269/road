using System;
using System.Collections.Generic;
using System.Reflection;
using CitiesHarmony.API;
using HarmonyLib;
using IMT.Manager;
using IMT.Utilities;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal static class ImtCrosswalkTrajectoryHook
    {
        private const string PatchTypeName = "RoadRuntimeHost.Runtime.ImtCrosswalkTrajectoryPatch";
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
                Type patchType = typeof(ImtCrosswalkTrajectoryHook).Assembly.GetType(PatchTypeName, true);
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
                "imt_crosswalk_hook_load_failed",
                "IMT crosswalk trajectories remain native because the single-hook integration could not be loaded",
                error);
        }
    }

    // This type is loaded only after a target road is registered. Keeping the
    // Harmony and IMT-internal references behind that boundary lets validation
    // and shutdown run without loading optional game-side assemblies.
    internal static class ImtCrosswalkTrajectoryPatch
    {
        private static readonly string PatchId = "RoadRuntimeHost.ImtCrosswalkTrajectory."
            + typeof(ImtCrosswalkTrajectoryPatch).Module.ModuleVersionId.ToString("N");
        private static Harmony _harmony;
        private static MethodInfo _original;
        private static FieldInfo _decalPoints;
        private static FieldInfo _decalTextureData;
        private static FieldInfo _decalEffectData;
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
                    "imt_crosswalk_hook_removed",
                    "IMT crosswalk trajectory hook was removed");
            }
            _harmony = null;
            _original = null;
            _decalPoints = null;
            _decalTextureData = null;
            _decalEffectData = null;
            _applyFailureReported = false;
        }

        private static void Install()
        {
            if (_stopped || !ImtCrosswalkTrajectoryHook.HasTargets) return;
            try
            {
                Assembly imtAssembly = typeof(MarkingCrosswalk).Assembly;
                Version version = imtAssembly.GetName().Version;
                if (version == null || version.Major != 1 || version.Minor != 15)
                    throw new NotSupportedException(
                        "IMT crosswalk hook supports 1.15.x only; loaded "
                        + (version == null ? "<unknown>" : version.ToString()));

                Type actionType = typeof(Action<IStyleData>);
                _original = typeof(ZebraCrosswalkStyle).GetMethod(
                    "CalculateImpl",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(MarkingCrosswalk), typeof(MarkingLOD), actionType },
                    null);
                if (_original == null)
                    throw new MissingMethodException(typeof(ZebraCrosswalkStyle).FullName, "CalculateImpl");

                _decalPoints = RequireDecalField("points");
                _decalTextureData = RequireDecalField("textureData");
                _decalEffectData = RequireDecalField("effectData");

                _harmony = new Harmony(PatchId);
                _harmony.Unpatch(_original, HarmonyPatchType.All, PatchId);
                MethodInfo prefix = typeof(ImtCrosswalkTrajectoryPatch).GetMethod(
                    "Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null)
                    throw new MissingMethodException(typeof(ImtCrosswalkTrajectoryPatch).FullName, "Prefix");
                _harmony.Patch(_original, new HarmonyMethod(prefix));
                _applyFailureReported = false;
                DiagnosticLog.Info(
                    "SUCCESS",
                    "imt_crosswalk_hook_installed",
                    "Installed one IMT recalculation hook for target-road crosswalk trajectories",
                    "imt_version", version.ToString(),
                    "hook_method", "ZebraCrosswalkStyle.CalculateImpl");
            }
            catch (Exception error)
            {
                _harmony = null;
                _original = null;
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "imt_crosswalk_hook_install_failed",
                    "IMT crosswalk trajectories remain native because the single recalculation hook could not be installed",
                    error);
            }
        }

        private static FieldInfo RequireDecalField(string name)
        {
            FieldInfo field = typeof(DecalData).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(typeof(DecalData).FullName, name);
            return field;
        }

        private static void Prefix(MarkingCrosswalk crosswalk, ref Action<IStyleData> addData)
        {
            Entrance entrance = crosswalk.EnterLine.Start.Enter;
            ref NetSegment segment = ref entrance.GetSegment();
            if (!ImtCrosswalkTrajectoryHook.ContainsTarget(segment.Info) || addData == null) return;

            Action<IStyleData> nativeAddData = addData;
            addData = delegate(IStyleData data)
            {
                IStyleData output = data;
                try
                {
                    output = ExtendDecal(data, entrance);
                }
                catch (Exception error)
                {
                    if (!_applyFailureReported)
                    {
                        _applyFailureReported = true;
                        DiagnosticLog.Error(
                            "MOD_COMPATIBILITY",
                            "imt_crosswalk_hook_apply_failed",
                            "IMT returned native zebra decal data because wall alignment failed; repeated failures are suppressed",
                            error);
                    }
                }
                nativeAddData(output);
            };
        }

        private static IStyleData ExtendDecal(IStyleData styleData, Entrance entrance)
        {
            if (!(styleData is DecalData)) return styleData;
            DecalData decal = (DecalData)styleData;
            Vector3[] points = DecodeWorldPoints(decal);
            if (!CrosswalkTrajectoryGeometry.ExtendPolygonToBoundaries(
                points,
                entrance.Position,
                entrance.FirstPointSide,
                entrance.LastPointSide,
                entrance.CornerDir,
                entrance.NormalDir)) return styleData;

            DecalData.TextureData textureData = (DecalData.TextureData)_decalTextureData.GetValue(decal);
            DecalData.EffectData effectData = (DecalData.EffectData)_decalEffectData.GetValue(decal);
            return new DecalData(
                DecalData.DecalType.Crosswalk,
                decal.LOD,
                points,
                CrosswalkTrajectoryGeometry.DecodeSourceColor(decal.color, textureData.mainTexture != null),
                textureData,
                effectData);
        }

        private static Vector3[] DecodeWorldPoints(DecalData decal)
        {
            Vector4[] packed = (Vector4[])_decalPoints.GetValue(decal);
            if (packed == null || packed.Length == 0)
                throw new InvalidOperationException("IMT crosswalk decal did not contain polygon points");

            List<Vector2> normalized = new List<Vector2>(packed.Length * 2);
            foreach (Vector4 pair in packed)
            {
                normalized.Add(new Vector2(pair.x, pair.y));
                normalized.Add(new Vector2(pair.z, pair.w));
            }
            while (normalized.Count > 3
                && (normalized[normalized.Count - 1] - normalized[normalized.Count - 2]).sqrMagnitude < 0.0000001f)
                normalized.RemoveAt(normalized.Count - 1);

            Vector3[] result = new Vector3[normalized.Count];
            for (int index = 0; index < result.Length; ++index)
            {
                Vector2 point = normalized[index];
                Vector3 local = new Vector3(
                    (point.x - 0.5f) * decal.size.x,
                    0f,
                    (point.y - 0.5f) * decal.size.z);
                result[index] = decal.position + decal.rotation * local;
            }
            return result;
        }
    }
}

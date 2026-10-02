using System;
using System.IO;
using System.Reflection;
using RoadRuntimeHost.Runtime;
using RoadRuntimeHost.Loader;
using ICities;
using UnityEngine;

namespace RoadRuntimeHost.ContractSmoke
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("usage: RoadRuntimeHost.ContractSmoke <preview-path>");
                return 2;
            }
            try
            {
                if (!typeof(IUserMod).IsAssignableFrom(typeof(RoadRuntimeHostMod))) throw new InvalidOperationException("loader mod entry is missing");
                if (!typeof(ILoadingExtension).IsAssignableFrom(typeof(RoadRuntimeHostLoading))) throw new InvalidOperationException("loader loading entry is missing");
                if (!typeof(IThreadingExtension).IsAssignableFrom(typeof(RoadRuntimeHostThreading))) throw new InvalidOperationException("loader threading entry is missing");
                Type runtime = typeof(RuntimeEntry);
                if (runtime.GetMethod("Start", new Type[] { typeof(string), typeof(string), typeof(string) }) == null
                    || runtime.GetMethod("Tick", new Type[] { typeof(float), typeof(float) }) == null
                    || runtime.GetMethod("Stop", Type.EmptyTypes) == null)
                    throw new InvalidOperationException("hot runtime reflection contract is incomplete");
                ValidatePrefabInspectionContract(runtime.Assembly);
                ValidatePackedTextureContract(runtime.Assembly);
                ValidateReferenceLanePropContract(runtime.Assembly);
                ValidateCrosswalkWallRenderingContract(runtime.Assembly);
                ValidateImtNodePolicyContract(runtime.Assembly);
                ValidateRoadPlacementMarkingContract(runtime.Assembly);
                ValidateRoadToolbarRefreshContract(runtime.Assembly);
                LaneOwnershipContract.Validate(runtime.Assembly);
                string temp = Path.Combine(Path.GetTempPath(), "RoadRuntimeHost.ContractSmoke." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    Type coordinator = typeof(RoadRuntimeHostMod).Assembly.GetType("RoadRuntimeHost.Loader.HotReloadCoordinator", true);
                    MethodInfo resolveRoot = coordinator.GetMethod("ResolveRootPath", BindingFlags.Static | BindingFlags.NonPublic, null, new Type[] { typeof(string), typeof(string) }, null);
                    if (resolveRoot == null) throw new InvalidOperationException("loader root path resolver is missing");
                    string pluginResolved = (string)resolveRoot.Invoke(null, new object[] { temp, string.Empty });
                    if (!string.Equals(pluginResolved, Path.GetFullPath(temp).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("PluginManager root path was not preferred");
                    string assemblyResolved = (string)resolveRoot.Invoke(null, new object[] { string.Empty, Path.Combine(temp, "RoadRuntimeHost.Loader.dll") });
                    if (!string.Equals(assemblyResolved, Path.GetFullPath(temp).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("assembly location fallback did not resolve the containing directory");
                    bool missingRootRejected = false;
                    try { resolveRoot.Invoke(null, new object[] { string.Empty, string.Empty }); }
                    catch (TargetInvocationException error) { missingRootRejected = error.InnerException is InvalidOperationException; }
                    if (!missingRootRejected) throw new InvalidOperationException("missing loader root inputs were accepted");

                    string log = Path.Combine(temp, "diagnostics.jsonl");
                    Type loaderLog = typeof(RoadRuntimeHostMod).Assembly.GetType("RoadRuntimeHost.Loader.DiagnosticLog", true);
                    loaderLog.GetMethod("Configure", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { log });
                    loaderLog.GetMethod("Info", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { "MOD", "contract_loader_probe", "Loader diagnostic probe", new string[] { "probe", "true" } });

                    string suppressionRoot = Path.Combine(temp, "failure-suppression");
                    Directory.CreateDirectory(suppressionRoot);
                    File.WriteAllText(Path.Combine(suppressionRoot, "catalog.json"), "{");
                    File.WriteAllText(Path.Combine(suppressionRoot, "manifest.json"), "{");
                    string suppressionLog = Path.Combine(temp, "failure-suppression.jsonl");
                    RuntimeEntry pollingRuntime = new RuntimeEntry();
                    pollingRuntime.Start(suppressionRoot, "ContractSmoke", suppressionLog);
                    pollingRuntime.Tick(1.1f, 0f);
                    string failureContents = File.ReadAllText(suppressionLog);
                    if (CountOccurrences(failureContents, "\"event\":\"catalog_apply_failed\"") != 1
                        || CountOccurrences(failureContents, "\"event\":\"manifest_apply_failed\"") != 1)
                        throw new InvalidOperationException("identical catalog/manifest failures were not suppressed");
                    File.WriteAllText(Path.Combine(suppressionRoot, "catalog.json"), "{\"schema_version\":1,\"revision\":\"fixed\",\"roads\":[],\"props\":[],\"conditions\":[],\"test_scenarios\":[]}");
                    File.WriteAllText(Path.Combine(suppressionRoot, "manifest.json"), "{\"schema_version\":1,\"revision\":\"fixed\",\"roads\":[]}");
                    pollingRuntime.Tick(1.1f, 0f);
                    string recoveryContents = File.ReadAllText(suppressionLog);
                    if (!recoveryContents.Contains("catalog_apply_recovered") || !recoveryContents.Contains("manifest_apply_recovered"))
                        throw new InvalidOperationException("catalog/manifest recovery was not logged");

                    string rejectedRoadRoot = Path.Combine(temp, "rejected-road");
                    WriteLaneContractPreview(rejectedRoadRoot, 3.25f);
                    string rejectedRoadLog = Path.Combine(temp, "rejected-road.jsonl");
                    RuntimeEntry rejectedRoadRuntime = new RuntimeEntry();
                    rejectedRoadRuntime.Start(rejectedRoadRoot, "ContractSmoke", rejectedRoadLog);
                    rejectedRoadRuntime.Tick(1.1f, 0f);
                    rejectedRoadRuntime.Tick(1.1f, 0f);
                    string rejectedRoadContents = File.ReadAllText(rejectedRoadLog);
                    if (CountOccurrences(rejectedRoadContents, "\"event\":\"road_apply_begin\"") != 1
                        || CountOccurrences(rejectedRoadContents, "\"event\":\"road_apply_failed\"") != 1
                        || CountOccurrences(rejectedRoadContents, "\"event\":\"manifest_changed\"") != 1
                        || CountOccurrences(rejectedRoadContents, "\"event\":\"manifest_processed_with_rejected_roads\"") != 1)
                        throw new InvalidOperationException("rejected road input was retried without an input change");
                    rejectedRoadRuntime.Stop();

                    string result = RuntimeEntry.ValidatePreview(args[0], log);
                    string matched = Path.Combine(temp, "matched");
                    WriteLaneContractPreview(matched, 3.0f);
                    RuntimeEntry.ValidatePreview(matched, log);
                    string missingCornerOffset = Path.Combine(temp, "missing-corner-offset");
                    WriteLaneContractPreview(missingCornerOffset, 3.0f);
                    string missingCornerBundle = Path.Combine(Path.Combine(missingCornerOffset, "roads"), "matched-road.json");
                    File.WriteAllText(
                        missingCornerBundle,
                        File.ReadAllText(missingCornerBundle).Replace(",\"node_min_corner_offset\":12", string.Empty));
                    bool missingCornerRejected = false;
                    try { RuntimeEntry.ValidatePreview(missingCornerOffset, log); }
                    catch { missingCornerRejected = true; }
                    if (!missingCornerRejected) throw new InvalidOperationException("missing node min corner offset was accepted");
                    string mismatched = Path.Combine(temp, "mismatched");
                    WriteLaneContractPreview(mismatched, 3.25f);
                    bool mismatchRejected = false;
                    try { RuntimeEntry.ValidatePreview(mismatched, log); }
                    catch { mismatchRejected = true; }
                    if (!mismatchRejected) throw new InvalidOperationException("lane contract mismatch was accepted");
                    string malformed = Path.Combine(temp, "malformed");
                    Directory.CreateDirectory(malformed);
                    File.WriteAllText(Path.Combine(malformed, "catalog.json"), "{\"schema_version\":\"wrong\"}");
                    try { RuntimeEntry.ValidatePreview(malformed, log); }
                    catch { }
                    string contents = File.ReadAllText(log);
                    if (!contents.Contains("\"event\":\"preview_validation_success\"")
                        || !contents.Contains("\"category\":\"SUCCESS\"")
                        || !contents.Contains("\"category\":\"CONTRACT_TYPE\"")
                        || !contents.Contains("lane_contract_mismatch")
                        || !contents.Contains("lane-main.width blender=3.25 catalog=3")
                        || !contents.Contains("\"component\":\"loader\"")
                        || !contents.Contains("\"event\":\"contract_loader_probe\""))
                        throw new InvalidOperationException("dedicated diagnostic log contract is incomplete");
                    Console.WriteLine("RUNTIME_CONTRACT_OK " + result + ", diagnostic_log=ok");
                }
                finally { Directory.Delete(temp, true); }
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void ValidatePackedTextureContract(Assembly runtimeAssembly)
        {
            Type updater = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.PrefabUpdater", true);
            MethodInfo packApr = updater.GetMethod("PackAprPixels", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo packXys = updater.GetMethod("PackXysPixels", BindingFlags.Static | BindingFlags.NonPublic);
            if (packApr == null || packXys == null)
                throw new InvalidOperationException("runtime texture packers are missing");
            Color32[] alpha = { new Color32(10, 10, 10, 255) };
            Color32[] pavement = { new Color32(20, 20, 20, 255) };
            Color32[] road = { new Color32(30, 30, 30, 255) };
            Color32[] apr = (Color32[])packApr.Invoke(null, new object[] { alpha, pavement, road, 1 });
            if (apr[0].r != 245 || apr[0].g != 235 || apr[0].b != 30 || apr[0].a != 255)
                throw new InvalidOperationException("APR channel packing disagrees with the CS1 texture contract");
            Color32[] aprDefaults = (Color32[])packApr.Invoke(null, new object[] { null, null, null, 1 });
            if (aprDefaults[0].r != 0 || aprDefaults[0].g != 255 || aprDefaults[0].b != 0)
                throw new InvalidOperationException("APR default channels are invalid");
            Color32[] normal = { new Color32(40, 50, 255, 255) };
            Color32[] specular = { new Color32(60, 60, 60, 255) };
            Color32[] xys = (Color32[])packXys.Invoke(null, new object[] { normal, specular, 1 });
            if (xys[0].r != 40 || xys[0].g != 50 || xys[0].b != 195 || xys[0].a != 255)
                throw new InvalidOperationException("XYS channel packing disagrees with the CS1 texture contract");
            Color32[] xysDefaults = (Color32[])packXys.Invoke(null, new object[] { null, null, 1 });
            if (xysDefaults[0].r != 128 || xysDefaults[0].g != 128 || xysDefaults[0].b != 255)
                throw new InvalidOperationException("XYS default channels are invalid");
        }

        private static void ValidateReferenceLanePropContract(Assembly runtimeAssembly)
        {
            Type updater = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.PrefabUpdater", true);
            if (updater.GetMethod(
                    "CopyReferenceLaneProps",
                    BindingFlags.Static | BindingFlags.NonPublic) == null
                || updater.GetMethod(
                    "IsRoadArrowProp",
                    BindingFlags.Static | BindingFlags.NonPublic) == null
                || updater.GetMethod(
                    "IsTrafficLightProp",
                    BindingFlags.Static | BindingFlags.NonPublic) == null)
                throw new InvalidOperationException(
                    "reference-road arrow and traffic-light prop transfer is missing");

            Type trafficPolicy = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.TmpeTrafficPolicy", true);
            if (trafficPolicy.GetMethod(
                    "TryEnableTrafficLight",
                    BindingFlags.Instance | BindingFlags.Public) == null)
                throw new InvalidOperationException(
                    "new intersections cannot request their default traffic light");
            if (trafficPolicy.GetMethod(
                    "TryEnableDefaultPedestrianCrossing",
                    BindingFlags.Instance | BindingFlags.Public) == null)
                throw new InvalidOperationException(
                    "new or copied intersections cannot request default pedestrian crossings");
        }

        private static void ValidateCrosswalkWallRenderingContract(Assembly runtimeAssembly)
        {
            if (runtimeAssembly.GetType("RoadRuntimeHost.Runtime.ImtCrosswalkTrajectoryPatch", false) != null)
                throw new InvalidOperationException(
                    "the removed per-decal crosswalk extension returned");

            Type hook = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.ImtCrosswalkWallPatch", true);
            MethodInfo prefix = hook.GetMethod(
                "Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo zebraDashesPrefix = hook.GetMethod(
                "ZebraDashesPrefix",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (prefix == null || zebraDashesPrefix == null)
                throw new InvalidOperationException(
                    "IMT crosswalk-boundary or stable dash-count hook is missing");
            ParameterInfo[] prefixParameters = prefix.GetParameters();
            if (prefixParameters.Length != 2
                || prefixParameters[0].ParameterType.FullName
                    != "IMT.Manager.MarkingCrosswalk"
                || !prefixParameters[1].ParameterType.IsByRef)
                throw new InvalidOperationException(
                    "IMT crosswalk-boundary hook does not replace one coherent trajectory result");

            Type imtCrosswalk = Type.GetType(
                "IMT.Manager.MarkingCrosswalk, IntersectionMarkingTool", true);
            Type zebraStyle = Type.GetType(
                "IMT.Manager.ZebraCrosswalkStyle, IntersectionMarkingTool", true);
            Type straightTrajectory = Type.GetType(
                "ModsCommon.Utilities.StraightTrajectory, IntersectionMarkingTool", true);
            MethodInfo boundaryBuilder = imtCrosswalk.GetMethod(
                "GetTrajectory",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            MethodInfo dashBuilder = zebraStyle.GetMethod(
                "GetDashes",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new Type[] { imtCrosswalk, straightTrajectory },
                null);
            PropertyInfo rightBorder = imtCrosswalk.GetProperty(
                "RightBorderTrajectory",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            PropertyInfo leftBorder = imtCrosswalk.GetProperty(
                "LeftBorderTrajectory",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (boundaryBuilder == null || dashBuilder == null
                || rightBorder == null || rightBorder.GetSetMethod(true) == null
                || leftBorder == null || leftBorder.GetSetMethod(true) == null)
                throw new InvalidOperationException(
                    "installed IMT does not expose the version-gated coherent boundary construction contract");

            Type geometry = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.CrosswalkWallGeometry", true);
            MethodInfo tryGetSpan = geometry.GetMethod(
                "TryGetSpan", BindingFlags.Static | BindingFlags.Public);
            MethodInfo stableDashCount = geometry.GetMethod(
                "GetStableDashCount", BindingFlags.Static | BindingFlags.Public);
            if (tryGetSpan == null || stableDashCount == null)
                throw new InvalidOperationException(
                    "crosswalk wall span or stable dash-count calculation is missing");

            if ((int)stableDashCount.Invoke(
                    null, new object[] { 7f, 0.45f, 0.55f }) != 7
                || (int)stableDashCount.Invoke(
                    null, new object[] { 6.9995f, 0.45f, 0.55f }) != 7
                || (int)stableDashCount.Invoke(
                    null, new object[] { 6.99f, 0.45f, 0.55f }) != 6)
                throw new InvalidOperationException(
                    "crosswalk dash count is unstable at an exact wall-width period boundary");

            object[] values = new object[]
            {
                new Vector3(-1f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(-3f, 0f, 2f),
                new Vector3(3f, 0f, 2f),
                new Vector3(0f, 0f, 1f),
                0f,
                0f
            };
            if (!(bool)tryGetSpan.Invoke(null, values)
                || Math.Abs((float)values[5] + 1f) > 0.0001f
                || Math.Abs((float)values[6] - 2f) > 0.0001f)
                throw new InvalidOperationException(
                    "crosswalk trajectory was not extended to both wall lines before dash generation");

            object[] parallel = new object[]
            {
                Vector3.zero,
                new Vector3(2f, 0f, 0f),
                Vector3.zero,
                new Vector3(2f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                0f,
                0f
            };
            if ((bool)tryGetSpan.Invoke(null, parallel))
                throw new InvalidOperationException(
                    "parallel wall directions must fall back to native IMT geometry");

            if (runtimeAssembly.GetType(
                    "RoadRuntimeHost.Runtime.ImtRestoreDefaultsPatch", false) == null)
                throw new InvalidOperationException(
                    "IMT road-default restore action is missing");
        }

        private static void ValidateImtNodePolicyContract(Assembly runtimeAssembly)
        {
            Type policy = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.ImtNodePolicy", true);
            MethodInfo corner = policy.GetMethod(
                "ShouldConnectRoadLines", BindingFlags.Static | BindingFlags.Public);
            MethodInfo crosswalk = policy.GetMethod(
                "ShouldCreateCrosswalk", BindingFlags.Static | BindingFlags.Public);
            MethodInfo expectedCrosswalks = policy.GetMethod(
                "ExpectedCrosswalkCount", BindingFlags.Static | BindingFlags.Public);
            MethodInfo stopLine = policy.GetMethod(
                "ShouldCreateStopLine", BindingFlags.Static | BindingFlags.Public);
            MethodInfo oppositePoint = policy.GetMethod(
                "OppositePointOrdinal", BindingFlags.Static | BindingFlags.Public);
            MethodInfo matchBoundaries = policy.GetMethod(
                "MatchTwoSegmentBoundaries", BindingFlags.Static | BindingFlags.Public);
            MethodInfo matchRoles = policy.GetMethod(
                "MatchBoundaryRoles", BindingFlags.Static | BindingFlags.Public);
            MethodInfo canConnect = policy.GetMethod(
                "CanConnectBoundaryRoles", BindingFlags.Static | BindingFlags.Public);
            MethodInfo connectorStyle = policy.GetMethod(
                "ConnectorStyle", BindingFlags.Static | BindingFlags.Public);
            if (corner == null || crosswalk == null || expectedCrosswalks == null
                || stopLine == null
                || oppositePoint == null || matchBoundaries == null
                || matchRoles == null || canConnect == null || connectorStyle == null)
                throw new InvalidOperationException("IMT node policy contract is incomplete");

            Type roleType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.ImtBoundaryRole", true);
            object roadside = Enum.Parse(roleType, "Roadside");
            object centerRole = Enum.Parse(roleType, "Center");
            object incoming = Enum.Parse(roleType, "SeparatorIncoming");
            object outgoing = Enum.Parse(roleType, "SeparatorOutgoing");
            if (!(bool)canConnect.Invoke(null, new object[] { roadside, roadside })
                || !(bool)canConnect.Invoke(null, new object[] { centerRole, centerRole })
                || !(bool)canConnect.Invoke(null, new object[] { incoming, outgoing })
                || !(bool)canConnect.Invoke(null, new object[] { outgoing, incoming })
                || (bool)canConnect.Invoke(null, new object[] { centerRole, outgoing })
                || (bool)canConnect.Invoke(null, new object[] { roadside, incoming }))
                throw new InvalidOperationException(
                    "roadside, separator, and center connector roles are mixed");

            if (!(bool)corner.Invoke(null, new object[] { 2 })
                || (bool)corner.Invoke(null, new object[] { 3 }))
                throw new InvalidOperationException("only two-segment nodes may connect road lines");
            if ((bool)crosswalk.Invoke(null, new object[] { 2, true, true })
                || (bool)crosswalk.Invoke(null, new object[] { 3, false, true })
                || (bool)crosswalk.Invoke(null, new object[] { 3, true, false })
                || !(bool)crosswalk.Invoke(null, new object[] { 3, true, true }))
                throw new InvalidOperationException("crosswalk policy must require a junction, pedestrian lane, and crossing permission");
            for (int entranceCount = 3; entranceCount <= 8; ++entranceCount)
            {
                int combinations = 1 << entranceCount;
                for (int pedestrianMask = 0; pedestrianMask < combinations; ++pedestrianMask)
                {
                    for (int allowedMask = 0; allowedMask < combinations; ++allowedMask)
                    {
                        bool[] pedestrians = new bool[entranceCount];
                        bool[] allowed = new bool[entranceCount];
                        int expected = 0;
                        for (int index = 0; index < entranceCount; ++index)
                        {
                            pedestrians[index] = (pedestrianMask & (1 << index)) != 0;
                            allowed[index] = (allowedMask & (1 << index)) != 0;
                            if (pedestrians[index] && allowed[index]) ++expected;
                        }
                        int actual = (int)expectedCrosswalks.Invoke(
                            null,
                            new object[] { entranceCount, pedestrians, allowed });
                        if (actual != expected)
                            throw new InvalidOperationException(
                                "3-8 entrance crosswalk eligibility matrix is incomplete");
                    }
                }
            }
            if ((bool)stopLine.Invoke(null, new object[] { 3, true, false, false, false })
                || (bool)stopLine.Invoke(null, new object[] { 3, false, false, false, true })
                || (bool)stopLine.Invoke(null, new object[] { 2, true, false, false, true })
                || !(bool)stopLine.Invoke(null, new object[] { 3, true, true, false, false })
                || !(bool)stopLine.Invoke(null, new object[] { 3, true, false, true, false })
                || !(bool)stopLine.Invoke(null, new object[] { 3, true, false, false, true }))
                throw new InvalidOperationException(
                    "stop-line policy must require a junction and a signal, stop sign, or blocked-junction wait rule");
            if ((int)oppositePoint.Invoke(null, new object[] { 0, 5 }) != 4
                || (int)oppositePoint.Invoke(null, new object[] { 1, 5 }) != 3
                || (int)oppositePoint.Invoke(null, new object[] { 4, 5 }) != 0)
                throw new InvalidOperationException(
                    "two-segment node boundaries must connect in opposite entrance order");
            int[] reduced = (int[])matchBoundaries.Invoke(
                null, new object[] { 5, 7, 2, 3 });
            int[] expectedReduced = new int[] { 0, 6, 4, 0, 2, 3, 3, 2, 1, 4 };
            if (reduced.Length != expectedReduced.Length)
                throw new InvalidOperationException(
                    "lane-count transition did not retain the connectable boundaries");
            for (int index = 0; index < reduced.Length; ++index)
                if (reduced[index] != expectedReduced[index])
                    throw new InvalidOperationException(
                        "lane-count transition did not prioritize center-outward boundaries: "
                        + string.Join(",", Array.ConvertAll(reduced, delegate(int value) { return value.ToString(); })));
            for (int firstLaneCount = 1; firstLaneCount <= 4; ++firstLaneCount)
            {
                for (int secondLaneCount = 1; secondLaneCount <= 4; ++secondLaneCount)
                {
                    Array firstRoles = SeparatorRoles(
                        roleType, roadside, incoming, firstLaneCount);
                    Array secondRoles = SeparatorRoles(
                        roleType, roadside, outgoing, secondLaneCount);
                    int[] pairs = (int[])matchRoles.Invoke(
                        null, new object[] { firstRoles, secondRoles });
                    int expectedPairCount = 2 + Math.Min(
                        firstLaneCount - 1, secondLaneCount - 1);
                    if (pairs.Length != expectedPairCount * 2)
                        throw new InvalidOperationException(
                            "1-4 lane separator matrix omitted or invented a connector");
                    AssertOneToOnePairs(pairs);
                }
            }
            Array twoWay = Array.CreateInstance(roleType, 3);
            twoWay.SetValue(roadside, 0);
            twoWay.SetValue(centerRole, 1);
            twoWay.SetValue(roadside, 2);
            Array oneWay = Array.CreateInstance(roleType, 3);
            oneWay.SetValue(roadside, 0);
            oneWay.SetValue(outgoing, 1);
            oneWay.SetValue(roadside, 2);
            int[] mixedDirection = (int[])matchRoles.Invoke(
                null, new object[] { twoWay, oneWay });
            if (mixedDirection.Length != 4)
                throw new InvalidOperationException(
                    "a two-way center line was connected to a one-way separator");
            if ((string)connectorStyle.Invoke(
                    null, new object[] { roadside, "DASHED_WHITE", "DASHED_WHITE", 180f })
                    != "SOLID_WHITE"
                || (string)connectorStyle.Invoke(
                    null, new object[] { incoming, "DASHED_WHITE", "DASHED_WHITE", 135f })
                    != "DASHED_WHITE"
                || (string)connectorStyle.Invoke(
                    null, new object[] { incoming, "DASHED_WHITE", "DASHED_WHITE", 134.999f })
                    != "SOLID_WHITE"
                || (string)connectorStyle.Invoke(
                    null, new object[] { centerRole, "SOLID_YELLOW", "DASHED_WHITE", 90f })
                    != "SOLID_YELLOW")
                throw new InvalidOperationException(
                    "connector style role or 135-degree policy is incorrect");
        }

        private static Array SeparatorRoles(
            Type roleType,
            object roadside,
            object separator,
            int laneCount)
        {
            Array roles = Array.CreateInstance(roleType, laneCount + 1);
            roles.SetValue(roadside, 0);
            roles.SetValue(roadside, laneCount);
            for (int index = 1; index < laneCount; ++index)
                roles.SetValue(separator, index);
            return roles;
        }

        private static void AssertOneToOnePairs(int[] pairs)
        {
            System.Collections.Generic.HashSet<int> first =
                new System.Collections.Generic.HashSet<int>();
            System.Collections.Generic.HashSet<int> second =
                new System.Collections.Generic.HashSet<int>();
            for (int index = 0; index + 1 < pairs.Length; index += 2)
                if (!first.Add(pairs[index]) || !second.Add(pairs[index + 1]))
                    throw new InvalidOperationException(
                        "lane transition contains a fan connector");
        }

        private static void ValidateRoadPlacementMarkingContract(Assembly runtimeAssembly)
        {
            Type styleType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.ImtMarkingStyleBundle", true);
            Type selectionType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RoadPlacementMarkingSelection", true);
            Type panelType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RoadPlacementMarkingPanel", true);
            Type panelWatcherType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RoadPlacementMarkingPanelWatcher", true);
            Type controllerType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RoadPlacementMarkingController", true);
            Type previewType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.ImtPreviewService", true);
            if (!typeof(ColossalFramework.UI.UIPanel).IsAssignableFrom(panelType))
                throw new InvalidOperationException(
                    "road placement markings are not exposed through a CS1 UI panel");
            if (panelType.GetMethod(
                    "Update",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly) != null
                || panelType.GetMethod(
                    "LateUpdate",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly) != null)
                throw new InvalidOperationException(
                    "road placement panel overrides a CS1 UI hierarchy lifecycle method");
            if (!typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(panelWatcherType))
                throw new InvalidOperationException(
                    "road placement panel selection watcher is not a Unity component");
            if (controllerType.GetField(
                    "_selection", BindingFlags.Instance | BindingFlags.NonPublic) == null
                || controllerType.GetField(
                    "_roadSelections", BindingFlags.Instance | BindingFlags.NonPublic) != null)
                throw new InvalidOperationException(
                    "road placement marking choices are still stored per road");
            MethodInfo watcherUpdate = panelWatcherType.GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (watcherUpdate == null)
                throw new InvalidOperationException(
                    "road placement panel has no independent selection refresh callback");
            if (previewType.GetMethod(
                    "Scan", BindingFlags.Instance | BindingFlags.NonPublic) != null)
                throw new InvalidOperationException(
                    "road placement markings still depend on a full segment scan");
            if (previewType.GetMethod(
                    "OnSegmentReleased", BindingFlags.Instance | BindingFlags.NonPublic) == null
                || previewType.GetField(
                    "_pendingNodes", BindingFlags.Instance | BindingFlags.NonPublic) == null)
                throw new InvalidOperationException(
                    "road deletion and upgrade do not queue their affected nodes");
            if (previewType.GetMethod(
                    "VerifyCrosswalkPostcondition",
                    BindingFlags.Instance | BindingFlags.NonPublic) == null)
                throw new InvalidOperationException(
                    "affected intersections do not verify eligible crosswalk completeness");
            Type mutationPatch = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.NetSegmentMutationPatch", true);
            if (mutationPatch.GetMethod(
                    "CreatePostfix", BindingFlags.Static | BindingFlags.NonPublic) == null
                || mutationPatch.GetMethod(
                    "ReleasePrefix", BindingFlags.Static | BindingFlags.NonPublic) == null
                || mutationPatch.GetMethod(
                    "ReleasePostfix", BindingFlags.Static | BindingFlags.NonPublic) == null)
                throw new InvalidOperationException(
                    "event-driven road create/release coverage is incomplete");
            MethodInfo previewTick = previewType.GetMethod(
                "Tick", BindingFlags.Instance | BindingFlags.Public);
            Type runtimeType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeEntry", true);
            MethodInfo runtimeTick = runtimeType.GetMethod(
                "Tick", BindingFlags.Instance | BindingFlags.Public);
            if (previewTick == null || runtimeTick == null || !CallsMethod(
                    runtimeTick,
                    "RoadRuntimeHost.Runtime.ImtPreviewService",
                    "Tick"))
                throw new InvalidOperationException(
                    "stale IMT entrances cannot receive one delayed targeted retry");

            object defaults = Activator.CreateInstance(styleType, true);
            styleType.GetField("RoadsideLines").SetValue(defaults, true);
            styleType.GetField("LaneSeparatorStyle").SetValue(
                defaults, "DASHED_WHITE");
            styleType.GetField("CenterLineStyle").SetValue(
                defaults, "DASHED_WHITE");
            MethodInfo fromStyle = selectionType.GetMethod(
                "FromStyle", BindingFlags.Static | BindingFlags.Public);
            MethodInfo applyTo = selectionType.GetMethod(
                "ApplyTo", BindingFlags.Instance | BindingFlags.Public);
            if (fromStyle == null || applyTo == null)
                throw new InvalidOperationException(
                    "road placement marking selection contract is incomplete");

            object selection = fromStyle.Invoke(null, new object[] { defaults });
            selectionType.GetField("RoadsideLines").SetValue(selection, false);
            selectionType.GetField("LaneSeparatorStyle").SetValue(
                selection, "SOLID_WHITE");
            selectionType.GetField("CenterLineStyle").SetValue(
                selection, "SOLID_YELLOW");
            object captured = Activator.CreateInstance(styleType, true);
            applyTo.Invoke(selection, new object[] { captured });
            if ((bool)styleType.GetField("RoadsideLines").GetValue(captured)
                || (string)styleType.GetField("LaneSeparatorStyle").GetValue(captured)
                    != "SOLID_WHITE"
                || (string)styleType.GetField("CenterLineStyle").GetValue(captured)
                    != "SOLID_YELLOW"
                || !(bool)styleType.GetField("CenterLineYellow").GetValue(captured))
                throw new InvalidOperationException(
                    "placement-time choices were not converted into one segment style snapshot");
        }

        private static void ValidateRoadToolbarRefreshContract(Assembly runtimeAssembly)
        {
            Type refreshType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RoadToolbarRefresh", true);
            MethodInfo refresh = refreshType.GetMethod(
                "Refresh", BindingFlags.Static | BindingFlags.Public);
            if (refresh == null || refresh.ReturnType != typeof(void))
                throw new InvalidOperationException(
                    "road toolbar refresh entry point is missing");

            Type runtimeType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeEntry", true);
            MethodInfo poll = runtimeType.GetMethod(
                "Poll", BindingFlags.Instance | BindingFlags.NonPublic);
            if (poll == null || !CallsMethod(
                    poll,
                    "RoadRuntimeHost.Runtime.RoadToolbarRefresh",
                    "Refresh"))
                throw new InvalidOperationException(
                    "manifest application does not refresh the road toolbar");
        }

        private static bool CallsMethod(
            MethodInfo caller,
            string declaringType,
            string methodName)
        {
            byte[] il = caller.GetMethodBody().GetILAsByteArray();
            for (int index = 0; index + 4 < il.Length; ++index)
            {
                if (il[index] != 0x28 && il[index] != 0x6f) continue;
                int token = BitConverter.ToInt32(il, index + 1);
                try
                {
                    MethodBase called = caller.Module.ResolveMethod(token);
                    if (called.DeclaringType != null
                        && called.DeclaringType.FullName == declaringType
                        && called.Name == methodName) return true;
                }
                catch (ArgumentException) { }
            }
            return false;
        }

        private static void ValidatePrefabInspectionContract(Assembly runtimeAssembly)
        {
            Type inspector = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.PrefabInspectionService", true);
            MethodInfo validate = inspector.GetMethod("ValidateRequestForSmoke", BindingFlags.Static | BindingFlags.NonPublic);
            if (validate == null) throw new InvalidOperationException("prefab inspection request validator is missing");
            string capped = (string)validate.Invoke(null, new object[] { "{\"schema_version\":1,\"request_id\":\"smoke\",\"command\":\"find_net\",\"name_contains\":\"Road\",\"limit\":999}" });
            if (capped != "find_net|20") throw new InvalidOperationException("prefab inspection result cap is not enforced");

            bool bulkRejected = false;
            try
            {
                validate.Invoke(null, new object[] { "{\"schema_version\":1,\"request_id\":\"smoke\",\"command\":\"dump_all\"}" });
            }
            catch (TargetInvocationException error)
            {
                bulkRejected = error.InnerException is InvalidDataException;
            }
            if (!bulkRejected) throw new InvalidOperationException("bulk prefab dump request was accepted");
        }

        private static void WriteLaneContractPreview(string root, float bundleWidth)
        {
            Directory.CreateDirectory(Path.Combine(root, "roads"));
            string lane = "{\"lane_id\":\"lane-main\",\"position\":0,\"width\":3,\"vertical_offset\":0,\"stop_offset\":0,\"speed_limit\":1,\"direction\":\"Forward\",\"lane_type\":\"Vehicle\",\"vehicle_type\":\"Car\",\"allow_connect\":true}";
            string bundleLane = lane.Replace("\"width\":3", "\"width\":" + bundleWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(root, "catalog.json"), "{\"schema_version\":1,\"revision\":\"catalog\",\"roads\":[{\"road_id\":\"matched-road\",\"lanes\":[" + lane + "]}]}");
            File.WriteAllText(Path.Combine(root, "manifest.json"), "{\"schema_version\":1,\"revision\":\"manifest\",\"roads\":[{\"road_id\":\"matched-road\",\"bundle_path\":\"roads/matched-road.json\",\"revision\":\"bundle\"}]}");
            File.WriteAllText(Path.Combine(Path.Combine(root, "roads"), "matched-road.json"), "{\"schema_version\":1,\"road_id\":\"matched-road\",\"revision\":\"bundle\",\"half_width\":6,\"pavement_width\":2.5,\"node_min_corner_offset\":12,\"lanes\":[" + bundleLane + "],\"modes\":[{\"mode\":\"basic\",\"entries\":[]}]}");
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int offset = 0;
            while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
            {
                ++count;
                offset += value.Length;
            }
            return count;
        }
    }
}

using System;
using System.Collections;
using System.IO;
using System.Reflection;
using RoadRuntimeHost.Runtime;
using RoadRuntimeHost.Loader;
using ICities;
using UnityEngine;
using ColossalFramework.Math;

namespace RoadRuntimeHost.ContractSmoke
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: RoadRuntimeHost.ContractSmoke <preview-path> <placement-preview-path>");
                return 2;
            }
            try
            {
                ValidatePlacementLibrary(typeof(RuntimeEntry).Assembly, args[1]);
                ValidatePlacementSelection(typeof(RuntimeEntry).Assembly);
                ValidatePlacementCompression(typeof(RuntimeEntry).Assembly);
                ValidateHeightTransitionKernel(typeof(RuntimeEntry).Assembly);
                SegmentLaneIsolationContract.Validate(typeof(RuntimeEntry).Assembly);
                SharedHeightContract.Validate(typeof(RuntimeEntry).Assembly, args[1]);
                NetworkGeometryCacheContract.Validate(typeof(RuntimeEntry).Assembly, args[1]);
                RoadIconContract.Validate(typeof(RuntimeEntry).Assembly, args[0]);
                ValidateGameModuleBoundary(typeof(RuntimeEntry).Assembly);
                ValidateImporterOutputContract();
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
                ValidateMeshTangentContract(runtime.Assembly);
                ValidateReferenceLanePropContract(runtime.Assembly);
                ValidateCrosswalkWallRenderingContract(runtime.Assembly);
                ValidateImtNodePolicyContract(runtime.Assembly);
                ValidateRuntimeMarkingPlanContract(runtime.Assembly);
                ValidateCornerLookupContract(runtime.Assembly);
                ValidateImtPointSourceContract(runtime.Assembly);
                ValidateTmpeInterfaceLookup(runtime.Assembly);
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

        private sealed class CornerPointProbe : IMT.API.IEntrancePointData
        {
            private readonly ushort _entrance;
            public CornerPointProbe(ushort entrance) { _entrance = entrance; }
            public IMT.API.IDataProviderV1 DataProvider { get { return null; } }
            public IMT.API.IMarkingData Marking { get { return null; } }
            public IMT.API.IEntranceData Entrance { get { return null; } }
            public ushort MarkingId { get { return 0; } }
            public ushort EntranceId { get { return _entrance; } }
            public byte Index { get { return 1; } }
            public IMT.API.IPointSourceData Source { get { return null; } }
            public float Offset { get; set; }
            public float Position { get { return 0; } }
        }

        private static void ValidateCornerLookupContract(Assembly runtimeAssembly)
        {
            // Reproduce the installed IMT API's same-entrance guard without a
            // live NetManager. This is an actual API call, not a mocked oracle.
            Type providerType = Type.GetType("IMT.Utilities.API.NodeMarkingDataProvider, IntersectionMarkingTool", true);
            IMT.API.INodeMarkingData provider = (IMT.API.INodeMarkingData)Activator.CreateInstance(providerType);
            bool rejected = false;
            try
            {
                IMT.API.IRegularLineData ignored;
                provider.TryGetRegularLine(new CornerPointProbe(1), new CornerPointProbe(2), out ignored);
            }
            catch (IMT.API.CreateLineException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("IMT cross-entrance lookup constraint changed");

            Type service = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.ImtPreviewService", true);
            MethodInfo cleanup = service.GetMethod("RemoveOwnedCornerLines", BindingFlags.Instance | BindingFlags.NonPublic);
            if (CallsMethod(cleanup, "IMT.API.INodeMarkingData", "TryGetRegularLine")
                || !CallsMethod(cleanup, "RoadRuntimeHost.Runtime.ImtInternalAdapter", "GetRegularLine"))
                throw new InvalidOperationException("corner cleanup uses IMT's same-entrance-only public lookup");
        }

        private static void ValidateImtPointSourceContract(Assembly runtimeAssembly)
        {
            Type sourceType = Type.GetType("IMT.Utilities.API.PointSourceDataProvider, IntersectionMarkingTool", true);
            Type service = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.ImtPreviewService", true);
            MethodInfo convert = service.GetMethod("RuntimeBoundaryFromSource", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (IMT.API.PointLocation location in new IMT.API.PointLocation[] {
                IMT.API.PointLocation.Left, IMT.API.PointLocation.Rigth, IMT.API.PointLocation.Between })
            {
                // Actual API DTO: absent lane ID and its index are both zero.
                // Between(0,0) is deliberately NOT an edge: lane zero is valid.
                object source = Activator.CreateInstance(sourceType, new object[] {
                    location, location == IMT.API.PointLocation.Left ? (uint)0 : (uint)11, 0,
                    location == IMT.API.PointLocation.Rigth ? (uint)0 : (uint)12, 0 });
                object boundary = convert.Invoke(null, new object[] { 1, 0, source });
                Type type = boundary.GetType();
                int left = (int)type.GetField("LeftLaneIndex").GetValue(boundary);
                int right = (int)type.GetField("RightLaneIndex").GetValue(boundary);
                if (left != (location == IMT.API.PointLocation.Left ? -1 : 0)
                    || right != (location == IMT.API.PointLocation.Rigth ? -1 : 0))
                    throw new InvalidOperationException("IMT point location was lost during snapshot conversion: " + location);
                Type segmentType = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.RuntimeSegmentSnapshot", true);
                Type laneType = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.RuntimeLaneSnapshot", true);
                object segment = CreateRuntimeSegment(segmentType, laneType, type, 1, true, 1, 1);
                Type builder = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.RuntimeMarkingPlanBuilder", true);
                object semantic = builder.GetMethod("ClassifyBoundary").Invoke(null, new object[] { segment, boundary });
                bool edge = location != IMT.API.PointLocation.Between;
                if ((semantic.ToString() == "Roadside") != edge)
                    throw new InvalidOperationException("IMT edge classified as a lane separator");
                foreach (bool enabled in new bool[] { false, true })
                {
                    bool permitted = (bool)builder.GetMethod("ShouldCreateBoundary").Invoke(null, new object[] { semantic, enabled });
                    if (permitted != (!edge || enabled)) throw new InvalidOperationException("roadside OFF changed the wrong boundary role");
                }
                if (edge)
                {
                    Type policy = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.ImtNodePolicy", true);
                    Type role = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.ImtBoundaryRole", true);
                    foreach (float angle in new float[] { 90f, 135f, 180f })
                        if ((string)policy.GetMethod("ConnectorStyle").Invoke(null, new object[] {
                            Enum.Parse(role, "Roadside"), "DASHED_WHITE", "DASHED_WHITE", angle }) != "SOLID_WHITE")
                            throw new InvalidOperationException("roadside inherited dashed separator style");
                }
            }
        }

        private interface SignalManagerProbe
        {
            bool CanToggleTrafficLight(ushort nodeId);
        }

        private sealed class ExplicitSignalManagerProbe : SignalManagerProbe
        {
            bool SignalManagerProbe.CanToggleTrafficLight(ushort nodeId) { return nodeId == 17; }
        }

        private static void ValidateTmpeInterfaceLookup(Assembly runtimeAssembly)
        {
            Type policy = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.TmpeTrafficPolicy", true);
            MethodInfo lookup = policy.GetMethod("RequireMethod", BindingFlags.Static | BindingFlags.NonPublic);
            object manager = new ExplicitSignalManagerProbe();
            MethodInfo method = (MethodInfo)lookup.Invoke(null, new object[] { manager,
                "CanToggleTrafficLight", new Type[] { typeof(ushort) } });
            if (!(bool)method.Invoke(manager, new object[] { (ushort)17 }))
                throw new InvalidOperationException("explicit TMPE interface method did not resolve");
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

        private static void ValidatePlacementCompression(Assembly assembly)
        {
            MethodInfo decode = assembly.GetType("RoadRuntimeHost.Runtime.PlacementVariantLibrary", true)
                .GetMethod("DecodeSnapshot", BindingFlags.Static | BindingFlags.NonPublic);
            if (decode == null) throw new InvalidOperationException("Placement snapshot decoder is missing");
            byte[] text = System.Text.Encoding.UTF8.GetBytes("placement");
            if ((string)decode.Invoke(null, new object[] { text, false }) != "placement")
                throw new InvalidOperationException("Legacy plain snapshot decoding changed");
            // Fixed gzip bytes produced outside the C# decoder, with a literal
            // expected value rather than a round-trip through production code.
            byte[] knownGzip = Convert.FromBase64String("H4sIAAAAAAAC/yvISUxOzU3NKwEADnXbSAkAAAA=");
            if ((string)decode.Invoke(null, new object[] { knownGzip, true }) != "placement")
                throw new InvalidOperationException("Known gzip snapshot payload decoded incorrectly");
            using (MemoryStream packed = new MemoryStream())
            {
                using (System.IO.Compression.GZipStream zipper = new System.IO.Compression.GZipStream(
                    packed, System.IO.Compression.CompressionMode.Compress, true))
                {
                    byte[] chunk = new byte[1024 * 1024];
                    for (int i = 0; i < 33; ++i) zipper.Write(chunk, 0, chunk.Length);
                }
                bool refused = false;
                try { decode.Invoke(null, new object[] { packed.ToArray(), true }); }
                catch (TargetInvocationException error) { refused = error.InnerException is InvalidDataException; }
                if (!refused) throw new InvalidOperationException("Oversized expanded snapshot was accepted");
            }
        }

        private static void ValidatePlacementSelection(Assembly assembly)
        {
            Type type = assembly.GetType("RoadRuntimeHost.Runtime.PlacementOptionSelection", true);
            string original = "NARROW|CENTERED|NONE|STANDARD|DEPRESSED";
            object selection = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public,
                null, new object[] { original }, null);
            MethodInfo propose = type.GetMethod("Propose");
            MethodInfo commit = type.GetMethod("Commit");
            PropertyInfo key = type.GetProperty("Key");
            string wide = "WIDE|CENTERED|NONE|STANDARD|DEPRESSED";
            string offset = "WIDE|OFFSET_LEFT|NONE|STANDARD|DEPRESSED";
            string[] menu = { original, wide, offset };
            object proposed = propose.Invoke(selection, new object[] { menu, 0, "WIDE" });
            if ((string)proposed != wide || (string)key.GetValue(selection, null) != original)
                throw new InvalidOperationException("Proposing a placement change mutated active options before registration");
            commit.Invoke(selection, new object[] { proposed });
            object next = propose.Invoke(selection, new object[] { menu, 1, "OFFSET_LEFT" });
            if ((string)next != offset) throw new InvalidOperationException("Placement selection lost previously selected width");
            bool refused = false;
            try { propose.Invoke(selection, new object[] { menu, 2, "WIDE" }); }
            catch (TargetInvocationException e) { refused = e.InnerException is InvalidDataException; }
            if (!refused || (string)key.GetValue(selection, null) != wide)
                throw new InvalidOperationException("An unavailable choice was guessed or changed active options");
            // Switching Base Road does not reset the shared options. The new
            // one-way menu cannot accommodate OFFSET_LEFT; no silent centering.
            commit.Invoke(selection, new object[] { next });
            refused = false;
            try { propose.Invoke(selection, new object[] { new string[] { wide }, 4, "DEPRESSED" }); }
            catch (TargetInvocationException e) { refused = e.InnerException is InvalidDataException; }
            if (!refused || (string)key.GetValue(selection, null) != offset)
                throw new InvalidOperationException("Changing Base Road silently remapped the common placement selection");
        }

        private static void ValidateHeightTransitionKernel(Assembly assembly)
        {
            Type kernel = assembly.GetType("RoadRuntimeHost.Runtime.HeightTransitionKernel", true);
            MethodInfo offsets = kernel.GetMethod("CurveYOffsets", BindingFlags.Static | BindingFlags.Public);
            MethodInfo joins = kernel.GetMethod("LevelsJoin", BindingFlags.Static | BindingFlags.Public);
            foreach (float a in new float[] { -0.3f, 0f })
                foreach (float b in new float[] { -0.3f, 0f })
                    if ((bool)joins.Invoke(null, new object[] { a, b }) != (a == b))
                        throw new InvalidOperationException("Direct D0/D30 step was permitted or equal levels were refused");
            float[] forward = (float[])offsets.Invoke(null, new object[] { -0.3f, 0f, -0.3f, false });
            float[] reversed = (float[])offsets.Invoke(null, new object[] { -0.3f, 0f, -0.3f, true });
            float[] up = { 0f, 0.1f, 0.2f, 0.3f }, down = { 0.3f, 0.2f, 0.1f, 0f };
            for (int i = 0; i < 4; ++i)
                if (Math.Abs(forward[i] - up[i]) > 0.0001f || Math.Abs(reversed[i] - down[i]) > 0.0001f)
                    throw new InvalidOperationException("Height curve controls do not form the specified linear ramp");
            // The curve is physically segment-start -> segment-end. Lane
            // direction does not enter this API. Preserve an existing terrain
            // curve by adding only the local physical profile.
            foreach (float t in new float[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                float v = 1f - t;
                float added = v*v*v*forward[0] + 3f*v*v*t*forward[1] + 3f*v*t*t*forward[2] + t*t*t*forward[3];
                if (Math.Abs(added - 0.3f*t) > 0.0001f)
                    throw new InvalidOperationException("Height curve interpolates nonlinearly");
            }
            float[] walk = (float[])offsets.Invoke(null, new object[] { 0f, 0f, 0f, false });
            foreach (float value in walk) if (value != 0f) throw new InvalidOperationException("Transition lowered pedestrians");
            Bezier3 terrain = new Bezier3(new Vector3(1f, 3f, 2f), new Vector3(2f, 5f, 4f),
                new Vector3(4f, 4f, 6f), new Vector3(7f, 8f, 9f));
            Bezier3 graded = (Bezier3)kernel.GetMethod("OffsetCurve").Invoke(null,
                new object[] { terrain, -0.3f, 0f, -0.3f, false });
            if (graded.a.x != 1f || graded.a.z != 2f || graded.b.x != 2f || graded.b.z != 4f
                || graded.c.x != 4f || graded.c.z != 6f || graded.d.x != 7f || graded.d.z != 9f
                || Math.Abs(graded.a.y-3f) > 0.0001f || Math.Abs(graded.b.y-5.1f) > 0.0001f
                || Math.Abs(graded.c.y-4.2f) > 0.0001f || Math.Abs(graded.d.y-8.3f) > 0.0001f)
                throw new InvalidOperationException("Transition replaced the existing terrain/edited curve instead of adding its profile");
            Type runType = assembly.GetType("RoadRuntimeHost.Runtime.HeightTransitionPlacementRun", true);
            object run = Activator.CreateInstance(runType, new object[] { "transition", "target" });
            MethodInfo choose = runType.GetMethod("Choose", new Type[] { typeof(string) }), complete = runType.GetMethod("Complete");
            if ((string)choose.Invoke(run, new object[] { "other" }) != "other") throw new InvalidOperationException("Unrelated split prefab was changed");
            complete.Invoke(run, new object[] { "other", true });
            complete.Invoke(run, new object[] { "transition", false });
            if ((string)choose.Invoke(run, new object[] { "transition" }) != "transition") throw new InvalidOperationException("Failure consumed the transition");
            complete.Invoke(run, new object[] { "transition", true });
            if ((string)choose.Invoke(run, new object[] { "transition" }) != "target") throw new InvalidOperationException("Transition repeated in one placement operation");
            object next = Activator.CreateInstance(runType, new object[] { "transition", "target" });
            if ((string)choose.Invoke(next, new object[] { "transition" }) != "transition") throw new InvalidOperationException("Transition state leaked into the next operation");
            MethodInfo oriented = runType.GetMethod("Choose", new Type[] { typeof(string), typeof(bool) });
            foreach (bool invert in new bool[] { false, true })
            {
                object stroke = Activator.CreateInstance(runType, new object[] { "forward-ramp", "reverse-ramp", "ordinary-target" });
                string first = (string)oriented.Invoke(stroke, new object[] { "forward-ramp", invert });
                if (first != (invert ? "reverse-ramp" : "forward-ramp")) throw new InvalidOperationException("Draw inversion chose the wrong physical ramp");
                complete.Invoke(stroke, new object[] { first, false });
                if ((string)oriented.Invoke(stroke, new object[] { "forward-ramp", invert }) != first)
                    throw new InvalidOperationException("Failed inverted placement consumed the ramp");
                complete.Invoke(stroke, new object[] { first, true });
                foreach (bool subsequentInvert in new bool[] { false, true })
                    if ((string)oriented.Invoke(stroke, new object[] { "forward-ramp", subsequentInvert }) != "ordinary-target")
                        throw new InvalidOperationException("A multi-segment stroke repeated its ramp after success");
            }
            Type patch = assembly.GetType("RoadRuntimeHost.Runtime.PlacementHeightTransitionPatch", true);
            MethodInfo placement = (MethodInfo)patch.GetMethod("PlacementMethod", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            ParameterInfo[] operationParameters = placement.GetParameters();
            if (operationParameters.Length != 19 || operationParameters[6].Name != "test"
                || operationParameters[7].Name != "testEnds" || operationParameters[8].Name != "visualize"
                || operationParameters[14].ParameterType != typeof(ushort).MakeByRefType()
                || operationParameters[16].Name != "segment")
                throw new InvalidOperationException("Hook did not select the shipped CS1 placement operation");
            Type updater = assembly.GetType("RoadRuntimeHost.Runtime.PrefabUpdater", true);
            Type segmentType = typeof(NetInfo.Segment);
            object raw = Activator.CreateInstance(segmentType);
            updater.GetMethod("ConfigureHeightSegmentFacing", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { raw });
            MethodInfo check = segmentType.GetMethod("CheckFlags");
            foreach (bool inverted in new bool[] { false, true })
            {
                object[] args = { inverted ? NetSegment.Flags.Invert : NetSegment.Flags.Created, NetSegment.Flags2.None, false };
                if (!(bool)check.Invoke(raw, args) || (bool)args[2] != inverted)
                    throw new InvalidOperationException("Native transition selector and physical inversion disagree");
            }
            NetInfo.Node sourceStart = new NetInfo.Node(), sourceEnd = new NetInfo.Node();
            sourceStart.flagsRequired = NetNode.FlagsLong.Junction;
            sourceEnd.flagsRequired = NetNode.FlagsLong.Junction;
            NetInfo.Node[] selected = (NetInfo.Node[])updater.GetMethod("HeightEndpointNodes", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { new NetInfo.Node[] { sourceStart }, new NetInfo.Node[] { sourceEnd } });
            if (selected.Length != 2 || ReferenceEquals(selected[0], sourceStart) || ReferenceEquals(selected[1], sourceEnd)
                || !selected[0].CheckFlags(NetNode.FlagsLong.Junction | NetNode.FlagsLong.OnSegmentStart)
                || selected[0].CheckFlags(NetNode.FlagsLong.Junction | NetNode.FlagsLong.OnSegmentEnd)
                || !selected[1].CheckFlags(NetNode.FlagsLong.Junction | NetNode.FlagsLong.OnSegmentEnd)
                || selected[1].CheckFlags(NetNode.FlagsLong.Junction | NetNode.FlagsLong.OnSegmentStart)
                || sourceStart.flagsRequired != NetNode.FlagsLong.Junction || sourceEnd.flagsRequired != NetNode.FlagsLong.Junction)
                throw new InvalidOperationException("Endpoint node selector changed the ordinary source or chose the wrong side");
        }

        private static void ValidateHeightTransitionLibrary(Type libraryType, object library)
        {
            MethodInfo resolve = libraryType.GetMethod("ResolveHeightTransition");
            if (resolve == null) throw new InvalidOperationException("Height transition has no compiled reader");
            const string lower = "NARROW|CENTERED|NONE|NONE|DEPRESSED";
            const string flush = "NARROW|CENTERED|NONE|NONE|FLUSH";
            object edge = resolve.Invoke(library, new object[] { lower, flush });
            Type edgeType = edge.GetType();
            object start = edgeType.GetField("Start").GetValue(edge);
            object end = edgeType.GetField("End").GetValue(edge);
            object segment = edgeType.GetField("Segment").GetValue(edge);
            Type updater = libraryType.Assembly.GetType("RoadRuntimeHost.Runtime.PrefabUpdater", true);
            object compiled = updater.GetMethod("CompileHeightTransition", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { edge });
            float[] starts = (float[])compiled.GetType().GetField("StartOffsets").GetValue(compiled);
            float[] ends = (float[])compiled.GetType().GetField("EndOffsets").GetValue(compiled);
            float[] physicalStart = { 0f, -0.3f, -0.3f, -0.3f, 0f };
            if (starts.Length != 5 || ends.Length != 5) throw new InvalidOperationException("Native transition lost lane endpoints");
            for (int i = 0; i < 5; ++i)
                if (Math.Abs(starts[i] - physicalStart[i]) > 0.0001f || ends[i] != 0f)
                    throw new InvalidOperationException("Native transition guessed endpoint lane heights");
            Type packetType = start.GetType();
            Type segmentType = segment.GetType();
            object startSection = packetType.GetField("Section").GetValue(start);
            object endSection = packetType.GetField("Section").GetValue(end);
            Type sectionType = startSection.GetType();
            if (Math.Abs((float)sectionType.GetField("RoadwayZ").GetValue(startSection) + 0.3f) > 0.0001f
                || Math.Abs((float)sectionType.GetField("RoadwayZ").GetValue(endSection)) > 0.0001f
                || (string)segmentType.GetField("StartIdentity").GetValue(segment) != (string)packetType.GetField("Identity").GetValue(start)
                || (string)segmentType.GetField("EndIdentity").GetValue(segment) != (string)packetType.GetField("Identity").GetValue(end))
                throw new InvalidOperationException("Height transition lost its ordered ordinary endpoints");
            Array startLanes = (Array)packetType.GetField("Lanes").GetValue(start);
            Array endLanes = (Array)packetType.GetField("Lanes").GetValue(end);
            for (int i = 0; i < startLanes.Length; ++i)
            {
                object a = startLanes.GetValue(i), b = endLanes.GetValue(i);
                Type laneType = a.GetType();
                bool car = (string)laneType.GetField("LaneType").GetValue(a) == "VEHICLE";
                if (Math.Abs((float)laneType.GetField("VerticalOffset").GetValue(a) - (car ? -0.3f : 0f)) > 0.0001f
                    || Math.Abs((float)laneType.GetField("VerticalOffset").GetValue(b)) > 0.0001f)
                    throw new InvalidOperationException("Height transition lane endpoints do not match the road/walk levels");
            }
            Array modes = (Array)segmentType.GetField("Modes").GetValue(segment);
            if (modes.Length != 5) throw new InvalidOperationException("Height transition lost network modes");
            foreach (object mode in modes)
                foreach (object entry in (Array)mode.GetType().GetField("Entries").GetValue(mode))
                    if ((string)entry.GetType().GetField("Kind").GetValue(entry) != "segment")
                        throw new InvalidOperationException("Height transition duplicated endpoint nodes");
            object reverse = resolve.Invoke(library, new object[] { flush, lower });
            object reverseSegment = edgeType.GetField("Segment").GetValue(reverse);
            if ((string)segmentType.GetField("Identity").GetValue(segment) == (string)segmentType.GetField("Identity").GetValue(reverseSegment))
                throw new InvalidOperationException("Opposite transitions have the same identity");
            bool rejected = false;
            try { resolve.Invoke(library, new object[] { lower, lower }); }
            catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
            if (!rejected) throw new InvalidOperationException("Ordinary road was guessed as a transition");
            MethodInfo require = libraryType.GetMethod("RequireHeightTransition", BindingFlags.Static | BindingFlags.NonPublic);
            sectionType.GetField("CurbRight").SetValue(endSection, 7f);
            rejected = false;
            try { require.Invoke(null, new object[] { segment, start, end }); }
            catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
            if (!rejected) throw new InvalidOperationException("Height-only transition accepted a changed horizontal endpoint");
            object fresh = resolve.Invoke(library, new object[] { lower, flush });
            object freshEnd = edgeType.GetField("End").GetValue(fresh);
            object freshSection = packetType.GetField("Section").GetValue(freshEnd);
            if (Math.Abs((float)sectionType.GetField("CurbRight").GetValue(freshSection) - 6f) > 0.0001f)
                throw new InvalidOperationException("A consumer changed cached transition endpoints");
            // Faults at independent representation boundaries: endpoint identity,
            // lane semantics, and accidental node entries in a segment payload.
            object intact = resolve.Invoke(library, new object[] { lower, flush });
            object intactStart = edgeType.GetField("Start").GetValue(intact);
            object intactEnd = edgeType.GetField("End").GetValue(intact);
            object intactSegment = edgeType.GetField("Segment").GetValue(intact);
            segmentType.GetField("EndIdentity").SetValue(intactSegment, (string)packetType.GetField("Identity").GetValue(intactStart));
            AssertTransitionRejected(require, intactSegment, intactStart, intactEnd, "Reversed/identical endpoint reference was accepted");
            intact = resolve.Invoke(library, new object[] { lower, flush });
            intactStart = edgeType.GetField("Start").GetValue(intact);
            intactEnd = edgeType.GetField("End").GetValue(intact);
            intactSegment = edgeType.GetField("Segment").GetValue(intact);
            Array metadata = (Array)packetType.GetField("Lanes").GetValue(intactEnd);
            object carLane = metadata.GetValue(1);
            carLane.GetType().GetField("SpeedLimit").SetValue(carLane, 2f);
            AssertTransitionRejected(require, intactSegment, intactStart, intactEnd, "Changed lane metadata was accepted");
            intact = resolve.Invoke(library, new object[] { lower, flush });
            intactStart = edgeType.GetField("Start").GetValue(intact);
            intactEnd = edgeType.GetField("End").GetValue(intact);
            intactSegment = edgeType.GetField("Segment").GetValue(intact);
            object firstMode = ((Array)segmentType.GetField("Modes").GetValue(intactSegment)).GetValue(0);
            object firstEntry = ((Array)firstMode.GetType().GetField("Entries").GetValue(firstMode)).GetValue(0);
            firstEntry.GetType().GetField("Kind").SetValue(firstEntry, "node");
            AssertTransitionRejected(require, intactSegment, intactStart, intactEnd, "Transition payload contained an endpoint node");
        }

        private static void AssertTransitionRejected(MethodInfo require, object segment, object start, object end, string message)
        {
            bool rejected = false;
            try { require.Invoke(null, new object[] { segment, start, end }); }
            catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
            if (!rejected) throw new InvalidOperationException(message);
        }

        private static void ValidatePlacementLibrary(Assembly runtimeAssembly, string root)
        {
            Type libraryType = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.PlacementVariantLibrary", true);
            object library = Activator.CreateInstance(libraryType, BindingFlags.Instance | BindingFlags.Public,
                null, new object[] { root, "jp-4x4l", "placement/jp-4x4l/index.json" }, null);
            PropertyInfo loadedCount = libraryType.GetProperty("LoadedCount");
            MethodInfo resolve = libraryType.GetMethod("Resolve");
            bool wrongRevisionRejected = false;
            try { Activator.CreateInstance(libraryType, BindingFlags.Instance | BindingFlags.Public, null,
                new object[] { root, "jp-4x4l", "placement/jp-4x4l/index.json", new string('0', 64) }, null); }
            catch (TargetInvocationException error) { wrongRevisionRejected = error.InnerException is InvalidDataException; }
            if (!wrongRevisionRejected) throw new InvalidOperationException("Manifest/index revision mismatch was accepted");
            if ((int)loadedCount.GetValue(library, null) != 0)
                throw new InvalidOperationException("Reading a placement index eagerly loaded geometry");
            MethodInfo getChoices = libraryType.GetMethod("GetChoices");
            if (getChoices == null) throw new InvalidOperationException("Placement UI cannot obtain the compiled choice index");
            Array choices = (Array)getChoices.Invoke(library, null);
            if (choices.Length != 2 || (int)loadedCount.GetValue(library, null) != 0)
                throw new InvalidOperationException("Placement menu enumeration eagerly loaded or omitted choices");
            object menuChoice = choices.GetValue(0);
            Type choiceType = menuChoice.GetType();
            if (Math.Abs((float)choiceType.GetField("TotalWidth").GetValue(menuChoice) - 32f) > 0.0001f)
                throw new InvalidOperationException("Expanded placement width is not available before loading geometry");
            choiceType.GetField("Key").SetValue(menuChoice, "consumer mutation");
            choiceType.GetField("TotalWidth").SetValue(menuChoice, 999f);
            Array freshChoices = (Array)getChoices.Invoke(library, null);
            if ((string)choiceType.GetField("Key").GetValue(freshChoices.GetValue(0)) == "consumer mutation"
                || Math.Abs((float)choiceType.GetField("TotalWidth").GetValue(freshChoices.GetValue(0)) - 32f) > 0.0001f)
                throw new InvalidOperationException("A menu consumer mutated the compiled choice index");
            object first = resolve.Invoke(library, new object[] { "NARROW|CENTERED|NONE|STANDARD|DEPRESSED" });
            // Display metadata is not a second authority: a menu that advertises
            // 48 m for this actual 32 m snapshot must be rejected when selected.
            string invalidRoot = Path.Combine(Path.GetTempPath(), "RoadPlacement.Menu." + Guid.NewGuid().ToString("N"));
            try
            {
                object descriptor = freshChoices.GetValue(0);
                string relative = (string)choiceType.GetField("BundlePath").GetValue(descriptor);
                string destination = Path.Combine(invalidRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(Path.Combine(root, relative), destination);
                string menu = "{\"schema_version\":2,\"base_road_id\":\"jp-4x4l\",\"default_key\":\"NARROW|CENTERED|NONE|STANDARD|DEPRESSED\",\"choices\":[{\"key\":\"NARROW|CENTERED|NONE|STANDARD|DEPRESSED\","
                    + "\"identity\":\"" + choiceType.GetField("Identity").GetValue(descriptor) + "\","
                    + "\"bundle_path\":\"" + relative + "\",\"content_sha256\":\""
                    + choiceType.GetField("ContentSha256").GetValue(descriptor)
                    + "\",\"total_width\":48,\"roadway_z\":-0.3,\"traffic_boundary\":0}]}";
                File.WriteAllText(Path.Combine(invalidRoot, "placement/bad-menu.json"), menu);
                object badLibrary = Activator.CreateInstance(libraryType, BindingFlags.Instance | BindingFlags.Public,
                    null, new object[] { invalidRoot, "jp-4x4l", "placement/bad-menu.json" }, null);
                bool mismatchRejected = false;
                try { resolve.Invoke(badLibrary, new object[] { "NARROW|CENTERED|NONE|STANDARD|DEPRESSED" }); }
                catch (TargetInvocationException error) { mismatchRejected = error.InnerException is InvalidDataException; }
                if (!mismatchRejected || (int)loadedCount.GetValue(badLibrary, null) != 0)
                    throw new InvalidOperationException("Incorrect menu dimensions were accepted or cached");
            }
            finally { if (Directory.Exists(invalidRoot)) Directory.Delete(invalidRoot, true); }
            object second = resolve.Invoke(library, new object[] { "STANDARD|CENTERED|NONE|STANDARD|DEPRESSED" });
            Type snapshotType = first.GetType();
            string identity = (string)snapshotType.GetField("Identity").GetValue(first);
            if (identity != (string)snapshotType.GetField("Identity").GetValue(second)
                || (int)loadedCount.GetValue(library, null) != 1)
                throw new InvalidOperationException("Equivalent placement choices were not reused");
            Array lanes = (Array)snapshotType.GetField("Lanes").GetValue(first);
            object lane = lanes.GetValue(1);
            lane.GetType().GetField("Position").SetValue(lane, 100f);
            object third = resolve.Invoke(library, new object[] { "NARROW|CENTERED|NONE|STANDARD|DEPRESSED" });
            Array originalLanes = (Array)snapshotType.GetField("Lanes").GetValue(third);
            if (Math.Abs((float)originalLanes.GetValue(1).GetType().GetField("Position").GetValue(originalLanes.GetValue(1)) + 10.5f) > 0.0001f)
                throw new InvalidOperationException("A consumer mutated an immutable placement snapshot");
            object asymmetric = Activator.CreateInstance(libraryType, BindingFlags.Instance | BindingFlags.Public,
                null, new object[] { root, "jp-1x2l", "placement/jp-1x2l/index.json" }, null);
            object packet = resolve.Invoke(asymmetric, new object[] { "NARROW|CENTERED|NONE|NONE|DEPRESSED" });
            Array physical = (Array)snapshotType.GetField("Lanes").GetValue(packet);
            float[] positions = { -9.5f, -1.5f, 1.5f, 4.5f, 11f };
            float[] widths = { 12.5f, 3f, 3f, 3f, 9.5f };
            for (int i = 0; i < positions.Length; ++i)
            {
                object item = physical.GetValue(i);
                Type laneType = item.GetType();
                if (Math.Abs((float)laneType.GetField("Position").GetValue(item) - positions[i]) > 0.0001f
                    || Math.Abs((float)laneType.GetField("Width").GetValue(item) - widths[i]) > 0.0001f)
                    throw new InvalidOperationException("Actual Blender placement coordinates disagree with the physical oracle");
            }
            object section = snapshotType.GetField("Section").GetValue(packet);
            ValidateHeightTransitionLibrary(libraryType, asymmetric);
            // A displaced metadata center must be rejected, not guessed from the lane pack.
            section.GetType().GetField("TrafficBoundary").SetValue(section, (float?)1.5f);
            bool rejected = false;
            try
            {
                libraryType.GetMethod("Require", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                    new object[] { packet, "jp-1x2l", snapshotType.GetField("Identity").GetValue(packet) });
            }
            catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
            if (!rejected) throw new InvalidOperationException("Wrong placement traffic boundary was accepted");
            rejected = false;
            try { resolve.Invoke(asymmetric, new object[] { "UNKNOWN" }); }
            catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
            if (!rejected) throw new InvalidOperationException("An uncompiled placement choice was guessed");
            object intact = resolve.Invoke(asymmetric, new object[] { "NARROW|CENTERED|NONE|NONE|DEPRESSED" });
            Type updaterType = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.PrefabUpdater", true);
            MethodInfo compilePlacement = updaterType.GetMethod("CompilePlacementBundle", BindingFlags.Static | BindingFlags.NonPublic);
            if (compilePlacement == null) throw new InvalidOperationException("Placement native adapter has no managed definition boundary");
            object definition = compilePlacement.Invoke(null, new object[] { intact });
            Type definitionType = definition.GetType();
            if (Math.Abs((float)definitionType.GetField("HalfWidth").GetValue(definition) - 16f) > 0.0001f
                || Math.Abs((float)definitionType.GetField("PavementWidth").GetValue(definition) - 11.5f) > 0.0001f
                || Math.Abs((float)definitionType.GetField("SurfaceLevel").GetValue(definition) + 0.3f) > 0.0001f
                || !ReferenceEquals(definitionType.GetField("Lanes").GetValue(definition), snapshotType.GetField("Lanes").GetValue(intact)))
                throw new InvalidOperationException("Asymmetric section was replaced by symmetric/default native metadata");
            // Old asymmetric snapshots did not encode a reference and must not
            // be retroactively guessed. Preserve this conservative migration gate.
            snapshotType.GetField("SchemaVersion").SetValue(intact, 1);
            snapshotType.GetField("NodeReferencePavementWidth").SetValue(intact, float.NaN);
            rejected = false;
            try
            {
                compilePlacement.Invoke(null, new object[] { intact });
            }
            catch (TargetInvocationException error)
            {
                FieldInfo code = error.InnerException.GetType().GetField("EventName");
                rejected = code != null && (string)code.GetValue(error.InnerException)
                    == "placement_legacy_asymmetric_snapshot_requires_reexport";
            }
            if (!rejected) throw new InvalidOperationException("Missing legacy asymmetric reference was guessed");
            object oneWay = Activator.CreateInstance(libraryType, BindingFlags.Instance | BindingFlags.Public,
                null, new object[] { root, "jp-3l-oneway", "placement/jp-3l-oneway/index.json" }, null);
            object oneWayPacket = resolve.Invoke(oneWay, new object[] { "NARROW|CENTERED|NONE|STANDARD|FLUSH" });
            object oneWaySection = snapshotType.GetField("Section").GetValue(oneWayPacket);
            if (oneWaySection.GetType().GetField("TrafficBoundary").GetValue(oneWaySection) != null)
                throw new InvalidOperationException("One-way snapshot invented an opposing center boundary");
            snapshotType.GetField("SchemaVersion").SetValue(oneWayPacket, 1);
            snapshotType.GetField("NodeReferencePavementWidth").SetValue(oneWayPacket, float.NaN);
            object legacy = compilePlacement.Invoke(null, new object[] { oneWayPacket });
            if (Math.Abs((float)definitionType.GetField("PavementWidth").GetValue(legacy) - 3f) > 0.0001f)
                throw new InvalidOperationException("Legacy symmetric reference was not preserved");
            object wrongReference = resolve.Invoke(asymmetric, new object[] { "NARROW|CENTERED|NONE|NONE|DEPRESSED" });
            snapshotType.GetField("NodeReferencePavementWidth").SetValue(wrongReference, 13f);
            rejected = false;
            try { compilePlacement.Invoke(null, new object[] { wrongReference }); }
            catch (TargetInvocationException error) { rejected = error.InnerException is InvalidDataException; }
            if (!rejected) throw new InvalidOperationException("Physical left sidewalk was accepted as the neutral shader reference");
        }

        private static void ValidateMeshTangentContract(Assembly runtimeAssembly)
        {
            MethodInfo calculate = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.PrefabUpdater", true)
                .GetMethod("CalculateMeshTangents", BindingFlags.Static | BindingFlags.NonPublic);
            if (calculate == null) throw new InvalidOperationException("Runtime mesh tangent calculation is missing");
            // A top face, including the reversed-U mapping used on the other
            // side of a road. Expected tangent and handedness are geometric.
            Vector3[] vertices = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1) };
            Vector3[] normals = { Vector3.up, Vector3.up, Vector3.up };
            foreach (float mirror in new float[] { 1f, -1f })
            {
                Vector2[] uv = { new Vector2(0, 0), new Vector2(mirror, 0), new Vector2(0, 1) };
                Vector4[] tangents = (Vector4[])calculate.Invoke(null,
                    new object[] { vertices, normals, uv, new int[] { 0, 2, 1 } });
                foreach (Vector4 tangent in tangents)
                    if (Math.Abs(tangent.x - mirror) > 0.0001f || Math.Abs(tangent.y) > 0.0001f
                        || Math.Abs(tangent.z) > 0.0001f || Math.Abs(tangent.w + mirror) > 0.0001f)
                        throw new InvalidOperationException("UV tangent direction or mirror handedness is incorrect");
            }
            // Curb wall: U runs down the wall, V runs along the road.
            Vector3[] wall = { new Vector3(0, 1, 0), new Vector3(0, 0, 0), new Vector3(0, 1, 1) };
            Vector3[] wallNormals = { Vector3.left, Vector3.left, Vector3.left };
            Vector4[] wallTangents = (Vector4[])calculate.Invoke(null, new object[] {
                wall, wallNormals, new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) },
                new int[] { 0, 1, 2 } });
            foreach (Vector4 tangent in wallTangents)
                if (Math.Abs(tangent.x) > 0.0001f || Math.Abs(tangent.y + 1f) > 0.0001f
                    || Math.Abs(tangent.z) > 0.0001f || Math.Abs(tangent.w - 1f) > 0.0001f)
                    throw new InvalidOperationException("Curb wall tangent frame is incorrect");
        }

        private static void ValidateGameModuleBoundary(Assembly assembly)
        {
            Type entry = assembly.GetType("RoadRuntimeHost.Runtime.RuntimeEntry",true);
            Type session = assembly.GetType("RoadRuntimeHost.Runtime.RoadGameSession",true);
            if (!CallsMethod(entry.GetMethod("Start"),session.FullName,"Start")
                || !CallsMethod(entry.GetMethod("Stop"),session.FullName,"Dispose"))
                throw new InvalidOperationException("Preview does not delegate game lifecycle to the separable module");
            Type controller = assembly.GetType("RoadRuntimeHost.Runtime.RoadPlacementGeometryController",true);
            Type port = assembly.GetType("RoadRuntimeHost.Runtime.IPlacementPrefabFactory",true);
            foreach (FieldInfo field in controller.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
                if (field.FieldType.FullName=="RoadRuntimeHost.Runtime.PrefabUpdater"
                    || field.FieldType.FullName=="RoadRuntimeHost.Runtime.CatalogRoad")
                    throw new InvalidOperationException("Game controller holds a preview implementation");
            if (controller.GetConstructor(new Type[]{port,assembly.GetType("RoadRuntimeHost.Runtime.ImtPreviewService",true)})==null)
                throw new InvalidOperationException("Game prefab capability port is missing");
        }

        private static void ValidateImporterOutputContract()
        {
            System.Xml.Serialization.XmlSerializer serializer = new System.Xml.Serialization.XmlSerializer(typeof(RoadImporter.RoadAssetInfo));
            RoadImporter.RoadAssetInfo legacy;
            using (StringReader reader = new StringReader("<RoadAssetInfo><name>old job</name><basicModel><segmentMeshes><CSMesh><name>road</name><shader>Custom/Net/Road</shader></CSMesh></segmentMeshes></basicModel></RoadAssetInfo>"))
                legacy = (RoadImporter.RoadAssetInfo)serializer.Deserialize(reader);
            if (legacy.referencePropTemplate!=null || legacy.basicModel.segmentMeshes[0].mainTextureScale!=null)
                throw new InvalidOperationException("Old importer XML acquired implicit prop or scale settings");
            RoadImporter.RoadAssetInfo source = new RoadImporter.RoadAssetInfo {
                name="new job",referencePropTemplate="Basic Road",
                basic=new RoadImporter.CSNetInfo {m_lanes=new RoadImporter.CSNetInfo.Lane[]{
                    new RoadImporter.CSNetInfo.Lane {m_laneProps=new RoadImporter.CSNetInfo.Prop[0]},
                    new RoadImporter.CSNetInfo.Lane {m_laneProps=null}}}
            };
            source.basicModel.segmentMeshes=new RoadImporter.NetModelInfo.CSMesh[]{
                new RoadImporter.NetModelInfo.CSMesh {shader="Custom/Net/RoadBridge",mainTextureScale=new float[]{1f,0.5f}}};
            string xml;
            using (StringWriter writer=new StringWriter()) {serializer.Serialize(writer,source);xml=writer.ToString();}
            RoadImporter.RoadAssetInfo restored;
            using(StringReader reader=new StringReader(xml)) restored=(RoadImporter.RoadAssetInfo)serializer.Deserialize(reader);
            if(restored.referencePropTemplate!="Basic Road" || restored.basicModel.segmentMeshes[0].shader!="Custom/Net/RoadBridge"
                || restored.basicModel.segmentMeshes[0].mainTextureScale[1]!=0.5f
                || restored.basic.m_lanes[0].m_laneProps==null || restored.basic.m_lanes[0].m_laneProps.Length!=0
                || restored.basic.m_lanes[1].m_laneProps!=null)
                throw new InvalidOperationException("Importer output contract lost explicit/default ownership");
            if(!CallsMethod(typeof(RoadImporter.RoadAssetInfo).GetMethod("WriteToGame"),"RoadBuilder.Import.ReferenceLaneProps","ApplyDefaults"))
                throw new InvalidOperationException("Importer bypasses shared prop selection");
            foreach(float[] invalid in new float[][]{new float[]{1f},new float[]{1f,0f},new float[]{float.NaN,1f}})
            {
                bool rejected=false;
                try {RoadImporter.ModelImport.RunImport(new RoadImporter.ModelImport.Job {mainTextureScale=invalid});}
                catch(ArgumentException) {rejected=true;}
                if(!rejected) throw new InvalidOperationException("Invalid texture scale reached the native importer");
            }
            Console.WriteLine("IMPORT_OUTPUT_CONTRACT_OK legacy_preserved explicit_props template shader scale");
        }

        private static void ValidateReferenceLanePropContract(Assembly runtimeAssembly)
        {
            Type updater = runtimeAssembly.GetType(
                "RoadBuilder.Import.ReferenceLaneProps", true);
            if (updater.GetMethod("Copy", BindingFlags.Static | BindingFlags.Public) == null
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

        private static void ValidateRuntimeMarkingPlanContract(Assembly runtimeAssembly)
        {
            Type segmentType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeSegmentSnapshot", true);
            Type laneType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeLaneSnapshot", true);
            Type boundaryType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeBoundarySnapshot", true);
            Type entranceType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeEntranceSnapshot", true);
            Type nodeType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeNodeSnapshot", true);
            Type builderType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeMarkingPlanBuilder", true);
            Type semanticType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeBoundarySemantic", true);
            MethodInfo build = builderType.GetMethod(
                "Build", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (build == null) throw new InvalidOperationException(
                "raw runtime topology marking-plan builder is missing");

            object oneWay = CreateRuntimeSegment(
                segmentType, laneType, boundaryType, 1, true, 1, 1);
            object oneWayNode = CreateRuntimeNode(
                nodeType, entranceType, oneWay, true, true, true, true, true);
            object oneWayPlan = build.Invoke(null, new object[] { oneWayNode });
            IList oneWayBoundaries = (IList)oneWayPlan.GetType().GetField(
                "SegmentBoundaries", BindingFlags.Instance | BindingFlags.Public).GetValue(oneWayPlan);
            foreach (object boundary in oneWayBoundaries)
            {
                object semantic = boundary.GetType().GetField(
                    "Semantic", BindingFlags.Instance | BindingFlags.Public).GetValue(boundary);
                if (string.Equals(semantic.ToString(), "OpposingCenter", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "raw one-way topology produced an opposing center boundary");
            }

            object twoWay = CreateRuntimeSegment(
                segmentType, laneType, boundaryType, 2, true, 1, 2);
            object twoWayNode = CreateRuntimeNode(
                nodeType, entranceType, twoWay, true, true, true, true, true);
            object twoWayPlan = build.Invoke(null, new object[] { twoWayNode });
            IList twoWayBoundaries = (IList)twoWayPlan.GetType().GetField(
                "SegmentBoundaries", BindingFlags.Instance | BindingFlags.Public).GetValue(twoWayPlan);
            int centers = 0;
            foreach (object boundary in twoWayBoundaries)
            {
                object semantic = boundary.GetType().GetField(
                    "Semantic", BindingFlags.Instance | BindingFlags.Public).GetValue(boundary);
                if (string.Equals(semantic.ToString(), "OpposingCenter", StringComparison.Ordinal)) ++centers;
            }
            if (centers != 1) throw new InvalidOperationException(
                "raw two-way topology did not produce exactly one opposing center boundary");

            object nonTargetA = CreateRuntimeSegment(
                segmentType, laneType, boundaryType, 3, true, 1, 1);
            object nonTargetB = CreateRuntimeSegment(
                segmentType, laneType, boundaryType, 4, false, 1, 1);
            object nonTargetC = CreateRuntimeSegment(
                segmentType, laneType, boundaryType, 5, false, 1, 1);
            object mixedNode = CreateRuntimeNode(
                nodeType, entranceType, new object[] { nonTargetA, nonTargetB, nonTargetC },
                true, true, true, true, true);
            object mixedPlan = build.Invoke(null, new object[] { mixedNode });
            IList mixedCrosswalks = (IList)mixedPlan.GetType().GetField(
                "CrosswalkSegments", BindingFlags.Instance | BindingFlags.Public)
                .GetValue(mixedPlan);
            if (mixedCrosswalks.Count != 1)
                throw new InvalidOperationException(
                    "target filtering changed the full runtime junction degree or crosswalk ownership");

            object incomplete = CreateRuntimeNode(
                nodeType, entranceType, new object[] { oneWay, twoWay },
                true, false, false, false, false);
            Array incompleteEntrances = Array.CreateInstance(entranceType, 1);
            incompleteEntrances.SetValue(CreateRuntimeEntrance(
                entranceType, 1, true, false, false, false, false), 0);
            SetField(nodeType, incomplete, "Entrances", incompleteEntrances);
            bool rejected = false;
            try { build.Invoke(null, new object[] { incomplete }); }
            catch (TargetInvocationException error)
            {
                rejected = error.InnerException is InvalidOperationException;
            }
            if (!rejected) throw new InvalidOperationException(
                "incomplete raw runtime topology was accepted");

            // Node-local physical order differs from prefab lane order. A 1+4
            // road meeting a 2+3 road must not join the surplus incoming
            // separator to another incoming separator across the center.
            object bendA = CreateRuntimeSegment(segmentType, laneType, boundaryType, 11, true, 2, 1);
            object bendB = CreateRuntimeSegment(segmentType, laneType, boundaryType, 12, true, 2, 1);
            Array bendLanesA = Array.CreateInstance(laneType, 5);
            Array bendLanesB = Array.CreateInstance(laneType, 5);
            Array bendPointsA = Array.CreateInstance(boundaryType, 6);
            Array bendPointsB = Array.CreateInstance(boundaryType, 6);
            for (int index = 0; index < 5; ++index)
            {
                Type flow = runtimeAssembly.GetType("RoadRuntimeHost.Runtime.RuntimeLaneFlow", true);
                bendLanesA.SetValue(CreateRuntimeLane(laneType, flow, index, index == 0 ? 2 : 1), index);
                bendLanesB.SetValue(CreateRuntimeLane(laneType, flow, index, index < 2 ? 2 : 1), index);
            }
            for (int index = 0; index < 6; ++index)
            {
                bendPointsA.SetValue(CreateRuntimeBoundary(boundaryType, index, index,
                    index == 0 ? -1 : 5 - index, index == 5 ? -1 : 4 - index), index);
                bendPointsB.SetValue(CreateRuntimeBoundary(boundaryType, index, index,
                    index - 1, index == 5 ? -1 : index), index);
            }
            SetField(segmentType, bendA, "Lanes", bendLanesA);
            SetField(segmentType, bendA, "Boundaries", bendPointsA);
            SetField(segmentType, bendB, "Lanes", bendLanesB);
            SetField(segmentType, bendB, "Boundaries", bendPointsB);
            SetField(segmentType, bendB, "StartNode", (ushort)10);
            SetField(segmentType, bendB, "EndNode", (ushort)20);
            object bendNode = CreateRuntimeNode(nodeType, entranceType, new object[] { bendA, bendB },
                true, true, true, true, true);
            Array bendEntrances = (Array)nodeType.GetField("Entrances").GetValue(bendNode);
            SetField(entranceType, bendEntrances.GetValue(1), "IsStartSide", true);
            object bendPlan = build.Invoke(null, new object[] { bendNode });
            IList connectors = (IList)bendPlan.GetType().GetField("NodeConnectors").GetValue(bendPlan);
            System.Collections.Generic.HashSet<string> expected = new System.Collections.Generic.HashSet<string>(
                new string[] { "0:5", "5:0", "4:2", "3:3", "2:4" });
            foreach (object connector in connectors)
            {
                Type type = connector.GetType();
                string pair = type.GetField("FirstPointIndex").GetValue(connector) + ":"
                    + type.GetField("SecondPointIndex").GetValue(connector);
                if (!expected.Remove(pair)) throw new InvalidOperationException(
                    "asymmetric bend joined wrong flow or outer surplus separator: " + pair);
            }
            if (expected.Count != 0) throw new InvalidOperationException("asymmetric bend omitted an inner connector");

            for (int a = 1; a <= 4; ++a)
            for (int b = 1; b <= 4; ++b)
            for (int c = 1; c <= 4; ++c)
            for (int d = 1; d <= 4; ++d)
            for (int reversed = 0; reversed < 2; ++reversed)
            {
                object firstRoad = CreateBendRoad(segmentType, laneType, boundaryType, 21, a, b, false);
                object secondRoad = CreateBendRoad(segmentType, laneType, boundaryType, 22, c, d, true);
                if (reversed != 0)
                {
                    // Reversing endpoint and Invert together leaves the road's
                    // physical lanes and flow at this node unchanged.
                    SetField(segmentType, firstRoad, "StartNode", (ushort)10);
                    SetField(segmentType, firstRoad, "EndNode", (ushort)1);
                    SetField(segmentType, firstRoad, "Invert", true);
                    SetField(segmentType, secondRoad, "StartNode", (ushort)20);
                    SetField(segmentType, secondRoad, "EndNode", (ushort)10);
                    SetField(segmentType, secondRoad, "Invert", true);
                }
                object rawNode = CreateRuntimeNode(nodeType, entranceType, new object[] { firstRoad, secondRoad },
                    true, true, true, true, true);
                object rawPlan = build.Invoke(null, new object[] { rawNode });
                IList actual = (IList)rawPlan.GetType().GetField("NodeConnectors").GetValue(rawPlan);
                System.Collections.Generic.HashSet<string> pairs = new System.Collections.Generic.HashSet<string>();
                pairs.Add("0:" + (c + d));
                pairs.Add((a + b) + ":0");
                pairs.Add(b + ":" + c);
                for (int rank = 1; rank < Math.Min(b, d); ++rank) pairs.Add((b - rank) + ":" + (c + rank));
                for (int rank = 1; rank < Math.Min(a, c); ++rank) pairs.Add((b + rank) + ":" + (c - rank));
                foreach (object connector in actual)
                {
                    Type type = connector.GetType();
                    string pair = type.GetField("FirstPointIndex").GetValue(connector) + ":"
                        + type.GetField("SecondPointIndex").GetValue(connector);
                    if (!pairs.Remove(pair)) throw new InvalidOperationException("bend flow/rank matrix mismatch: " + pair);
                }
                if (pairs.Count != 0) throw new InvalidOperationException("bend flow/rank matrix has missing pairs");
                // Degree transitions must leave neither a zebra nor a stop line
                // in corner/end plans, even when crossing and traffic flags remain.
                IList crosswalks = (IList)rawPlan.GetType().GetField("CrosswalkSegments").GetValue(rawPlan);
                IList stops = (IList)rawPlan.GetType().GetField("StopLineSegments").GetValue(rawPlan);
                if (crosswalks.Count != 0 || stops.Count != 0) throw new InvalidOperationException("corner plan retained junction defaults");
            }
        }

        private static object CreateBendRoad(Type segmentType, Type laneType, Type boundaryType,
            ushort id, int backward, int forward, bool start)
        {
            object result = CreateRuntimeSegment(segmentType, laneType, boundaryType, id, true, 2, 1);
            int count = backward + forward;
            Type flow = segmentType.Assembly.GetType("RoadRuntimeHost.Runtime.RuntimeLaneFlow", true);
            Array lanes = Array.CreateInstance(laneType, count);
            Array points = Array.CreateInstance(boundaryType, count + 1);
            for (int index = 0; index < count; ++index)
                lanes.SetValue(CreateRuntimeLane(laneType, flow, index, index < backward ? 2 : 1), index);
            for (int index = 0; index <= count; ++index)
                points.SetValue(CreateRuntimeBoundary(boundaryType, index, index,
                    index == 0 ? -1 : (start ? index - 1 : count - index),
                    index == count ? -1 : (start ? index : count - index - 1)), index);
            SetField(segmentType, result, "Lanes", lanes);
            SetField(segmentType, result, "Boundaries", points);
            SetField(segmentType, result, "StartNode", (ushort)(start ? 10 : 1));
            SetField(segmentType, result, "EndNode", (ushort)(start ? 20 : 10));
            return result;
        }

        private static object CreateRuntimeSegment(
            Type segmentType,
            Type laneType,
            Type boundaryType,
            ushort segmentId,
            bool target,
            int leftFlow,
            int rightFlow)
        {
            Type flowType = segmentType.Assembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeLaneFlow", true);
            Array lanes = Array.CreateInstance(laneType, 2);
            lanes.SetValue(CreateRuntimeLane(laneType, flowType, 0, leftFlow), 0);
            lanes.SetValue(CreateRuntimeLane(laneType, flowType, 1, rightFlow), 1);
            Array boundaries = Array.CreateInstance(boundaryType, 3);
            boundaries.SetValue(CreateRuntimeBoundary(boundaryType, 0, 0, -1, 0), 0);
            boundaries.SetValue(CreateRuntimeBoundary(boundaryType, 1, 1, 0, 1), 1);
            boundaries.SetValue(CreateRuntimeBoundary(boundaryType, 2, 2, 1, -1), 2);
            object result = Activator.CreateInstance(segmentType, true);
            SetField(segmentType, result, "SegmentId", segmentId);
            SetField(segmentType, result, "Target", target);
            SetField(segmentType, result, "StartNode", (ushort)1);
            SetField(segmentType, result, "EndNode", (ushort)10);
            SetField(segmentType, result, "Invert", false);
            SetField(segmentType, result, "Lanes", lanes);
            SetField(segmentType, result, "Boundaries", boundaries);
            return result;
        }

        private static object CreateRuntimeLane(Type laneType, Type flowType, int index, int flow)
        {
            object result = Activator.CreateInstance(laneType, true);
            SetField(laneType, result, "Index", index);
            SetField(laneType, result, "Flow", Enum.ToObject(flowType, flow));
            SetField(laneType, result, "Vehicle", true);
            SetField(laneType, result, "Pedestrian", false);
            return result;
        }

        private static object CreateRuntimeBoundary(
            Type boundaryType, int pointIndex, int ordinal, int left, int right)
        {
            object result = Activator.CreateInstance(boundaryType, true);
            SetField(boundaryType, result, "PointIndex", pointIndex);
            SetField(boundaryType, result, "PhysicalOrdinal", ordinal);
            SetField(boundaryType, result, "LeftLaneIndex", left);
            SetField(boundaryType, result, "RightLaneIndex", right);
            return result;
        }

        private static object CreateRuntimeEntrance(
            Type entranceType, ushort segmentId, bool target,
            bool pedestrian, bool crossing, bool incoming, bool control)
        {
            object result = Activator.CreateInstance(entranceType, true);
            SetField(entranceType, result, "SegmentId", segmentId);
            SetField(entranceType, result, "Target", target);
            SetField(entranceType, result, "IsStartSide", false);
            SetField(entranceType, result, "HasPedestrianLane", pedestrian);
            SetField(entranceType, result, "CrossingAllowed", crossing);
            SetField(entranceType, result, "HasIncomingVehicleLane", incoming);
            SetField(entranceType, result, "HasTrafficControl", control);
            SetField(entranceType, result, "Boundaries", Array.CreateInstance(
                entranceType.Assembly.GetType("RoadRuntimeHost.Runtime.RuntimeBoundarySnapshot", true), 0));
            return result;
        }

        private static object CreateRuntimeNode(
            Type nodeType, Type entranceType, object firstSegment,
            bool target, bool pedestrian, bool crossing, bool incoming, bool control)
        {
            return CreateRuntimeNode(nodeType, entranceType,
                new object[] { firstSegment }, target, pedestrian, crossing, incoming, control);
        }

        private static object CreateRuntimeNode(
            Type nodeType, Type entranceType, object[] segmentValues,
            bool target, bool pedestrian, bool crossing, bool incoming, bool control)
        {
            Type segmentType = segmentValues[0].GetType();
            Array segments = Array.CreateInstance(segmentType, segmentValues.Length);
            Array entrances = Array.CreateInstance(entranceType, segmentValues.Length);
            for (int index = 0; index < segmentValues.Length; ++index)
            {
                segments.SetValue(segmentValues[index], index);
                ushort id = (ushort)segmentType.GetField(
                    "SegmentId", BindingFlags.Instance | BindingFlags.Public).GetValue(segmentValues[index]);
                bool isTarget = (bool)segmentType.GetField(
                    "Target", BindingFlags.Instance | BindingFlags.Public).GetValue(segmentValues[index]);
                entrances.SetValue(CreateRuntimeEntrance(
                    entranceType, id, isTarget, pedestrian, crossing, incoming, control), index);
            }
            object result = Activator.CreateInstance(nodeType, true);
            SetField(nodeType, result, "NodeId", (ushort)10);
            SetField(nodeType, result, "Segments", segments);
            SetField(nodeType, result, "Entrances", entrances);
            return result;
        }

        private static void SetField(Type type, object instance, string name, object value)
        {
            FieldInfo field = type.GetField(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(type.FullName, name);
            field.SetValue(instance, value);
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
            Type topologyHookType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.ImtTopologyUpdatePatch", true);
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
            Type runtimeType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.RuntimeEntry", true);
            MethodInfo runtimeTick = runtimeType.GetMethod(
                "Tick", BindingFlags.Instance | BindingFlags.Public);
            MethodInfo topologyPostfix = topologyHookType.GetMethod(
                "UpdatePostfix", BindingFlags.Static | BindingFlags.NonPublic);
            if (runtimeTick == null || topologyPostfix == null
                || previewType.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public) != null)
                throw new InvalidOperationException(
                    "IMT topology completion must be event-driven rather than Runtime tick polling");

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

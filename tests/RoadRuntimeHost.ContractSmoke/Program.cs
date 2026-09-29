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

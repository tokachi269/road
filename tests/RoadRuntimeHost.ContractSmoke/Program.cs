using System;
using System.IO;
using System.Reflection;
using RoadRuntimeHost.Runtime;
using RoadRuntimeHost.Loader;
using ICities;

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
                string temp = Path.Combine(Path.GetTempPath(), "RoadRuntimeHost.ContractSmoke." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    string log = Path.Combine(temp, "diagnostics.jsonl");
                    Type loaderLog = typeof(RoadRuntimeHostMod).Assembly.GetType("RoadRuntimeHost.Loader.DiagnosticLog", true);
                    loaderLog.GetMethod("Configure", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { log });
                    loaderLog.GetMethod("Info", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { "MOD", "contract_loader_probe", "Loader diagnostic probe", new string[] { "probe", "true" } });
                    string result = RuntimeEntry.ValidatePreview(args[0], log);
                    string malformed = Path.Combine(temp, "malformed");
                    Directory.CreateDirectory(malformed);
                    File.WriteAllText(Path.Combine(malformed, "catalog.json"), "{\"schema_version\":\"wrong\"}");
                    try { RuntimeEntry.ValidatePreview(malformed, log); }
                    catch { }
                    string contents = File.ReadAllText(log);
                    if (!contents.Contains("\"event\":\"preview_validation_success\"")
                        || !contents.Contains("\"category\":\"SUCCESS\"")
                        || !contents.Contains("\"category\":\"CONTRACT_TYPE\"")
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
    }
}

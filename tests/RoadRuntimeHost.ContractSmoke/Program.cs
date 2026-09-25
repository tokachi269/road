using System;
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
                if (runtime.GetMethod("Start") == null || runtime.GetMethod("Tick") == null || runtime.GetMethod("Stop") == null)
                    throw new InvalidOperationException("hot runtime reflection contract is incomplete");
                Console.WriteLine("RUNTIME_CONTRACT_OK " + RuntimeEntry.ValidatePreview(args[0]));
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

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using RoadRuntimeHost.Runtime;
using RoadBuilder.Import;

internal static class Program
{
    private sealed class AssetPrefabProvider : IPlacementPrefabFactory
    {
        public NetInfo GetOrCreateSharedHeight(SharedHeightFamily family, string roadId)
        { throw new InvalidOperationException("No native prefabs should be created by this offline probe"); }
    }

    private static int Main(string[] args)
    {
        try
        {
            Assembly assembly = typeof(RoadGameSession).Assembly;
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
                if (reference.Name.StartsWith("RoadRuntimeHost", StringComparison.Ordinal))
                    throw new InvalidOperationException("Game module depends on the development Host assembly");
            if (assembly.GetType("RoadRuntimeHost.Runtime.PrefabUpdater", false) != null
                || assembly.GetType("RoadRuntimeHost.Runtime.Catalog", false) != null)
                throw new InvalidOperationException("Host implementation leaked into the game-only build");
            RoadGameSession session = new RoadGameSession(new AssetPrefabProvider());
            session.Dispose(); session.Dispose(); // unstarted/disposed sessions own no global hooks
            bool rejected = false;
            try { session.RegisterRoad("asset", null, null); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Unstarted game session accepted a road");

            PlacementOptionSelection selection = new PlacementOptionSelection("STANDARD|CENTERED|NONE|STANDARD|DEPRESSED");
            string proposed = selection.Propose(new string[]{"STANDARD|CENTERED|NONE|STANDARD|FLUSH"},4,"FLUSH");
            if (selection.Key.EndsWith("FLUSH") || !proposed.EndsWith("FLUSH"))
                throw new InvalidOperationException("Placement proposal mutated global road definitions");
            if (!HeightTransitionKernel.LevelsJoin(0.3f,0.30001f)
                || HeightTransitionKernel.LevelsJoin(0f,0.3f)) throw new InvalidOperationException("Height boundary changed");
            if (args.Length == 1)
            {
                PlacementVariantLibrary library = new PlacementVariantLibrary(args[0],"jp-1x2l","placement/jp-1x2l/index.json");
                if (library.GetChoices().Length == 0) throw new InvalidOperationException("Asset-backed compiled menu is empty");
            }
            ValidateProps();
            RoadRuntimeHost.ContractSmoke.SegmentLaneIsolationContract.Validate(assembly);
            Console.WriteLine("ROAD_GAME_STANDALONE_OK host_references=0 provider_port props flags lane_isolation");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void ValidateProps()
    {
        NetLaneProps container = (NetLaneProps)FormatterServices.GetUninitializedObject(typeof(NetLaneProps));
        NetLaneProps.Prop signal = new NetLaneProps.Prop {
            m_startFlagsRequired=NetNode.Flags.TrafficLights,
            m_endFlagsForbidden=NetNode.Flags.End,
            m_probability=73, m_repeatDistance=17f, m_angle=90f
        };
        container.m_props = new NetLaneProps.Prop[]{signal,new NetLaneProps.Prop()};
        NetInfo.Lane[] reference = {new NetInfo.Lane {m_laneType=NetInfo.LaneType.Pedestrian, m_position=-5f,m_laneProps=container}};
        List<NetLaneProps.Prop> result = ReferenceLaneProps.Select(container.m_props,false);
        if (result.Count != 1 || object.ReferenceEquals(result[0],signal) || result[0].m_probability!=73
            || result[0].m_repeatDistance!=17f || result[0].m_angle!=90f
            || result[0].m_endFlagsForbidden!=NetNode.Flags.End)
            throw new InvalidOperationException("Import signal props lost their flags or were not copied independently");
        result[0].m_probability=1;
        if (signal.m_probability!=73) throw new InvalidOperationException("Imported road changed the reference road");
        if (ReferenceLaneProps.Copy(reference,0f,"Vehicle","Bicycle").Count!=0)
            throw new InvalidOperationException("Unknown lane was assigned Car props");
    }
}

using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class TestLayoutManager
    {
        private sealed class LayoutState
        {
            public readonly List<ushort> Nodes = new List<ushort>();
            public readonly List<ushort> Segments = new List<ushort>();
        }

        private readonly Dictionary<string, LayoutState> _states = new Dictionary<string, LayoutState>();
        public void Rebuild(string roadId, NetInfo info, TestScenario scenario, int roadIndex)
        {
            DiagnosticLog.Info("CS1_ENVIRONMENT", "test_layout_scheduled", "Test layout rebuild was queued on the simulation thread", "road_id", roadId, "scenario_id", scenario.ScenarioId ?? string.Empty, "layout", scenario.Layout ?? string.Empty);
            SimulationManager.instance.AddAction(delegate
            {
                try
                {
                    LayoutState previous;
                    if (_states.TryGetValue(roadId, out previous)) Release(previous);
                    LayoutState state = Create(info, scenario, roadIndex);
                    _states[roadId] = state;
                    DiagnosticLog.Info("SUCCESS", "test_layout_rebuilt", "Test layout was rebuilt", "road_id", roadId, "node_count", state.Nodes.Count.ToString(), "segment_count", state.Segments.Count.ToString());
                }
                catch (Exception error)
                {
                    DiagnosticLog.Error("CS1_ENVIRONMENT", "test_layout_rebuild_failed", "CS1 rejected or failed the test layout rebuild", error, "road_id", roadId, "scenario_id", scenario.ScenarioId ?? string.Empty, "layout", scenario.Layout ?? string.Empty);
                }
            });
        }

        public void ReleaseAll()
        {
            if (SimulationManager.exists)
            {
                SimulationManager.instance.AddAction(delegate
                {
                    try
                    {
                        foreach (LayoutState state in _states.Values) Release(state);
                        int count = _states.Count;
                        _states.Clear();
                        DiagnosticLog.Info("SUCCESS", "test_layouts_released", "All runtime-owned test layouts were released", "road_count", count.ToString());
                    }
                    catch (Exception error) { DiagnosticLog.Error("CS1_ENVIRONMENT", "test_layout_release_all_failed", "Some runtime-owned test layouts could not be released", error); }
                });
            }
            else _states.Clear();
        }

        public void Release(string roadId)
        {
            SimulationManager.instance.AddAction(delegate
            {
                try
                {
                    LayoutState state;
                    if (!_states.TryGetValue(roadId, out state)) return;
                    Release(state);
                    _states.Remove(roadId);
                    DiagnosticLog.Info("SUCCESS", "test_layout_released", "Runtime-owned test layout was released", "road_id", roadId);
                }
                catch (Exception error) { DiagnosticLog.Error("CS1_ENVIRONMENT", "test_layout_release_failed", "Runtime-owned test layout could not be released", error, "road_id", roadId); }
            });
        }

        private static LayoutState Create(NetInfo info, TestScenario scenario, int roadIndex)
        {
            LayoutState state = new LayoutState();
            float[] source = scenario.Origin ?? new float[0];
            float spacing = scenario.Spacing > 0f ? scenario.Spacing : 96f;
            Vector3 origin = new Vector3(
                (source.Length > 0 ? source[0] : 0f) + roadIndex * spacing * 2f,
                source.Length > 1 ? source[1] : 0f,
                source.Length > 2 ? source[2] : 0f);
            NetManager manager = NetManager.instance;
            Randomizer randomizer = new Randomizer((uint)(roadIndex + 1));

            ushort a = AddNode(manager, ref randomizer, info, origin + new Vector3(0f, 0f, -spacing), state);
            ushort b = AddNode(manager, ref randomizer, info, origin, state);
            ushort c = AddNode(manager, ref randomizer, info, origin + new Vector3(0f, 0f, spacing), state);
            AddSegment(manager, ref randomizer, info, a, b, Vector3.forward, Vector3.back, state);
            AddSegment(manager, ref randomizer, info, b, c, Vector3.forward, Vector3.back, state);

            if (string.Equals(scenario.Layout, "STRAIGHT_BEND_JUNCTION", StringComparison.OrdinalIgnoreCase))
            {
                ushort left = AddNode(manager, ref randomizer, info, origin + new Vector3(-spacing, 0f, 0f), state);
                ushort right = AddNode(manager, ref randomizer, info, origin + new Vector3(spacing, 0f, spacing * 0.5f), state);
                AddSegment(manager, ref randomizer, info, left, b, Vector3.right, Vector3.left, state);
                AddSegment(manager, ref randomizer, info, b, right, new Vector3(1f, 0f, 0.25f).normalized, new Vector3(-1f, 0f, -0.25f).normalized, state);
            }
            return state;
        }

        private static ushort AddNode(NetManager manager, ref Randomizer randomizer, NetInfo info, Vector3 position, LayoutState state)
        {
            ushort node;
            uint buildIndex = SimulationManager.instance.m_currentBuildIndex++;
            if (!manager.CreateNode(out node, ref randomizer, info, position, buildIndex))
                throw new InvalidOperationException("failed to create test node");
            state.Nodes.Add(node);
            return node;
        }

        private static void AddSegment(NetManager manager, ref Randomizer randomizer, NetInfo info, ushort start, ushort end, Vector3 startDirection, Vector3 endDirection, LayoutState state)
        {
            ushort segment;
            uint buildIndex = SimulationManager.instance.m_currentBuildIndex++;
            if (!manager.CreateSegment(out segment, ref randomizer, info, start, end, startDirection, endDirection, buildIndex, buildIndex, false))
                throw new InvalidOperationException("failed to create test segment");
            state.Segments.Add(segment);
        }

        private static void Release(LayoutState state)
        {
            NetManager manager = NetManager.instance;
            foreach (ushort segment in state.Segments)
                if ((manager.m_segments.m_buffer[segment].m_flags & NetSegment.Flags.Created) != 0) manager.ReleaseSegment(segment, true);
            foreach (ushort node in state.Nodes)
                if ((manager.m_nodes.m_buffer[node].m_flags & NetNode.Flags.Created) != 0) manager.ReleaseNode(node);
        }
    }
}

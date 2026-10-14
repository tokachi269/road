using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ColossalFramework.Math;
using CitiesHarmony.API;
using HarmonyLib;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal static class HeightTransitionKernel
    {
        public static bool LevelsJoin(float a, float b)
        {
            return !float.IsNaN(a) && !float.IsNaN(b) && !float.IsInfinity(a) && !float.IsInfinity(b)
                && Math.Abs(a-b) <= 0.0001f;
        }
        public static float[] CurveYOffsets(float start, float end, float nativeOffset, bool turned)
        {
            float a, d;
            EndOffsets(start, end, nativeOffset, turned, out a, out d);
            return new float[] { a, a + (d-a)/3f, a + (d-a)*2f/3f, d };
        }

        public static Bezier3 OffsetCurve(Bezier3 curve, float start, float end, float nativeOffset, bool turned)
        {
            float a, d;
            EndOffsets(start, end, nativeOffset, turned, out a, out d);
            curve.a.y += a; curve.b.y += a + (d-a)/3f;
            curve.c.y += a + (d-a)*2f/3f; curve.d.y += d;
            return curve;
        }

        private static void EndOffsets(float start, float end, float nativeOffset, bool turned, out float a, out float d)
        {
            if (float.IsNaN(start) || float.IsInfinity(start) || float.IsNaN(end) || float.IsInfinity(end)
                || float.IsNaN(nativeOffset) || float.IsInfinity(nativeOffset))
                throw new InvalidDataException("Height transition offsets must be finite");
            a = (turned ? end : start) - nativeOffset;
            d = (turned ? start : end) - nativeOffset;
            if (float.IsInfinity(a) || float.IsInfinity(d) || float.IsInfinity(d-a))
                throw new InvalidDataException("Height transition delta overflows");
        }

        // Called immediately after vanilla rebuilds this segment's native
        // curves. Shared lane definitions are read-only. Validate the complete
        // chain first, so a foreign/partial chain cannot receive partial writes.
        public static float ApplyLaneHeights(NetLane[] buffer, ushort segmentId, uint firstLane,
            NetInfo.Lane[] shared, float[] start, float[] end, bool turned)
        {
            if (buffer == null || segmentId == 0 || shared == null || shared.Length == 0
                || start == null || end == null || start.Length != shared.Length || end.Length != shared.Length)
                throw new InvalidDataException("Individual lane heights require matching segment topology");
            uint id = firstLane;
            for (int i = 0; i < shared.Length; ++i)
            {
                if (shared[i] == null || id == 0 || id >= buffer.Length || buffer[id].m_segment != segmentId)
                    throw new InvalidDataException("Lane chain is incomplete or belongs to another segment");
                float a, d;
                EndOffsets(start[i], end[i], shared[i].m_verticalOffset, turned, out a, out d);
                id = buffer[id].m_nextLane;
            }
            if (id != 0) throw new InvalidDataException("Lane chain is longer than segment topology or cyclic");

            id = firstLane;
            float totalLength = 0f;
            for (int i = 0; i < shared.Length; ++i)
            {
                ref NetLane lane = ref buffer[id];
                lane.m_bezier = OffsetCurve(lane.m_bezier, start[i], end[i], shared[i].m_verticalOffset, turned);
                totalLength += lane.UpdateLength();
                id = lane.m_nextLane;
            }
            return totalLength / shared.Length;
        }
    }

    // A single NetTool invocation owns this state. Preview, failures and
    // unrelated split prefabs cannot consume the temporary segment.
    internal sealed class HeightTransitionPlacementRun
    {
        private readonly string _transition;
        private readonly string _reverse;
        private readonly string _target;
        public bool Consumed { get; private set; }
        public HeightTransitionPlacementRun(string transition, string target)
            : this(transition, transition, target) { }
        public HeightTransitionPlacementRun(string transition, string reverse, string target)
        {
            if (string.IsNullOrEmpty(transition) || string.IsNullOrEmpty(reverse) || string.IsNullOrEmpty(target)
                || transition == target || reverse == target)
                throw new InvalidDataException("Height transition operation requires distinct prefabs");
            _transition = transition; _reverse = reverse; _target = target;
        }
        public string Choose(string requested) { return Choose(requested, false); }
        public string Choose(string requested, bool invert)
        {
            if (requested != _transition) return requested;
            return Consumed ? _target : invert ? _reverse : _transition;
        }
        public void Complete(string actual, bool success)
        {
            if (success && (actual == _transition || actual == _reverse)) Consumed = true;
        }
    }

    internal sealed class PlacementHeightTransitionData : MonoBehaviour
    {
        public string Identity;
        public string Mode;
        public NetInfo StartInfo;
        public NetInfo EndInfo;
        public float[] StartOffsets;
        public float[] EndOffsets;
    }

    internal static class PlacementHeightTransitionHook
    {
        private sealed class Data
        {
            public string Identity, Mode;
            public NetInfo StartInfo, EndInfo;
            public float[] StartOffsets, EndOffsets;
        }
        private static readonly Dictionary<NetInfo, Data> Cache = new Dictionary<NetInfo, Data>();
        private static readonly HashSet<NetInfo> NonTransitions = new HashSet<NetInfo>();
        private static readonly object Sync = new object();
        private static bool _started;
        public static bool Ready { get; internal set; }
        private static Func<NetInfo, NetInfo> _armed;
        private static Action<NetInfo> _completed;
        [ThreadStatic] private static Operation _operation;
        internal sealed class Operation
        {
            public SharedHeightHook.Operation Shared;
            public NetInfo Requested;
            public string Identity;
            public NetInfo ReverseBasic;
            public string ReverseIdentity;
            public HeightTransitionPlacementRun Run;
        }

        public static void Start(Func<NetInfo, NetInfo> armed, Action<NetInfo> completed)
        {
            _armed = armed; _completed = completed;
            if (_started) return;
            _started = true;
            InvokePatch("Start");
        }
        public static void Stop()
        {
            if (_started) InvokePatch("Stop");
            _started = false; Ready = false; _armed = null; _completed = null;
            lock (Sync) { Cache.Clear(); NonTransitions.Clear(); }
        }
        private static void InvokePatch(string method)
        {
            try
            {
                typeof(PlacementHeightTransitionHook).Assembly.GetType("RoadRuntimeHost.Runtime.PlacementHeightTransitionPatch", true)
                    .GetMethod(method, BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("MOD_DEPENDENCY", "height_transition_hook_failed", "Height transition native hooks are unavailable", error);
            }
        }

        private static Data Find(NetInfo info)
        {
            if (info == null) return null;
            Data data;
            lock (Sync) {
                if (NonTransitions.Contains(info)) return null;
                if (Cache.TryGetValue(info, out data)) return data;
            }
            foreach (Component component in info.gameObject.GetComponents(typeof(Component)))
            {
                if (component == null || component.GetType().FullName != typeof(PlacementHeightTransitionData).FullName) continue;
                // Immutable tags from an older hot DLL remain valid. No scan of
                // segments is needed to recover their endpoint references.
                Type type = component.GetType();
                data = new Data {
                    Identity = (string)type.GetField("Identity").GetValue(component),
                    Mode = (string)type.GetField("Mode").GetValue(component),
                    StartInfo = (NetInfo)type.GetField("StartInfo").GetValue(component),
                    EndInfo = (NetInfo)type.GetField("EndInfo").GetValue(component),
                    StartOffsets = (float[])type.GetField("StartOffsets").GetValue(component),
                    EndOffsets = (float[])type.GetField("EndOffsets").GetValue(component)
                };
                if (data.StartInfo == null || data.EndInfo == null || data.StartOffsets == null || data.EndOffsets == null)
                    throw new InvalidDataException("Height transition prefab has incomplete endpoints");
                lock (Sync) {
                    Data existing;
                    if (Cache.TryGetValue(info, out existing)) return existing;
                    Cache.Add(info, data);
                }
                return data;
            }
            lock (Sync) NonTransitions.Add(info);
            return null;
        }

        private static float OwnedEndpointLevel(NetInfo info, bool start, NetSegment.Flags flags, NetSegment.Flags2 flags2, ushort segmentId = 0)
        {
            if (info == null || !info.name.StartsWith("RoadRuntimeHost.Placement.", StringComparison.Ordinal)) return float.NaN;
            float sharedLevel;
            if (SharedHeightHook.TryLevel(info, segmentId, start, (flags & NetSegment.Flags.Invert) != 0, out sharedLevel)) return sharedLevel;
            for (int i = 0; i < info.m_lanes.Length; ++i)
            {
                NetInfo.Lane lane = info.m_lanes[i];
                if (lane.m_laneType != NetInfo.LaneType.Vehicle || lane.m_vehicleType != VehicleInfo.VehicleType.Car) continue;
                Data data = Find(info);
                if (data == null) return lane.m_verticalOffset;
                bool turned = MeshTurned(info, flags, flags2);
                return start != turned ? data.StartOffsets[i] : data.EndOffsets[i];
            }
            return float.NaN;
        }

        internal static bool CanConnect(NetInfo info, bool invert, ushort startNode, ushort endNode)
        {
            NetSegment.Flags flags = NetSegment.Flags.Created | (invert ? NetSegment.Flags.Invert : NetSegment.Flags.None);
            NetManager manager = NetManager.instance;
            for (int side = 0; side < 2; ++side)
            {
                ushort nodeId = side == 0 ? startNode : endNode;
                if (nodeId == 0) continue;
                float level = OwnedEndpointLevel(info, side == 0, flags, NetTool.m_zoneGridFlags);
                if (float.IsNaN(level)) continue;
                ref NetNode node = ref manager.m_nodes.m_buffer[nodeId];
                for (int index = 0; index < 8; ++index)
                {
                    ushort id = node.GetSegment(index);
                    if (id == 0) continue;
                    ref NetSegment neighbour = ref manager.m_segments.m_buffer[id];
                    float peer = OwnedEndpointLevel(neighbour.Info, neighbour.m_startNode == nodeId,
                        neighbour.m_flags, neighbour.m_flags2, id);
                    // Unknown external road levels are not inferred from lane
                    // offsets. Known generated levels must actually meet.
                    if (!float.IsNaN(peer) && !HeightTransitionKernel.LevelsJoin(level, peer))
                    {
                        DiagnosticLog.Warn("MOD_CONTRACT", "height_transition_required", "Generated road levels do not meet; use an explicit height transition", "node_id", nodeId.ToString());
                        return false;
                    }
                }
            }
            return true;
        }

        internal static Operation BeginPlacement(NetInfo info, bool test, bool visualize)
        {
            SharedHeightHook.Operation shared = SharedHeightHook.Begin(info, test, visualize);
            if (shared != null) return new Operation { Shared = shared };
            if (_operation != null || test || visualize || _armed == null) return null;
            NetInfo reverse = _armed(info);
            if (reverse == null) return null;
            Data data = Find(info);
            if (data == null) return null;
            Data reverseData = Find(reverse);
            if (reverseData == null) throw new InvalidDataException("Reverse height transition was not registered");
            Operation operation = new Operation { Requested = info, Identity = data.Identity,
                ReverseBasic = reverse, ReverseIdentity = reverseData.Identity,
                Run = new HeightTransitionPlacementRun(data.Identity, reverseData.Identity, data.EndInfo.name) };
            _operation = operation; return operation;
        }

        internal static NetInfo ChooseCreatedPrefab(NetInfo requested, bool invert)
        {
            if (_operation == null) return requested;
            Data data = Find(requested);
            if (data == null || data.Identity != _operation.Identity) return requested;
            string selected = _operation.Run.Choose(data.Identity, invert);
            if (selected == _operation.Identity) return requested;
            if (selected == _operation.ReverseIdentity) return PrefabUpdater.ModeTemplate(_operation.ReverseBasic, data.Mode);
            return data.EndInfo;
        }

        internal static void CompleteCreatedPrefab(NetInfo actual, bool success)
        {
            if (_operation == null || !success) return;
            Data data = Find(actual);
            if (data != null && (data.Identity == _operation.Identity || data.Identity == _operation.ReverseIdentity))
                _operation.Run.Complete(data.Identity, true);
        }

        internal static void EndPlacement(Operation operation)
        {
            if (operation != null && operation.Shared != null) { SharedHeightHook.End(operation.Shared); return; }
            if (operation == null || !ReferenceEquals(_operation, operation)) return;
            _operation = null;
            if (operation.Run.Consumed && _completed != null) _completed(operation.Requested);
        }

        internal static bool MeshTurned(NetInfo info, NetSegment.Flags flags, NetSegment.Flags2 flags2)
        {
            bool? result = null;
            foreach (NetInfo.Segment entry in info.m_segments)
            {
                bool turned;
                if (!entry.CheckFlags(flags, flags2, out turned)) continue;
                if (result.HasValue && result.Value != turned) throw new InvalidDataException("Transition meshes disagree on longitudinal facing");
                result = turned;
            }
            if (!result.HasValue) throw new InvalidDataException("Height transition has no matching segment mesh");
            return result.Value;
        }

        internal static void UpdateLanes(ushort segmentId, ref NetSegment segment)
        {
            if (SharedHeightHook.UpdateLanes(segmentId, ref segment)) return;
            NetInfo info = segment.Info;
            Data data = Find(info);
            if (data == null) return;
            if (data.StartOffsets.Length != info.m_lanes.Length || data.EndOffsets.Length != info.m_lanes.Length)
                throw new InvalidDataException("Height transition lane endpoints disagree with prefab topology");
            bool turned = MeshTurned(info, segment.m_flags, segment.m_flags2);
            NetManager manager = NetManager.instance;
            segment.m_averageLength = HeightTransitionKernel.ApplyLaneHeights(manager.m_lanes.m_buffer,
                segmentId, segment.m_lanes, info.m_lanes, data.StartOffsets, data.EndOffsets, turned);
        }

        internal static void UpdateNode(ushort nodeId, ref NetNode node)
        {
            NetManager manager = NetManager.instance;
            NetInfo endpoint = null;
            for (int index = 0; index < 8; ++index)
            {
                ushort id = node.GetSegment(index);
                if (id == 0) continue;
                ref NetSegment segment = ref manager.m_segments.m_buffer[id];
                Data data = Find(segment.Info);
                if (data == null) continue;
                bool turned = MeshTurned(segment.Info, segment.m_flags, segment.m_flags2);
                NetInfo candidate = (segment.m_startNode == nodeId) != turned ? data.StartInfo : data.EndInfo;
                if (endpoint != null && !ReferenceEquals(endpoint, candidate))
                    throw new InvalidDataException("Transition endpoints disagree at one node");
                endpoint = candidate;
            }
            // Only select the compiled ordinary node. Never touch position,
            // elevation, heightOffset, directions or Node Controller flags.
            if (endpoint != null) node.Info = endpoint;
        }
    }

    internal static class PlacementHeightTransitionPatch
    {
        private static readonly string PatchId = "RoadRuntimeHost.HeightTransition." + typeof(PlacementHeightTransitionPatch).Module.ModuleVersionId.ToString("N");
        private static Harmony _harmony;
        private static MethodInfo _lanes, _node, _placement;
        private static bool _stopped = true;
        internal static MethodInfo PlacementMethod()
        {
            foreach (MethodInfo method in typeof(NetTool).GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == "CreateNode" && parameters.Length == 19 && parameters[0].ParameterType == typeof(NetInfo)
                    && parameters[7].Name == "testEnds") return method;
            }
            throw new MissingMethodException("NetTool.CreateNode operation overload was not found");
        }
        public static void Start() { _stopped = false; HarmonyHelper.DoOnHarmonyReady(Install); }
        public static void Stop()
        {
            _stopped = true;
            if (_harmony != null) {
                if (_lanes != null) _harmony.Unpatch(_lanes, HarmonyPatchType.All, PatchId);
                if (_node != null) _harmony.Unpatch(_node, HarmonyPatchType.All, PatchId);
                if (_placement != null) _harmony.Unpatch(_placement, HarmonyPatchType.All, PatchId);
            }
            PlacementHeightTransitionHook.Ready = false;
            _harmony = null; _lanes = null; _node = null; _placement = null;
        }
        private static void Install()
        {
            if (_stopped) return;
            try
            {
                _lanes = typeof(NetAI).GetMethod("UpdateLanes", new Type[] { typeof(ushort), typeof(NetSegment).MakeByRefType(), typeof(bool) });
                _node = typeof(NetNode).GetMethod("CalculateNode", new Type[] { typeof(ushort) });
                _placement = PlacementMethod();
                if (_lanes == null || _node == null || _placement == null) throw new MissingMethodException("CS1 height transition methods not found");
                _harmony = new Harmony(PatchId);
                _harmony.Patch(_lanes, null, new HarmonyMethod(typeof(PlacementHeightTransitionPatch).GetMethod("LanesPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                _harmony.Patch(_node, null, new HarmonyMethod(typeof(PlacementHeightTransitionPatch).GetMethod("NodePostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                _harmony.Patch(_placement,
                    new HarmonyMethod(typeof(PlacementHeightTransitionPatch).GetMethod("PlacementPrefix", BindingFlags.Static | BindingFlags.NonPublic)),
                    null, null,
                    new HarmonyMethod(typeof(PlacementHeightTransitionPatch).GetMethod("PlacementFinalizer", BindingFlags.Static | BindingFlags.NonPublic)));
                PlacementHeightTransitionHook.Ready = true;
                DiagnosticLog.Info("SUCCESS", "height_transition_hooks_installed", "Installed bounded height transition lane and node adapters");
            }
            catch (Exception error) { Stop(); DiagnosticLog.Error("MOD_COMPATIBILITY", "height_transition_install_failed", "Height transition adapters were not installed", error); }
        }
        private static void LanesPostfix(ushort segmentID, ref NetSegment data)
        {
            try { PlacementHeightTransitionHook.UpdateLanes(segmentID, ref data); }
            catch (Exception error) { DiagnosticLog.Error("MOD_CONTRACT", "height_transition_lanes_failed", "Could not apply compiled lane endpoints", error); }
        }
        private static void PlacementPrefix(NetInfo info, bool test, bool visualize, out PlacementHeightTransitionHook.Operation __state)
        {
            __state = PlacementHeightTransitionHook.BeginPlacement(info, test, visualize);
        }
        private static Exception PlacementFinalizer(Exception __exception, PlacementHeightTransitionHook.Operation __state)
        {
            PlacementHeightTransitionHook.EndPlacement(__state);
            return __exception;
        }
        private static void NodePostfix(ushort nodeID, ref NetNode __instance)
        {
            try { PlacementHeightTransitionHook.UpdateNode(nodeID, ref __instance); }
            catch (Exception error) { DiagnosticLog.Error("MOD_CONTRACT", "height_transition_node_failed", "Could not select the compiled endpoint node", error); }
        }
    }
}

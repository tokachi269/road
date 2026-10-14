using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class SharedHeightData : MonoBehaviour
    {
        public string Identity;
        public float[][] Starts, Ends;
        public int[] SegmentCodes;
        public bool[] NodeFlush;
        public bool Complete;
    }

    internal static class SharedHeightHook
    {
        internal sealed class Data
        {
            public string Identity;
            public float[][] Starts, Ends;
            public int[] SegmentCodes;
            public bool[] NodeFlush;
        }
        internal sealed class Operation
        {
            public NetInfo Info;
            public bool Consumed;
            public int Code;
        }
        private static readonly Dictionary<NetInfo, Data> Infos = new Dictionary<NetInfo, Data>();
        private static readonly HashSet<NetInfo> Unowned = new HashSet<NetInfo>();
        private static readonly object Sync = new object();
        public static AdaptiveRoadsBridge Bridge { get; private set; }
        public static bool Ready { get; internal set; }
        internal static bool ActivationPending { get; set; }
        private static bool _patchStarted;
        private static bool _running;
        private static Func<NetInfo, int> _selection;
        private static Action<NetInfo> _completed;
        [ThreadStatic] private static Operation _operation;
        [ThreadStatic] private static NetInfo _creating;
        [ThreadStatic] private static int _creatingCode;
        [ThreadStatic] private static bool _rebuilding;

        public static void Start(Func<NetInfo, int> selection, Action<NetInfo> completed)
        {
            _selection = selection; _completed = completed;
            _running = true;
            TryActivate();
        }
        private static void TryActivate()
        {
            if (!_running || Ready || ActivationPending) return;
            try {
                Bridge = AdaptiveRoadsBridge.FindLoaded();
                if (Bridge == null) return;
                _patchStarted = true; ActivationPending = true;
                InvokePatch("Start");
            } catch (Exception error) {
                ActivationPending = false;
                DiagnosticLog.Error("MOD_DEPENDENCY", "shared_height_unavailable", "AR height sharing is unavailable", error);
            }
        }
        public static void Stop()
        {
            _running = false;
            if (_patchStarted) InvokePatch("Stop");
            _patchStarted = false; ActivationPending = false; Bridge = null; Ready = false; _selection = null; _completed = null;
            lock (Sync) { Infos.Clear(); Unowned.Clear(); }
        }
        private static void InvokePatch(string method)
        {
            typeof(SharedHeightHook).Assembly.GetType("RoadRuntimeHost.Runtime.SharedHeightPatch", true)
                .GetMethod(method, BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
        }
        public static void RequireReady()
        {
            // AR installs its normal renderer patches after all level-loaded
            // callbacks. Retry only at a placement request, never on a tick.
            if (!Ready) TryActivate();
            if (!Ready || Bridge == null) throw new InvalidOperationException("Adaptive Roads shared-height hooks are not ready; no extra height NetInfo will be created");
        }
        public static Data Find(NetInfo info)
        {
            if (ReferenceEquals(info, null)) return null;
            lock (Sync) {
                Data known;
                if (Infos.TryGetValue(info, out known)) return known;
                if (Unowned.Contains(info)) return null;
            }
            foreach (Component component in info.gameObject.GetComponents(typeof(Component)))
            {
                if (component == null || component.GetType().FullName != typeof(SharedHeightData).FullName) continue;
                Type type = component.GetType();
                FieldInfo complete = type.GetField("Complete");
                if (complete == null || !(bool)complete.GetValue(component)) return null;
                Data data = new Data { Identity = (string)type.GetField("Identity").GetValue(component),
                    Starts = (float[][])type.GetField("Starts").GetValue(component), Ends = (float[][])type.GetField("Ends").GetValue(component),
                    SegmentCodes = (int[])type.GetField("SegmentCodes").GetValue(component), NodeFlush = (bool[])type.GetField("NodeFlush").GetValue(component) };
                lock (Sync) Infos[info] = data;
                return data;
            }
            lock (Sync) Unowned.Add(info);
            return null;
        }
        public static bool Owns(NetInfo info) { return Find(info) != null; }
        public static Operation Begin(NetInfo info, bool test, bool visualize)
        {
            if (test || visualize || _operation != null || !Owns(info) || _selection == null) return null;
            int code = _selection(info);
            if (code < 2) return null;
            _operation = new Operation { Info = info, Code = code };
            return _operation;
        }
        public static void End(Operation operation)
        {
            if (operation == null || !ReferenceEquals(operation, _operation)) return;
            _operation = null;
            if (operation.Consumed && _completed != null) _completed(operation.Info);
        }
        public static void PrepareCreation(NetInfo info, bool invert)
        {
            _creating = null;
            if (!Ready || !Owns(info)) return;
            int code = _selection == null ? -1 : _selection(info);
            if (_operation != null && ReferenceEquals(info, _operation.Info))
                code = _operation.Consumed ? (_operation.Code == 2 ? 1 : 0)
                    : (invert ? (_operation.Code == 2 ? 3 : 2) : _operation.Code);
            if (code < 0) return;
            _creating = info; _creatingCode = code;
        }
        public static int ProposedCode(NetInfo info)
        {
            return ReferenceEquals(info, _creating) ? _creatingCode : 0;
        }
        public static void Created(ushort id, NetInfo info, bool success)
        {
            if (!success || id == 0 || !ReferenceEquals(info, _creating)) { _creating = null; return; }
            int code = _creatingCode; _creating = null;
            int previous = Bridge.ReadCode(id);
            Bridge.WriteCode(id, code);
            try { Rebuild(id); }
            catch { Bridge.WriteCode(id, previous); throw; }
            if (_operation != null && ReferenceEquals(info, _operation.Info) && code >= 2) _operation.Consumed = true;
        }
        public static void Rebuild(ushort id)
        {
            if (!Ready || _rebuilding || id == 0) return;
            NetManager manager = NetManager.instance;
            ref NetSegment segment = ref manager.m_segments.m_buffer[id];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0 || !Owns(segment.Info)) return;
            _rebuilding = true;
            try {
                segment.Info.m_netAI.UpdateLanes(id, ref segment, false);
                manager.UpdateSegmentRenderer(id, true);
                if (segment.m_startNode != 0) manager.UpdateNodeRenderer(segment.m_startNode, true);
                if (segment.m_endNode != 0) manager.UpdateNodeRenderer(segment.m_endNode, true);
            } finally { _rebuilding = false; }
        }
        public static bool UpdateLanes(ushort id, ref NetSegment segment)
        {
            Data data = Find(segment.Info);
            if (data == null || Bridge == null) return false;
            int code = Bridge.ReadCode(id);
            bool turned = (segment.m_flags & NetSegment.Flags.Invert) != 0;
            segment.m_averageLength = HeightTransitionKernel.ApplyLaneHeights(NetManager.instance.m_lanes.m_buffer,
                id, segment.m_lanes, segment.Info.m_lanes, data.Starts[code], data.Ends[code], turned);
            Bridge.WriteEndpoint(id, true, SharedHeightFamily.EndpointFlush(code, true, turned));
            Bridge.WriteEndpoint(id, false, SharedHeightFamily.EndpointFlush(code, false, turned));
            return true;
        }
        public static bool TryLevel(NetInfo info, ushort id, bool start, bool turned, out float level)
        {
            Data data = Find(info); level = 0;
            if (data == null || Bridge == null) return false;
            int code = id == 0 ? ProposedCode(info) : Bridge.ReadCode(id);
            // Endpoint arrays, rather than a newly invented road-level preset,
            // own the physical vertical value.
            float[] offsets = start != turned ? data.Starts[code] : data.Ends[code];
            for (int i = 0; i < info.m_lanes.Length; ++i)
                if (info.m_lanes[i].m_laneType == NetInfo.LaneType.Vehicle) { level = offsets[i]; return true; }
            throw new InvalidDataException("Shared height road has no vehicle lane");
        }
        public static bool PreviewSegment(NetInfo info, NetInfo.Segment entry, out bool visible)
        {
            visible = false;
            Data data = Find(info);
            if (data == null || _selection == null) return false;
            int selected = _selection(info); if (selected < 0) return false;
            for (int i = 0; i < info.m_segments.Length; ++i)
                if (ReferenceEquals(entry, info.m_segments[i])) { visible = data.SegmentCodes[i] == selected; return true; }
            return false;
        }
        public static bool PreviewNode(NetInfo info, NetInfo.Node entry, out bool visible)
        {
            visible = false;
            Data data = Find(info);
            if (data == null || _selection == null) return false;
            int code = _selection(info); if (code < 0) return false;
            // A preview node belongs to the current ordinary endpoint. A
            // transition segment must not turn the node into another ramp.
            bool flush = code == 1 || code == 3;
            for (int i = 0; i < info.m_nodes.Length; ++i)
                if (ReferenceEquals(entry, info.m_nodes[i])) { visible = data.NodeFlush[i] == flush; return true; }
            return false;
        }
    }
}

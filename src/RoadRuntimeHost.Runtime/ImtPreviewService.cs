using System;
using System.Collections.Generic;
using ColossalFramework;
using IMT.API;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class ImtPreviewService
    {
        private sealed class TargetRoad
        {
            public string RoadId;
            public NetInfo Info;
            public ImtMarkingStyleBundle Style;
        }

        private sealed class SegmentStamp
        {
            public uint BuildIndex;
            public NetInfo Info;
            public ushort StartNode;
            public ushort EndNode;
            public ImtMarkingStyleBundle Style;
        }

        private sealed class PendingSegment
        {
            public ushort SegmentId;
            public TargetRoad Target;
        }

        private const string ProviderId = "RoadRuntimeHost";
        private IDataProviderV1 _provider;
        private bool _reportedUnavailable;
        private readonly Dictionary<NetInfo, TargetRoad> _targets = new Dictionary<NetInfo, TargetRoad>();
        private readonly Dictionary<ushort, SegmentStamp> _segments = new Dictionary<ushort, SegmentStamp>();
        private readonly HashSet<ulong> _initializedSegmentDefaults = new HashSet<ulong>();
        private readonly HashSet<string> _ownedNodeLinePairs = new HashSet<string>();
        private readonly HashSet<string> _ownedNodeDefaults = new HashSet<string>();
        private readonly HashSet<string> _initializedNodeDefaults = new HashSet<string>();
        private readonly HashSet<ulong> _nativeCrosswalksRestored = new HashSet<ulong>();
        private readonly ImtInternalAdapter _internalAdapter = new ImtInternalAdapter();
        private readonly TmpeTrafficPolicy _trafficPolicy;
        private readonly object _targetSync = new object();
        private readonly object _pendingSegmentSync = new object();
        private readonly object _pendingTrafficPolicySync = new object();
        private readonly Dictionary<ushort, TargetRoad> _pendingSegments =
            new Dictionary<ushort, TargetRoad>();
        private readonly HashSet<ushort> _pendingNodes = new HashSet<ushort>();
        private readonly HashSet<ushort> _pendingTrafficPolicyNodes = new HashSet<ushort>();
        private readonly HashSet<ushort> _pendingTrafficPolicySegments = new HashSet<ushort>();
        private readonly Dictionary<ushort, uint> _pendingTopologyRetryNodes =
            new Dictionary<ushort, uint>();
        private volatile HashSet<NetInfo> _targetInfos = new HashSet<NetInfo>();
        private volatile bool _segmentBatchPending;
        private volatile bool _trafficPolicyRefreshPending;
        private volatile bool _stopped;
        private readonly RoadPlacementMarkingController _placementMarkings;

        public ImtPreviewService()
        {
            _placementMarkings = new RoadPlacementMarkingController();
            _trafficPolicy = new TmpeTrafficPolicy(QueueTrafficPolicyRefresh);
            NetSegmentCreationHook.Start(OnSegmentCreated, OnSegmentReleased);
            ImtRestoreDefaultsHook.Start(CanRestoreDefaults, RestoreDefaults);
        }

        public void ApplyRoad(string roadId, NetInfo info, ImtMarkingStyleBundle style)
        {
            if (info == null) return;
            style = EffectiveStyle(style);
            RegisterTarget(roadId, info, style);
            RoadAI ai = info.m_netAI as RoadAI;
            if (ai != null)
            {
                RegisterTarget(roadId, ai.m_elevatedInfo, style);
                RegisterTarget(roadId, ai.m_bridgeInfo, style);
                RegisterTarget(roadId, ai.m_slopeInfo, style);
                RegisterTarget(roadId, ai.m_tunnelInfo, style);
            }
            ForgetTargetSegments(info);
            if (ai != null)
            {
                ForgetTargetSegments(ai.m_elevatedInfo);
                ForgetTargetSegments(ai.m_bridgeInfo);
                ForgetTargetSegments(ai.m_slopeInfo);
                ForgetTargetSegments(ai.m_tunnelInfo);
            }
        }

        private void ForgetTargetSegments(NetInfo info)
        {
            if (info == null) return;
            List<ushort> remove = new List<ushort>();
            foreach (KeyValuePair<ushort, SegmentStamp> item in _segments)
                if (ReferenceEquals(item.Value.Info, info)) remove.Add(item.Key);
            foreach (ushort segmentId in remove) _segments.Remove(segmentId);
        }

        public void Stop()
        {
            _stopped = true;
            NetSegmentCreationHook.Stop();
            ImtRestoreDefaultsHook.Stop();
            ImtCrosswalkWallHook.Stop();
            _trafficPolicy.Stop();
            _placementMarkings.Stop();
            lock (_targetSync)
            {
                _targets.Clear();
                _targetInfos = new HashSet<NetInfo>();
            }
            lock (_pendingSegmentSync)
            {
                _pendingSegments.Clear();
                _pendingNodes.Clear();
            }
            lock (_pendingTrafficPolicySync)
            {
                _pendingTrafficPolicyNodes.Clear();
                _pendingTrafficPolicySegments.Clear();
                _trafficPolicyRefreshPending = false;
            }
            lock (_pendingSegmentSync) _pendingTopologyRetryNodes.Clear();
            _initializedSegmentDefaults.Clear();
            _ownedNodeLinePairs.Clear();
            _ownedNodeDefaults.Clear();
            _initializedNodeDefaults.Clear();
            _nativeCrosswalksRestored.Clear();
        }

        private void RegisterTarget(string roadId, NetInfo info, ImtMarkingStyleBundle style)
        {
            if (info == null) return;
            ImtCrosswalkWallHook.RegisterTarget(info);
            lock (_targetSync)
            {
                TargetRoad target;
                if (!_targets.TryGetValue(info, out target))
                {
                    target = new TargetRoad();
                    target.Info = info;
                    _targets.Add(info, target);
                }
                target.RoadId = roadId ?? string.Empty;
                target.Style = style;
                _placementMarkings.Register(roadId, info, style);
                HashSet<NetInfo> targetInfos = new HashSet<NetInfo>(_targetInfos);
                targetInfos.Add(info);
                _targetInfos = targetInfos;
            }
        }

        private bool CanRestoreDefaults(bool isNode, ushort id)
        {
            if (_stopped || id == 0) return false;
            NetManager manager = NetManager.instance;
            if (manager == null) return false;

            if (!isNode)
            {
                if (id >= manager.m_segments.m_size) return false;
                ref NetSegment segment = ref manager.m_segments.m_buffer[id];
                return (segment.m_flags & NetSegment.Flags.Created) != 0
                    && segment.Info != null
                    && _targetInfos.Contains(segment.Info);
            }

            if (id >= manager.m_nodes.m_size)
                return false;
            ref NetNode node = ref manager.m_nodes.m_buffer[id];
            if ((node.m_flags & NetNode.Flags.Created) == 0) return false;
            for (int slot = 0; slot < 8; ++slot)
            {
                ushort segmentId = node.GetSegment(slot);
                if (segmentId == 0 || segmentId >= manager.m_segments.m_size)
                    continue;
                NetInfo info = manager.m_segments.m_buffer[segmentId].Info;
                if (info != null && _targetInfos.Contains(info)) return true;
            }
            return false;
        }

        private bool RestoreDefaults(bool isNode, ushort id)
        {
            if (!CanRestoreDefaults(isNode, id)) return false;
            IDataProviderV1 provider = GetProvider();
            if (provider == null) return false;

            try
            {
                if (isNode)
                    RestoreNodeDefaults(provider, id);
                else
                    RestoreSegmentDefaults(provider, id);

                DiagnosticLog.Info(
                    "SUCCESS",
                    "imt_road_defaults_restored",
                    "IMT markings were explicitly replaced with current generated road defaults",
                    "marking_type", isNode ? "Node" : "Segment",
                    "marking_id", id.ToString());
                return true;
            }
            catch (Exception error)
            {
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "imt_road_defaults_restore_failed",
                    "IMT markings could not be replaced with current generated road defaults",
                    error,
                    "marking_type", isNode ? "Node" : "Segment",
                    "marking_id", id.ToString());
                return false;
            }
        }

        private void RestoreSegmentDefaults(IDataProviderV1 provider, ushort segmentId)
        {
            ref NetSegment segment = ref NetManager.instance.m_segments.m_buffer[segmentId];
            TargetRoad target = FindTarget(segment.Info);
            if (target == null)
                throw new InvalidOperationException(
                    "Selected IMT segment no longer belongs to a target road");

            ISegmentMarkingData marking =
                provider.GetOrCreateSegmentMarking(segmentId);
            marking.ClearMarkings();
            ForgetInitializedSegmentDefaults(segmentId);

            int added = 0;
            int existing = 0;
            ApplySegmentLines(
                provider, segmentId, target.Style, ref added, ref existing);
            NetManager.instance.UpdateSegmentRenderer(segmentId, true);
        }

        private void RestoreNodeDefaults(IDataProviderV1 provider, ushort nodeId)
        {
            INodeMarkingData marking = provider.GetOrCreateNodeMarking(nodeId);
            marking.ClearMarkings();
            ForgetInitializedNodeDefaults(nodeId);
            _nativeCrosswalksRestored.Clear();

            ref NetNode node = ref NetManager.instance.m_nodes.m_buffer[nodeId];
            HashSet<NetInfo> appliedInfos = new HashSet<NetInfo>();
            int linesAdded = 0;
            int linesExisting = 0;
            int crosswalksAdded = 0;
            int crosswalksExisting = 0;
            int stopLinesAdded = 0;
            int stopLinesExisting = 0;
            for (int slot = 0; slot < 8; ++slot)
            {
                ushort segmentId = node.GetSegment(slot);
                if (segmentId == 0) continue;
                NetInfo info = NetManager.instance.m_segments.m_buffer[segmentId].Info;
                if (info == null || !appliedInfos.Add(info)) continue;
                TargetRoad target = FindTarget(info);
                if (target == null) continue;
                ApplyNodeMarkings(
                    provider, nodeId, target.Info, target.Style,
                    ref linesAdded, ref linesExisting,
                    ref crosswalksAdded, ref crosswalksExisting,
                    ref stopLinesAdded, ref stopLinesExisting);
            }
            NetManager.instance.UpdateNodeRenderer(nodeId, true);
        }

        private TargetRoad FindTarget(NetInfo info)
        {
            if (info == null) return null;
            lock (_targetSync)
            {
                TargetRoad target;
                return _targets.TryGetValue(info, out target) ? target : null;
            }
        }

        private void ForgetInitializedSegmentDefaults(ushort segmentId)
        {
            List<ulong> remove = new List<ulong>();
            foreach (ulong key in _initializedSegmentDefaults)
                if ((ushort)(key >> 48) == segmentId) remove.Add(key);
            foreach (ulong key in remove) _initializedSegmentDefaults.Remove(key);
        }

        private void ForgetInitializedNodeDefaults(ushort nodeId)
        {
            ForgetNodeDefaults(nodeId);
        }

        private void QueueTrafficPolicyRefresh(ushort instanceId, bool isSegment)
        {
            if (_stopped || instanceId == 0 || _targetInfos.Count == 0
                || !SimulationManager.exists) return;
            lock (_pendingTrafficPolicySync)
            {
                if (isSegment) _pendingTrafficPolicySegments.Add(instanceId);
                else _pendingTrafficPolicyNodes.Add(instanceId);
                if (_trafficPolicyRefreshPending) return;
                _trafficPolicyRefreshPending = true;
            }
            SimulationManager.instance.AddAction(ProcessPendingTrafficPolicyRefresh);
        }

        private void ProcessPendingTrafficPolicyRefresh()
        {
            ushort[] nodeIds;
            ushort[] segmentIds;
            lock (_pendingTrafficPolicySync)
            {
                nodeIds = new List<ushort>(_pendingTrafficPolicyNodes).ToArray();
                segmentIds = new List<ushort>(_pendingTrafficPolicySegments).ToArray();
                _pendingTrafficPolicyNodes.Clear();
                _pendingTrafficPolicySegments.Clear();
                _trafficPolicyRefreshPending = false;
            }
            if (_stopped || (nodeIds.Length == 0 && segmentIds.Length == 0)) return;
            RefreshTrafficPolicyNodes(nodeIds, segmentIds);
        }

        private void RefreshTrafficPolicyNodes(ushort[] notifiedNodeIds, ushort[] notifiedSegmentIds)
        {
            IDataProviderV1 provider = GetProvider();
            if (provider == null) return;
            TargetRoad[] targets = SnapshotTargets();
            Dictionary<NetInfo, TargetRoad> targetByInfo = new Dictionary<NetInfo, TargetRoad>();
            foreach (TargetRoad target in targets)
                if (target.Info != null) targetByInfo[target.Info] = target;
            NetManager manager = NetManager.instance;
            HashSet<ushort> affectedNodeIds = new HashSet<ushort>(notifiedNodeIds);
            foreach (ushort segmentId in notifiedSegmentIds)
            {
                if (segmentId == 0 || segmentId >= manager.m_segments.m_size) continue;
                ref NetSegment segment = ref manager.m_segments.m_buffer[segmentId];
                if ((segment.m_flags & NetSegment.Flags.Created) == 0) continue;
                affectedNodeIds.Add(segment.m_startNode);
                affectedNodeIds.Add(segment.m_endNode);
            }

            Dictionary<ushort, List<TargetRoad>> nodes = new Dictionary<ushort, List<TargetRoad>>();
            foreach (ushort nodeId in affectedNodeIds)
            {
                if (nodeId == 0 || nodeId >= manager.m_nodes.m_size) continue;
                ref NetNode node = ref manager.m_nodes.m_buffer[nodeId];
                if ((node.m_flags & NetNode.Flags.Created) == 0) continue;
                HashSet<NetInfo> addedInfos = new HashSet<NetInfo>();
                for (int slot = 0; slot < 8; ++slot)
                {
                    ushort connectedSegmentId = node.GetSegment(slot);
                    if (connectedSegmentId == 0
                        || connectedSegmentId >= manager.m_segments.m_size) continue;
                    ref NetSegment connectedSegment = ref manager.m_segments.m_buffer[connectedSegmentId];
                    TargetRoad target;
                    if ((connectedSegment.m_flags & NetSegment.Flags.Created) == 0
                        || connectedSegment.Info == null
                        || !addedInfos.Add(connectedSegment.Info)
                        || !targetByInfo.TryGetValue(connectedSegment.Info, out target)) continue;
                    AddNodeTarget(nodes, nodeId, target);
                }
            }
            if (nodes.Count == 0) return;

            int linesAdded = 0;
            int linesExisting = 0;
            int crosswalksAdded = 0;
            int crosswalksExisting = 0;
            int stopLinesAdded = 0;
            int stopLinesExisting = 0;
            int failures = 0;
            Exception firstFailure = null;
            foreach (KeyValuePair<ushort, List<TargetRoad>> item in nodes)
            {
                foreach (TargetRoad target in item.Value)
                {
                    try
                    {
                        ApplyNodeMarkings(
                            provider, item.Key, target.Info, target.Style,
                            ref linesAdded, ref linesExisting,
                            ref crosswalksAdded, ref crosswalksExisting,
                            ref stopLinesAdded, ref stopLinesExisting);
                        manager.UpdateNodeRenderer(item.Key, true);
                    }
                    catch (Exception error)
                    {
                        ++failures;
                        if (firstFailure == null) firstFailure = error;
                    }
                }
            }

            if (failures != 0)
            {
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "tmpe_policy_refresh_partial_failure",
                    "Some IMT defaults could not be refreshed after a TM:PE policy change",
                    firstFailure,
                    "notified_node_count", notifiedNodeIds.Length.ToString(),
                    "notified_segment_count", notifiedSegmentIds.Length.ToString(),
                    "node_count", nodes.Count.ToString(),
                    "failure_count", failures.ToString());
            }
            DiagnosticLog.Info(
                failures == 0 ? "SUCCESS" : "MOD_COMPATIBILITY",
                "tmpe_policy_refresh_applied",
                "IMT defaults were refreshed only around a TM:PE policy change",
                "notified_node_count", notifiedNodeIds.Length.ToString(),
                "notified_segment_count", notifiedSegmentIds.Length.ToString(),
                "node_count", nodes.Count.ToString(),
                "line_added_count", linesAdded.ToString(),
                "crosswalk_added_count", crosswalksAdded.ToString(),
                "stop_line_added_count", stopLinesAdded.ToString(),
                "failure_count", failures.ToString());
        }

        private TargetRoad[] SnapshotTargets()
        {
            lock (_targetSync) return new List<TargetRoad>(_targets.Values).ToArray();
        }

        public void Tick()
        {
            if (_stopped || !SimulationManager.exists) return;
            ushort[] ready;
            uint currentTick = SimulationManager.instance.m_currentTickIndex;
            lock (_pendingSegmentSync)
            {
                List<ushort> nodes = new List<ushort>();
                foreach (KeyValuePair<ushort, uint> item in _pendingTopologyRetryNodes)
                    if (item.Value != currentTick) nodes.Add(item.Key);
                foreach (ushort nodeId in nodes)
                    _pendingTopologyRetryNodes.Remove(nodeId);
                ready = nodes.ToArray();
            }
            if (ready.Length == 0) return;
            SimulationManager.instance.AddAction(delegate
            {
                if (!_stopped)
                    ApplySegmentBatch(new PendingSegment[0], ready, false);
            });
        }

        private void OnSegmentCreated(ushort segmentId, NetInfo info)
        {
            if (_stopped || segmentId == 0 || !SimulationManager.exists) return;
            TargetRoad captured = null;
            if (info != null && _targetInfos.Contains(info))
            {
                TargetRoad registered;
                lock (_targetSync) _targets.TryGetValue(info, out registered);
                if (registered != null)
                {
                    captured = new TargetRoad
                    {
                        RoadId = registered.RoadId,
                        Info = registered.Info,
                        Style = _placementMarkings.Capture(info, registered.Style)
                    };
                }
            }
            lock (_pendingSegmentSync)
            {
                if (captured != null) _pendingSegments[segmentId] = captured;
                AddCurrentSegmentNodes(
                    segmentId, _pendingNodes, captured != null);
                if (captured == null && _pendingNodes.Count == 0) return;
                if (_segmentBatchPending) return;
                _segmentBatchPending = true;
            }

            SimulationManager.instance.AddAction(ProcessPendingSegments);
        }

        private void OnSegmentReleased(
            ushort segmentId,
            ushort startNode,
            ushort endNode,
            NetInfo info)
        {
            if (_stopped || segmentId == 0 || !SimulationManager.exists) return;
            bool releasedTarget = info != null && _targetInfos.Contains(info);
            lock (_pendingSegmentSync)
            {
                _pendingSegments.Remove(segmentId);
                if (startNode != 0
                    && (releasedTarget || NodeIdHasTarget(startNode)))
                    _pendingNodes.Add(startNode);
                if (endNode != 0
                    && (releasedTarget || NodeIdHasTarget(endNode)))
                    _pendingNodes.Add(endNode);
                _segments.Remove(segmentId);
                ForgetInitializedSegmentDefaults(segmentId);
                if (_pendingNodes.Count == 0) return;
                if (_segmentBatchPending) return;
                _segmentBatchPending = true;
            }
            SimulationManager.instance.AddAction(ProcessPendingSegments);
        }

        private void AddCurrentSegmentNodes(
            ushort segmentId,
            HashSet<ushort> nodes,
            bool targetSegment)
        {
            NetManager manager = NetManager.instance;
            if (manager == null || segmentId == 0
                || segmentId >= manager.m_segments.m_size) return;
            ref NetSegment segment = ref manager.m_segments.m_buffer[segmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0) return;
            if (segment.m_startNode != 0
                && (targetSegment || NodeIdHasTarget(segment.m_startNode)))
                nodes.Add(segment.m_startNode);
            if (segment.m_endNode != 0
                && (targetSegment || NodeIdHasTarget(segment.m_endNode)))
                nodes.Add(segment.m_endNode);
        }

        private void ProcessPendingSegments()
        {
            PendingSegment[] segments;
            ushort[] nodes;
            lock (_pendingSegmentSync)
            {
                List<PendingSegment> captured = new List<PendingSegment>();
                foreach (KeyValuePair<ushort, TargetRoad> item in _pendingSegments)
                    captured.Add(new PendingSegment
                    {
                        SegmentId = item.Key,
                        Target = item.Value
                    });
                segments = captured.ToArray();
                nodes = new List<ushort>(_pendingNodes).ToArray();
                _pendingSegments.Clear();
                _pendingNodes.Clear();
                _segmentBatchPending = false;
            }
            if (_stopped || (segments.Length == 0 && nodes.Length == 0)) return;
            EnableDefaultTrafficLights(nodes);
            ApplySegmentBatch(segments, nodes, true);
        }

        private void EnableDefaultTrafficLights(ushort[] nodeIds)
        {
            NetManager manager = NetManager.instance;
            foreach (ushort nodeId in nodeIds)
            {
                if (nodeId == 0 || nodeId >= manager.m_nodes.m_size) continue;
                ref NetNode node = ref manager.m_nodes.m_buffer[nodeId];
                if (CountSegments(node) >= 3 && NodeHasTarget(node))
                {
                    EnsureDefaultTrafficLight(nodeId, ref node);
                    EnsureDefaultPedestrianCrossings(nodeId, ref node);
                }
            }
        }

        private bool NodeHasTarget(NetNode node)
        {
            NetManager manager = NetManager.instance;
            for (int slot = 0; slot < 8; ++slot)
            {
                ushort segmentId = node.GetSegment(slot);
                if (segmentId == 0 || segmentId >= manager.m_segments.m_size) continue;
                NetInfo info = manager.m_segments.m_buffer[segmentId].Info;
                if (info != null && _targetInfos.Contains(info)) return true;
            }
            return false;
        }

        private bool NodeIdHasTarget(ushort nodeId)
        {
            NetManager manager = NetManager.instance;
            if (manager == null || nodeId == 0
                || nodeId >= manager.m_nodes.m_size) return false;
            ref NetNode node = ref manager.m_nodes.m_buffer[nodeId];
            return NodeHasTarget(node);
        }

        private void EnsureDefaultPedestrianCrossings(
            ushort nodeId,
            ref NetNode node)
        {
            NetManager manager = NetManager.instance;
            for (int slot = 0; slot < 8; ++slot)
            {
                ushort segmentId = node.GetSegment(slot);
                if (segmentId == 0 || segmentId >= manager.m_segments.m_size) continue;
                ref NetSegment segment = ref manager.m_segments.m_buffer[segmentId];
                if (segment.Info == null || !_targetInfos.Contains(segment.Info)
                    || !HasPedestrianLane(segment.Info)) continue;
                bool startNode = segment.m_startNode == nodeId;
                if (IsPedestrianCrossingAllowed(
                        segmentId, nodeId, ref segment)) continue;

                bool? tmpeEnabled = null;
                try
                {
                    tmpeEnabled = _trafficPolicy.TryEnableDefaultPedestrianCrossing(
                        segmentId, startNode);
                }
                catch (Exception error)
                {
                    DiagnosticLog.Warn(
                        "MOD_COMPATIBILITY",
                        "tmpe_default_pedestrian_crossing_failed",
                        "TM:PE could not enable the default pedestrian crossing",
                        "node_id", nodeId.ToString(),
                        "segment_id", segmentId.ToString(),
                        "reason", error.GetBaseException().Message);
                    continue;
                }
                if (tmpeEnabled.HasValue && !tmpeEnabled.Value) continue;
                if (!tmpeEnabled.HasValue)
                {
                    NetSegment.Flags flag = startNode
                        ? NetSegment.Flags.CrossingStart
                        : NetSegment.Flags.CrossingEnd;
                    segment.m_flags |= flag;
                    manager.UpdateSegmentRenderer(segmentId, true);
                }
                DiagnosticLog.Info(
                    "SUCCESS",
                    "intersection_default_pedestrian_crossing_enabled",
                    "Enabled a default pedestrian crossing for a newly affected intersection entrance",
                    "node_id", nodeId.ToString(),
                    "segment_id", segmentId.ToString(),
                    "provider", tmpeEnabled.HasValue ? "TMPE" : "vanilla");
            }
        }

        private void ApplySegmentBatch(
            PendingSegment[] pendingSegments,
            ushort[] affectedNodeIds,
            bool allowTopologyRetry)
        {
            IDataProviderV1 provider = GetProvider();
            if (provider == null)
            {
                lock (_pendingSegmentSync)
                {
                    foreach (PendingSegment pending in pendingSegments)
                        _pendingSegments[pending.SegmentId] = pending.Target;
                    foreach (ushort nodeId in affectedNodeIds)
                        if (nodeId != 0) _pendingNodes.Add(nodeId);
                }
                return;
            }

            int changedSegments = 0;
            int linesAdded = 0;
            int linesExisting = 0;
            int crosswalksAdded = 0;
            int crosswalksExisting = 0;
            int stopLinesAdded = 0;
            int stopLinesExisting = 0;
            int failures = 0;
            Exception firstFailure = null;
            HashSet<ushort> nodes = new HashSet<ushort>(affectedNodeIds);
            HashSet<ushort> topologyRetryNodes = new HashSet<ushort>();
            NetManager manager = NetManager.instance;

            foreach (PendingSegment pending in pendingSegments)
            {
                ushort segmentId = pending.SegmentId;
                if (segmentId == 0 || segmentId >= manager.m_segments.m_size) continue;
                ref NetSegment segment = ref manager.m_segments.m_buffer[segmentId];
                TargetRoad target = pending.Target;
                if ((segment.m_flags & NetSegment.Flags.Created) == 0
                    || segment.Info == null
                    || target == null
                    || !ReferenceEquals(segment.Info, target.Info)) continue;

                SegmentStamp stamp;
                if (_segments.TryGetValue(segmentId, out stamp)
                    && stamp.BuildIndex == segment.m_buildIndex
                    && ReferenceEquals(stamp.Info, segment.Info)
                    && stamp.StartNode == segment.m_startNode
                    && stamp.EndNode == segment.m_endNode) continue;

                ++changedSegments;
                _segments[segmentId] = new SegmentStamp
                {
                    BuildIndex = segment.m_buildIndex,
                    Info = segment.Info,
                    StartNode = segment.m_startNode,
                    EndNode = segment.m_endNode,
                    Style = target.Style
                };
                nodes.Add(segment.m_startNode);
                nodes.Add(segment.m_endNode);
                try
                {
                    ApplySegmentLines(provider, segmentId, target.Style, ref linesAdded, ref linesExisting);
                    manager.UpdateSegmentRenderer(segmentId, true);
                }
                catch (Exception error)
                {
                    ++failures;
                    if (firstFailure == null) firstFailure = error;
                }
            }

            foreach (ushort nodeId in nodes)
            {
                if (nodeId == 0 || nodeId >= manager.m_nodes.m_size) continue;
                ref NetNode node = ref manager.m_nodes.m_buffer[nodeId];
                if (CountSegments(node) < 2)
                {
                    ForgetNodeDefaults(nodeId);
                    continue;
                }
                int connectedSegments = CountSegments(node);
                INodeMarkingData currentMarking =
                    provider.GetOrCreateNodeMarking(nodeId);
                int markingEntrances = CountEntrances(currentMarking);
                if (markingEntrances != connectedSegments)
                {
                    if (allowTopologyRetry)
                        topologyRetryNodes.Add(nodeId);
                    else
                        DiagnosticLog.Warn(
                            "MOD_COMPATIBILITY",
                            "imt_node_topology_not_ready",
                            "IMT node entrances were still stale after one targeted retry; markings were not partially generated",
                            "node_id", nodeId.ToString(),
                            "connected_segment_count", connectedSegments.ToString(),
                            "imt_entrance_count", markingEntrances.ToString());
                    continue;
                }
                HashSet<NetInfo> appliedInfos = new HashSet<NetInfo>();
                bool appliedTarget = false;
                for (int slot = 0; slot < 8; ++slot)
                {
                    ushort connectedSegmentId = node.GetSegment(slot);
                    if (connectedSegmentId == 0) continue;
                    NetInfo connectedInfo = manager.m_segments.m_buffer[connectedSegmentId].Info;
                    if (connectedInfo == null
                        || !appliedInfos.Add(connectedInfo)) continue;
                    TargetRoad target = FindTarget(connectedInfo);
                    if (target == null) continue;
                    appliedTarget = true;
                    try
                    {
                        ApplyNodeMarkings(
                            provider, nodeId, target.Info, target.Style,
                            ref linesAdded, ref linesExisting,
                            ref crosswalksAdded, ref crosswalksExisting,
                            ref stopLinesAdded, ref stopLinesExisting);
                        manager.UpdateNodeRenderer(nodeId, true);
                    }
                    catch (Exception error)
                    {
                        ++failures;
                        if (firstFailure == null) firstFailure = error;
                    }
                }
                if (appliedTarget)
                    VerifyCrosswalkPostcondition(
                        currentMarking, nodeId, ref node);
            }

            if (changedSegments == 0 && nodes.Count == 0) return;
            if (failures != 0)
            {
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "imt_preview_segment_batch_partial_failure",
                    "IMT preview markings were applied only partially for a changed-node batch",
                    firstFailure,
                    "notified_segment_count", pendingSegments.Length.ToString(),
                    "changed_segment_count", changedSegments.ToString(),
                    "affected_node_count", nodes.Count.ToString(),
                    "failure_count", failures.ToString());
            }
            DiagnosticLog.Info(
                failures == 0 ? "SUCCESS" : "MOD_COMPATIBILITY",
                "imt_preview_segment_batch_applied",
                "IMT preview markings were evaluated only around event-driven segment changes",
                "notified_segment_count", pendingSegments.Length.ToString(),
                "changed_segment_count", changedSegments.ToString(),
                "affected_node_count", nodes.Count.ToString(),
                "line_added_count", linesAdded.ToString(),
                "line_existing_count", linesExisting.ToString(),
                "crosswalk_added_count", crosswalksAdded.ToString(),
                "crosswalk_existing_count", crosswalksExisting.ToString(),
                "stop_line_added_count", stopLinesAdded.ToString(),
                "stop_line_existing_count", stopLinesExisting.ToString(),
                "failure_count", failures.ToString(),
                "preview_only", bool.TrueString);

            if (topologyRetryNodes.Count != 0 && SimulationManager.exists)
            {
                uint queuedTick = SimulationManager.instance.m_currentTickIndex;
                lock (_pendingSegmentSync)
                {
                    foreach (ushort nodeId in topologyRetryNodes)
                        _pendingTopologyRetryNodes[nodeId] = queuedTick;
                }
            }
        }

        private void EnsureDefaultTrafficLight(ushort nodeId, ref NetNode node)
        {
            if ((node.m_flags & NetNode.Flags.TrafficLights) != 0) return;
            bool enabled = false;
            try { enabled = _trafficPolicy.TryEnableTrafficLight(nodeId); }
            catch (Exception error)
            {
                DiagnosticLog.Warn(
                    "MOD_COMPATIBILITY",
                    "tmpe_default_traffic_light_failed",
                    "TM:PE could not enable the default traffic light; the vanilla node flag will be used",
                    "node_id", nodeId.ToString(),
                    "reason", error.GetBaseException().Message);
            }
            if (!enabled)
            {
                node.m_flags |= NetNode.Flags.TrafficLights;
                NetManager.instance.UpdateNodeRenderer(nodeId, true);
            }
            DiagnosticLog.Info(
                "SUCCESS",
                "intersection_default_traffic_light_enabled",
                "Enabled the default traffic light for a newly affected intersection",
                "node_id", nodeId.ToString(),
                "provider", enabled ? "TMPE" : "vanilla");
        }

        private static void AddNodeTarget(Dictionary<ushort, List<TargetRoad>> nodes, ushort nodeId, TargetRoad target)
        {
            List<TargetRoad> targets;
            if (!nodes.TryGetValue(nodeId, out targets))
            {
                targets = new List<TargetRoad>();
                nodes.Add(nodeId, targets);
            }
            foreach (TargetRoad current in targets)
                if (ReferenceEquals(current.Info, target.Info)) return;
            targets.Add(target);
        }

        private IDataProviderV1 GetProvider()
        {
            if (_provider != null) return _provider;
            _provider = Helper.GetProvider(ProviderId);
            if (_provider == null)
            {
                if (!_reportedUnavailable)
                {
                    _reportedUnavailable = true;
                    DiagnosticLog.Warn(
                        "MOD_COMPATIBILITY",
                        "imt_api_unavailable",
                        "Intersection Marking Tool is disabled, unavailable, or not ready; preview markings were not applied",
                        "provider_id", ProviderId);
                }
                return null;
            }

            _reportedUnavailable = false;
            DiagnosticLog.Info(
                "SUCCESS",
                "imt_api_ready",
                "Intersection Marking Tool API provider is ready",
                "provider_id", ProviderId,
                "imt_version", _provider.ModVersion == null ? string.Empty : _provider.ModVersion.ToString(),
                "imt_beta", _provider.IsBeta.ToString());
            return _provider;
        }

        private void ApplySegmentLines(IDataProviderV1 provider, ushort segmentId, ImtMarkingStyleBundle appearance, ref int added, ref int existing)
        {
            ISegmentMarkingData marking = provider.GetOrCreateSegmentMarking(segmentId);
            List<IEntrancePointData> starts = GetPoints(marking.StartEntrance.EntrancePoints);
            List<IEntrancePointData> ends = GetPoints(marking.EndEntrance.EntrancePoints);
            if (starts.Count < 2 || ends.Count < 2) return;

            HashSet<byte> usedEndIndexes = new HashSet<byte>();
            for (int index = 0; index < starts.Count; ++index)
            {
                IEntrancePointData start = starts[index];
                IEntrancePointData end = FindClosestUnused(ends, start.Position, usedEndIndexes);
                if (end == null) continue;
                usedEndIndexes.Add(end.Index);

                // IMT 1.15's SegmentMarkingDataProvider.TryGetRegularLine has a
                // contradictory same-entrance guard. RegularLineExist and
                // RemoveRegularLine accept the valid cross-entrance pair.
                ulong pairKey = SegmentLinePairKey(segmentId, start, end);
                if (marking.RegularLineExist(start, end))
                {
                    ++existing;
                    _initializedSegmentDefaults.Add(pairKey);
                    continue;
                }
                if (_initializedSegmentDefaults.Contains(pairKey)) continue;

                bool edge = index == 0 || index == starts.Count - 1;
                if (edge && !appearance.RoadsideLines) continue;
                bool opposing = !edge && IsOpposingBoundary(segmentId, start.Source);
                IRegularLineStyleData style = edge
                    ? CreateSolidStyle(provider, appearance)
                    : CreateConfiguredLineStyle(
                        provider,
                        appearance,
                        opposing
                            ? appearance.CenterLineStyle
                            : appearance.LaneSeparatorStyle);
                marking.AddRegularLine(start, end, style);
                _initializedSegmentDefaults.Add(pairKey);
                ++added;
            }
        }

        private static ulong SegmentLinePairKey(
            ushort segmentId,
            IEntrancePointData start,
            IEntrancePointData end)
        {
            return ((ulong)segmentId << 48)
                | ((ulong)start.EntranceId << 32)
                | ((ulong)start.Index << 24)
                | ((ulong)end.EntranceId << 8)
                | end.Index;
        }

        private static string NodeDefaultKey(
            string kind,
            ushort nodeId,
            IPointData start,
            IPointData end)
        {
            string first = start.EntranceId + ":" + start.Index;
            string second = end.EntranceId + ":" + end.Index;
            if (string.CompareOrdinal(first, second) > 0)
            {
                string swap = first;
                first = second;
                second = swap;
            }
            return kind + ":" + nodeId + ":" + first + ":" + second;
        }

        private void ForgetNodeDefaults(ushort nodeId)
        {
            string token = ":" + nodeId + ":";
            RemoveKeysContaining(_initializedNodeDefaults, token);
            RemoveKeysContaining(_ownedNodeDefaults, token);
            RemoveKeysContaining(_ownedNodeLinePairs, token);
        }

        private static void RemoveKeysContaining(
            HashSet<string> values,
            string token)
        {
            List<string> remove = new List<string>();
            foreach (string value in values)
                if (value.IndexOf(token, StringComparison.Ordinal) >= 0)
                    remove.Add(value);
            foreach (string value in remove) values.Remove(value);
        }

        private void ApplyNodeMarkings(
            IDataProviderV1 provider,
            ushort nodeId,
            NetInfo targetInfo,
            ImtMarkingStyleBundle appearance,
            ref int linesAdded,
            ref int linesExisting,
            ref int crosswalksAdded,
            ref int crosswalksExisting,
            ref int stopLinesAdded,
            ref int stopLinesExisting)
        {
            INodeMarkingData marking = provider.GetOrCreateNodeMarking(nodeId);
            ref NetNode node = ref NetManager.instance.m_nodes.m_buffer[nodeId];
            int segmentCount = CountSegments(node);
            if (ImtNodePolicy.ShouldConnectRoadLines(segmentCount))
            {
                ApplyCornerLines(
                    provider, marking, appearance,
                    ref linesAdded, ref linesExisting);
                RemoveOwnedCrosswalksAndStopLines(marking);
                return;
            }
            RemoveOwnedCornerLines(marking);

            for (int slot = 0; slot < 8; ++slot)
            {
                ushort segmentId = node.GetSegment(slot);
                if (segmentId == 0) continue;
                ref NetSegment segment = ref NetManager.instance.m_segments.m_buffer[segmentId];
                if (segment.Info != targetInfo) continue;

                ISegmentEntranceData entrance;
                if (!marking.TryGetEntrance(segmentId, out entrance) || entrance.PointCount < 2) continue;

                ICrosswalkPointData crosswalkStart;
                ICrosswalkPointData crosswalkEnd;
                if (entrance.GetCrosswalkPoint(1, out crosswalkStart)
                    && entrance.GetCrosswalkPoint((byte)entrance.PointCount, out crosswalkEnd))
                {
                    string crosswalkKey = NodeDefaultKey(
                        "crosswalk", nodeId, crosswalkStart, crosswalkEnd);
                    bool existed = marking.CrosswalkExist(crosswalkStart, crosswalkEnd);
                    bool shouldHaveCrosswalk = ImtNodePolicy.ShouldCreateCrosswalk(
                        segmentCount,
                        HasPedestrianLane(segment.Info),
                        IsPedestrianCrossingAllowed(segmentId, nodeId, ref segment));
                    if (!shouldHaveCrosswalk)
                    {
                        if (_ownedNodeDefaults.Remove(crosswalkKey) && existed)
                            marking.RemoveCrosswalk(crosswalkStart, crosswalkEnd);
                        _initializedNodeDefaults.Remove(crosswalkKey);
                    }
                    else if (existed || !_initializedNodeDefaults.Contains(crosswalkKey))
                    {
                        ICrosswalkData currentCrosswalk = existed
                            ? _internalAdapter.GetCrosswalk(marking, crosswalkStart, crosswalkEnd)
                            : null;
                        ICrosswalkData effectiveCrosswalk = currentCrosswalk;
                        if (existed)
                        {
                            ++crosswalksExisting;
                        }
                        else
                        {
                            ICrosswalkData createdCrosswalk = marking.AddCrosswalk(crosswalkStart, crosswalkEnd, CreateCrosswalkStyle(provider, appearance));
                            effectiveCrosswalk = createdCrosswalk;
                            _ownedNodeDefaults.Add(crosswalkKey);
                            ++crosswalksAdded;
                        }
                        _initializedNodeDefaults.Add(crosswalkKey);

                        if (effectiveCrosswalk != null)
                        {
                            ulong crosswalkId = _internalAdapter.GetCrosswalkLineId(effectiveCrosswalk);
                            if (!_nativeCrosswalksRestored.Contains(crosswalkId))
                            {
                                _internalAdapter.RestoreNativeCrosswalk(marking, effectiveCrosswalk);
                                _nativeCrosswalksRestored.Add(crosswalkId);
                            }
                        }
                    }
                }

                IEntrancePointData stopStart;
                IEntrancePointData stopEnd;
                if (TryGetIncomingStopLinePoints(entrance, nodeId, ref segment, out stopStart, out stopEnd))
                {
                    string stopLineKey = NodeDefaultKey(
                        "stop", nodeId, stopStart, stopEnd);
                    bool shouldHaveStopLine = ImtNodePolicy.ShouldCreateStopLine(
                        segmentCount,
                        true,
                        HasTrafficLight(nodeId, ref node),
                        HasStopSign(segmentId, nodeId, ref segment),
                        MustWaitForClearJunction(segmentId, nodeId, ref segment));
                    IStopLineData current;
                    bool existed = marking.TryGetStopLine(stopStart, stopEnd, out current);
                    if (!shouldHaveStopLine)
                    {
                        if (_ownedNodeDefaults.Remove(stopLineKey) && existed)
                            marking.RemoveStopLine(stopStart, stopEnd);
                        _initializedNodeDefaults.Remove(stopLineKey);
                        continue;
                    }

                    RemoveLegacyFullWidthStopLine(marking, entrance, stopStart, stopEnd);
                    if (existed)
                    {
                        ++stopLinesExisting;
                        _initializedNodeDefaults.Add(stopLineKey);
                        continue;
                    }
                    if (_initializedNodeDefaults.Contains(stopLineKey)) continue;
                    marking.AddStopLine(stopStart, stopEnd, CreateStopLineStyle(provider, appearance));
                    _ownedNodeDefaults.Add(stopLineKey);
                    _initializedNodeDefaults.Add(stopLineKey);
                    ++stopLinesAdded;
                }
            }
        }

        private void ApplyCornerLines(
            IDataProviderV1 provider,
            INodeMarkingData marking,
            ImtMarkingStyleBundle appearance,
            ref int added,
            ref int existing)
        {
            List<ISegmentEntranceData> entrances = new List<ISegmentEntranceData>();
            foreach (ISegmentEntranceData entrance in marking.Entrances)
                entrances.Add(entrance);
            if (entrances.Count != 2) return;

            List<IEntrancePointData> first = GetPointsByIndex(entrances[0].EntrancePoints);
            List<IEntrancePointData> second = GetPointsByIndex(entrances[1].EntrancePoints);
            ImtBoundaryRole[] firstRoles = BoundaryRoles(
                entrances[0].Id, marking.Id, first);
            ImtBoundaryRole[] secondRoles = BoundaryRoles(
                entrances[1].Id, marking.Id, second);
            int[] pairs = ImtNodePolicy.MatchBoundaryRoles(
                firstRoles, secondRoles);
            ImtMarkingStyleBundle firstAppearance = StyleForSegment(
                entrances[0].Id, appearance);
            ImtMarkingStyleBundle secondAppearance = StyleForSegment(
                entrances[1].Id, appearance);
            float connectorAngle = ConnectorAngle(
                marking.Id, entrances[0].Id, entrances[1].Id);
            for (int pairIndex = 0; pairIndex + 1 < pairs.Length; pairIndex += 2)
            {
                int firstOrdinal = pairs[pairIndex];
                int secondOrdinal = pairs[pairIndex + 1];
                IEntrancePointData start = first[firstOrdinal];
                IEntrancePointData end = second[secondOrdinal];
                ImtBoundaryRole role = firstRoles[firstOrdinal];
                string defaultKey = NodeDefaultKey("line", marking.Id, start, end);
                if (marking.RegularLineExist(start, end))
                {
                    ++existing;
                    _initializedNodeDefaults.Add(defaultKey);
                    continue;
                }
                if (_initializedNodeDefaults.Contains(defaultKey)) continue;

                if (role == ImtBoundaryRole.Roadside
                    && (!firstAppearance.RoadsideLines
                    || !secondAppearance.RoadsideLines)) continue;
                string connectorStyle = ImtNodePolicy.ConnectorStyle(
                    role,
                    role == ImtBoundaryRole.Center
                        ? firstAppearance.CenterLineStyle
                        : firstAppearance.LaneSeparatorStyle,
                    role == ImtBoundaryRole.Center
                        ? secondAppearance.CenterLineStyle
                        : secondAppearance.LaneSeparatorStyle,
                    connectorAngle);
                IRegularLineStyleData style = CreateConfiguredLineStyle(
                    provider, firstAppearance, connectorStyle);
                marking.AddRegularLine(start, end, style);
                _ownedNodeLinePairs.Add(defaultKey);
                _initializedNodeDefaults.Add(defaultKey);
                ++added;
            }
        }

        private void RemoveOwnedCornerLines(INodeMarkingData marking)
        {
            List<ISegmentEntranceData> entrances = new List<ISegmentEntranceData>();
            foreach (ISegmentEntranceData entrance in marking.Entrances)
                entrances.Add(entrance);
            for (int leftIndex = 0; leftIndex < entrances.Count; ++leftIndex)
            {
                List<IEntrancePointData> left = GetPointsByIndex(entrances[leftIndex].EntrancePoints);
                for (int rightIndex = leftIndex + 1; rightIndex < entrances.Count; ++rightIndex)
                {
                    List<IEntrancePointData> right = GetPointsByIndex(entrances[rightIndex].EntrancePoints);
                    foreach (IEntrancePointData start in left)
                    {
                        foreach (IEntrancePointData end in right)
                        {
                            string defaultKey = NodeDefaultKey(
                                "line", marking.Id, start, end);
                            if (!_ownedNodeLinePairs.Remove(defaultKey)) continue;
                            if (marking.RegularLineExist(start, end))
                                marking.RemoveRegularLine(start, end);
                            _initializedNodeDefaults.Remove(defaultKey);
                        }
                    }
                }
            }
        }

        private static ImtBoundaryRole[] BoundaryRoles(
            ushort segmentId,
            ushort nodeId,
            List<IEntrancePointData> points)
        {
            ImtBoundaryRole[] roles = new ImtBoundaryRole[points.Count];
            for (int ordinal = 0; ordinal < points.Count; ++ordinal)
            {
                if (ordinal == 0 || ordinal == points.Count - 1)
                {
                    roles[ordinal] = ImtBoundaryRole.Roadside;
                    continue;
                }
                IPointSourceData source = points[ordinal].Source;
                if (IsOpposingBoundary(segmentId, source))
                {
                    roles[ordinal] = ImtBoundaryRole.Center;
                    continue;
                }
                roles[ordinal] = SeparatorRole(segmentId, nodeId, source);
            }
            return roles;
        }

        private ImtMarkingStyleBundle StyleForSegment(
            ushort segmentId,
            ImtMarkingStyleBundle fallback)
        {
            SegmentStamp stamp;
            if (_segments.TryGetValue(segmentId, out stamp) && stamp.Style != null)
                return stamp.Style;
            if (segmentId == 0 || segmentId >= NetManager.instance.m_segments.m_size)
                return fallback;
            NetInfo info = NetManager.instance.m_segments.m_buffer[segmentId].Info;
            TargetRoad target = FindTarget(info);
            return target == null || target.Style == null ? fallback : target.Style;
        }

        private void RemoveOwnedCrosswalksAndStopLines(INodeMarkingData marking)
        {
            foreach (ISegmentEntranceData entrance in marking.Entrances)
            {
                if (entrance.PointCount < 2) continue;
                ICrosswalkPointData crosswalkStart;
                ICrosswalkPointData crosswalkEnd;
                if (entrance.GetCrosswalkPoint(1, out crosswalkStart)
                    && entrance.GetCrosswalkPoint((byte)entrance.PointCount, out crosswalkEnd)
                    )
                {
                    string key = NodeDefaultKey(
                        "crosswalk", marking.Id, crosswalkStart, crosswalkEnd);
                    if (_ownedNodeDefaults.Remove(key)
                        && marking.CrosswalkExist(crosswalkStart, crosswalkEnd))
                        marking.RemoveCrosswalk(crosswalkStart, crosswalkEnd);
                    _initializedNodeDefaults.Remove(key);
                }

                List<IEntrancePointData> points = GetPoints(entrance.EntrancePoints);
                for (int startIndex = 0; startIndex < points.Count; ++startIndex)
                {
                    for (int endIndex = startIndex + 1; endIndex < points.Count; ++endIndex)
                    {
                        string key = NodeDefaultKey(
                            "stop", marking.Id, points[startIndex], points[endIndex]);
                        IStopLineData stopLine;
                        if (_ownedNodeDefaults.Remove(key)
                            && marking.TryGetStopLine(points[startIndex], points[endIndex], out stopLine))
                            marking.RemoveStopLine(points[startIndex], points[endIndex]);
                        _initializedNodeDefaults.Remove(key);
                    }
                }
            }
        }

        private bool IsPedestrianCrossingAllowed(
            ushort segmentId,
            ushort nodeId,
            ref NetSegment segment)
        {
            bool startNode = segment.m_startNode == nodeId;
            bool? tmpe = _trafficPolicy.IsPedestrianCrossingAllowed(segmentId, startNode);
            if (tmpe.HasValue) return tmpe.Value;
            NetSegment.Flags flag = startNode
                ? NetSegment.Flags.CrossingStart
                : NetSegment.Flags.CrossingEnd;
            return (segment.m_flags & flag) != 0;
        }

        private void VerifyCrosswalkPostcondition(
            INodeMarkingData marking,
            ushort nodeId,
            ref NetNode node)
        {
            int connectedSegments = CountSegments(node);
            if (connectedSegments < 3) return;
            NetManager manager = NetManager.instance;
            int expected = 0;
            int actual = 0;
            List<string> missing = new List<string>();
            foreach (ISegmentEntranceData entrance in marking.Entrances)
            {
                ushort segmentId = entrance.Id;
                if (segmentId == 0 || segmentId >= manager.m_segments.m_size)
                    continue;
                ref NetSegment segment = ref manager.m_segments.m_buffer[segmentId];
                if (segment.Info == null || FindTarget(segment.Info) == null
                    || !HasPedestrianLane(segment.Info)
                    || !IsPedestrianCrossingAllowed(
                        segmentId, nodeId, ref segment)) continue;

                ICrosswalkPointData start;
                ICrosswalkPointData end;
                if (!entrance.GetCrosswalkPoint(1, out start)
                    || !entrance.GetCrosswalkPoint(
                        (byte)entrance.PointCount, out end))
                {
                    missing.Add(segmentId + ":points");
                    ++expected;
                    continue;
                }
                string key = NodeDefaultKey(
                    "crosswalk", nodeId, start, end);
                bool exists = marking.CrosswalkExist(start, end);
                if (!exists && _initializedNodeDefaults.Contains(key)
                    && !_ownedNodeDefaults.Contains(key))
                    continue;
                ++expected;
                if (exists) ++actual;
                else missing.Add(segmentId.ToString());
            }
            if (actual == expected) return;
            DiagnosticLog.Error(
                "MOD_COMPATIBILITY",
                "imt_crosswalk_postcondition_failed",
                "An affected intersection is missing an eligible generated-road crosswalk",
                null,
                "node_id", nodeId.ToString(),
                "connected_segment_count", connectedSegments.ToString(),
                "expected_crosswalk_count", expected.ToString(),
                "actual_crosswalk_count", actual.ToString(),
                "missing_segment_ids", string.Join(",", missing.ToArray()));
        }

        private bool HasTrafficLight(ushort nodeId, ref NetNode node)
        {
            bool? tmpeLight = _trafficPolicy.HasTrafficLight(nodeId);
            return tmpeLight.HasValue
                ? tmpeLight.Value
                : (node.m_flags & NetNode.Flags.TrafficLights) != 0;
        }

        private bool MustWaitForClearJunction(
            ushort segmentId,
            ushort nodeId,
            ref NetSegment segment)
        {
            bool startNode = segment.m_startNode == nodeId;
            bool? enteringAllowed = _trafficPolicy.IsEnteringBlockedJunctionAllowed(
                segmentId, startNode);
            return !enteringAllowed.GetValueOrDefault(false);
        }

        private bool HasStopSign(
            ushort segmentId,
            ushort nodeId,
            ref NetSegment segment)
        {
            bool startNode = segment.m_startNode == nodeId;
            bool? tmpeStop = _trafficPolicy.HasStopSign(segmentId, startNode);
            return tmpeStop.HasValue
                ? tmpeStop.Value
                : (segment.m_flags & (startNode
                    ? NetSegment.Flags.YieldStart
                    : NetSegment.Flags.YieldEnd)) != 0;
        }

        private static bool HasPedestrianLane(NetInfo info)
        {
            if (info == null || info.m_lanes == null) return false;
            foreach (NetInfo.Lane lane in info.m_lanes)
                if ((lane.m_laneType & NetInfo.LaneType.Pedestrian) != 0)
                    return true;
            return false;
        }

        private static ImtBoundaryRole SeparatorRole(
            ushort segmentId,
            ushort nodeId,
            IPointSourceData source)
        {
            if (source == null) return ImtBoundaryRole.Other;
            NetManager manager = NetManager.instance;
            if (manager == null || segmentId == 0
                || segmentId >= manager.m_segments.m_size)
                return ImtBoundaryRole.Other;
            ref NetSegment segment = ref manager.m_segments.m_buffer[segmentId];
            NetInfo info = segment.Info;
            if (info == null || info.m_lanes == null
                || source.LeftIndex < 0 || source.RightIndex < 0
                || source.LeftIndex >= info.m_lanes.Length
                || source.RightIndex >= info.m_lanes.Length)
                return ImtBoundaryRole.Other;

            NetInfo.Direction incoming = nodeId == segment.m_endNode
                ? NetInfo.Direction.Forward
                : NetInfo.Direction.Backward;
            if ((segment.m_flags & NetSegment.Flags.Invert) != 0)
                incoming = NetInfo.InvertDirection(incoming);
            NetInfo.Direction outgoing = NetInfo.InvertDirection(incoming);
            NetInfo.Direction left = info.m_lanes[source.LeftIndex].m_finalDirection;
            NetInfo.Direction right = info.m_lanes[source.RightIndex].m_finalDirection;
            bool leftIncoming = (left & incoming) != 0 && (left & outgoing) == 0;
            bool rightIncoming = (right & incoming) != 0 && (right & outgoing) == 0;
            if (leftIncoming && rightIncoming)
                return ImtBoundaryRole.SeparatorIncoming;
            bool leftOutgoing = (left & outgoing) != 0 && (left & incoming) == 0;
            bool rightOutgoing = (right & outgoing) != 0 && (right & incoming) == 0;
            if (leftOutgoing && rightOutgoing)
                return ImtBoundaryRole.SeparatorOutgoing;
            return ImtBoundaryRole.Other;
        }

        private static float ConnectorAngle(
            ushort nodeId,
            ushort firstSegmentId,
            ushort secondSegmentId)
        {
            NetManager manager = NetManager.instance;
            if (manager == null || firstSegmentId == 0 || secondSegmentId == 0
                || firstSegmentId >= manager.m_segments.m_size
                || secondSegmentId >= manager.m_segments.m_size)
                return 180f;
            ref NetSegment first = ref manager.m_segments.m_buffer[firstSegmentId];
            ref NetSegment second = ref manager.m_segments.m_buffer[secondSegmentId];
            Vector3 firstDirection = first.m_startNode == nodeId
                ? first.m_startDirection
                : first.m_endDirection;
            Vector3 secondDirection = second.m_startNode == nodeId
                ? second.m_startDirection
                : second.m_endDirection;
            firstDirection.y = 0f;
            secondDirection.y = 0f;
            if (firstDirection.sqrMagnitude < 0.000001f
                || secondDirection.sqrMagnitude < 0.000001f)
                return 180f;
            return Vector3.Angle(firstDirection, secondDirection);
        }

        private static bool TryGetIncomingStopLinePoints(
            ISegmentEntranceData entrance,
            ushort nodeId,
            ref NetSegment segment,
            out IEntrancePointData start,
            out IEntrancePointData end)
        {
            start = null;
            end = null;
            NetInfo info = segment.Info;
            if (info == null || info.m_lanes == null) return false;

            NetInfo.Direction incoming = nodeId == segment.m_endNode
                ? NetInfo.Direction.Forward
                : NetInfo.Direction.Backward;
            if ((segment.m_flags & NetSegment.Flags.Invert) != 0)
                incoming = NetInfo.InvertDirection(incoming);

            HashSet<int> incomingLanes = new HashSet<int>();
            NetInfo.LaneType vehicleLanes = NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle;
            for (int index = 0; index < info.m_lanes.Length; ++index)
            {
                NetInfo.Lane lane = info.m_lanes[index];
                if ((lane.m_laneType & vehicleLanes) != 0
                    && (lane.m_finalDirection & incoming) != 0)
                    incomingLanes.Add(index);
            }
            if (incomingLanes.Count == 0) return false;

            List<IEntrancePointData> boundaries = new List<IEntrancePointData>();
            foreach (IEntrancePointData point in entrance.EntrancePoints)
            {
                IPointSourceData source = point.Source;
                bool leftIncoming = source != null && incomingLanes.Contains(source.LeftIndex);
                bool rightIncoming = source != null && incomingLanes.Contains(source.RightIndex);
                if (leftIncoming != rightIncoming) boundaries.Add(point);
            }
            if (boundaries.Count < 2) return false;
            boundaries.Sort(delegate(IEntrancePointData left, IEntrancePointData right)
            {
                return left.Position.CompareTo(right.Position);
            });
            start = boundaries[0];
            end = boundaries[boundaries.Count - 1];
            return start.Index != end.Index;
        }

        private static void RemoveLegacyFullWidthStopLine(
            INodeMarkingData marking,
            ISegmentEntranceData entrance,
            IEntrancePointData incomingStart,
            IEntrancePointData incomingEnd)
        {
            IEntrancePointData fullStart;
            IEntrancePointData fullEnd;
            if (!entrance.GetEntrancePoint(1, out fullStart)
                || !entrance.GetEntrancePoint((byte)entrance.PointCount, out fullEnd)
                || (fullStart.Index == incomingStart.Index && fullEnd.Index == incomingEnd.Index))
                return;

            IStopLineData legacy;
            if (marking.TryGetStopLine(fullStart, fullEnd, out legacy))
                marking.RemoveStopLine(fullStart, fullEnd);
        }

        private static IRegularLineStyleData CreateSolidStyle(
            IDataProviderV1 provider,
            ImtMarkingStyleBundle appearance,
            bool yellow = false)
        {
            ISolidLineStyle style = provider.SolidLineStyle;
            style.Color = Color(yellow
                ? appearance.YellowColor
                : appearance.WhiteColor);
            style.Width = appearance.LineWidth;
            ApplyWear(style, appearance);
            return style;
        }

        private static IRegularLineStyleData CreateDashedStyle(IDataProviderV1 provider, ImtMarkingStyleBundle appearance, bool yellow)
        {
            IDashedLineStyle style = provider.DashedLineStyle;
            style.Color = Color(yellow ? appearance.YellowColor : appearance.WhiteColor);
            style.Width = appearance.LineWidth;
            style.DashLength = appearance.DashLength;
            style.SpaceLength = appearance.DashGap;
            ApplyWear(style, appearance);
            return style;
        }

        private static IRegularLineStyleData CreateConfiguredLineStyle(
            IDataProviderV1 provider,
            ImtMarkingStyleBundle appearance,
            string styleId)
        {
            if (string.Equals(styleId, "SOLID_YELLOW", StringComparison.OrdinalIgnoreCase))
                return CreateSolidStyle(provider, appearance, true);
            if (string.Equals(styleId, "SOLID_WHITE", StringComparison.OrdinalIgnoreCase))
                return CreateSolidStyle(provider, appearance);
            return CreateDashedStyle(provider, appearance, false);
        }

        private static IStopLineStyleData CreateStopLineStyle(IDataProviderV1 provider, ImtMarkingStyleBundle appearance)
        {
            ISolidStopLineStyle style = provider.SolidStopLineStyle;
            style.Color = Color(appearance.WhiteColor);
            style.Width = appearance.StopLineWidth;
            ApplyWear(style, appearance);
            return style;
        }

        private static ICrosswalkStyleData CreateCrosswalkStyle(IDataProviderV1 provider, ImtMarkingStyleBundle appearance)
        {
            IZebraCrosswalkStyle style = provider.ZebraCrosswalkStyle;
            style.Color = Color(appearance.WhiteColor);
            style.Width = appearance.CrosswalkWidth;
            style.DashLength = appearance.CrosswalkDashLength;
            style.SpaceLength = appearance.CrosswalkGapLength;
            style.OffsetBefore = appearance.CrosswalkOffset;
            style.OffsetAfter = appearance.CrosswalkOffset;
            // IMT 1.15 exposes the API enum as DashEnd, while the concrete
            // zebra style publishes the option under its actual name DashType.
            style.SetValue("DashType", (int)DashEnd.ParallelSlope);
            ApplyWear(style, appearance);
            return style;
        }

        private static void ApplyWear(IEffectStyleData style, ImtMarkingStyleBundle appearance)
        {
            style.Texture = appearance.Texture;
            style.Cracks = new Vector2(appearance.Cracks[0], appearance.Cracks[1]);
            style.Voids = new Vector2(appearance.Voids[0], appearance.Voids[1]);
        }

        private static ImtMarkingStyleBundle EffectiveStyle(ImtMarkingStyleBundle style)
        {
            if (style != null)
            {
                if (string.IsNullOrEmpty(style.LaneSeparatorStyle))
                    style.LaneSeparatorStyle = "DASHED_WHITE";
                if (string.IsNullOrEmpty(style.CenterLineStyle))
                    style.CenterLineStyle = style.CenterLineYellow
                        ? "SOLID_YELLOW"
                        : "DASHED_WHITE";
                style.CenterLineYellow = string.Equals(
                    style.CenterLineStyle,
                    "SOLID_YELLOW",
                    StringComparison.OrdinalIgnoreCase);
                return style;
            }
            return new ImtMarkingStyleBundle
            {
                WhiteColor = new float[] { 245f / 255f, 245f / 255f, 235f / 255f, 1f },
                YellowColor = new float[] { 1f, 0.72f, 0f, 1f },
                CenterLineYellow = false,
                RoadsideLines = true,
                LaneSeparatorStyle = "DASHED_WHITE",
                CenterLineStyle = "DASHED_WHITE",
                Texture = 0.25f,
                Cracks = new float[] { 0.70f, 0.40f },
                Voids = new float[] { 0.20f, 1f },
                CrosswalkWidth = 3f,
                CrosswalkDashLength = 0.45f,
                CrosswalkGapLength = 0.55f,
                CrosswalkOffset = 0.40f,
                StopLineWidth = 0.30f,
                LineWidth = 0.15f,
                DashLength = 6f,
                DashGap = 10f,
            };
        }

        private static Color32 Color(float[] value)
        {
            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Clamp01(value[0]) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(value[1]) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(value[2]) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(value.Length > 3 ? value[3] : 1f) * 255f));
        }

        private static bool IsOpposingBoundary(ushort segmentId, IPointSourceData source)
        {
            if (source == null) return false;
            NetInfo info = NetManager.instance.m_segments.m_buffer[segmentId].Info;
            if (info == null || info.m_lanes == null
                || source.LeftIndex < 0 || source.RightIndex < 0
                || source.LeftIndex >= info.m_lanes.Length
                || source.RightIndex >= info.m_lanes.Length) return false;
            NetInfo.Direction left = info.m_lanes[source.LeftIndex].m_finalDirection;
            NetInfo.Direction right = info.m_lanes[source.RightIndex].m_finalDirection;
            bool leftForward = (left & NetInfo.Direction.Forward) != 0;
            bool leftBackward = (left & NetInfo.Direction.Backward) != 0;
            bool rightForward = (right & NetInfo.Direction.Forward) != 0;
            bool rightBackward = (right & NetInfo.Direction.Backward) != 0;
            return (leftForward && rightBackward && !leftBackward && !rightForward)
                || (leftBackward && rightForward && !leftForward && !rightBackward);
        }

        private static List<IEntrancePointData> GetPoints(IEnumerable<IEntrancePointData> source)
        {
            List<IEntrancePointData> points = new List<IEntrancePointData>();
            foreach (IEntrancePointData point in source) points.Add(point);
            points.Sort(delegate(IEntrancePointData left, IEntrancePointData right) { return left.Position.CompareTo(right.Position); });
            return points;
        }

        private static List<IEntrancePointData> GetPointsByIndex(IEnumerable<IEntrancePointData> source)
        {
            List<IEntrancePointData> points = new List<IEntrancePointData>();
            foreach (IEntrancePointData point in source) points.Add(point);
            points.Sort(delegate(IEntrancePointData left, IEntrancePointData right)
            {
                return left.Index.CompareTo(right.Index);
            });
            return points;
        }

        private static IEntrancePointData FindClosestUnused(List<IEntrancePointData> points, float position, HashSet<byte> used)
        {
            IEntrancePointData best = null;
            float bestDistance = float.MaxValue;
            foreach (IEntrancePointData point in points)
            {
                if (used.Contains(point.Index)) continue;
                float distance = Mathf.Abs(point.Position - position);
                if (distance < bestDistance)
                {
                    best = point;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static int CountSegments(NetNode node)
        {
            int count = 0;
            for (int slot = 0; slot < 8; ++slot)
                if (node.GetSegment(slot) != 0) ++count;
            return count;
        }

        private static int CountEntrances(INodeMarkingData marking)
        {
            int count = 0;
            foreach (ISegmentEntranceData entrance in marking.Entrances)
                ++count;
            return count;
        }
    }
}

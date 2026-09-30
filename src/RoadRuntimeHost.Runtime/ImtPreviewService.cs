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
        }

        private const string ProviderId = "RoadRuntimeHost";
        private IDataProviderV1 _provider;
        private bool _reportedUnavailable;
        private readonly Dictionary<NetInfo, TargetRoad> _targets = new Dictionary<NetInfo, TargetRoad>();
        private readonly Dictionary<ushort, SegmentStamp> _segments = new Dictionary<ushort, SegmentStamp>();
        private readonly HashSet<ulong> _ownedLineIds = new HashSet<ulong>();
        private readonly HashSet<ulong> _ownedSegmentLinePairs = new HashSet<ulong>();
        private readonly HashSet<ulong> _nativeCrosswalksRestored = new HashSet<ulong>();
        private readonly ImtInternalAdapter _internalAdapter = new ImtInternalAdapter();
        private readonly object _targetSync = new object();
        private readonly object _pendingSegmentSync = new object();
        private readonly HashSet<ushort> _pendingSegments = new HashSet<ushort>();
        private volatile HashSet<NetInfo> _targetInfos = new HashSet<NetInfo>();
        private volatile bool _scanPending;
        private volatile bool _segmentBatchPending;
        private volatile bool _stopped;

        public ImtPreviewService()
        {
            NetSegmentCreationHook.Start(OnSegmentCreated);
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
            QueueFullScan();
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
            ImtCrosswalkTrajectoryHook.Stop();
            lock (_targetSync)
            {
                _targets.Clear();
                _targetInfos = new HashSet<NetInfo>();
            }
            lock (_pendingSegmentSync) _pendingSegments.Clear();
            _ownedLineIds.Clear();
            _ownedSegmentLinePairs.Clear();
            _nativeCrosswalksRestored.Clear();
        }

        private void RegisterTarget(string roadId, NetInfo info, ImtMarkingStyleBundle style)
        {
            if (info == null) return;
            ImtCrosswalkTrajectoryHook.RegisterTarget(info);
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
                HashSet<NetInfo> targetInfos = new HashSet<NetInfo>(_targetInfos);
                targetInfos.Add(info);
                _targetInfos = targetInfos;
            }
        }

        private void QueueFullScan()
        {
            if (_stopped || _scanPending || _targetInfos.Count == 0 || !SimulationManager.exists) return;
            _scanPending = true;

            SimulationManager.instance.AddAction(delegate
            {
                try
                {
                    if (_stopped) return;
                    Scan(SnapshotTargets());
                }
                finally { _scanPending = false; }
            });
        }

        private TargetRoad[] SnapshotTargets()
        {
            lock (_targetSync) return new List<TargetRoad>(_targets.Values).ToArray();
        }

        private void OnSegmentCreated(ushort segmentId, NetInfo info)
        {
            if (_stopped || segmentId == 0 || info == null || !_targetInfos.Contains(info) || !SimulationManager.exists) return;
            lock (_pendingSegmentSync)
            {
                _pendingSegments.Add(segmentId);
                if (_segmentBatchPending) return;
                _segmentBatchPending = true;
            }

            SimulationManager.instance.AddAction(ProcessPendingSegments);
        }

        private void ProcessPendingSegments()
        {
            ushort[] segmentIds;
            lock (_pendingSegmentSync)
            {
                segmentIds = new List<ushort>(_pendingSegments).ToArray();
                _pendingSegments.Clear();
                _segmentBatchPending = false;
            }
            if (_stopped || segmentIds.Length == 0) return;
            ApplySegmentBatch(segmentIds);
        }

        private void ApplySegmentBatch(ushort[] segmentIds)
        {
            IDataProviderV1 provider = GetProvider();
            if (provider == null)
            {
                lock (_pendingSegmentSync)
                    foreach (ushort segmentId in segmentIds) _pendingSegments.Add(segmentId);
                return;
            }

            TargetRoad[] targets = SnapshotTargets();
            Dictionary<NetInfo, TargetRoad> targetByInfo = new Dictionary<NetInfo, TargetRoad>();
            foreach (TargetRoad target in targets)
                if (target.Info != null) targetByInfo[target.Info] = target;

            int changedSegments = 0;
            int linesAdded = 0;
            int linesExisting = 0;
            int crosswalksAdded = 0;
            int crosswalksExisting = 0;
            int stopLinesAdded = 0;
            int stopLinesExisting = 0;
            int failures = 0;
            Exception firstFailure = null;
            HashSet<ushort> nodes = new HashSet<ushort>();
            NetManager manager = NetManager.instance;

            foreach (ushort segmentId in segmentIds)
            {
                if (segmentId == 0 || segmentId >= manager.m_segments.m_size) continue;
                ref NetSegment segment = ref manager.m_segments.m_buffer[segmentId];
                TargetRoad target;
                if ((segment.m_flags & NetSegment.Flags.Created) == 0
                    || segment.Info == null
                    || !targetByInfo.TryGetValue(segment.Info, out target)) continue;

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
                    EndNode = segment.m_endNode
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
                if (CountSegments(node) < 3) continue;
                HashSet<NetInfo> appliedInfos = new HashSet<NetInfo>();
                for (int slot = 0; slot < 8; ++slot)
                {
                    ushort connectedSegmentId = node.GetSegment(slot);
                    if (connectedSegmentId == 0) continue;
                    NetInfo connectedInfo = manager.m_segments.m_buffer[connectedSegmentId].Info;
                    TargetRoad target;
                    if (connectedInfo == null
                        || !appliedInfos.Add(connectedInfo)
                        || !targetByInfo.TryGetValue(connectedInfo, out target)) continue;
                    try
                    {
                        ApplyNodeMarkings(provider, nodeId, target.Info, target.Style, ref crosswalksAdded, ref crosswalksExisting, ref stopLinesAdded, ref stopLinesExisting);
                        manager.UpdateNodeRenderer(nodeId, true);
                    }
                    catch (Exception error)
                    {
                        ++failures;
                        if (firstFailure == null) firstFailure = error;
                    }
                }
            }

            if (changedSegments == 0) return;
            if (failures != 0)
            {
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "imt_preview_segment_batch_partial_failure",
                    "IMT preview markings were applied only partially for a new-segment batch",
                    firstFailure,
                    "notified_segment_count", segmentIds.Length.ToString(),
                    "changed_segment_count", changedSegments.ToString(),
                    "affected_node_count", nodes.Count.ToString(),
                    "failure_count", failures.ToString());
            }
            DiagnosticLog.Info(
                failures == 0 ? "SUCCESS" : "MOD_COMPATIBILITY",
                "imt_preview_segment_batch_applied",
                "IMT preview markings were evaluated for an event-driven new-segment batch",
                "notified_segment_count", segmentIds.Length.ToString(),
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
        }

        private void Scan(TargetRoad[] targets)
        {
            IDataProviderV1 provider = GetProvider();
            if (provider == null) return;

            Dictionary<NetInfo, TargetRoad> targetByInfo = new Dictionary<NetInfo, TargetRoad>();
            foreach (TargetRoad target in targets)
                if (target.Info != null) targetByInfo[target.Info] = target;

            int matchingSegments = 0;
            int changedSegments = 0;
            int linesAdded = 0;
            int linesExisting = 0;
            int crosswalksAdded = 0;
            int crosswalksExisting = 0;
            int stopLinesAdded = 0;
            int stopLinesExisting = 0;
            int failures = 0;
            Exception firstFailure = null;
            Dictionary<ushort, List<TargetRoad>> nodes = new Dictionary<ushort, List<TargetRoad>>();
            NetManager manager = NetManager.instance;

            for (ushort segmentId = 1; segmentId < manager.m_segments.m_size; ++segmentId)
            {
                ref NetSegment segment = ref manager.m_segments.m_buffer[segmentId];
                TargetRoad target;
                if ((segment.m_flags & NetSegment.Flags.Created) == 0
                    || segment.Info == null
                    || !targetByInfo.TryGetValue(segment.Info, out target)) continue;

                ++matchingSegments;
                SegmentStamp stamp;
                if (_segments.TryGetValue(segmentId, out stamp)
                    && stamp.BuildIndex == segment.m_buildIndex
                    && ReferenceEquals(stamp.Info, segment.Info)
                    && stamp.StartNode == segment.m_startNode
                    && stamp.EndNode == segment.m_endNode) continue;

                ++changedSegments;
                stamp = new SegmentStamp();
                stamp.BuildIndex = segment.m_buildIndex;
                stamp.Info = segment.Info;
                stamp.StartNode = segment.m_startNode;
                stamp.EndNode = segment.m_endNode;
                _segments[segmentId] = stamp;
                AddNodeTarget(nodes, segment.m_startNode, target);
                AddNodeTarget(nodes, segment.m_endNode, target);
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

            foreach (KeyValuePair<ushort, List<TargetRoad>> item in nodes)
            {
                if (CountSegments(manager.m_nodes.m_buffer[item.Key]) < 3) continue;
                foreach (TargetRoad target in item.Value)
                {
                    try
                    {
                        ApplyNodeMarkings(provider, item.Key, target.Info, target.Style, ref crosswalksAdded, ref crosswalksExisting, ref stopLinesAdded, ref stopLinesExisting);
                        manager.UpdateNodeRenderer(item.Key, true);
                    }
                    catch (Exception error)
                    {
                        ++failures;
                        if (firstFailure == null) firstFailure = error;
                    }
                }
            }

            if (changedSegments == 0) return;

            if (failures != 0)
            {
                DiagnosticLog.Error(
                    "MOD_COMPATIBILITY",
                    "imt_preview_apply_partial_failure",
                    "IMT preview markings were applied only partially",
                    firstFailure,
                    "target_prefab_count", targetByInfo.Count.ToString(),
                    "matching_segment_count", matchingSegments.ToString(),
                    "changed_segment_count", changedSegments.ToString(),
                    "failure_count", failures.ToString());
            }

            DiagnosticLog.Info(
                failures == 0 ? "SUCCESS" : "MOD_COMPATIBILITY",
                "imt_preview_applied",
                "IMT preview markings were evaluated without changing the road mesh",
                "target_prefab_count", targetByInfo.Count.ToString(),
                "matching_segment_count", matchingSegments.ToString(),
                "changed_segment_count", changedSegments.ToString(),
                "line_added_count", linesAdded.ToString(),
                "line_existing_count", linesExisting.ToString(),
                "crosswalk_added_count", crosswalksAdded.ToString(),
                "crosswalk_existing_count", crosswalksExisting.ToString(),
                "stop_line_added_count", stopLinesAdded.ToString(),
                "stop_line_existing_count", stopLinesExisting.ToString(),
                "failure_count", failures.ToString(),
                "preview_only", bool.TrueString);
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
                    if (!_ownedSegmentLinePairs.Contains(pairKey)) continue;
                    if (!marking.RemoveRegularLine(start, end))
                        throw new InvalidOperationException("Existing IMT preview line could not be replaced");
                    _ownedSegmentLinePairs.Remove(pairKey);
                }

                bool edge = index == 0 || index == starts.Count - 1;
                bool yellow = !edge && appearance.CenterLineYellow && IsOpposingBoundary(segmentId, start.Source);
                IRegularLineStyleData style = edge ? CreateSolidStyle(provider, appearance) : CreateDashedStyle(provider, appearance, yellow);
                marking.AddRegularLine(start, end, style);
                _ownedSegmentLinePairs.Add(pairKey);
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

        private void ApplyNodeMarkings(
            IDataProviderV1 provider,
            ushort nodeId,
            NetInfo targetInfo,
            ImtMarkingStyleBundle appearance,
            ref int crosswalksAdded,
            ref int crosswalksExisting,
            ref int stopLinesAdded,
            ref int stopLinesExisting)
        {
            INodeMarkingData marking = provider.GetOrCreateNodeMarking(nodeId);
            ref NetNode node = ref NetManager.instance.m_nodes.m_buffer[nodeId];
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
                    bool existed = marking.CrosswalkExist(crosswalkStart, crosswalkEnd);
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
                        ++crosswalksAdded;
                    }

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

                IEntrancePointData stopStart;
                IEntrancePointData stopEnd;
                if (TryGetIncomingStopLinePoints(entrance, nodeId, ref segment, out stopStart, out stopEnd))
                {
                    RemoveLegacyFullWidthStopLine(marking, entrance, stopStart, stopEnd);
                    IStopLineData current;
                    if (marking.TryGetStopLine(stopStart, stopEnd, out current))
                    {
                        ++stopLinesExisting;
                        if (!_ownedLineIds.Contains(current.Id)) continue;
                        if (!marking.RemoveStopLine(stopStart, stopEnd))
                            throw new InvalidOperationException("Existing IMT preview stop line could not be replaced");
                        _ownedLineIds.Remove(current.Id);
                    }
                    IStopLineData createdStopLine = marking.AddStopLine(stopStart, stopEnd, CreateStopLineStyle(provider, appearance));
                    _ownedLineIds.Add(createdStopLine.Id);
                    ++stopLinesAdded;
                }
            }
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

        private static IRegularLineStyleData CreateSolidStyle(IDataProviderV1 provider, ImtMarkingStyleBundle appearance)
        {
            ISolidLineStyle style = provider.SolidLineStyle;
            style.Color = Color(appearance.WhiteColor);
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
            if (style != null) return style;
            return new ImtMarkingStyleBundle
            {
                WhiteColor = new float[] { 245f / 255f, 245f / 255f, 235f / 255f, 1f },
                YellowColor = new float[] { 1f, 0.72f, 0f, 1f },
                CenterLineYellow = false,
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
    }
}

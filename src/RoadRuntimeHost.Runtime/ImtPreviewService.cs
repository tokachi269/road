using System;
using System.Collections.Generic;
using ColossalFramework;
using IMT.API;
using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal sealed class ImtPreviewService
    {
        private const string ProviderId = "RoadRuntimeHost";
        private IDataProviderV1 _provider;
        private bool _reportedUnavailable;

        public void ApplyRoad(string roadId, NetInfo info)
        {
            if (info == null || !SimulationManager.exists) return;

            SimulationManager.instance.AddAction(delegate
            {
                IDataProviderV1 provider = GetProvider();
                if (provider == null) return;

                int matchingSegments = 0;
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

                for (ushort segmentId = 1; segmentId < NetManager.MAX_SEGMENT_COUNT; ++segmentId)
                {
                    ref NetSegment segment = ref manager.m_segments.m_buffer[segmentId];
                    if ((segment.m_flags & NetSegment.Flags.Created) == 0 || segment.Info != info) continue;

                    ++matchingSegments;
                    nodes.Add(segment.m_startNode);
                    nodes.Add(segment.m_endNode);
                    try
                    {
                        ApplySegmentLines(provider, segmentId, ref linesAdded, ref linesExisting);
                    }
                    catch (Exception error)
                    {
                        ++failures;
                        if (firstFailure == null) firstFailure = error;
                    }
                }

                foreach (ushort nodeId in nodes)
                {
                    try
                    {
                        if (CountSegments(manager.m_nodes.m_buffer[nodeId]) < 3) continue;
                        ApplyNodeMarkings(provider, nodeId, info, ref crosswalksAdded, ref crosswalksExisting, ref stopLinesAdded, ref stopLinesExisting);
                    }
                    catch (Exception error)
                    {
                        ++failures;
                        if (firstFailure == null) firstFailure = error;
                    }
                }

                if (failures != 0)
                {
                    DiagnosticLog.Error(
                        "MOD_COMPATIBILITY",
                        "imt_preview_apply_partial_failure",
                        "IMT preview markings were applied only partially",
                        firstFailure,
                        "road_id", roadId ?? string.Empty,
                        "prefab_name", info.name ?? string.Empty,
                        "matching_segment_count", matchingSegments.ToString(),
                        "failure_count", failures.ToString());
                }

                DiagnosticLog.Info(
                    "SUCCESS",
                    "imt_preview_applied",
                    "IMT preview markings were evaluated without changing the road mesh",
                    "road_id", roadId ?? string.Empty,
                    "prefab_name", info.name ?? string.Empty,
                    "matching_segment_count", matchingSegments.ToString(),
                    "line_added_count", linesAdded.ToString(),
                    "line_existing_count", linesExisting.ToString(),
                    "crosswalk_added_count", crosswalksAdded.ToString(),
                    "crosswalk_existing_count", crosswalksExisting.ToString(),
                    "stop_line_added_count", stopLinesAdded.ToString(),
                    "stop_line_existing_count", stopLinesExisting.ToString(),
                    "failure_count", failures.ToString(),
                    "preview_only", bool.TrueString);
            });
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

        private static void ApplySegmentLines(IDataProviderV1 provider, ushort segmentId, ref int added, ref int existing)
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

                IRegularLineData current;
                if (marking.TryGetRegularLine(start, end, out current))
                {
                    ++existing;
                    continue;
                }

                bool edge = index == 0 || index == starts.Count - 1;
                IRegularLineStyleData style = edge ? CreateSolidStyle(provider) : CreateDashedStyle(provider);
                marking.AddRegularLine(start, end, style);
                ++added;
            }
        }

        private static void ApplyNodeMarkings(
            IDataProviderV1 provider,
            ushort nodeId,
            NetInfo targetInfo,
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
                    if (marking.CrosswalkExist(crosswalkStart, crosswalkEnd)) ++crosswalksExisting;
                    else
                    {
                        marking.AddCrosswalk(crosswalkStart, crosswalkEnd, CreateCrosswalkStyle(provider));
                        ++crosswalksAdded;
                    }
                }

                IEntrancePointData stopStart;
                IEntrancePointData stopEnd;
                if (entrance.GetEntrancePoint(1, out stopStart)
                    && entrance.GetEntrancePoint((byte)entrance.PointCount, out stopEnd))
                {
                    IStopLineData current;
                    if (marking.TryGetStopLine(stopStart, stopEnd, out current)) ++stopLinesExisting;
                    else
                    {
                        marking.AddStopLine(stopStart, stopEnd, CreateStopLineStyle(provider));
                        ++stopLinesAdded;
                    }
                }
            }
        }

        private static IRegularLineStyleData CreateSolidStyle(IDataProviderV1 provider)
        {
            ISolidLineStyle style = provider.SolidLineStyle;
            style.Color = new Color32(245, 245, 235, 255);
            style.Width = 0.15f;
            ApplyWear(style);
            return style;
        }

        private static IRegularLineStyleData CreateDashedStyle(IDataProviderV1 provider)
        {
            IDashedLineStyle style = provider.DashedLineStyle;
            style.Color = new Color32(245, 245, 235, 255);
            style.Width = 0.15f;
            style.DashLength = 6f;
            style.SpaceLength = 10f;
            ApplyWear(style);
            return style;
        }

        private static IStopLineStyleData CreateStopLineStyle(IDataProviderV1 provider)
        {
            ISolidStopLineStyle style = provider.SolidStopLineStyle;
            style.Color = new Color32(245, 245, 235, 255);
            style.Width = 0.30f;
            ApplyWear(style);
            return style;
        }

        private static ICrosswalkStyleData CreateCrosswalkStyle(IDataProviderV1 provider)
        {
            IZebraCrosswalkStyle style = provider.ZebraCrosswalkStyle;
            style.Color = new Color32(245, 245, 235, 255);
            style.Width = 3f;
            style.DashLength = 0.45f;
            style.SpaceLength = 0.55f;
            style.OffsetBefore = 0.30f;
            style.OffsetAfter = 0.30f;
            ApplyWear(style);
            return style;
        }

        private static void ApplyWear(IEffectStyleData style)
        {
            style.Texture = 0.25f;
            style.Cracks = new Vector2(0.10f, 1f);
            style.Voids = new Vector2(0.05f, 1f);
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

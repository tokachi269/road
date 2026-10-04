using System;
using System.Collections.Generic;

namespace RoadRuntimeHost.Runtime
{
    // These DTOs deliberately describe the runtime topology before IMT line
    // mutation.  They contain no CS1 or IMT interfaces, which keeps the
    // interpretation step testable without constructing a live marking.
    internal enum RuntimeLaneFlow
    {
        None = 0,
        Forward = 1,
        Backward = 2,
        Both = 3,
    }

    internal enum RuntimeBoundarySemantic
    {
        Other = 0,
        Roadside = 1,
        SameDirectionSeparator = 2,
        OpposingCenter = 3,
    }

    internal sealed class RuntimeLaneSnapshot
    {
        public int Index;
        public RuntimeLaneFlow Flow;
        public bool Vehicle;
        public bool Pedestrian;
    }

    internal sealed class RuntimeBoundarySnapshot
    {
        public int PointIndex;
        public int PhysicalOrdinal;
        public int LeftLaneIndex;
        public int RightLaneIndex;
    }

    internal sealed class RuntimeSegmentSnapshot
    {
        public ushort SegmentId;
        public bool Target;
        public ushort StartNode;
        public ushort EndNode;
        public bool Invert;
        public RuntimeLaneSnapshot[] Lanes;
        public RuntimeBoundarySnapshot[] Boundaries;
    }

    internal sealed class RuntimeEntranceSnapshot
    {
        public ushort SegmentId;
        public bool Target;
        public bool IsStartSide;
        public bool HasPedestrianLane;
        public bool CrossingAllowed;
        public bool HasIncomingVehicleLane;
        public bool HasTrafficControl;
        public RuntimeBoundarySnapshot[] Boundaries;
    }

    internal sealed class RuntimeNodeSnapshot
    {
        public ushort NodeId;
        public RuntimeSegmentSnapshot[] Segments;
        public RuntimeEntranceSnapshot[] Entrances;
    }

    internal sealed class RuntimeBoundaryPlan
    {
        public ushort SegmentId;
        public int PointIndex;
        public RuntimeBoundarySemantic Semantic;
        public RuntimeLaneFlow FlowAtNode;
        public int InnerDistance;
        public int PhysicalOrdinal;
    }

    internal sealed class RuntimeConnectorPlan
    {
        public ushort FirstSegmentId;
        public int FirstPointIndex;
        public ushort SecondSegmentId;
        public int SecondPointIndex;
        public RuntimeBoundarySemantic Semantic;
    }

    internal sealed class RuntimeMarkingPlan
    {
        public readonly List<RuntimeBoundaryPlan> SegmentBoundaries =
            new List<RuntimeBoundaryPlan>();
        public readonly List<RuntimeConnectorPlan> NodeConnectors =
            new List<RuntimeConnectorPlan>();
        public readonly List<ushort> CrosswalkSegments = new List<ushort>();
        public readonly List<ushort> StopLineSegments = new List<ushort>();
    }

    internal static class RuntimeMarkingPlanBuilder
    {
        public static bool ShouldCreateBoundary(RuntimeBoundarySemantic semantic, bool roadsideEnabled)
        {
            return semantic != RuntimeBoundarySemantic.Other
                && (semantic != RuntimeBoundarySemantic.Roadside || roadsideEnabled);
        }

        public static RuntimeMarkingPlan Build(RuntimeNodeSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            RuntimeMarkingPlan plan = new RuntimeMarkingPlan();
            RuntimeSegmentSnapshot[] allSegments = snapshot.Segments ?? new RuntimeSegmentSnapshot[0];
            if (snapshot.Entrances == null
                || snapshot.Entrances.Length != allSegments.Length)
                throw new InvalidOperationException(
                    "Runtime marking plan requires one IMT entrance per connected segment");
            List<RuntimeSegmentSnapshot> targetSegments = new List<RuntimeSegmentSnapshot>();
            foreach (RuntimeSegmentSnapshot segment in allSegments)
                if (segment != null && segment.Target) targetSegments.Add(segment);
            RuntimeEntranceSnapshot[] entrances = snapshot.Entrances ?? new RuntimeEntranceSnapshot[0];
            for (int segmentIndex = 0; segmentIndex < targetSegments.Count; ++segmentIndex)
            {
                RuntimeSegmentSnapshot segment = targetSegments[segmentIndex];
                if (segment == null) continue;
                RuntimeBoundarySnapshot[] boundaries = OrderedBoundaries(segment.Boundaries);
                for (int index = 0; index < boundaries.Length; ++index)
                {
                    RuntimeBoundarySnapshot boundary = boundaries[index];
                    plan.SegmentBoundaries.Add(new RuntimeBoundaryPlan
                    {
                        SegmentId = segment.SegmentId,
                        PointIndex = boundary.PointIndex,
                        Semantic = ClassifyBoundary(segment, boundary),
                        FlowAtNode = FlowAtNode(segment, boundary, snapshot.NodeId),
                        InnerDistance = InnerDistance(segment, boundary),
                        PhysicalOrdinal = boundary.PhysicalOrdinal,
                    });
                }
            }

            if (allSegments.Length == 2 && targetSegments.Count == 2
                && entrances.Length == 2)
                AddTwoSegmentConnectors(plan, targetSegments.ToArray(), entrances);

            if (allSegments.Length >= 3)
            {
                for (int index = 0; index < entrances.Length; ++index)
                {
                    RuntimeEntranceSnapshot entrance = entrances[index];
                    if (entrance == null) continue;
                    if (!entrance.Target) continue;
                    if (entrance.HasPedestrianLane && entrance.CrossingAllowed)
                        plan.CrosswalkSegments.Add(entrance.SegmentId);
                    if (entrance.HasIncomingVehicleLane && entrance.HasTrafficControl)
                        plan.StopLineSegments.Add(entrance.SegmentId);
                }
            }
            return plan;
        }

        public static RuntimeBoundarySemantic ClassifyBoundary(
            RuntimeSegmentSnapshot segment,
            RuntimeBoundarySnapshot boundary)
        {
            if (segment == null || boundary == null) return RuntimeBoundarySemantic.Other;
            if (boundary.LeftLaneIndex < 0 || boundary.RightLaneIndex < 0)
                return RuntimeBoundarySemantic.Roadside;
            RuntimeLaneSnapshot left = FindLane(segment.Lanes, boundary.LeftLaneIndex);
            RuntimeLaneSnapshot right = FindLane(segment.Lanes, boundary.RightLaneIndex);
            if (left == null || right == null) return RuntimeBoundarySemantic.Other;
            RuntimeLaneFlow leftFlow = NormalizeFlow(left.Flow, segment.Invert);
            RuntimeLaneFlow rightFlow = NormalizeFlow(right.Flow, segment.Invert);
            if (IsOneWay(leftFlow) && IsOneWay(rightFlow))
                return leftFlow != rightFlow
                    ? RuntimeBoundarySemantic.OpposingCenter
                    : RuntimeBoundarySemantic.SameDirectionSeparator;
            if (leftFlow == RuntimeLaneFlow.Both || rightFlow == RuntimeLaneFlow.Both)
                return RuntimeBoundarySemantic.SameDirectionSeparator;
            return RuntimeBoundarySemantic.Other;
        }

        private static void AddTwoSegmentConnectors(
            RuntimeMarkingPlan plan,
            RuntimeSegmentSnapshot[] segments,
            RuntimeEntranceSnapshot[] entrances)
        {
            RuntimeSegmentSnapshot firstSegment = FindSegment(segments, entrances[0].SegmentId);
            RuntimeSegmentSnapshot secondSegment = FindSegment(segments, entrances[1].SegmentId);
            if (firstSegment == null || secondSegment == null) return;
            RuntimeBoundaryPlan[] first = BoundaryPlans(plan, firstSegment.SegmentId);
            RuntimeBoundaryPlan[] second = BoundaryPlans(plan, secondSegment.SegmentId);
            Array.Sort(first, delegate(RuntimeBoundaryPlan left, RuntimeBoundaryPlan right)
            {
                if (left.Semantic == RuntimeBoundarySemantic.SameDirectionSeparator
                    && right.Semantic == left.Semantic)
                {
                    int distance = left.InnerDistance.CompareTo(right.InnerDistance);
                    if (distance != 0) return distance;
                }
                int semantic = ((int)left.Semantic).CompareTo((int)right.Semantic);
                return semantic != 0 ? semantic : left.PhysicalOrdinal.CompareTo(right.PhysicalOrdinal);
            });
            bool[] used = new bool[second.Length];
            for (int firstIndex = 0; firstIndex < first.Length; ++firstIndex)
            {
                RuntimeBoundaryPlan candidate = first[firstIndex];
                if (candidate.Semantic == RuntimeBoundarySemantic.Other) continue;
                int best = -1;
                for (int secondIndex = 0; secondIndex < second.Length; ++secondIndex)
                {
                    int mirrored = second.Length - 1 - secondIndex;
                    if (used[mirrored]
                        || second[mirrored].Semantic != candidate.Semantic) continue;
                    if (candidate.Semantic == RuntimeBoundarySemantic.SameDirectionSeparator)
                    {
                        if (!IsOneWay(candidate.FlowAtNode)
                            || second[mirrored].FlowAtNode == candidate.FlowAtNode
                            || !IsOneWay(second[mirrored].FlowAtNode)) continue;
                        if (best >= 0 && second[best].InnerDistance <= second[mirrored].InnerDistance)
                            continue;
                    }
                    best = mirrored;
                    if (candidate.Semantic != RuntimeBoundarySemantic.SameDirectionSeparator) break;
                }
                if (best < 0) continue;
                used[best] = true;
                plan.NodeConnectors.Add(new RuntimeConnectorPlan
                {
                    FirstSegmentId = candidate.SegmentId,
                    FirstPointIndex = candidate.PointIndex,
                    SecondSegmentId = second[best].SegmentId,
                    SecondPointIndex = second[best].PointIndex,
                    Semantic = candidate.Semantic,
                });
            }
        }

        private static RuntimeLaneFlow FlowAtNode(RuntimeSegmentSnapshot segment,
            RuntimeBoundarySnapshot boundary, ushort nodeId)
        {
            RuntimeLaneSnapshot lane = FindLane(segment.Lanes, boundary.LeftLaneIndex);
            if (lane == null) return RuntimeLaneFlow.None;
            // Forward here means outgoing; Backward means incoming. A node's
            // two entrances have opposite normals, so continuity joins unlike flows.
            return NormalizeFlow(lane.Flow, segment.Invert ^ (segment.EndNode == nodeId));
        }

        private static int InnerDistance(RuntimeSegmentSnapshot segment, RuntimeBoundarySnapshot boundary)
        {
            RuntimeBoundarySnapshot[] ordered = OrderedBoundaries(segment.Boundaries);
            foreach (RuntimeBoundarySnapshot other in ordered)
                if (ClassifyBoundary(segment, other) == RuntimeBoundarySemantic.OpposingCenter)
                    return Math.Abs(boundary.PhysicalOrdinal - other.PhysicalOrdinal);
            // A one-way road has no center boundary. Use its physical middle,
            // never the numeric lane index or an arbitrary first matching point.
            if (ordered.Length == 0) return 0;
            return Math.Abs(2 * boundary.PhysicalOrdinal
                - ordered[0].PhysicalOrdinal - ordered[ordered.Length - 1].PhysicalOrdinal);
        }

        private static RuntimeBoundaryPlan[] BoundaryPlans(
            RuntimeMarkingPlan plan, ushort segmentId)
        {
            List<RuntimeBoundaryPlan> result = new List<RuntimeBoundaryPlan>();
            foreach (RuntimeBoundaryPlan boundary in plan.SegmentBoundaries)
                if (boundary.SegmentId == segmentId) result.Add(boundary);
            return result.ToArray();
        }

        private static RuntimeBoundarySnapshot[] OrderedBoundaries(
            RuntimeBoundarySnapshot[] boundaries)
        {
            List<RuntimeBoundarySnapshot> result = new List<RuntimeBoundarySnapshot>();
            if (boundaries != null)
                foreach (RuntimeBoundarySnapshot boundary in boundaries)
                    if (boundary != null) result.Add(boundary);
            result.Sort(delegate(RuntimeBoundarySnapshot left, RuntimeBoundarySnapshot right)
            {
                return left.PhysicalOrdinal.CompareTo(right.PhysicalOrdinal);
            });
            return result.ToArray();
        }

        private static RuntimeLaneFlow NormalizeFlow(RuntimeLaneFlow flow, bool invert)
        {
            if (!invert || flow == RuntimeLaneFlow.Both || flow == RuntimeLaneFlow.None)
                return flow;
            return flow == RuntimeLaneFlow.Forward
                ? RuntimeLaneFlow.Backward
                : RuntimeLaneFlow.Forward;
        }

        private static bool IsOneWay(RuntimeLaneFlow flow)
        {
            return flow == RuntimeLaneFlow.Forward || flow == RuntimeLaneFlow.Backward;
        }

        private static RuntimeLaneSnapshot FindLane(
            RuntimeLaneSnapshot[] lanes, int index)
        {
            if (lanes == null) return null;
            foreach (RuntimeLaneSnapshot lane in lanes)
                if (lane != null && lane.Index == index) return lane;
            return null;
        }

        private static RuntimeSegmentSnapshot FindSegment(
            RuntimeSegmentSnapshot[] segments, ushort segmentId)
        {
            foreach (RuntimeSegmentSnapshot segment in segments)
                if (segment != null && segment.SegmentId == segmentId) return segment;
            return null;
        }
    }
}

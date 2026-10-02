using System;
using System.Collections.Generic;

namespace RoadRuntimeHost.Runtime
{
    internal enum ImtBoundaryRole
    {
        Other = 0,
        Roadside = 1,
        Center = 2,
        SeparatorIncoming = 3,
        SeparatorOutgoing = 4,
    }

    internal static class ImtNodePolicy
    {
        public const float DashedConnectorMinimumAngle = 135f;

        public static bool ShouldConnectRoadLines(int connectedSegments)
        {
            return connectedSegments == 2;
        }

        public static int OppositePointOrdinal(int pointOrdinal, int pointCount)
        {
            return pointCount - 1 - pointOrdinal;
        }

        public static bool CanConnectBoundaryRoles(
            ImtBoundaryRole first,
            ImtBoundaryRole second)
        {
            if (first == ImtBoundaryRole.Roadside)
                return second == ImtBoundaryRole.Roadside;
            if (first == ImtBoundaryRole.Center)
                return second == ImtBoundaryRole.Center;
            if (first == ImtBoundaryRole.SeparatorIncoming)
                return second == ImtBoundaryRole.SeparatorOutgoing;
            if (first == ImtBoundaryRole.SeparatorOutgoing)
                return second == ImtBoundaryRole.SeparatorIncoming;
            return false;
        }

        public static int[] MatchBoundaryRoles(
            ImtBoundaryRole[] first,
            ImtBoundaryRole[] second)
        {
            List<int> pairs = new List<int>();
            if (first == null || second == null || first.Length < 2 || second.Length < 2)
                return pairs.ToArray();

            AddRolePair(pairs, first, second, 0, second.Length - 1);
            AddRolePair(pairs, first, second, first.Length - 1, 0);

            List<int> firstInternal = InnerFirstOrdinals(first.Length);
            List<int> secondInternal = InnerFirstOrdinals(second.Length);
            HashSet<int> usedSecond = new HashSet<int>();
            foreach (int firstOrdinal in firstInternal)
            {
                foreach (int secondFirstOrder in secondInternal)
                {
                    int secondOrdinal = second.Length - 1 - secondFirstOrder;
                    if (usedSecond.Contains(secondOrdinal)
                        || !CanConnectBoundaryRoles(
                            first[firstOrdinal], second[secondOrdinal])) continue;
                    AddPair(pairs, firstOrdinal, secondOrdinal);
                    usedSecond.Add(secondOrdinal);
                    break;
                }
            }
            return pairs.ToArray();
        }

        // Compatibility helper for offline consumers. Runtime line creation
        // uses MatchBoundaryRoles so centers are never treated as separators.
        public static int[] MatchTwoSegmentBoundaries(
            int firstCount,
            int secondCount,
            int firstCenterOrdinal,
            int secondCenterOrdinal)
        {
            return MatchBoundaryRoles(
                LegacyRoles(firstCount, firstCenterOrdinal),
                LegacyRoles(secondCount, secondCenterOrdinal));
        }

        public static string ConnectorStyle(
            ImtBoundaryRole role,
            string firstStyle,
            string secondStyle,
            float connectorAngle)
        {
            if (role == ImtBoundaryRole.Roadside) return "SOLID_WHITE";
            bool centerLine = role == ImtBoundaryRole.Center;
            string first = NormalizeStyle(firstStyle, centerLine);
            string second = NormalizeStyle(secondStyle, centerLine);
            string resolved;
            if (string.Equals(first, second, StringComparison.Ordinal))
                resolved = first;
            else if (centerLine
                && (first == "SOLID_YELLOW" || second == "SOLID_YELLOW"))
                resolved = "SOLID_YELLOW";
            else if (first == "SOLID_WHITE" || second == "SOLID_WHITE")
                resolved = "SOLID_WHITE";
            else
                resolved = "DASHED_WHITE";

            if (connectorAngle < DashedConnectorMinimumAngle
                && resolved == "DASHED_WHITE")
                return "SOLID_WHITE";
            return resolved;
        }

        private static ImtBoundaryRole[] LegacyRoles(int count, int centerOrdinal)
        {
            if (count < 0) count = 0;
            ImtBoundaryRole[] roles = new ImtBoundaryRole[count];
            for (int index = 0; index < count; ++index)
            {
                if (index == 0 || index == count - 1)
                    roles[index] = ImtBoundaryRole.Roadside;
                else if (index == centerOrdinal)
                    roles[index] = ImtBoundaryRole.Center;
                else
                    roles[index] = index < (count - 1) * 0.5f
                        ? ImtBoundaryRole.SeparatorIncoming
                        : ImtBoundaryRole.SeparatorOutgoing;
            }
            return roles;
        }

        private static string NormalizeStyle(string style, bool centerLine)
        {
            if (centerLine && string.Equals(
                    style, "SOLID_YELLOW", StringComparison.OrdinalIgnoreCase))
                return "SOLID_YELLOW";
            if (string.Equals(style, "SOLID_WHITE", StringComparison.OrdinalIgnoreCase))
                return "SOLID_WHITE";
            return "DASHED_WHITE";
        }

        private static void AddRolePair(
            List<int> pairs,
            ImtBoundaryRole[] first,
            ImtBoundaryRole[] second,
            int firstOrdinal,
            int secondOrdinal)
        {
            if (CanConnectBoundaryRoles(first[firstOrdinal], second[secondOrdinal]))
                AddPair(pairs, firstOrdinal, secondOrdinal);
        }

        private static List<int> InnerFirstOrdinals(int count)
        {
            List<int> values = new List<int>();
            for (int ordinal = 1; ordinal < count - 1; ++ordinal)
                values.Add(ordinal);
            float center = (count - 1) * 0.5f;
            values.Sort(delegate(int left, int right)
            {
                int distance = Math.Abs(left - center).CompareTo(
                    Math.Abs(right - center));
                return distance != 0 ? distance : right.CompareTo(left);
            });
            return values;
        }

        private static void AddPair(List<int> pairs, int first, int second)
        {
            pairs.Add(first);
            pairs.Add(second);
        }

        public static bool ShouldCreateCrosswalk(
            int connectedSegments,
            bool hasPedestrianLane,
            bool crossingAllowed)
        {
            return connectedSegments >= 3 && hasPedestrianLane && crossingAllowed;
        }

        public static int ExpectedCrosswalkCount(
            int connectedSegments,
            bool[] hasPedestrianLane,
            bool[] crossingAllowed)
        {
            if (hasPedestrianLane == null || crossingAllowed == null
                || hasPedestrianLane.Length != crossingAllowed.Length)
                return 0;
            int count = 0;
            for (int index = 0; index < hasPedestrianLane.Length; ++index)
                if (ShouldCreateCrosswalk(
                        connectedSegments,
                        hasPedestrianLane[index],
                        crossingAllowed[index])) ++count;
            return count;
        }

        public static bool ShouldCreateStopLine(
            int connectedSegments,
            bool hasIncomingVehicleLane,
            bool hasTrafficLight,
            bool hasStopSign,
            bool mustWaitForClearJunction)
        {
            return connectedSegments >= 3
                && hasIncomingVehicleLane
                && (hasTrafficLight || hasStopSign || mustWaitForClearJunction);
        }
    }
}

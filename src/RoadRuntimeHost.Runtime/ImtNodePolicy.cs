namespace RoadRuntimeHost.Runtime
{
    internal static class ImtNodePolicy
    {
        public static bool ShouldConnectRoadLines(int connectedSegments)
        {
            return connectedSegments == 2;
        }

        public static bool ShouldCreateCrosswalk(
            int connectedSegments,
            bool hasPedestrianLane,
            bool crossingAllowed)
        {
            return connectedSegments >= 3 && hasPedestrianLane && crossingAllowed;
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

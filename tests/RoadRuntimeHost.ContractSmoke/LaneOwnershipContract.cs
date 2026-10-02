using System;
using System.Reflection;

namespace RoadRuntimeHost.ContractSmoke
{
    internal static class LaneOwnershipContract
    {
        public static void Validate(Assembly runtimeAssembly)
        {
            Type laneType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.LaneBundle", true);
            Type validatorType = runtimeAssembly.GetType(
                "RoadRuntimeHost.Runtime.LaneContractValidator", true);
            MethodInfo requireMatch = validatorType.GetMethod(
                "RequireMatch", BindingFlags.Static | BindingFlags.Public);
            if (requireMatch == null || requireMatch.ReturnType != laneType.MakeArrayType())
                throw new InvalidOperationException(
                    "lane contract validator does not return the authoring bundle lanes");

            object bundleLane = NewLane(laneType, 3f);
            object catalogLane = NewLane(laneType, 3f);
            Array bundleLanes = Array.CreateInstance(laneType, 1);
            Array catalogLanes = Array.CreateInstance(laneType, 1);
            bundleLanes.SetValue(bundleLane, 0);
            catalogLanes.SetValue(catalogLane, 0);

            object selected = requireMatch.Invoke(
                null, new object[] { "ownership-road", bundleLanes, catalogLanes });
            if (!ReferenceEquals(selected, bundleLanes))
                throw new InvalidOperationException(
                    "matching catalog lanes replaced the authoring bundle lanes");

            laneType.GetField("Width").SetValue(catalogLane, 3.25f);
            bool mismatchRejected = false;
            try
            {
                requireMatch.Invoke(
                    null, new object[] { "ownership-road", bundleLanes, catalogLanes });
            }
            catch (TargetInvocationException error)
            {
                Exception inner = error.InnerException;
                mismatchRejected = inner != null
                    && inner.GetType().FullName
                        == "RoadRuntimeHost.Runtime.DiagnosticException"
                    && string.Equals(
                        (string)inner.GetType().GetField("EventName").GetValue(inner),
                        "lane_contract_mismatch",
                        StringComparison.Ordinal);
            }
            if (!mismatchRejected)
                throw new InvalidOperationException(
                    "catalog and bundle lane mismatch was not rejected");
        }

        private static object NewLane(Type laneType, float width)
        {
            object lane = Activator.CreateInstance(laneType, true);
            laneType.GetField("LaneId").SetValue(lane, "lane-main");
            laneType.GetField("Width").SetValue(lane, width);
            laneType.GetField("Direction").SetValue(lane, "FORWARD");
            laneType.GetField("LaneType").SetValue(lane, "VEHICLE");
            laneType.GetField("VehicleType").SetValue(lane, "CAR");
            laneType.GetField("AllowConnect").SetValue(lane, true);
            return lane;
        }
    }
}

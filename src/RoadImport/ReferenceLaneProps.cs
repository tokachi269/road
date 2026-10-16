using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoadBuilder.Import
{
    internal static class ReferenceLaneProps
    {
        // Capture before importing when the editor road is itself the template.
        public static NetInfo.Lane[][] CaptureModes(NetInfo basic)
        {
            NetInfo[] modes = Modes(basic);
            NetInfo.Lane[][] lanes = new NetInfo.Lane[modes.Length][];
            for (int i=0; i<modes.Length; ++i)
                lanes[i] = modes[i] == null ? null : modes[i].m_lanes;
            return lanes;
        }

        public static void ApplyDefaults(NetInfo target, NetInfo.Lane[] reference, bool[] explicitProps)
        {
            if (target == null || target.m_lanes == null || reference == null) return;
            for (int i=0; i<target.m_lanes.Length; ++i)
            {
                NetInfo.Lane lane = target.m_lanes[i];
                // XML arrays (including explicit empty arrays) are authoritative.
                if (lane == null || (explicitProps != null && i<explicitProps.Length && explicitProps[i])) continue;
                List<NetLaneProps.Prop> values = Copy(reference,lane.m_position,lane.m_laneType.ToString(),lane.m_vehicleType.ToString());
                if (values.Count == 0) continue;
                lane.m_laneProps = ScriptableObject.CreateInstance<NetLaneProps>();
                lane.m_laneProps.name = target.name + ".reference." + i;
                lane.m_laneProps.m_props = values.ToArray();
            }
        }

        public static NetInfo[] Modes(NetInfo basic)
        {
            RoadAI ai = basic == null ? null : basic.m_netAI as RoadAI;
            return new NetInfo[]{basic,ai==null?null:ai.m_elevatedInfo,ai==null?null:ai.m_bridgeInfo,
                ai==null?null:ai.m_slopeInfo,ai==null?null:ai.m_tunnelInfo};
        }

        public static List<NetLaneProps.Prop> Copy(
            NetInfo.Lane[] referenceLanes,
            float position, string laneType, string vehicleType)
        {
            List<NetLaneProps.Prop> result = new List<NetLaneProps.Prop>();
            bool vehicleLane = string.Equals(
                laneType, "Vehicle", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    vehicleType, "Car", StringComparison.OrdinalIgnoreCase);
            bool pedestrianLane = string.Equals(
                laneType, "Pedestrian", StringComparison.OrdinalIgnoreCase);
            if (!vehicleLane && !pedestrianLane) return result;

            NetInfo.Lane reference = FindReferenceLane(
                referenceLanes, position, vehicleLane);
            if (reference == null || reference.m_laneProps == null
                || reference.m_laneProps.m_props == null) return result;

            return Select(reference.m_laneProps.m_props, vehicleLane);
        }

        // Pure managed selection/clone boundary; obtaining the loaded prefab
        // and native NetLaneProps container remains a CS1 adapter concern.
        public static List<NetLaneProps.Prop> Select(NetLaneProps.Prop[] props, bool vehicleLane)
        {
            List<NetLaneProps.Prop> result = new List<NetLaneProps.Prop>();
            if (props == null) return result;
            foreach (NetLaneProps.Prop prop in props)
            {
                if (prop == null) continue;
                if (vehicleLane && IsRoadArrowProp(prop))
                    result.Add(CloneLaneProp(prop));
                else if (!vehicleLane && IsTrafficLightProp(prop))
                    result.Add(CloneLaneProp(prop));
            }
            return result;
        }

        private static NetInfo.Lane FindReferenceLane(
            NetInfo.Lane[] lanes,
            float targetPosition,
            bool vehicleLane)
        {
            NetInfo.Lane best = null;
            float bestDistance = float.MaxValue;
            foreach (NetInfo.Lane lane in lanes)
            {
                if (lane == null) continue;
                bool matches = vehicleLane
                    ? (lane.m_laneType & NetInfo.LaneType.Vehicle) != 0
                        && (lane.m_vehicleType & VehicleInfo.VehicleType.Car) != 0
                    : (lane.m_laneType & NetInfo.LaneType.Pedestrian) != 0;
                if (!matches || lane.m_laneProps == null) continue;
                float distance = Mathf.Abs(lane.m_position - targetPosition);
                if (distance >= bestDistance) continue;
                best = lane;
                bestDistance = distance;
            }
            return best;
        }

        private static bool IsRoadArrowProp(NetLaneProps.Prop prop)
        {
            PropInfo info = prop.m_prop ?? prop.m_finalProp;
            return info != null && info.name != null
                && info.name.StartsWith("Road Arrow ", StringComparison.Ordinal);
        }

        private static bool IsTrafficLightProp(NetLaneProps.Prop prop)
        {
            NetNode.Flags required = prop.m_startFlagsRequired | prop.m_endFlagsRequired;
            return (required & NetNode.Flags.TrafficLights) != 0;
        }

        private static NetLaneProps.Prop CloneLaneProp(NetLaneProps.Prop source)
        {
            NetLaneProps.Prop value = new NetLaneProps.Prop();
            value.m_flagsRequired = source.m_flagsRequired;
            value.m_flagsForbidden = source.m_flagsForbidden;
            value.m_startFlagsRequired = source.m_startFlagsRequired;
            value.m_startFlagsRequired2 = source.m_startFlagsRequired2;
            value.m_startFlagsForbidden = source.m_startFlagsForbidden;
            value.m_startFlagsForbidden2 = source.m_startFlagsForbidden2;
            value.m_endFlagsRequired = source.m_endFlagsRequired;
            value.m_endFlagsRequired2 = source.m_endFlagsRequired2;
            value.m_endFlagsForbidden = source.m_endFlagsForbidden;
            value.m_endFlagsForbidden2 = source.m_endFlagsForbidden2;
            value.m_colorMode = source.m_colorMode;
            value.m_prop = source.m_prop;
            value.m_tree = source.m_tree;
            value.m_position = source.m_position;
            value.m_angle = source.m_angle;
            value.m_segmentOffset = source.m_segmentOffset;
            value.m_repeatDistance = source.m_repeatDistance;
            value.m_minLength = source.m_minLength;
            value.m_cornerAngle = source.m_cornerAngle;
            value.m_probability = source.m_probability;
            value.m_upgradable = source.m_upgradable;
            value.m_disableRuined = source.m_disableRuined;
            value.m_finalProp = source.m_finalProp;
            value.m_finalTree = source.m_finalTree;
            return value;
        }
    }
}

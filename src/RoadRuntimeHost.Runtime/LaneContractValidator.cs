using System;
using System.Collections.Generic;
using System.Globalization;

namespace RoadRuntimeHost.Runtime
{
    internal static class LaneContractValidator
    {
        private const float Tolerance = 0.0001f;

        public static LaneBundle[] RequireMatch(string roadId, LaneBundle[] blenderLanes, LaneBundle[] catalogLanes)
        {
            LaneBundle[] blender = blenderLanes ?? new LaneBundle[0];
            LaneBundle[] catalog = catalogLanes ?? new LaneBundle[0];
            List<string> differences = new List<string>();
            if (blender.Length != catalog.Length)
                differences.Add("lane_count blender=" + blender.Length + " catalog=" + catalog.Length);

            int count = Math.Min(blender.Length, catalog.Length);
            for (int index = 0; index != count && differences.Count < 20; ++index)
            {
                LaneBundle left = blender[index];
                LaneBundle right = catalog[index];
                if (left == null || right == null)
                {
                    if (left != right) differences.Add("index=" + index + " lane blender=" + Value(left) + " catalog=" + Value(right));
                    continue;
                }
                string lane = !string.IsNullOrEmpty(left.LaneId) ? left.LaneId : "index-" + index;
                CompareText(differences, lane, "lane_id", left.LaneId, right.LaneId, false);
                CompareNumber(differences, lane, "position", left.Position, right.Position);
                CompareNumber(differences, lane, "width", left.Width, right.Width);
                CompareNumber(differences, lane, "vertical_offset", left.VerticalOffset, right.VerticalOffset);
                CompareNumber(differences, lane, "stop_offset", left.StopOffset, right.StopOffset);
                CompareNumber(differences, lane, "speed_limit", left.SpeedLimit, right.SpeedLimit);
                CompareText(differences, lane, "direction", left.Direction, right.Direction, true);
                CompareText(differences, lane, "lane_type", left.LaneType, right.LaneType, true);
                CompareText(differences, lane, "vehicle_type", left.VehicleType, right.VehicleType, true);
                if (left.AllowConnect != right.AllowConnect)
                    differences.Add(lane + ".allow_connect blender=" + left.AllowConnect + " catalog=" + right.AllowConnect);
            }

            if (differences.Count == 0) return blender;
            string detail = string.Join("; ", differences.ToArray());
            throw new DiagnosticException(
                "DATA_INVALID", "lane_contract_mismatch",
                "Blender bundle lanes and catalog lanes disagree for road " + roadId + ": " + detail);
        }

        private static void CompareNumber(List<string> differences, string lane, string field, float blender, float catalog)
        {
            if (differences.Count >= 20 || Math.Abs(blender - catalog) <= Tolerance) return;
            differences.Add(lane + "." + field + " blender=" + blender.ToString("R", CultureInfo.InvariantCulture)
                + " catalog=" + catalog.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void CompareText(List<string> differences, string lane, string field, string blender, string catalog, bool enumLike)
        {
            if (differences.Count >= 20) return;
            string left = enumLike ? NormalizeEnum(blender) : (blender ?? string.Empty).Trim();
            string right = enumLike ? NormalizeEnum(catalog) : (catalog ?? string.Empty).Trim();
            if (!string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                differences.Add(lane + "." + field + " blender=" + Value(blender) + " catalog=" + Value(catalog));
        }

        private static string NormalizeEnum(string value)
        {
            return (value ?? string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).Trim();
        }

        private static string Value(object value)
        {
            return value == null ? "<null>" : "'" + value + "'";
        }
    }
}

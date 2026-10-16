using System;
using System.IO;

namespace RoadRuntimeHost.Runtime
{
    // Consumer of offline PNGs. No drawing, font or overlay decisions here.
    internal static class RoadIconAssets
    {
        public static readonly string[] States = { "", "Hovered", "Pressed", "Focused", "Disabled" };

        public static bool Counts(LaneBundle[] lanes, out int backward, out int forward)
        {
            backward = forward = 0;
            if (lanes == null) return false;
            foreach (LaneBundle lane in lanes)
            {
                if (lane == null) return false;
                if (Equal(lane.LaneType, "Pedestrian") || Equal(lane.LaneType, "Parking")) continue;
                if (!Equal(lane.LaneType, "Vehicle") || !Equal(lane.VehicleType, "Car")) return false;
                if (Equal(lane.Direction, "Backward")) ++backward;
                else if (Equal(lane.Direction, "Forward")) ++forward;
                else return false;
            }
            return backward <= 4 && forward <= 4 && backward + forward > 0;
        }

        private static bool Equal(string value, string expected)
        { return string.Equals((value ?? string.Empty).Trim(), expected, StringComparison.OrdinalIgnoreCase); }

        public static string Name(int backward, int forward) { return "RoadTopology_" + backward + "_" + forward; }

        public static byte[] Read(string root, int backward, int forward)
        {
            if (backward < 0 || backward > 4 || forward < Math.Max(1, backward) || forward > 4)
                throw new ArgumentException("Unsupported icon topology");
            string path = Path.Combine(Path.Combine(root, "icons"), backward + "-" + forward + ".png");
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Toolbar PNG exceeds 1 MiB");
            byte[] png = File.ReadAllBytes(path);
            byte[] signature = {137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82};
            if (png.Length < 33) throw new InvalidDataException("Truncated toolbar PNG");
            for (int i = 0; i < signature.Length; ++i)
                if (png[i] != signature[i]) throw new InvalidDataException("Invalid toolbar PNG header");
            for (int i = 16; i < 24; ++i)
                if (png[i] != (i == 19 || i == 23 ? 128 : 0))
                    throw new InvalidDataException("Toolbar PNG must be 128x128");
            return png;
        }
    }
}

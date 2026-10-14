using System;
using System.IO;

namespace RoadRuntimeHost.Runtime
{
    // UI representation only. Physical geometry, widths, road level and
    // marking choices never enter this renderer. Also linked by PNG exporter.
    internal static class RoadIconRaster
    {
        public const int Size = 128;
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
        private static bool Equal(string value, string expected) { return string.Equals((value ?? string.Empty).Trim(), expected, StringComparison.OrdinalIgnoreCase); }
        public static void RequireOverlayPng(byte[] encoded)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (encoded == null || encoded.Length < 33 || encoded.Length > 1024 * 1024)
                throw new InvalidDataException("Toolbar overlay must be a 128x128 PNG under 1 MiB");
            for (int i = 0; i < signature.Length; ++i)
                if (encoded[i] != signature[i]) throw new InvalidDataException("Toolbar overlay is not PNG");
            if (encoded[12] != 73 || encoded[13] != 72 || encoded[14] != 68 || encoded[15] != 82)
                throw new InvalidDataException("Toolbar overlay has no leading IHDR");
            for (int i = 16; i < 24; ++i)
                if (encoded[i] != (i == 19 || i == 23 ? 128 : 0))
                    throw new InvalidDataException("Toolbar overlay must be 128x128 PNG");
        }
        public static string Name(int backward, int forward) { return "RoadTopology_" + backward + "_" + forward; }
        public static string Label(int backward, int forward)
        {
            Require(backward, forward);
            return backward == 0 || forward == 0 ? "→" + (backward + forward) : backward + "+" + forward;
        }
        public static byte[] Render(int backward, int forward, byte[] overlay)
        {
            Require(backward, forward);
            if (overlay != null && overlay.Length != Size * Size * 4) throw new ArgumentException("Overlay must be 128x128 RGBA", "overlay");
            byte[] image = new byte[Size * Size * 4];
            // Straight-alpha RGBA, top-left origin for PNG and overlay.
            Fill(image, 2, 2, 126, 126, 30, 37, 44);
            Fill(image, 2, 94, 126, 126, 17, 25, 34);
            int total = backward + forward;
            int width = Math.Min(112, 24 + 12 * total), left = (Size - width) / 2, right = left + width;
            Fill(image, left, 9, right, 88, 74, 81, 87);
            Fill(image, left + 4, 9, right - 4, 88, 45, 51, 57);
            double laneWidth = (width - 8.0) / total;
            for (int i = 1; i < total; ++i)
            {
                int x = (int)Math.Round(left + 4 + i * laneWidth);
                if (backward != 0 && forward != 0 && i == backward)
                    Fill(image, x - 1, 9, x + 2, 88, 246, 195, 67);
                else
                    for (int y = 12; y < 86; y += 18) Fill(image, x - 1, y, x + 1, Math.Min(y + 10, 86), 171, 182, 191);
            }
            for (int i = 0; i < total; ++i)
            {
                int x = (int)Math.Round(left + 4 + (i + 0.5) * laneWidth);
                bool down = backward != 0 && forward != 0 && i < backward;
                Arrow(image, x, down, Math.Min(6, (int)(laneWidth * 0.35)));
            }
            string label = Label(backward, forward);
            int textWidth = label.Length * 18 - 3;
            for (int i = 0; i < label.Length; ++i) Glyph(image, label[i], (Size - textWidth) / 2 + i * 18, 100);
            if (overlay != null) Composite(image, overlay);
            return image;
        }

        public static void Composite(byte[] image, byte[] overlay)
        {
            if (image == null || overlay == null || image.Length != overlay.Length || image.Length % 4 != 0)
                throw new ArgumentException("RGBA images must have equal dimensions");
            for (int i = 0; i < image.Length; i += 4)
            {
                int sourceA = overlay[i + 3], destA = image[i + 3];
                int outA = sourceA * 255 + destA * (255 - sourceA);
                for (int c = 0; c < 3; ++c)
                    image[i + c] = outA == 0 ? (byte)0 : (byte)((overlay[i + c] * sourceA * 255
                        + image[i + c] * destA * (255 - sourceA) + outA / 2) / outA);
                image[i + 3] = (byte)((outA + 127) / 255);
            }
        }
        private static void Require(int backward, int forward)
        {
            if (backward < 0 || backward > 4 || forward < 0 || forward > 4 || backward + forward == 0)
                throw new ArgumentException("Icon supports 1 to 4 lanes in each direction");
        }
        private static void Fill(byte[] image, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(Size, y1); ++y)
                for (int x = Math.Max(0, x0); x < Math.Min(Size, x1); ++x)
                {
                    int p = (y * Size + x) * 4;
                    image[p] = r; image[p + 1] = g; image[p + 2] = b; image[p + 3] = 255;
                }
        }
        private static void Arrow(byte[] image, int x, bool down, int halfWidth)
        {
            int tip = down ? 70 : 27, sign = down ? -1 : 1;
            for (int row = 0; row < 10; ++row)
            {
                int half = Math.Max(1, halfWidth * row / 9);
                Fill(image, x - half, tip + sign * row, x + half + 1, tip + sign * row + 1, 244, 247, 250);
            }
            int y0 = down ? 27 : 37, y1 = down ? 61 : 71;
            Fill(image, x - 1, y0, x + 2, y1, 244, 247, 250);
        }
        private static void Glyph(byte[] image, char value, int x, int y)
        {
            string[] rows;
            switch (value)
            {
                case '1': rows = new string[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" }; break;
                case '2': rows = new string[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" }; break;
                case '3': rows = new string[] { "11110", "00001", "00001", "01110", "00001", "00001", "11110" }; break;
                case '4': rows = new string[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" }; break;
                case '+': rows = new string[] { "00000", "00100", "00100", "11111", "00100", "00100", "00000" }; break;
                case '→': rows = new string[] { "00000", "00100", "00010", "11111", "00010", "00100", "00000" }; break;
                default: throw new ArgumentException("Unsupported icon glyph");
            }
            for (int row = 0; row < 7; ++row)
                for (int col = 0; col < 5; ++col)
                    if (rows[row][col] == '1') Fill(image, x + col * 3, y + row * 3, x + col * 3 + 3, y + row * 3 + 3, 246, 248, 251);
        }
    }
}

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// Source-linked game module; independent of the preview Host assembly.
namespace RoadRuntimeHost.Runtime
{
    internal sealed class SharedHeightFamily
    {
        // AR 2-bit states: default D30, D0, D30->D0, D0->D30.
        public readonly string Identity;
        public readonly PlacementVariantSnapshot Depressed, Flush;
        public readonly PlacementHeightTransitionSnapshot Rise, Fall;
        public const int NativeModeCount = 5;

        public SharedHeightFamily(ResolvedHeightTransition rise, ResolvedHeightTransition fall)
        {
            PlacementVariantLibrary.RequireHeightTransition(rise.Segment, rise.Start, rise.End);
            PlacementVariantLibrary.RequireHeightTransition(fall.Segment, fall.Start, fall.End);
            if (rise.Start.Section.RoadwayZ != -0.3f || rise.End.Section.RoadwayZ != 0f
                || fall.Start.Identity != rise.End.Identity || fall.End.Identity != rise.Start.Identity)
                throw new InvalidDataException("Shared height family requires the two opposite compiled transitions");
            Depressed = rise.Start; Flush = rise.End; Rise = rise.Segment; Fall = fall.Segment;
            // These are shared native references, not extra physical presets.
            if (Depressed.NodeReferencePavementWidth != Flush.NodeReferencePavementWidth
                || Depressed.NodeMinCornerOffset != Flush.NodeMinCornerOffset || Depressed.TemplateName != Flush.TemplateName)
                throw new InvalidDataException("Height states disagree on shared native parameters");
            using (SHA256 hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes("rr-shared-height-v1\n" + Depressed.Identity + "\n"
                    + Flush.Identity + "\n" + Rise.Identity + "\n" + Fall.Identity));
                StringBuilder text = new StringBuilder(64);
                foreach (byte value in bytes) text.Append(value.ToString("x2"));
                Identity = text.ToString();
            }
        }

        public static bool EndpointFlush(int code, bool start, bool turned)
        {
            if (code < 0 || code > 3) throw new InvalidDataException("Unknown shared height state");
            if (code < 2) return code == 1;
            bool authoredStart = start != turned;
            return code == 2 ? !authoredStart : authoredStart;
        }
        public float[] Offsets(int code, bool start)
        {
            if (code < 0 || code > 3) throw new InvalidDataException("Unknown shared height state");
            LaneBundle[] lanes = EndpointFlush(code, start, false) ? Flush.Lanes : Depressed.Lanes;
            float[] result = new float[lanes.Length];
            for (int i = 0; i < result.Length; ++i) result[i] = lanes[i].VerticalOffset;
            return result;
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using ColossalFramework.Math;
using UnityEngine;

namespace RoadRuntimeHost.ContractSmoke
{
    internal static class SegmentLaneIsolationContract
    {
        private delegate float ApplyHeights(NetLane[] buffer, ushort segmentId, uint firstLane,
            NetInfo.Lane[] shared, float[] start, float[] end, bool turned);

        public static void Validate(Assembly assembly)
        {
            Type kernel = assembly.GetType("RoadRuntimeHost.Runtime.HeightTransitionKernel", true);
            MethodInfo method = kernel.GetMethod("ApplyLaneHeights", BindingFlags.Static | BindingFlags.Public);
            if (method == null) throw new InvalidOperationException("Segment-owned lane adapter is missing");
            ApplyHeights apply = (ApplyHeights)Delegate.CreateDelegate(typeof(ApplyHeights), method);
            NetInfo.Lane[] shared = {
                new NetInfo.Lane { m_position = -3f, m_width = 3f, m_verticalOffset = -0.3f },
                new NetInfo.Lane { m_position = 4f, m_width = 2f, m_verticalOffset = 0f }
            };
            float[] start = { -0.3f, 0f }, end = { 0f, 0f };
            foreach (bool turned in new bool[] { false, true })
            {
                NetLane[] buffer = Buffer(16);
                Bezier3 beforeA = buffer[1].m_bezier, beforeB = buffer[7].m_bezier;
                apply(buffer, 10, 1, shared, start, end, turned);
                AssertProfile(buffer[1].m_bezier, beforeA, turned);
                AssertSame(buffer[2].m_bezier, RoadCurve(0f), "Pedestrian lane changed");
                AssertSame(buffer[7].m_bezier, beforeB, "Another segment sharing the road definition changed");
                AssertSame(buffer[8].m_bezier, RoadCurve(0f), "Another segment's pedestrian lane changed");
                if (buffer[7].m_nextLane != 8 || buffer[7].m_segment != 20 || buffer[7].m_length != 123f)
                    throw new InvalidOperationException("Another segment's lane state changed");
                if (shared[0].m_position != -3f || shared[0].m_width != 3f || shared[0].m_verticalOffset != -0.3f
                    || shared[1].m_position != 4f || shared[1].m_width != 2f || shared[1].m_verticalOffset != 0f
                    || start[0] != -0.3f || start[1] != 0f || end[0] != 0f || end[1] != 0f)
                    throw new InvalidOperationException("Shared definition or compiled input changed");
                // Simulate vanilla rebuilding the native curves. Reapply from
                // that fresh baseline; do not test idempotence on an already
                // corrected curve, which is not the hooked call contract.
                buffer[1].m_bezier = beforeA;
                buffer[2].m_bezier = RoadCurve(0f);
                apply(buffer, 10, 1, shared, start, end, turned);
                AssertProfile(buffer[1].m_bezier, beforeA, turned);
            }
            // Same-level instances need no third, global road height.
            foreach (float level in new float[] { -0.3f, 0f })
            {
                NetLane[] buffer = Buffer(16);
                apply(buffer, 10, 1, shared, new float[] { level, 0f }, new float[] { level, 0f }, false);
                AssertNear(buffer[1].m_bezier.a.y, 5f + level + 0.3f);
                AssertNear(buffer[1].m_bezier.d.y, 8f + level + 0.3f);
            }
            for (int fault = 0; fault < 9; ++fault)
            {
                NetLane[] buffer = Buffer(16);
                float[] badStart = { -0.3f, 0f }, badEnd = { 0f, 0f };
                if (fault == 0) buffer[2].m_segment = 20; // would write foreign lane
                if (fault == 1) buffer[1].m_nextLane = 0; // partial chain
                if (fault == 2) buffer[2].m_nextLane = 7; // extra foreign tail
                if (fault == 3) buffer[2].m_nextLane = 1; // cycle
                if (fault == 4) buffer[1].m_nextLane = 99; // outside buffer
                if (fault == 5) badEnd[1] = float.NaN; // invalid later lane
                if (fault == 6) badStart = new float[] { -0.3f }; // wrong topology
                if (fault == 7) badStart[1] = float.PositiveInfinity;
                if (fault == 8) { badStart[1] = float.MaxValue; badEnd[1] = -float.MaxValue; }
                NetLane[] before = (NetLane[])buffer.Clone();
                bool rejected = false;
                try { apply(buffer, 10, 1, shared, badStart, badEnd, false); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Malformed lane chain or input was accepted: " + fault);
                for (int i = 0; i < buffer.Length; ++i)
                {
                    AssertSame(buffer[i].m_bezier, before[i].m_bezier, "Rejection partially changed a lane");
                    if (buffer[i].m_length != before[i].m_length || buffer[i].m_curve != before[i].m_curve)
                        throw new InvalidOperationException("Rejection partially changed lane length/curve");
                }
            }
            // The same two owned lanes in a full-capacity city buffer. The
            // unrelated high-ID entry catches broad writes independently of
            // any production iteration counter.
            NetLane[] city = Buffer(262144);
            city[262143].m_segment = 30;
            city[262143].m_bezier = RoadCurve(11f);
            apply(city, 10, 1, shared, start, end, false);
            AssertSame(city[262143].m_bezier, RoadCurve(11f), "High-ID unrelated lane changed");
            const int iterations = 100000;
            foreach (int capacity in new int[] { 16, 262144 })
            {
                NetLane[] buffer = Buffer(capacity);
                for (int i = 0; i < 1000; ++i) {
                    buffer[1].m_bezier = RoadCurve(5f);
                    apply(buffer, 10, 1, shared, start, end, false);
                }
                Stopwatch timer = Stopwatch.StartNew();
                for (int i = 0; i < iterations; ++i) {
                    buffer[1].m_bezier = RoadCurve(5f);
                    apply(buffer, 10, 1, shared, start, end, false);
                }
                timer.Stop();
                Console.WriteLine("SEGMENT_LANE_BENCH capacity={0} lanes=2 calls={1} elapsed_ms={2:F3}",
                    capacity, iterations, timer.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine("SEGMENT_LANE_ISOLATION_OK");
        }

        private static NetLane[] Buffer(int capacity)
        {
            NetLane[] lanes = new NetLane[capacity];
            lanes[1].m_segment = 10; lanes[1].m_nextLane = 2; lanes[1].m_bezier = RoadCurve(5f);
            lanes[2].m_segment = 10; lanes[2].m_bezier = RoadCurve(0f);
            lanes[7].m_segment = 20; lanes[7].m_nextLane = 8; lanes[7].m_bezier = RoadCurve(5f);
            lanes[7].m_length = 123f;
            lanes[8].m_segment = 20; lanes[8].m_bezier = RoadCurve(0f);
            return lanes;
        }
        private static Bezier3 RoadCurve(float y)
        {
            return new Bezier3(new Vector3(1f, y, 0f), new Vector3(2f, y+1f, 64f/3f),
                new Vector3(3f, y+2f, 128f/3f), new Vector3(4f, y+3f, 64f));
        }
        private static void AssertProfile(Bezier3 result, Bezier3 baseline, bool turned)
        {
            Vector3[] actual = { result.a, result.b, result.c, result.d };
            Vector3[] source = { baseline.a, baseline.b, baseline.c, baseline.d };
            float[] offsets = turned ? new float[] { 0.3f, 0.2f, 0.1f, 0f } : new float[] { 0f, 0.1f, 0.2f, 0.3f };
            for (int i = 0; i < 4; ++i) {
                AssertNear(actual[i].x, source[i].x); AssertNear(actual[i].z, source[i].z);
                AssertNear(actual[i].y, source[i].y + offsets[i]);
            }
        }
        private static void AssertSame(Bezier3 actual, Bezier3 expected, string message)
        {
            if (!actual.a.Equals(expected.a) || !actual.b.Equals(expected.b)
                || !actual.c.Equals(expected.c) || !actual.d.Equals(expected.d))
                throw new InvalidOperationException(message);
        }
        private static void AssertNear(float actual, float expected)
        {
            if (float.IsNaN(actual) || Math.Abs(actual-expected) > 0.0001f)
                throw new InvalidOperationException("Expected lane coordinate " + expected + ", got " + actual);
        }
    }
}

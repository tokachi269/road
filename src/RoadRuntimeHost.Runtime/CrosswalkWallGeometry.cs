using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal static class CrosswalkWallGeometry
    {
        private const float Epsilon = 0.00001f;

        public static bool TryGetSpan(
            Vector3 trajectoryStart,
            Vector3 trajectoryEnd,
            Vector3 firstWallPoint,
            Vector3 lastWallPoint,
            Vector3 wallDirection,
            out float startT,
            out float endT)
        {
            Vector3 trajectoryDirection = trajectoryEnd - trajectoryStart;
            trajectoryDirection.y = 0f;
            wallDirection.y = 0f;
            if (trajectoryDirection.sqrMagnitude < Epsilon
                || wallDirection.sqrMagnitude < Epsilon)
            {
                startT = 0f;
                endT = 1f;
                return false;
            }

            float firstT;
            float lastT;
            if (!TryIntersectXZ(
                    trajectoryStart, trajectoryDirection,
                    firstWallPoint, wallDirection, out firstT)
                || !TryIntersectXZ(
                    trajectoryStart, trajectoryDirection,
                    lastWallPoint, wallDirection, out lastT))
            {
                startT = 0f;
                endT = 1f;
                return false;
            }

            startT = Mathf.Min(firstT, lastT);
            endT = Mathf.Max(firstT, lastT);
            return endT - startT >= Epsilon;
        }

        private static bool TryIntersectXZ(
            Vector3 trajectoryStart,
            Vector3 trajectoryDirection,
            Vector3 wallPoint,
            Vector3 wallDirection,
            out float trajectoryT)
        {
            float denominator = CrossXZ(trajectoryDirection, wallDirection);
            if (Mathf.Abs(denominator) < Epsilon)
            {
                trajectoryT = 0f;
                return false;
            }

            trajectoryT = CrossXZ(wallPoint - trajectoryStart, wallDirection)
                / denominator;
            return !float.IsNaN(trajectoryT) && !float.IsInfinity(trajectoryT);
        }

        private static float CrossXZ(Vector3 left, Vector3 right)
        {
            return left.x * right.z - left.z * right.x;
        }
    }
}

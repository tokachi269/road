using UnityEngine;

namespace RoadRuntimeHost.Runtime
{
    internal static class CrosswalkTrajectoryGeometry
    {
        private const float ParallelEpsilon = 0.00001f;

        public static Color32 DecodeSourceColor(Color renderedColor, bool hasTexture)
        {
            if (hasTexture) return (Color32)renderedColor;
            return (Color32)new Color(
                Mathf.Pow(Mathf.Clamp01(renderedColor.r), 0.25f),
                Mathf.Pow(Mathf.Clamp01(renderedColor.g), 0.25f),
                Mathf.Pow(Mathf.Clamp01(renderedColor.b), 0.25f),
                Mathf.Sqrt(Mathf.Clamp01(renderedColor.a)));
        }

        public static bool ExtendPolygonToBoundaries(
            Vector3[] points,
            Vector3 center,
            Vector3 firstBoundaryPoint,
            Vector3 lastBoundaryPoint,
            Vector3 movementDirection,
            Vector3 boundaryDirection)
        {
            if (points == null || points.Length < 3) return false;
            movementDirection.y = 0f;
            boundaryDirection.y = 0f;
            if (movementDirection.sqrMagnitude < ParallelEpsilon
                || boundaryDirection.sqrMagnitude < ParallelEpsilon) return false;
            movementDirection.Normalize();
            boundaryDirection.Normalize();

            float minimum = float.MaxValue;
            float maximum = float.MinValue;
            for (int index = 0; index < points.Length; ++index)
            {
                float lateral = Vector3.Dot(points[index] - center, movementDirection);
                minimum = Mathf.Min(minimum, lateral);
                maximum = Mathf.Max(maximum, lateral);
            }
            if (maximum - minimum < ParallelEpsilon) return false;

            float middle = (minimum + maximum) * 0.5f;
            bool changed = false;
            for (int index = 0; index < points.Length; ++index)
            {
                Vector3 point = points[index];
                float lateral = Vector3.Dot(point - center, movementDirection);
                bool firstSide = lateral <= middle;
                Vector3 boundaryPoint = firstSide ? firstBoundaryPoint : lastBoundaryPoint;
                float distance;
                if (!TryIntersectXZ(point, movementDirection, boundaryPoint, boundaryDirection, out distance))
                    continue;

                if ((firstSide && distance >= 0f) || (!firstSide && distance <= 0f))
                    continue;

                points[index] = point + movementDirection * distance;
                changed = true;
            }
            return changed;
        }

        private static bool TryIntersectXZ(
            Vector3 trajectoryStart,
            Vector3 trajectoryDirection,
            Vector3 boundaryPoint,
            Vector3 boundaryDirection,
            out float trajectoryT)
        {
            float denominator = CrossXZ(trajectoryDirection, boundaryDirection);
            if (Mathf.Abs(denominator) < ParallelEpsilon)
            {
                trajectoryT = 0f;
                return false;
            }

            trajectoryT = CrossXZ(boundaryPoint - trajectoryStart, boundaryDirection) / denominator;
            return !float.IsNaN(trajectoryT) && !float.IsInfinity(trajectoryT);
        }

        private static float CrossXZ(Vector3 left, Vector3 right)
        {
            return left.x * right.z - left.z * right.x;
        }
    }
}

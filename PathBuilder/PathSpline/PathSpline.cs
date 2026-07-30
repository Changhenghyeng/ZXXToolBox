using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace ZXXTools.PathBuilder
{
    public enum PathBuildMode
    {
        Mountain,
        River
    }

    [Serializable]
    public sealed class PathKnot
    {
        public Vector3 position;
        public Vector3 tangentIn = Vector3.left * 2f;
        public Vector3 tangentOut = Vector3.right * 2f;
        public bool brokenTangents;
        [Min(0.01f)] public float width = 4f;

        public PathKnot()
        {
        }

        public PathKnot(Vector3 position)
        {
            this.position = position;
        }

        public PathKnot Clone()
        {
            return new PathKnot
            {
                position = position,
                tangentIn = tangentIn,
                tangentOut = tangentOut,
                brokenTangents = brokenTangents,
                width = width
            };
        }
    }

    [Serializable]
    public sealed class MountainPrefabEntry
    {
        public GameObject prefab;
        [Min(0f)] public float weight = 1f;
    }

    public sealed class PathSpline : ScriptableObject
    {
        [SerializeField, HideInInspector]
        private Transform anchor;

        [Header("Spline")]
        public PathBuildMode mode = PathBuildMode.Mountain;
        public bool closed;
        public bool autoRebuild = true;
        [Range(4, 64)] public int samplesPerCurve = 24;
        public List<PathKnot> knots = new List<PathKnot>();

        [Header("Surface")]
        public bool projectToSurface;
        public LayerMask surfaceMask = ~0;
        [Min(0.1f)] public float rayHeight = 500f;
        public float surfaceOffset = 0.02f;

        [Header("Mountain")]
        public List<MountainPrefabEntry> mountainPrefabs = new List<MountainPrefabEntry>();
        public int randomSeed = 12345;
        public Vector2 spacingRange = new Vector2(2f, 4f);
        [Min(0f)] public float brushWidth = 2f;
        [Range(0f, 1f)] public float density = 1f;
        public Vector2 horizontalScaleRange = new Vector2(0.85f, 1.15f);
        public Vector2 verticalScaleRange = new Vector2(0.85f, 1.15f);
        public Vector2 yawRange = new Vector2(0f, 360f);
        public bool alignToSpline;

        [Header("River")]
        public Material riverMaterial;
        [Min(0.05f)] public float riverSegmentLength = 0.75f;
        [FormerlySerializedAs("uvMetersPerTile")]
        [Min(0.01f)] public float riverUvRepeatLength = 4f;
        public bool generateCollider;

        [SerializeField, HideInInspector]
        private Transform generatedRoot;

        public Transform GeneratedRoot
        {
            get { return generatedRoot; }
            set { generatedRoot = value; }
        }

        public Transform Anchor
        {
            get { return anchor; }
            set { anchor = value; }
        }

        public int SegmentCount
        {
            get
            {
                if (knots == null || knots.Count < 2)
                    return 0;
                return closed ? knots.Count : knots.Count - 1;
            }
        }

        private void OnValidate()
        {
            samplesPerCurve = Mathf.Clamp(samplesPerCurve, 4, 64);
            rayHeight = Mathf.Max(0.1f, rayHeight);
            riverSegmentLength = Mathf.Max(0.05f, riverSegmentLength);
            riverUvRepeatLength = Mathf.Max(0.01f, riverUvRepeatLength);
            brushWidth = Mathf.Max(0f, brushWidth);
            density = Mathf.Clamp01(density);
            spacingRange = OrderedPositiveRange(spacingRange, 0.05f);
            horizontalScaleRange = OrderedPositiveRange(horizontalScaleRange, 0.01f);
            verticalScaleRange = OrderedPositiveRange(verticalScaleRange, 0.01f);

            if (knots == null)
                knots = new List<PathKnot>();

            for (int i = 0; i < knots.Count; i++)
            {
                if (knots[i] == null)
                    knots[i] = new PathKnot();
                knots[i].width = Mathf.Max(0.01f, knots[i].width);
            }
        }

        public void EnsureDefaults()
        {
            if (knots == null)
                knots = new List<PathKnot>();
        }

        public int AppendKnot(Vector3 localPosition)
        {
            if (knots == null)
                knots = new List<PathKnot>();

            PathKnot knot = new PathKnot(localPosition);
            if (knots.Count > 0)
            {
                PathKnot previous = knots[knots.Count - 1];
                Vector3 direction = localPosition - previous.position;
                float length = direction.magnitude;
                if (length > 0.001f)
                {
                    Vector3 tangent = direction.normalized * Mathf.Max(0.25f, length / 3f);
                    previous.tangentOut = tangent;
                    if (!previous.brokenTangents)
                        previous.tangentIn = -tangent;
                    knot.tangentIn = -tangent;
                    knot.tangentOut = tangent;
                }
                knot.width = previous.width;
            }

            knots.Add(knot);
            return knots.Count - 1;
        }

        public int InsertAfter(int index)
        {
            EnsureDefaults();
            if (knots.Count == 0)
                return AppendKnot(Vector3.zero);
            index = Mathf.Clamp(index, 0, knots.Count - 1);

            int nextIndex = index + 1;
            if (nextIndex >= knots.Count)
            {
                if (!closed)
                {
                    Vector3 direction = knots[index].tangentOut;
                    if (direction.sqrMagnitude < 0.001f)
                        direction = Vector3.right * 3f;
                    return AppendKnot(knots[index].position + direction.normalized * 5f);
                }
                nextIndex = 0;
            }

            PathKnot current = knots[index];
            PathKnot next = knots[nextIndex];
            Vector3 position = PathSplineUtility.EvaluatePosition(current, next, 0.5f);
            PathKnot inserted = new PathKnot(position)
            {
                width = Mathf.Lerp(current.width, next.width, 0.5f)
            };

            int insertIndex = index + 1;
            knots.Insert(insertIndex, inserted);
            SmoothKnot(insertIndex);
            SmoothKnot(index);
            SmoothKnot((insertIndex + 1) % knots.Count);
            return insertIndex;
        }

        public void RemoveKnot(int index)
        {
            if (knots == null || knots.Count <= 2)
                return;
            if (index < 0 || index >= knots.Count)
                return;
            knots.RemoveAt(index);
        }

        public void Reverse()
        {
            if (knots == null)
                return;

            knots.Reverse();
            for (int i = 0; i < knots.Count; i++)
            {
                Vector3 oldIn = knots[i].tangentIn;
                knots[i].tangentIn = knots[i].tangentOut;
                knots[i].tangentOut = oldIn;
            }
        }

        public void SmoothKnot(int index)
        {
            if (knots == null || knots.Count < 2 || index < 0 || index >= knots.Count)
                return;

            int previousIndex = index - 1;
            int nextIndex = index + 1;

            if (closed)
            {
                previousIndex = (previousIndex + knots.Count) % knots.Count;
                nextIndex %= knots.Count;
            }
            else
            {
                previousIndex = Mathf.Max(0, previousIndex);
                nextIndex = Mathf.Min(knots.Count - 1, nextIndex);
            }

            PathKnot knot = knots[index];
            Vector3 previousDelta = knot.position - knots[previousIndex].position;
            Vector3 nextDelta = knots[nextIndex].position - knot.position;

            Vector3 direction;
            if (!closed && index == 0)
                direction = nextDelta.normalized;
            else if (!closed && index == knots.Count - 1)
                direction = previousDelta.normalized;
            else
                direction = (previousDelta.normalized + nextDelta.normalized).normalized;

            if (direction.sqrMagnitude < 0.001f)
                direction = Vector3.right;

            knot.tangentIn = -direction * Mathf.Max(0.25f, previousDelta.magnitude / 3f);
            knot.tangentOut = direction * Mathf.Max(0.25f, nextDelta.magnitude / 3f);
            knot.brokenTangents = false;
        }

        private static Vector2 OrderedPositiveRange(Vector2 range, float minimum)
        {
            float min = Mathf.Max(minimum, Mathf.Min(range.x, range.y));
            float max = Mathf.Max(min, Mathf.Max(range.x, range.y));
            return new Vector2(min, max);
        }
    }

    public struct PathSample
    {
        public Vector3 position;
        public Vector3 tangent;
        public float width;
        public float distance;
    }

    public static class PathSplineUtility
    {
        public static Vector3 EvaluatePosition(
            PathKnot start,
            PathKnot end,
            float t)
        {
            t = Mathf.Clamp01(t);
            float oneMinusT = 1f - t;
            Vector3 p0 = start.position;
            Vector3 p1 = start.position + start.tangentOut;
            Vector3 p2 = end.position + end.tangentIn;
            Vector3 p3 = end.position;
            return oneMinusT * oneMinusT * oneMinusT * p0
                + 3f * oneMinusT * oneMinusT * t * p1
                + 3f * oneMinusT * t * t * p2
                + t * t * t * p3;
        }

        public static Vector3 EvaluateTangent(
            PathKnot start,
            PathKnot end,
            float t)
        {
            t = Mathf.Clamp01(t);
            float oneMinusT = 1f - t;
            Vector3 p0 = start.position;
            Vector3 p1 = start.position + start.tangentOut;
            Vector3 p2 = end.position + end.tangentIn;
            Vector3 p3 = end.position;
            Vector3 tangent =
                3f * oneMinusT * oneMinusT * (p1 - p0)
                + 6f * oneMinusT * t * (p2 - p1)
                + 3f * t * t * (p3 - p2);
            if (tangent.sqrMagnitude < 0.000001f)
                tangent = p3 - p0;
            return tangent;
        }

        public static float EvaluateWidth(
            PathKnot start,
            PathKnot end,
            float t)
        {
            t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            return Mathf.Lerp(start.width, end.width, t);
        }

        public static List<PathSample> BuildArcTable(PathSpline builder)
        {
            List<PathSample> samples = new List<PathSample>();
            if (builder == null || builder.SegmentCount == 0)
                return samples;

            float distance = 0f;
            Vector3 previousPosition = Vector3.zero;
            int resolution = Mathf.Clamp(builder.samplesPerCurve, 4, 64);

            for (int segmentIndex = 0; segmentIndex < builder.SegmentCount; segmentIndex++)
            {
                int nextIndex = (segmentIndex + 1) % builder.knots.Count;
                PathKnot start = builder.knots[segmentIndex];
                PathKnot end = builder.knots[nextIndex];
                int firstStep = segmentIndex == 0 ? 0 : 1;

                for (int step = firstStep; step <= resolution; step++)
                {
                    float t = step / (float)resolution;
                    Vector3 localPosition = EvaluatePosition(start, end, t);
                    Vector3 localTangent = EvaluateTangent(start, end, t);
                    if (!builder.Anchor)
                        return samples;
                    Vector3 worldPosition = builder.Anchor.TransformPoint(localPosition);
                    Vector3 worldTangent = builder.Anchor.TransformVector(localTangent).normalized;

                    if (samples.Count > 0)
                        distance += Vector3.Distance(previousPosition, worldPosition);

                    samples.Add(new PathSample
                    {
                        position = worldPosition,
                        tangent = worldTangent,
                        width = EvaluateWidth(start, end, t),
                        distance = distance
                    });
                    previousPosition = worldPosition;
                }
            }

            return samples;
        }

        public static bool TryEvaluateAtDistance(
            IList<PathSample> arcTable,
            float distance,
            out PathSample result)
        {
            result = default(PathSample);
            if (arcTable == null || arcTable.Count == 0)
                return false;
            if (arcTable.Count == 1)
            {
                result = arcTable[0];
                return true;
            }

            float totalLength = arcTable[arcTable.Count - 1].distance;
            distance = Mathf.Clamp(distance, 0f, totalLength);

            int low = 0;
            int high = arcTable.Count - 1;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (arcTable[middle].distance < distance)
                    low = middle + 1;
                else
                    high = middle;
            }

            int upperIndex = Mathf.Clamp(low, 1, arcTable.Count - 1);
            int lowerIndex = upperIndex - 1;
            PathSample lower = arcTable[lowerIndex];
            PathSample upper = arcTable[upperIndex];
            float span = upper.distance - lower.distance;
            float t = span > 0.000001f ? (distance - lower.distance) / span : 0f;

            result.position = Vector3.Lerp(lower.position, upper.position, t);
            result.tangent = Vector3.Lerp(lower.tangent, upper.tangent, t).normalized;
            result.width = Mathf.Lerp(lower.width, upper.width, t);
            result.distance = distance;
            return true;
        }
    }
}

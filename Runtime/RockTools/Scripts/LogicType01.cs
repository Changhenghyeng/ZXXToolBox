using System;
using UnityEngine;
using Random = System.Random;

namespace RockTools
{
    [Serializable]
    public class LogicType01 : LogicBase
    {
        protected override ERockType pRockType => ERockType.Cubic;

        private const int KCountMin = 1;
        private const int KCountMax = 135;
        private const float KRotationMin = -45f;
        private const float KRotationMax = 45;

        private const float KChangeSensitivity = 0.0001f;
        private const float KScaleLimitMin = 0.08f;

        [Header("Distribution")] [Range(KCountMin, KCountMax)] [SerializeField]
        private int density = 120;

        [Range(1f, 5f)] [SerializeField] private float radius = 5f;
        [Range(-1f, 1f)] [SerializeField] private float asymmetry;
        [Range(0f, 1f)] [SerializeField] private float wave;
        [Range(0f, 1f)] [SerializeField] private float decentralize = 0.5f;

        [Header("Scale"), Range(0f, 2f)] [SerializeField]
        private float scaleLocal = 2f;

        [SerializeField] private AnimationCurve scaleByDistance = new AnimationCurve(new Keyframe(0, 0, 0, 0), new Keyframe(1, 1, 2, 2));

        [Range(0f, 1f), Tooltip("Increase Height of the Rocks")] [SerializeField]
        private float tallness = 0.6f;

        [Range(0f, 1f)] [SerializeField] private float flatness;
        [Range(0f, 1f)] [SerializeField] private float wideness;

        [Header("Rotation")] [Range(KRotationMin, KRotationMax)] [SerializeField]
        private float rotation;

        [Range(0f, 359f)] [SerializeField] private float rotationLocal;
        [Range(0f, 1f)] [SerializeField] private float rotationRnd = 0.1f;

        // ----------------------------------------------------------------------------------------

        public override void UpdateRock(ref MeshBuffer meshBuffer, Random random)
        {
            base.UpdateRock(ref meshBuffer, random);

            if (rockMeshes != null && rockMeshes.Length > 0)
            {
                for (var i = 0; i < density; i++)
                {
                    var cell = new Cell();
                    cell.SetMesh(rockMeshes[i % rockMeshes.Length], pRockType == ERockType.Custom && normalizeCustomMeshes);
                    UpdatePosition(cell, random);
                    UpdateScale(cell);
                    UpdateRotation(cell, random);
                    placements.Add(new Placement
                    {
                        sourceIndex = i % rockMeshes.Length,
                        position = cell.position,
                        rotation = cell.rotation,
                        scale = cell.scale,
                    });
                    cell.WriteMesh(ref meshBuffer);
                }

                MeshSplitter.Split(meshBuffer, new Plane(Vector3.up, Vector3.zero));
            }
        }

        // ----------------------------------------------------------------------------------------

        private void UpdatePosition(Cell cell, Random random)
        {
            var pow = Mathf.Lerp(0.66f, 0.25f, decentralize);
            var randomDistance = (float) random.NextDouble();
            var d = Mathf.Pow(randomDistance, pow);
            var r = d * radius;
            var randomTheta = (float) random.NextDouble();
            var theta = randomTheta * 2 * Mathf.PI;
            var localPos = new Vector3(r * Mathf.Cos(theta), 0, r * Mathf.Sin(theta));
            cell.position = localPos;
        }

        // ----------------------------------------------------------------------------------------

        private void UpdateRotation(Cell cell, Random random)
        {
            var rot = new Vector3(0f, 0f, rotation);

            var rndRotX = (float) random.NextDouble();
            var rndRotY = (float) random.NextDouble();
            var rndRotZ = (float) random.NextDouble();

            if (rotationRnd > 0f)
            {
                var localCenter = new Vector3(asymmetry * radius, 0f, 0f);
                var distance = (cell.position - localCenter).magnitude;
                distance /= radius;

                if (distance > 0.5f && rotationRnd > 0f)
                {
                    var rndRotFactor = rotationRnd * 360 * distance;
                    rot.x += rndRotX * rndRotFactor;
                    rot.y += rndRotY * rndRotFactor;
                    rot.z += rndRotZ * rndRotFactor;
                }
            }

            cell.rotation = Quaternion.Euler(rot);
            cell.Rotate(Vector3.up, rotationLocal);
        }

        // ----------------------------------------------------------------------------------------

        private void UpdateScale(Cell cell)
        {
            var localCenter = new Vector3(asymmetry * radius, 0f, 0f);
            var distance = (cell.position - localCenter).magnitude;
            distance /= radius;
            var distCurve = scaleByDistance.Evaluate(1 - distance);
            var distCurveReverse = scaleByDistance.Evaluate(distance);
            var localScale = distCurve;
            localScale *= scaleLocal;

            var waveFactor = 0f;
            if (distance > 0.3f)
            {
                var wavePosition = Mathf.Lerp(radius, -radius, wave);
                var waveDist = Mathf.Abs(cell.position.x - wavePosition);
                waveFactor = Mathf.InverseLerp(radius / 4f, 0, waveDist) * (distCurveReverse * 4);
            }

            localScale += localScale * waveFactor;

            var finalScale = Vector3.zero;
            if (localScale > KScaleLimitMin)
            {
                var mTallness = Mathf.Lerp(1f, 3f, distCurve * tallness);
                var mFlatness = Mathf.Lerp(0f, 3f, distCurveReverse * flatness);
                var mWideness = Mathf.Lerp(0f, 3f, distCurveReverse * wideness);
                var height = Mathf.Clamp(mTallness - mFlatness, 0.1f, 4f);
                finalScale = new Vector3(1 + mWideness, height, 1 + mWideness) * localScale;
            }

            cell.scale = finalScale;
        }

        // ----------------------------------------------------------------------------------------
    }
}

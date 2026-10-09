using System;
using UnityEngine;
using Random = System.Random;

namespace RockTools
{
    [Serializable]
    public class LogicType03 : LogicBase
    {
        protected override ERockType pRockType => ERockType.Crystal;

        private const float ScaleBiasMin = -1;
        private const float ScaleBiasMax = 1;
        private const float ScaleRandom = 0.65f;
        private const float Spacing = 0.3f;

        [Space] [SerializeField, NotifyChange, Range(4, 30)]
        private int density = 15;

        [Space] [SerializeField, NotifyChange, Range(0f, 1f)]
        private float scaleByAngle = 1f;

        [SerializeField, NotifyChange, Range(0f, 2f)]
        private float scaleRandomOffset = 1f;

        [SerializeField, NotifyChange, Range(ScaleBiasMin, ScaleBiasMax)]
        private float scaleBias = 0f;

        [Space] [SerializeField, NotifyChange, Range(0f, 1f)]
        private float bloom = 0.7f;

        // ----------------------------------------------------------------------------------------

        public override void UpdateRock(ref MeshBuffer meshBuffer, Random random)
        {
            base.UpdateRock(ref meshBuffer, random);

            if (rockMeshes != null && rockMeshes.Length > 0)
            {
                var itemsCount = density - 1; // Exclude the first item already placed at the center
                var itemsPerRow = Mathf.CeilToInt(Mathf.Sqrt(itemsCount)); // Adjust as necessary for non-square grids
                var startPosition = CalculateStartPosition(itemsPerRow);

                for (var i = 0; i < density; i++)
                {
                    var cell = new Cell();
                    cell.SetMesh(rockMeshes[i % rockMeshes.Length], pRockType == ERockType.Custom && normalizeCustomMeshes);
                    UpdatePosition(cell, random, i, itemsPerRow, startPosition);
                    UpdateRotation(cell, random);
                    UpdateScale(cell, random, itemsPerRow);
                    placements.Add(new Placement
                    {
                        sourceIndex = i % rockMeshes.Length,
                        position = cell.position,
                        rotation = cell.rotation,
                        scale = cell.scale,
                    });
                    cell.WriteMesh(ref meshBuffer);
                }
            }
        }

        // ----------------------------------------------------------------------------------------

        private Vector3 CalculateStartPosition(int itemsPerRow)
        {
            var offset = (itemsPerRow - 1) * Spacing * 0.5f;
            return new Vector3(-offset, 0, offset);
        }

        // ----------------------------------------------------------------------------------------

        private void UpdatePosition(Cell cell, Random random, int index, int itemsPerRow, Vector3 startPosition)
        {
            if (index == 0)
            {
                cell.position = Vector3.zero;
                return;
            }

            var adjustedIndex = index - 1;
            var row = adjustedIndex / itemsPerRow;
            var column = adjustedIndex % itemsPerRow;

            cell.position = startPosition + new Vector3(column * Spacing, 0, -row * Spacing);
            const float halfSpacing = Spacing / 2f;
            cell.position += new Vector3((float) random.NextDouble() * halfSpacing, 0, (float) random.NextDouble() * halfSpacing);
        }

        // ----------------------------------------------------------------------------------------

        private void UpdateScale(Cell cell, Random random, int itemsPerRow)
        {
            var perlinNoise = GetPerlin(cell.position.x, cell.position.z, scaleRandomOffset + (float) random.NextDouble() * 3f);
            var noiseScale = Mathf.Lerp(1f, perlinNoise, ScaleRandom);
            var distance = Vector3.Distance(cell.position, new Vector3(scaleBias * Spacing * 0.75f, 0, 0)) / ((itemsPerRow + 1) * Spacing / 2f);
            var f = Mathf.Max(1f - distance, 0.15f);
            var distancedScale = Mathf.Lerp(1f, f, scaleByAngle);
            cell.scale = Vector3.one * (distancedScale * noiseScale);
        }

        // ----------------------------------------------------------------------------------------

        private void UpdateRotation(Cell cell, Random random)
        {
            var distance = Mathf.Clamp(cell.position.magnitude, 0f, 0.9f);
            var finalDirection = Vector3.Slerp(-cell.position.normalized, Vector3.up, distance * bloom);
            cell.rotation = finalDirection.sqrMagnitude != 0f ? Quaternion.LookRotation(finalDirection) : Quaternion.identity;
            cell.Rotate(Vector3.up, 45 * (float) random.NextDouble(), Space.World);
        }

        // ----------------------------------------------------------------------------------------

        private float GetPerlin(float x, float y = 0f, float offset = 0f, float scale = 1f, float amount = 1f)
        {
            var finalOffset = offset * scale;
            var xCoord = finalOffset + x * scale;
            var yCoord = finalOffset + y * scale;
            return Mathf.PerlinNoise(xCoord, yCoord) * amount;
        }

        // ----------------------------------------------------------------------------------------
    }
}

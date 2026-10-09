using System;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

namespace RockTools
{
    [Serializable]
    public class LogicBase : ScriptableObject
    {
        public struct Placement
        {
            public int sourceIndex;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;
        }

        public readonly List<Placement> placements = new List<Placement>();
        protected virtual ERockType pRockType { get; }

        protected Mesh[] rockMeshes;

        private Mesh[] customMeshes;
        protected bool normalizeCustomMeshes;

        // ----------------------------------------------------------------------------------------

        public virtual void UpdateRock(ref MeshBuffer meshBuffer, Random random)
        {
            placements.Clear();
            PrepareMeshes();
        }

        // ----------------------------------------------------------------------------------------

        protected void PrepareMeshes()
        {
            rockMeshes = pRockType == ERockType.Custom
                ? customMeshes
                : Resources.LoadAll<Mesh>(pRockType.GetResourcesPath());
        }

        public void SetCustomMeshes(Mesh[] meshes, bool normalize)
        {
            customMeshes = meshes;
            normalizeCustomMeshes = normalize;
        }

        // ----------------------------------------------------------------------------------------
    }
}

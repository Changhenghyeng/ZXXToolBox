using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZXXTools.PathBuilder
{
    [Serializable]
    internal sealed class MountainDecalMaterialPage
    {
        public Texture2D texture;
        public Material material;
    }

    /// <summary>
    /// Shared mountain art resources. Multiple paths can reference the same set
    /// while keeping their distribution and scale settings independent.
    /// </summary>
    public sealed class MountainDecalSet : ScriptableObject
    {
        public List<MountainSourceEntry> sources =
            new List<MountainSourceEntry>();
        public Material baseMaterial;
        public int renderQueue = 3000;

        [SerializeField, HideInInspector]
        internal List<MountainDecalMaterialPage> materialPages =
            new List<MountainDecalMaterialPage>();

        private void OnValidate()
        {
            renderQueue = Mathf.Clamp(renderQueue, 2501, 3999);
            if (sources == null)
                sources = new List<MountainSourceEntry>();
            if (materialPages == null)
            {
                materialPages = new List<MountainDecalMaterialPage>();
            }
        }
    }
}

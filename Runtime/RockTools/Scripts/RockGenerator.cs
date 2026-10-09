using System;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Random = System.Random;

namespace RockTools
{
    public class RockGenerator : MonoBehaviour
    {
        // ----------------------------------------------------------------------------------------

        [SerializeField] public int rndSeed;
        [SerializeField] private ERockType type;
        [SerializeField] private Material material;
        [SerializeField] private Mesh[] customRockMeshes = Array.Empty<Mesh>();
        [SerializeField] private GameObject[] customRockPrefabs = Array.Empty<GameObject>();
        [SerializeField] private CustomAssetMode customAssetMode;
        [SerializeField] private CustomSimulationMode customSimulationMode;
        [SerializeField] private bool normalizeCustomMeshes = true;
        [SerializeField] private bool normalizeCustomPrefabs = true;
        [SerializeField] public LogicBase logic;

        public int pRndSeed
        {
            get => rndSeed;
            set
            {
                if (value != rndSeed)
                {
                    rndSeed = value;
                    UpdateRock();
                }
            }
        }

        public ERockType pType
        {
            get => type;
            set
            {
                if (value != type)
                {
                    type = value;
                    UpdateRock();
                }
            }
        }

        public Material pMaterial
        {
            get => material;
            set
            {
                if (value != material)
                {
                    material = value;
                    UpdateMaterials();
                }
            }
        }

        public MeshFilter pMeshFilter
        {
            get
            {
                if (meshFilter == null)
                {
                    meshFilter = GetComponent<MeshFilter>();
                }

                return meshFilter;
            }
        }

        public MeshRenderer pMeshRenderer
        {
            get
            {
                if (meshRenderer == null)
                {
                    meshRenderer = GetComponent<MeshRenderer>();
                }

                return meshRenderer;
            }
        }

        // ----------------------------------------------------------------------------------------

        private MeshBuffer meshBuffer = new MeshBuffer(200000);
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Random random;
        private int selectedType = -1;
        private int selectedCustomSimulation = -1;

        // ----------------------------------------------------------------------------------------

        public static RockGenerator GetInstance()
        {
            return new GameObject("Rock Generator").AddComponent<RockGenerator>();
        }

        // ----------------------------------------------------------------------------------------

        private void Reset()
        {
            material = null;
            if (meshRenderer)
            {
                meshRenderer.sharedMaterial = material;
            }

            UpdateRock();
        }

        // ----------------------------------------------------------------------------------------

        public void UpdateRock()
        {
            PrepareRockGeneratorLogic();
            PrepareDefaultMaterial();
            PrepareComponents();
            RefreshRandomSeeds();

            meshBuffer.Clear();

            logic?.SetCustomMeshes(customRockMeshes, normalizeCustomMeshes);
            logic?.UpdateRock(ref meshBuffer, random);

            if (type == ERockType.Custom && customAssetMode == CustomAssetMode.PrefabInstances)
                BuildPrefabInstances();
            else
            {
                ClearPrefabInstances();
                BuildMesh();
                meshRenderer.enabled = true;
            }
        }

        // ----------------------------------------------------------------------------------------

        private void PrepareRockGeneratorLogic()
        {
            if (selectedType != (int) type ||
                (type == ERockType.Custom && selectedCustomSimulation != (int)customSimulationMode) ||
                ReferenceEquals(logic, null))
            {
                switch (type)
                {
                    case ERockType.Cubic:
                        logic = ScriptableObject.CreateInstance<LogicType01>();
                        break;
                    case ERockType.Sharp:
                        logic = ScriptableObject.CreateInstance<LogicType02>();
                        break;
                    case ERockType.Crystal:
                        logic = ScriptableObject.CreateInstance<LogicType03>();
                        break;
                    case ERockType.Custom:
                        logic = customSimulationMode == CustomSimulationMode.CrystalCluster
                            ? ScriptableObject.CreateInstance<LogicTypeCustomCrystal>()
                            : ScriptableObject.CreateInstance<LogicTypeCustom>();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                selectedType = (int) type;
                selectedCustomSimulation = (int)customSimulationMode;
            }
        }

        // ----------------------------------------------------------------------------------------

        public void Randomize(int increment)
        {
            rndSeed += increment;
            UpdateRock();
        }

        // ----------------------------------------------------------------------------------------

        private void PrepareDefaultMaterial()
        {
            if (material == null)
            {
                var defaultCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var diffuse = defaultCube.GetComponent<MeshRenderer>().sharedMaterial;
                material = diffuse;
                if (Application.isPlaying)
                    Destroy(defaultCube);
                else
                    DestroyImmediate(defaultCube);
            }
        }

        // ----------------------------------------------------------------------------------------

        private void PrepareComponents()
        {
            if (meshFilter == null)
            {
                meshFilter = GetComponent<MeshFilter>();

                if (meshFilter == null)
                {
                    meshFilter = gameObject.AddComponent<MeshFilter>();
                }
            }

            if (meshRenderer == null)
            {
                meshRenderer = GetComponent<MeshRenderer>();

                if (meshRenderer == null)
                {
                    meshRenderer = gameObject.AddComponent<MeshRenderer>();
                }
            }

            if (meshRenderer)
            {
                if (meshRenderer.sharedMaterial == null)
                {
                    meshRenderer.sharedMaterial = material;
                }
                else
                {
                    material = meshRenderer.sharedMaterial;
                }
            }
        }

        // ----------------------------------------------------------------------------------------

        private void RefreshRandomSeeds()
        {
            random = new Random(rndSeed);
        }

        // ----------------------------------------------------------------------------------------

        private void BuildMesh()
        {
            var mesh = meshBuffer.GetMesh();
            mesh.name = gameObject.name;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            meshFilter.sharedMesh = mesh;
        }

        private void BuildPrefabInstances()
        {
            ClearPrefabInstances();
            meshFilter.sharedMesh = null;
            meshRenderer.enabled = false;
            if (customRockPrefabs == null || customRockPrefabs.Length == 0 || logic == null)
                return;

            var root = new GameObject("__RockPrefabInstances").transform;
            root.SetParent(transform, false);
            foreach (var placement in logic.placements)
            {
                var prefab = customRockPrefabs[placement.sourceIndex % customRockPrefabs.Length];
                if (prefab == null || placement.scale.sqrMagnitude == 0f)
                    continue;

                var placementRoot = new GameObject(prefab.name).transform;
                placementRoot.SetParent(root, false);
                placementRoot.localPosition = placement.position;
                placementRoot.localRotation = placement.rotation;
                placementRoot.localScale = placement.scale;

                var prefabParent = placementRoot;
                if (normalizeCustomPrefabs && TryGetPrefabBounds(prefab, out var bounds))
                {
                    var maxSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                    if (maxSize > Mathf.Epsilon)
                    {
                        var normalizeRoot = new GameObject("Normalize").transform;
                        normalizeRoot.SetParent(placementRoot, false);
                        var inverseSize = 1f / maxSize;
                        normalizeRoot.localScale = Vector3.one * inverseSize;
                        normalizeRoot.localPosition = -new Vector3(bounds.center.x, bounds.min.y, bounds.center.z) * inverseSize;
                        prefabParent = normalizeRoot;
                    }
                }
#if UNITY_EDITOR
                var instance = PrefabUtility.InstantiatePrefab(prefab, prefabParent) as GameObject;
#else
                var instance = Instantiate(prefab, prefabParent);
#endif
                if (instance == null)
                    DestroyImmediate(placementRoot.gameObject);
            }
        }

        private static bool TryGetPrefabBounds(GameObject prefab, out Bounds result)
        {
            result = default;
            var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            var hasBounds = false;
            var rootToLocal = prefab.transform.worldToLocalMatrix;
            foreach (var filter in filters)
            {
                if (filter.sharedMesh == null) continue;
                var matrix = rootToLocal * filter.transform.localToWorldMatrix;
                var bounds = filter.sharedMesh.bounds;
                var min = bounds.min;
                var max = bounds.max;
                for (var x = 0; x < 2; x++)
                for (var y = 0; y < 2; y++)
                for (var z = 0; z < 2; z++)
                {
                    var point = matrix.MultiplyPoint3x4(new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z));
                    if (!hasBounds) { result = new Bounds(point, Vector3.zero); hasBounds = true; }
                    else result.Encapsulate(point);
                }
            }
            return hasBounds;
        }

        private void ClearPrefabInstances()
        {
            var child = transform.Find("__RockPrefabInstances");
            if (child == null) return;
            if (Application.isPlaying) Destroy(child.gameObject);
            else DestroyImmediate(child.gameObject);
        }

        // ----------------------------------------------------------------------------------------

        public void UpdateMaterials()
        {
            meshRenderer.sharedMaterial = material;
        }

        // ----------------------------------------------------------------------------------------

        public int GetVertexCount()
        {
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                return -1;
            }

            return meshFilter.sharedMesh.vertexCount;
        }

        // ----------------------------------------------------------------------------------------
    }
}

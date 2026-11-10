using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace RockTools
{
    [CustomEditor(typeof(RockGenerator))]
    public class RockGeneratorEditor : Editor
    {
        private const string KPropertyPathRandomSeed = "rndSeed";
        private const bool KOptimize = true;

        private RockGenerator rockGen;
        private SerializedProperty randomSeed;
        private SerializedProperty rockType;
        private SerializedProperty material;
        private SerializedProperty customRockMeshes;
        private SerializedProperty normalizeCustomMeshes;
        private SerializedProperty customAssetMode;
        private SerializedProperty customRockPrefabs;
        private SerializedProperty normalizeCustomPrefabs;
        private SerializedProperty customSimulationMode;

        private Editor rockTypeEditor;

        private int tmpRandomSeed;
        private ERockType tmpRockType;
        private Object tmpMaterial;

        private const bool AddCollider = false;

        private void OnEnable()
        {
            rockGen = target as RockGenerator;

            InitializeProperties();

            SceneView.duringSceneGui += DuringSceneGui;

            UpdateTmpValues();
        }

        private void InitializeProperties()
        {
            randomSeed = serializedObject.FindProperty("rndSeed");
            rockType = serializedObject.FindProperty("type");
            material = serializedObject.FindProperty("material");
            customRockMeshes = serializedObject.FindProperty("customRockMeshes");
            normalizeCustomMeshes = serializedObject.FindProperty("normalizeCustomMeshes");
            customAssetMode = serializedObject.FindProperty("customAssetMode");
            customRockPrefabs = serializedObject.FindProperty("customRockPrefabs");
            normalizeCustomPrefabs = serializedObject.FindProperty("normalizeCustomPrefabs");
            customSimulationMode = serializedObject.FindProperty("customSimulationMode");
        }

        private void UpdateTmpValues()
        {
            tmpRandomSeed = randomSeed.intValue;
            tmpRockType = (ERockType) rockType.intValue;
            tmpMaterial = material.objectReferenceValue;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DuringSceneGui;
        }

        public override void OnInspectorGUI()
        {
            if (rockGen == null)
            {
                return;
            }

            if (!rockGen.isActiveAndEnabled)
            {
                EditorGUILayout.HelpBox("Please enable rock generator's game object before editing!", MessageType.Warning);
            }

            EditorGUI.BeginDisabledGroup(!rockGen.isActiveAndEnabled);

            serializedObject.Update();
            DrawProperties();
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bake", EditorStyles.boldLabel);
            //addCollider = EditorGUILayout.Toggle("Add Collider", addCollider);

            var isPrefabMode = (ERockType)rockType.intValue == ERockType.Custom &&
                               (CustomAssetMode)customAssetMode.enumValueIndex == CustomAssetMode.PrefabInstances;
            using (new EditorGUI.DisabledScope(isPrefabMode))
            {
                if (GUILayout.Button("Bake"))
                {
                    PreBake(Bake);
                    GUIUtility.ExitGUI();
                    return;
                }
            }
            if (isPrefabMode)
                EditorGUILayout.HelpBox("Prefab Instances 已经是可直接编辑的 Prefab 实例，不需要 Bake。", MessageType.Info);

            if (tmpRandomSeed != randomSeed.intValue)
            {
                rockGen.UpdateRock();
            }
            else if (tmpRockType != (ERockType) rockType.intValue)
            {
                rockGen.UpdateRock();
            }
            else if (tmpMaterial != material.objectReferenceValue)
            {
                rockGen.UpdateMaterials();
            }

            UpdateTmpValues();

            EditorGUI.EndDisabledGroup();
        }

        private void DrawProperties()
        {
            DrawRandomSeedField();

            var iterator = serializedObject.GetIterator();
            var propertyToExclude = new[] {"m_Script", KPropertyPathRandomSeed, "logic", "customRockMeshes", "normalizeCustomMeshes", "customAssetMode", "customRockPrefabs", "normalizeCustomPrefabs", "customSimulationMode"};
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (!propertyToExclude.Contains(iterator.name))
                {
                    EditorGUILayout.PropertyField(iterator, true);
                }
            }

            if ((ERockType) rockType.intValue == ERockType.Custom)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Custom Rock Assets", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(customSimulationMode, new GUIContent("Simulation Mode"));
                EditorGUILayout.PropertyField(customAssetMode, new GUIContent("Custom Asset Mode"));
                if ((CustomAssetMode)customAssetMode.enumValueIndex == CustomAssetMode.MeshCombine)
                {
                    EditorGUILayout.PropertyField(normalizeCustomMeshes, new GUIContent("Normalize Meshes"));
                    EditorGUILayout.PropertyField(customRockMeshes, new GUIContent("Rock Meshes"), true);
                }
                else
                {
                    EditorGUILayout.PropertyField(normalizeCustomPrefabs, new GUIContent("Normalize Prefabs"));
                    EditorGUILayout.PropertyField(customRockPrefabs, new GUIContent("Rock Prefabs"), true);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties();
                    rockGen.UpdateRock();
                    serializedObject.Update();
                }
                EditorGUILayout.HelpBox((CustomAssetMode)customAssetMode.enumValueIndex == CustomAssetMode.MeshCombine
                    ? "Mesh Combine：合并为一个 Mesh；Normalize 不会修改原始资源。"
                    : "Prefab Instances：保留 Prefab 层级、材质、碰撞体和 Prefab 关联。", MessageType.Info);
                var prefabMode = (CustomAssetMode)customAssetMode.enumValueIndex == CustomAssetMode.PrefabInstances;
                if (!prefabMode && customRockMeshes.arraySize == 0)
                    EditorGUILayout.HelpBox("Mesh Combine 模式至少需要一个石头 Mesh。", MessageType.Warning);
                if (prefabMode && customRockPrefabs.arraySize == 0)
                    EditorGUILayout.HelpBox("Prefab Instances 模式至少需要一个石头 Prefab。", MessageType.Warning);
            }

            if (rockTypeEditor == null || rockTypeEditor.target != rockGen.logic)
            {
                if (rockTypeEditor != null)
                {
                    DestroyImmediate(rockTypeEditor);
                }

                rockTypeEditor = CreateEditor(rockGen.logic);
                if (rockTypeEditor is LogicEditorBase editorBase)
                {
                    editorBase.OnPropertyChanged += () => { rockGen.UpdateRock(); };
                }
            }

            rockTypeEditor?.OnInspectorGUI();
        }

        private void DrawRandomSeedField()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Random Seed");

            if (GUILayout.Button("Randomize"))
            {
                rockGen.Randomize(1);
            }

            rockGen.rndSeed = EditorGUILayout.IntField(rockGen.rndSeed);
            EditorGUILayout.EndHorizontal();
        }

        private void DuringSceneGui(SceneView obj)
        {
            if (rockGen != null)
            {
                var vertexCount = rockGen.GetVertexCount();
                if (vertexCount >= 0)
                {
                    Handles.BeginGUI();
                    GUILayout.BeginArea(new Rect(20, 20, 300, 60));
                    GUILayout.BeginVertical("Box");
                    GUILayout.Label($"{rockGen.name}");
                    GUILayout.Label($"Vertex Count (Before Bake): {vertexCount}");
                    GUILayout.EndVertical();
                    GUILayout.EndArea();
                    Handles.EndGUI();
                }
            }
        }

        private async void PreBake(Action<string> preBakeDone)
        {
            var scenePath = SceneManager.GetActiveScene().path;

            // check if scene has a valid path
            if (string.IsNullOrEmpty(scenePath))
            {
                if (EditorUtility.DisplayDialog("The untitled scene needs saving",
                        "You need to save the scene before baking rock.", "Save Scene", "Cancel"))
                    scenePath = EditorUtility.SaveFilePanel("Save Scene", "Assets/", "", "unity");

                scenePath = FileUtil.GetProjectRelativePath(scenePath);

                if (string.IsNullOrEmpty(scenePath))
                {
                    Debug.LogWarning("Scene was not saved, bake canceled.");
                    return;
                }

                var saveOk = EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scenePath);

                if (!saveOk)
                {
                    Debug.LogWarning("Scene was not saved, bake canceled.");
                    return;
                }

                AssetDatabase.Refresh();
                await Task.Delay(100);
            }

            scenePath = SceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(scenePath))
            {
                return;
            }

            var assetPath = $"{Path.ChangeExtension(scenePath, null)}-generated-mesh/baked-rock.asset";
            var assetDir = Path.GetDirectoryName(assetPath);

            if (string.IsNullOrEmpty(assetDir))
            {
                return;
            }

            if (!Directory.Exists(assetDir))
            {
                Directory.CreateDirectory(assetDir);
                AssetDatabase.Refresh();
                await Task.Delay(100);
            }

            assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            preBakeDone.Invoke(assetPath);
        }

        private void Bake(string path)
        {
            var usageTimer = System.Diagnostics.Stopwatch.StartNew();
            var bakedMeshFilter = new GameObject("Baked-Rock").AddComponent<MeshFilter>();
            var bakedMeshRenderer = bakedMeshFilter.gameObject.AddComponent<MeshRenderer>();
            bakedMeshRenderer.sharedMaterial = rockGen.pMeshRenderer.sharedMaterial;
            var parameters = new BakeParameters {addCollider = AddCollider, path = path, mergeVerticesThreshold = 0.1f, generateSecondaryUVSet = true, optimize = KOptimize};
            RockBaker.Bake(rockGen, parameters, bakedMeshFilter);
            if (bakedMeshFilter.sharedMesh != null && AssetDatabase.Contains(bakedMeshFilter.sharedMesh))
            {
                usageTimer.Stop();
                try
                {
                    var usageType = System.Type.GetType("ZXXToolUsageStats, Unity.Editor")
                        ?? System.AppDomain.CurrentDomain.GetAssemblies()
                            .Select(assembly => assembly.GetType("ZXXToolUsageStats"))
                            .FirstOrDefault(type => type != null);
                    usageType?.GetMethod("RecordCompleted", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        ?.Invoke(null, new object[] { "Rock Generator", 1, usageTimer.Elapsed.TotalSeconds });
                }
                catch (System.Exception exception)
                {
                    Debug.LogWarning("[Rock Generator] 使用统计保存失败：" + exception.Message);
                }
            }
        }
    }
}

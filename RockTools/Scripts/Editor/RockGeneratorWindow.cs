using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RockTools
{
    public class RockGeneratorWindow : EditorWindow
    {
        private RockGenerator generator;
        private Editor generatorEditor;
        private Vector2 scroll;

        public static void Open()
        {
            var window = GetWindow<RockGeneratorWindow>("Rock Generator");
            window.minSize = new Vector2(380f, 520f);
            window.TryUseSelectedGenerator();
            window.Show();
        }

        private void OnEnable()
        {
            TryUseSelectedGenerator();
        }

        private void OnDisable()
        {
            if (generatorEditor != null)
                DestroyImmediate(generatorEditor);
        }

        private void OnSelectionChange()
        {
            if (Selection.activeGameObject != null &&
                Selection.activeGameObject.TryGetComponent<RockGenerator>(out var selected))
                SetGenerator(selected);
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Rock Generator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("创建或指定一个生成器。选择 Project 中的 Mesh / FBX / Prefab 后，可直接填入自定义石头素材。", MessageType.Info);

            EditorGUI.BeginChangeCheck();
            var next = (RockGenerator)EditorGUILayout.ObjectField("Generator", generator, typeof(RockGenerator), true);
            if (EditorGUI.EndChangeCheck())
                SetGenerator(next);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Create Generator", GUILayout.Height(26f)))
                CreateGenerator();
            using (new EditorGUI.DisabledScope(generator == null))
            {
                if (GUILayout.Button("Select Generator", GUILayout.Height(26f)))
                    Selection.activeGameObject = generator.gameObject;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6f);
            using (new EditorGUI.DisabledScope(generator == null))
            {
                if (GUILayout.Button("Fill From Selection", GUILayout.Height(30f)))
                    FillFromSelection();
            }

            if (generator == null)
            {
                EditorGUILayout.HelpBox("先点击 Create Generator，或把场景中的 RockGenerator 拖到 Generator。", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(6f);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EnsureGeneratorEditor();
            generatorEditor?.OnInspectorGUI();
            EditorGUILayout.EndScrollView();
        }

        private void CreateGenerator()
        {
            var created = RockGenerator.GetInstance();
            Undo.RegisterCreatedObjectUndo(created.gameObject, "Create Rock Generator");
            SetGenerator(created);
            Selection.activeGameObject = created.gameObject;
        }

        private void FillFromSelection()
        {
            var prefabs = Selection.objects.OfType<GameObject>()
                .Where(item => item != generator.gameObject)
                .Select(ResolvePrefabAsset)
                .Where(item => item != null)
                .Distinct().ToArray();
            var meshes = Selection.objects
                .Where(item => item != generator && item != generator.gameObject)
                .SelectMany(GetMeshes)
                .Where(mesh => mesh != null)
                .Distinct()
                .ToArray();

            if (prefabs.Length > 0)
                meshes = prefabs.Select(prefab => GetMeshes(prefab).FirstOrDefault())
                    .Where(mesh => mesh != null).ToArray();

            if (meshes.Length == 0 && prefabs.Length == 0)
            {
                EditorUtility.DisplayDialog("未找到石头素材", "请先在 Project 中选中 Mesh、FBX 或 Prefab；也可以选中包含 MeshFilter 的场景物体。", "确定");
                return;
            }

            Undo.RecordObject(generator, "Fill Custom Rock Assets");
            var serialized = new SerializedObject(generator);
            var type = serialized.FindProperty("type");
            var customMeshes = serialized.FindProperty("customRockMeshes");
            var customPrefabs = serialized.FindProperty("customRockPrefabs");
            var assetMode = serialized.FindProperty("customAssetMode");
            type.intValue = (int)ERockType.Custom;
            if (prefabs.Length > 0)
            {
                assetMode.enumValueIndex = (int)CustomAssetMode.PrefabInstances;
                customPrefabs.arraySize = prefabs.Length;
                for (var i = 0; i < prefabs.Length; i++)
                    customPrefabs.GetArrayElementAtIndex(i).objectReferenceValue = prefabs[i];
            }
            customMeshes.arraySize = meshes.Length;
            for (var i = 0; i < meshes.Length; i++)
                customMeshes.GetArrayElementAtIndex(i).objectReferenceValue = meshes[i];
            serialized.ApplyModifiedProperties();

            generator.UpdateRock();
            EditorUtility.SetDirty(generator);
            Repaint();
            ShowNotification(new GUIContent(prefabs.Length > 0
                ? $"已填入 {prefabs.Length} 个石头 Prefab"
                : $"已填入 {meshes.Length} 个石头 Mesh"));
        }

        private static Mesh[] GetMeshes(Object selected)
        {
            if (selected == null)
                return Array.Empty<Mesh>();
            if (selected is Mesh mesh)
                return new[] { mesh };

            var path = AssetDatabase.GetAssetPath(selected);
            if (!string.IsNullOrEmpty(path))
            {
                var assetMeshes = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToArray();
                if (assetMeshes.Length > 0)
                    return assetMeshes;
            }

            return selected is GameObject gameObject
                ? gameObject.GetComponentsInChildren<MeshFilter>(true)
                    .Where(filter => filter.sharedMesh != null)
                    .Select(filter => filter.sharedMesh)
                    .ToArray()
                : Array.Empty<Mesh>();
        }

        private static GameObject ResolvePrefabAsset(GameObject selected)
        {
            if (selected == null)
                return null;

            // Project 中的普通 Prefab、Variant 和 FBX Model Prefab 都可直接使用。
            if (EditorUtility.IsPersistent(selected))
                return selected;

            // Hierarchy 中选中的是实例时，回到对应的 Prefab 源资源，保持 Prefab 关联。
            return PrefabUtility.GetCorrespondingObjectFromSource(selected);
        }

        private void TryUseSelectedGenerator()
        {
            if (Selection.activeGameObject != null &&
                Selection.activeGameObject.TryGetComponent<RockGenerator>(out var selected))
                SetGenerator(selected);
        }

        private void SetGenerator(RockGenerator value)
        {
            if (generator == value)
                return;
            generator = value;
            if (generatorEditor != null)
                DestroyImmediate(generatorEditor);
            generatorEditor = null;
        }

        private void EnsureGeneratorEditor()
        {
            if (generatorEditor == null || generatorEditor.target != generator)
                generatorEditor = Editor.CreateEditor(generator);
        }
    }
}

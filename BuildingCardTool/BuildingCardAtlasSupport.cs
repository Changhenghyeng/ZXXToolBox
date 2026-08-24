using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

internal static class BuildingCardTransparentSortUtility
{
    private const float MinimumBoundsExtent = 0.0001f;

    /// <summary>
    /// MeshRenderer transparency sorting uses Renderer.bounds.center. Re-centre that bounds at
    /// the prefab Root without moving vertices, so a card is sorted from its ground pivot while
    /// its bounds still fully contain the visible quad for correct frustum culling.
    /// </summary>
    public static void AlignBoundsCenterToRoot(GameObject root)
    {
        if (root == null) return;
        var filters = root.GetComponentsInChildren<MeshFilter>(true);
        for (var filterIndex = 0; filterIndex < filters.Length; filterIndex++)
        {
            var filter = filters[filterIndex];
            var mesh = filter.sharedMesh;
            if (mesh == null || mesh.vertexCount == 0) continue;

            var localRoot = filter.transform.InverseTransformPoint(root.transform.position);
            var extents = Vector3.zero;
            var vertices = mesh.vertices;
            for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
            {
                var distance = vertices[vertexIndex] - localRoot;
                extents = Vector3.Max(extents, new Vector3(
                    Mathf.Abs(distance.x),
                    Mathf.Abs(distance.y),
                    Mathf.Abs(distance.z)));
            }

            extents = Vector3.Max(extents, Vector3.one * MinimumBoundsExtent);
            var desiredBounds = new Bounds(localRoot, extents * 2f);
            var currentBounds = mesh.bounds;
            if ((currentBounds.center - desiredBounds.center).sqrMagnitude < 0.00000001f &&
                (currentBounds.size - desiredBounds.size).sqrMagnitude < 0.00000001f)
                continue;

            Undo.RecordObject(mesh, "Align Building Card Transparent Sort Pivot");
            mesh.bounds = desiredBounds;
            EditorUtility.SetDirty(mesh);
        }
    }
}

internal sealed class BuildingCardAtlasPanel
{
    private const string PrefsKey = "BuildingCardTool.AtlasPanel";

    [Serializable]
    private sealed class SavedState
    {
        public string atlasName = "BuildingAtlas";
        public string shaderGuid;
        public int maxSize = 4096;
        public int pixelsPerCell = 128;
        public bool fillAtlas;
        public int padding = 4;
        public int extrude = 4;
        public bool mipmaps = true;
        public bool advanced;
        public List<string> prefabGuids = new List<string>();
    }

    private readonly List<GameObject> prefabs = new List<GameObject>();
    private readonly List<Texture2D> previewTextures = new List<Texture2D>();
    private bool loaded;
    private bool previewActive;
    private int previewPage;
    private string atlasName = "BuildingAtlas";
    private Shader shader;
    private int maxSize = 4096;
    private int pixelsPerCell = 128;
    private bool fillAtlas;
    private int padding = 4;
    private int extrude = 4;
    private bool mipmaps = true;
    private bool advanced;

    public void Draw(string outputFolder)
    {
        EnsureLoaded();
        EditorGUILayout.Space(5f);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(new GUIContent("Atlas", "选择已经生成的建筑 Prefab，合并为共享图集；与上方 PNG 批量生成互不影响。"), EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        atlasName = EditorGUILayout.TextField(new GUIContent("Atlas Name", "图集资源名称；同名图集会原地更新。"), atlasName);
        pixelsPerCell = Mathf.Clamp(EditorGUILayout.IntField(
            new GUIContent("Pixels / Cell", "默认清晰度预算。建筑像素面积 = Grid W × Grid D × Pixels/Cell²；参与数量不会改变该值。"),
            pixelsPerCell), 16, 2048);
        DrawAtlasSize();
        fillAtlas = EditorGUILayout.Toggle(new GUIContent("Fill Atlas", "关闭：保持固定清晰度，装不下自动分页。开启：保持规格比例和宽高比，整体统一缩放并尽量填满一张图集。"), fillAtlas);
        DrawToolbar();
        DrawPrefabSlots();
        DrawDropArea();
        advanced = EditorGUILayout.Foldout(advanced, new GUIContent("Advanced", "图集尺寸、边距和 Shader 设置。"), true);
        if (advanced) DrawAdvancedSettings();
        if (EditorGUI.EndChangeCheck()) SaveState();

        EditorGUILayout.BeginHorizontal();
        GUI.enabled = prefabs.Count > 0;
        if (GUILayout.Button(new GUIContent("Build / Update Atlas", "仅合并当前槽中的 Prefab；已有同名图集会原地更新。"), GUILayout.Height(24f)))
            EditorApplication.delayCall += () => Build(outputFolder);
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        GUI.enabled = prefabs.Count > 0;
        if (GUILayout.Button(new GUIContent("Use Existing Atlas", "直接切换到已经生成的图集材质，不重新生成图集。"), GUILayout.Height(22f)))
            EditorApplication.delayCall += () => UseExistingAtlas(outputFolder);
        if (GUILayout.Button(new GUIContent("Restore Standalone", "把当前槽中的 Prefab 恢复为各自原始材质和 UV0。"), GUILayout.Height(24f)))
            EditorApplication.delayCall += Restore;
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent("Add Selected", "把 Project 中当前选中的建筑 Prefab 加入槽位。")))
            AddObjects(Selection.objects, true);
        if (GUILayout.Button(new GUIContent("Clear", "清空槽位，不修改任何 Prefab 或图集资源。"), GUILayout.Width(70f)))
        {
            prefabs.Clear();
            SaveState();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawPrefabSlots()
    {
        for (var i = 0; i < prefabs.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 58f;
            var replacement = EditorGUILayout.ObjectField(
                new GUIContent("Prefab " + (i + 1), "参与本次图集合并的建筑 Prefab。"),
                prefabs[i], typeof(GameObject), false, GUILayout.MinWidth(60f)) as GameObject;
            EditorGUIUtility.labelWidth = previousLabelWidth;
            if (replacement != prefabs[i])
            {
                if (replacement == null)
                {
                    prefabs.RemoveAt(i--);
                    SaveState();
                    EditorGUILayout.EndHorizontal();
                    continue;
                }
                if (TryGetPrefabAsset(replacement, out var prefab, out var error) && !prefabs.Contains(prefab))
                    prefabs[i] = prefab;
                else if (!string.IsNullOrEmpty(error))
                    Debug.LogWarning("[BuildingCardAtlas] " + error);
            }
            var prefabPath = AssetDatabase.GetAssetPath(prefabs[i]);
            if (!BuildingCardAtlasBuilder.TryGetGridSize(prefabPath, out var gridWidth, out var gridDepth))
                gridWidth = gridDepth = 1;
            GUILayout.Label(new GUIContent("Grid", "该 Prefab 的图集规格权重。修改后会重新分配图集像素，不会改动碰撞体或面片 Transform。"),
                EditorStyles.miniLabel, GUILayout.Width(26f));
            EditorGUI.BeginChangeCheck();
            gridWidth = Mathf.Max(1, EditorGUILayout.IntField(gridWidth, GUILayout.Width(34f)));
            GUILayout.Label("×", EditorStyles.miniLabel, GUILayout.Width(10f));
            gridDepth = Mathf.Max(1, EditorGUILayout.IntField(gridDepth, GUILayout.Width(34f)));
            if (EditorGUI.EndChangeCheck())
                BuildingCardAtlasBuilder.SetGridSize(prefabPath, gridWidth, gridDepth);
            if (GUILayout.Button("×", GUILayout.Width(24f)))
            {
                prefabs.RemoveAt(i--);
                SaveState();
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    private void DrawDropArea()
    {
        var rect = GUILayoutUtility.GetRect(0f, 34f, GUILayout.ExpandWidth(true));
        GUI.Box(rect, prefabs.Count == 0 ? "Drop Building Prefabs Here" : "Drop More Prefabs Here", EditorStyles.helpBox);
        var current = Event.current;
        if (!rect.Contains(current.mousePosition) ||
            current.type != EventType.DragUpdated && current.type != EventType.DragPerform) return;
        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
        if (current.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            AddObjects(DragAndDrop.objectReferences, true);
        }
        current.Use();
    }

    private void DrawAdvancedSettings()
    {
        shader = EditorGUILayout.ObjectField(new GUIContent("Shader", "图集模式共用的 URP Shader。项目中只需要这一份 Shader。"), shader, typeof(Shader), false) as Shader;
        padding = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Padding", "建筑图片之间保留的透明像素间距。"), padding));
        extrude = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Extrude", "向外复制边缘像素，减少缩小和 Mipmap 时的串色。"), extrude));
        mipmaps = EditorGUILayout.Toggle(new GUIContent("Mipmaps", "为图集生成 Mipmap。"), mipmaps);
    }

    private void DrawAtlasSize()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(new GUIContent("Atlas Size", "每一页输出图集的固定分辨率。默认模式装不下时自动生成更多同尺寸页面。"), GUILayout.Width(EditorGUIUtility.labelWidth));
        maxSize = EditorGUILayout.IntPopup(maxSize,
            new[] { "1024", "2048", "4096", "8192" }, new[] { 1024, 2048, 4096, 8192 });
        EditorGUILayout.EndHorizontal();
    }

    private void AddObjects(UnityEngine.Object[] objects, bool showWarning)
    {
        var added = 0;
        string lastError = null;
        for (var i = 0; i < objects.Length; i++)
        {
            if (!TryGetPrefabAsset(objects[i], out var prefab, out var error))
            {
                if (!string.IsNullOrEmpty(error)) lastError = error;
                continue;
            }
            if (prefabs.Contains(prefab)) continue;
            prefabs.Add(prefab);
            added++;
        }
        if (added > 0) SaveState();
        else if (showWarning && !string.IsNullOrEmpty(lastError))
            EditorUtility.DisplayDialog("Building Atlas", lastError, "OK");
    }

    private static bool TryGetPrefabAsset(UnityEngine.Object value, out GameObject prefab, out string error)
    {
        prefab = value as GameObject;
        error = null;
        if (prefab == null) return false;
        if (!EditorUtility.IsPersistent(prefab))
            prefab = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
        var path = prefab == null ? string.Empty : AssetDatabase.GetAssetPath(prefab);
        if (prefab == null || string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only Prefab assets can be added.";
            return false;
        }
        var filter = prefab.GetComponentInChildren<MeshFilter>(true);
        var renderer = prefab.GetComponentInChildren<MeshRenderer>(true);
        if (prefab.GetComponent<BoxCollider>() == null || filter == null || renderer == null ||
            filter.sharedMesh == null || filter.sharedMesh.vertexCount != 4)
        {
            error = prefab.name + " is not a generated Building Card Prefab.";
            return false;
        }
        return true;
    }

    private void Build(string outputFolder)
    {
        try
        {
            var paths = GetPrefabPaths();
            var report = BuildingCardAtlasBuilder.BuildPrefabs(atlasName, paths, new BuildingCardAtlasBuildSettings
            {
                outputFolder = outputFolder,
                shader = shader,
                maxSize = maxSize,
                pixelsPerCell = pixelsPerCell,
                fillAtlas = fillAtlas,
                padding = padding,
                extrude = extrude,
                mipmaps = mipmaps
            });
            RefreshAtlasPreview(outputFolder);
            EditorUtility.DisplayDialog("Building Atlas",
                "Updated " + report.buildingCount + " prefabs in " + report.pageCount + " atlas page(s).", "OK");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Building Atlas", exception.Message, "OK");
        }
    }

    private void Restore()
    {
        try
        {
            BuildingCardAtlasBuilder.RestorePrefabs(GetPrefabPaths());
            EditorUtility.DisplayDialog("Building Atlas", "Selected prefabs restored to Standalone mode.", "OK");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Building Atlas", exception.Message, "OK");
        }
    }

    private void UseExistingAtlas(string outputFolder)
    {
        try
        {
            BuildingCardAtlasBuilder.UseExistingAtlas(GetPrefabPaths(), outputFolder);
            RefreshAtlasPreview(outputFolder);
            EditorUtility.DisplayDialog("Building Atlas", "Selected prefabs switched to the existing Atlas mode.", "OK");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Building Atlas", exception.Message, "OK");
        }
    }

    public bool IsPreviewActive
    {
        get { return previewActive && previewTextures.Count > 0; }
    }

    public bool TryGetPreview(out Texture2D texture, out int pageIndex, out int pageCount)
    {
        texture = null;
        pageIndex = 0;
        pageCount = previewTextures.Count;
        if (!IsPreviewActive) return false;
        previewPage = Mathf.Clamp(previewPage, 0, previewTextures.Count - 1);
        texture = previewTextures[previewPage];
        pageIndex = previewPage;
        return texture != null;
    }

    public void MovePreviewPage(int delta)
    {
        if (previewTextures.Count == 0) return;
        previewPage = (previewPage + delta + previewTextures.Count) % previewTextures.Count;
    }

    public void HidePreview()
    {
        previewActive = false;
    }

    private void RefreshAtlasPreview(string outputFolder)
    {
        previewTextures.Clear();
        previewTextures.AddRange(BuildingCardAtlasBuilder.FindGeneratedAtlasTextures(atlasName, outputFolder, fillAtlas));
        previewPage = 0;
        previewActive = previewTextures.Count > 0;
    }

    private List<string> GetPrefabPaths()
    {
        var paths = new List<string>(prefabs.Count);
        for (var i = 0; i < prefabs.Count; i++)
        {
            var path = AssetDatabase.GetAssetPath(prefabs[i]);
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }
        return paths;
    }

    private void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        var json = EditorPrefs.GetString(PrefsKey, string.Empty);
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                var saved = JsonUtility.FromJson<SavedState>(json);
                if (saved != null)
                {
                    atlasName = string.IsNullOrWhiteSpace(saved.atlasName) ? "BuildingAtlas" : saved.atlasName;
                    maxSize = saved.maxSize <= 0 ? 4096 : saved.maxSize;
                    pixelsPerCell = saved.pixelsPerCell <= 0 ? 128 : saved.pixelsPerCell;
                    fillAtlas = saved.fillAtlas;
                    padding = Mathf.Max(0, saved.padding);
                    extrude = Mathf.Max(0, saved.extrude);
                    mipmaps = saved.mipmaps;
                    advanced = saved.advanced;
                    shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(saved.shaderGuid));
                    if (saved.prefabGuids != null)
                    {
                        for (var i = 0; i < saved.prefabGuids.Count; i++)
                        {
                            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(saved.prefabGuids[i]));
                            if (prefab != null && !prefabs.Contains(prefab)) prefabs.Add(prefab);
                        }
                    }
                }
            }
            catch { }
        }
        if (shader == null) shader = Shader.Find("Custom/Building Card URP");
        if (shader == null) shader = Shader.Find("ZXX/Building Card URP");
        if (shader == null)
        {
            var shaderGuids = AssetDatabase.FindAssets("BuildingCard_URP t:Shader");
            if (shaderGuids.Length > 0)
                shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(shaderGuids[0]));
        }
    }

    private void SaveState()
    {
        if (!loaded) return;
        var saved = new SavedState
        {
            atlasName = atlasName,
            shaderGuid = shader == null ? string.Empty : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(shader)),
            maxSize = maxSize,
            pixelsPerCell = pixelsPerCell,
            fillAtlas = fillAtlas,
            padding = padding,
            extrude = extrude,
            mipmaps = mipmaps,
            advanced = advanced
        };
        for (var i = 0; i < prefabs.Count; i++)
        {
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefabs[i]));
            if (!string.IsNullOrEmpty(guid)) saved.prefabGuids.Add(guid);
        }
        EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(saved));
    }
}

internal enum BuildingCardRenderMode
{
    Standalone,
    Atlas
}

[Serializable]
internal sealed class BuildingCardAssetMetadata
{
    public int version = 2;
    public string sourceTextureGuid;
    public string standaloneMaterialGuid;
    public int gridWidth;
    public int gridDepth;
    public string atlasGroup = "Default";
    public int atlasPage = -1;
    public Rect atlasUvRect;
    public string atlasMaterialGuid;
    public string atlasRuleSuffix;
    public BuildingCardRenderMode renderMode;
}

internal static class BuildingCardMetadataStore
{
    private const string Prefix = "ZXX_BUILDING_CARD:";

    public static bool TryLoad(string prefabPath, out BuildingCardAssetMetadata metadata)
    {
        metadata = null;
        var importer = AssetImporter.GetAtPath(prefabPath);
        if (importer == null || string.IsNullOrEmpty(importer.userData) ||
            !importer.userData.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        try
        {
            metadata = JsonUtility.FromJson<BuildingCardAssetMetadata>(importer.userData.Substring(Prefix.Length));
            return metadata != null;
        }
        catch
        {
            metadata = null;
            return false;
        }
    }

    public static void Save(string prefabPath, BuildingCardAssetMetadata metadata)
    {
        var importer = AssetImporter.GetAtPath(prefabPath);
        if (importer == null)
            throw new InvalidOperationException("Cannot access prefab importer: " + prefabPath);

        var value = Prefix + JsonUtility.ToJson(metadata);
        if (string.Equals(importer.userData, value, StringComparison.Ordinal)) return;
        importer.userData = value;
        EditorUtility.SetDirty(importer);
        AssetDatabase.WriteImportSettingsIfDirty(prefabPath);
    }

    public static void RecordGeneratedPrefab(string prefabPath, Texture2D sourceTexture, Material standaloneMaterial,
        int gridWidth, int gridDepth)
    {
        if (!TryLoad(prefabPath, out var metadata)) metadata = new BuildingCardAssetMetadata();
        metadata.version = 2;
        metadata.sourceTextureGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(sourceTexture));
        metadata.standaloneMaterialGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(standaloneMaterial));
        metadata.gridWidth = Mathf.Max(1, gridWidth);
        metadata.gridDepth = Mathf.Max(1, gridDepth);
        metadata.renderMode = BuildingCardRenderMode.Standalone;
        Save(prefabPath, metadata);
    }
}

internal sealed class BuildingCardAtlasBuildSettings
{
    public string outputFolder;
    public Shader shader;
    public int maxSize = 4096;
    public int pixelsPerCell = 128;
    public bool fillAtlas;
    public int padding = 4;
    public int extrude = 4;
    public bool mipmaps = true;
}

internal sealed class BuildingCardAtlasBuildReport
{
    public string group;
    public int buildingCount;
    public int pageCount;
}

internal static class BuildingCardAtlasBuilder
{
    private const int AtlasUvChannel = 2;
    private const int LegacyAtlasUvChannel = 3;
    private const int CompressionBlockSize = 4;
    private const string WorkspacePrefsKey = "BuildingCardTool.Settings.Workspace";
    private static string cachedWorkspaceJson;
    private static readonly Dictionary<string, Vector2Int> CachedGridSizes =
        new Dictionary<string, Vector2Int>(StringComparer.Ordinal);

    [Serializable]
    private sealed class SavedWorkspaceEntry
    {
        public string textureGuid = string.Empty;
        public int gridWidth = 1;
        public int gridDepth = 1;
    }

    [Serializable]
    private sealed class SavedWorkspace
    {
        public List<SavedWorkspaceEntry> entries = new List<SavedWorkspaceEntry>();
    }

    private sealed class Item
    {
        public string prefabPath;
        public BuildingCardAssetMetadata metadata;
        public Mesh mesh;
        public Texture2D sourceAsset;
        public Color32[] sourcePixels;
        public int sourceWidth;
        public int sourceHeight;
        public int cropX;
        public int cropY;
        public int sourceCropWidth;
        public int sourceCropHeight;
        public int baseTargetWidth;
        public int baseTargetHeight;
        public int targetWidth;
        public int targetHeight;
        public int slotWidth;
        public int slotHeight;
        public RectInt slotRect;
        public RectInt contentRect;
        public Rect atlasUvRect;
        public int pageIndex = -1;
    }

    private sealed class Page
    {
        public int size;
        public readonly List<Item> items = new List<Item>();
        public Color32[] pixels;
        public Texture2D texture;
        public Material material;
    }

    private sealed class MaxRectsBin
    {
        private readonly List<RectInt> freeRects = new List<RectInt>();

        public MaxRectsBin(int width, int height)
        {
            freeRects.Add(new RectInt(0, 0, width, height));
        }

        public bool TryInsert(int width, int height, out RectInt placement)
        {
            placement = default;
            var bestShortSide = int.MaxValue;
            var bestLongSide = int.MaxValue;
            var found = false;

            for (var i = 0; i < freeRects.Count; i++)
            {
                var free = freeRects[i];
                if (width > free.width || height > free.height) continue;
                var leftoverX = free.width - width;
                var leftoverY = free.height - height;
                var shortSide = Mathf.Min(leftoverX, leftoverY);
                var longSide = Mathf.Max(leftoverX, leftoverY);
                if (shortSide > bestShortSide || shortSide == bestShortSide && longSide >= bestLongSide)
                    continue;

                placement = new RectInt(free.x, free.y, width, height);
                bestShortSide = shortSide;
                bestLongSide = longSide;
                found = true;
            }

            if (!found) return false;
            SplitFreeRects(placement);
            PruneFreeRects();
            return true;
        }

        private void SplitFreeRects(RectInt used)
        {
            for (var i = freeRects.Count - 1; i >= 0; i--)
            {
                var free = freeRects[i];
                if (!Overlaps(free, used)) continue;
                freeRects.RemoveAt(i);

                if (used.x > free.x)
                    freeRects.Add(new RectInt(free.x, free.y, used.x - free.x, free.height));
                if (used.xMax < free.xMax)
                    freeRects.Add(new RectInt(used.xMax, free.y, free.xMax - used.xMax, free.height));
                if (used.y > free.y)
                    freeRects.Add(new RectInt(free.x, free.y, free.width, used.y - free.y));
                if (used.yMax < free.yMax)
                    freeRects.Add(new RectInt(free.x, used.yMax, free.width, free.yMax - used.yMax));
            }
        }

        private void PruneFreeRects()
        {
            for (var i = freeRects.Count - 1; i >= 0; i--)
            {
                if (freeRects[i].width <= 0 || freeRects[i].height <= 0)
                {
                    freeRects.RemoveAt(i);
                    continue;
                }

                for (var j = freeRects.Count - 1; j >= 0; j--)
                {
                    if (i == j) continue;
                    if (!Contains(freeRects[j], freeRects[i])) continue;
                    freeRects.RemoveAt(i);
                    break;
                }
            }
        }

        private static bool Overlaps(RectInt a, RectInt b)
        {
            return a.x < b.xMax && a.xMax > b.x && a.y < b.yMax && a.yMax > b.y;
        }

        private static bool Contains(RectInt outer, RectInt inner)
        {
            return inner.x >= outer.x && inner.y >= outer.y &&
                   inner.xMax <= outer.xMax && inner.yMax <= outer.yMax;
        }
    }

    public static bool TryGetGridSize(string prefabPath, out int gridWidth, out int gridDepth)
    {
        gridWidth = 0;
        gridDepth = 0;
        if (string.IsNullOrEmpty(prefabPath)) return false;
        if (!BuildingCardMetadataStore.TryLoad(prefabPath, out var metadata))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var renderer = prefab == null ? null : prefab.GetComponentInChildren<MeshRenderer>(true);
            if (renderer == null) return false;
            try { metadata = CaptureStandaloneMetadata(prefabPath, renderer); }
            catch { return false; }
        }
        if (metadata.gridWidth > 0 && metadata.gridDepth > 0)
        {
            gridWidth = metadata.gridWidth;
            gridDepth = metadata.gridDepth;
            return true;
        }
        if (!TryRecoverGridSize(metadata.sourceTextureGuid, out gridWidth, out gridDepth)) return false;
        return true;
    }

    public static void SetGridSize(string prefabPath, int gridWidth, int gridDepth)
    {
        if (string.IsNullOrEmpty(prefabPath)) return;
        if (!BuildingCardMetadataStore.TryLoad(prefabPath, out var metadata))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var renderer = prefab == null ? null : prefab.GetComponentInChildren<MeshRenderer>(true);
            if (renderer == null)
                throw new InvalidOperationException("Building Card renderer is missing: " + prefabPath);
            metadata = CaptureStandaloneMetadata(prefabPath, renderer);
        }
        metadata.version = 2;
        metadata.gridWidth = Mathf.Max(1, gridWidth);
        metadata.gridDepth = Mathf.Max(1, gridDepth);
        BuildingCardMetadataStore.Save(prefabPath, metadata);
    }

    public static List<Texture2D> FindGeneratedAtlasTextures(string group, string outputFolder, bool fillAtlas)
    {
        var textures = new List<Texture2D>();
        if (string.IsNullOrWhiteSpace(group) || string.IsNullOrEmpty(outputFolder)) return textures;
        var cleanGroup = SanitizeAssetName(group.Trim());
        var folder = outputFolder.TrimEnd('/') + "/Atlases/" + cleanGroup;
        if (!AssetDatabase.IsValidFolder(folder)) return textures;
        var prefix = "T_" + cleanGroup + "_Atlas_" + GetRuleSuffix(fillAtlas) + "_";
        var paths = new List<string>();
        var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
        for (var i = 0; i < guids.Length; i++)
        {
            var path = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (!Path.GetFileNameWithoutExtension(path).StartsWith(prefix, StringComparison.Ordinal) ||
                !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
            paths.Add(path);
        }
        paths.Sort((a, b) =>
        {
            var aName = Path.GetFileNameWithoutExtension(a);
            var bName = Path.GetFileNameWithoutExtension(b);
            var aPage = int.TryParse(aName.Substring(prefix.Length), out var parsedA) ? parsedA : int.MaxValue;
            var bPage = int.TryParse(bName.Substring(prefix.Length), out var parsedB) ? parsedB : int.MaxValue;
            return aPage != bPage ? aPage.CompareTo(bPage) : string.Compare(a, b, StringComparison.Ordinal);
        });
        for (var i = 0; i < paths.Count; i++)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(paths[i]);
            if (texture != null) textures.Add(texture);
        }
        return textures;
    }

    public static bool IsGeneratedAtlasTexture(Texture2D texture)
    {
        if (texture == null) return false;
        var path = AssetDatabase.GetAssetPath(texture);
        if (string.IsNullOrEmpty(path)) return false;
        var importer = AssetImporter.GetAtPath(path);
        if (importer != null && !string.IsNullOrEmpty(importer.userData) &&
            importer.userData.StartsWith("ZXX_BUILDING_CARD_ATLAS:", StringComparison.Ordinal)) return true;
        var normalized = path.Replace('\\', '/');
        return normalized.IndexOf("/Atlases/", StringComparison.OrdinalIgnoreCase) >= 0 &&
               Path.GetFileNameWithoutExtension(normalized).StartsWith("T_", StringComparison.Ordinal) &&
               Path.GetFileNameWithoutExtension(normalized).IndexOf("_Atlas_", StringComparison.Ordinal) >= 0;
    }

    public static BuildingCardAtlasBuildReport BuildPrefabs(string group, IReadOnlyList<string> prefabPaths,
        BuildingCardAtlasBuildSettings settings)
    {
        ValidateSettings(group, settings);
        if (prefabPaths == null || prefabPaths.Count == 0)
            throw new InvalidOperationException("Add at least one Building Card Prefab.");
        var shader = settings.shader;
        RestoreRemovedGroupMembers(group, prefabPaths, settings.outputFolder);
        var items = CollectItems(group, prefabPaths);
        if (items.Count == 0)
            throw new InvalidOperationException("No valid Building Card Prefabs were selected.");

        var margin = AlignUp(Mathf.Max(0, settings.padding) + Mathf.Max(0, settings.extrude), CompressionBlockSize);
        CalculateBaseTargetSizes(items, settings.pixelsPerCell);
        var pages = settings.fillAtlas
            ? PackSingleFilledPage(items, settings.maxSize, margin)
            : PackFixedSizePages(items, settings.maxSize, margin);
        for (var i = 0; i < pages.Count; i++)
            BuildPagePixels(pages[i], settings.extrude);

        var cleanGroup = SanitizeAssetName(group.Trim());
        var ruleSuffix = GetRuleSuffix(settings.fillAtlas);
        var atlasFolder = settings.outputFolder.TrimEnd('/') + "/Atlases/" + cleanGroup;
        EnsureAssetFolder(atlasFolder);
        WritePageAssets(pages, atlasFolder, cleanGroup, ruleSuffix, settings, shader);
        ApplyAtlasResults(items, pages, ruleSuffix);
        DeleteObsoletePages(atlasFolder, cleanGroup, ruleSuffix, pages.Count);
        DeleteLegacyPages(atlasFolder, cleanGroup);
        AssetDatabase.SaveAssets();

        return new BuildingCardAtlasBuildReport
        {
            group = group,
            buildingCount = items.Count,
            pageCount = pages.Count
        };
    }

    public static void RestorePrefabs(IReadOnlyList<string> prefabPaths)
    {
        if (prefabPaths == null) return;
        for (var i = 0; i < prefabPaths.Count; i++)
            SetStandaloneMode(prefabPaths[i], true);
        AssetDatabase.SaveAssets();
    }

    public static void UseExistingAtlas(IReadOnlyList<string> prefabPaths, string outputFolder)
    {
        if (prefabPaths == null) return;
        for (var i = 0; i < prefabPaths.Count; i++)
        {
            var prefabPath = prefabPaths[i];
            if (!BuildingCardMetadataStore.TryLoad(prefabPath, out var metadata) ||
                string.IsNullOrWhiteSpace(metadata.atlasGroup))
                throw new InvalidOperationException("Prefab has no existing Atlas data: " + prefabPath);

            var materialPath = AssetDatabase.GUIDToAssetPath(metadata.atlasMaterialGuid);
            if (string.IsNullOrEmpty(materialPath))
                materialPath = FindExistingAtlasMaterial(outputFolder, metadata);
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
                throw new InvalidOperationException("Existing Atlas material is missing. Build the Atlas once: " + prefabPath);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var filter = prefab == null ? null : prefab.GetComponentInChildren<MeshFilter>(true);
            var atlasUvs = new List<Vector2>();
            if (filter != null && filter.sharedMesh != null) filter.sharedMesh.GetUVs(AtlasUvChannel, atlasUvs);
            if (atlasUvs.Count != 4 && filter != null && filter.sharedMesh != null)
            {
                filter.sharedMesh.GetUVs(LegacyAtlasUvChannel, atlasUvs);
                if (atlasUvs.Count == 4)
                {
                    filter.sharedMesh.SetUVs(AtlasUvChannel, atlasUvs);
                    filter.sharedMesh.SetUVs(LegacyAtlasUvChannel, new List<Vector2>());
                    EditorUtility.SetDirty(filter.sharedMesh);
                }
            }
            if (atlasUvs.Count != 4)
                throw new InvalidOperationException("Prefab has no Atlas UV data. Build the Atlas once: " + prefabPath);

            ApplyMaterial(prefabPath, material);
            metadata.atlasMaterialGuid = AssetDatabase.AssetPathToGUID(materialPath);
            metadata.renderMode = BuildingCardRenderMode.Atlas;
            BuildingCardMetadataStore.Save(prefabPath, metadata);
        }
        AssetDatabase.SaveAssets();
    }

    private static void SetStandaloneMode(string prefabPath, bool throwIfMissing)
    {
        if (!BuildingCardMetadataStore.TryLoad(prefabPath, out var metadata))
        {
            if (throwIfMissing)
                throw new InvalidOperationException("Prefab has not been built into an atlas: " + prefabPath);
            return;
        }
        var materialPath = AssetDatabase.GUIDToAssetPath(metadata.standaloneMaterialGuid);
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            if (throwIfMissing)
                throw new InvalidOperationException("Standalone material is missing: " + prefabPath);
            return;
        }
        ApplyMaterial(prefabPath, material);
        metadata.renderMode = BuildingCardRenderMode.Standalone;
        BuildingCardMetadataStore.Save(prefabPath, metadata);
    }

    private static string FindExistingAtlasMaterial(string outputFolder, BuildingCardAssetMetadata metadata)
    {
        var cleanGroup = SanitizeAssetName(metadata.atlasGroup.Trim());
        var folder = outputFolder.TrimEnd('/') + "/Atlases/" + cleanGroup;
        var prefix = "M_" + cleanGroup + "_Atlas_";
        if (!string.IsNullOrEmpty(metadata.atlasRuleSuffix)) prefix += metadata.atlasRuleSuffix + "_";
        if (metadata.atlasPage >= 0)
        {
            var expected = folder + "/" + prefix + metadata.atlasPage + ".mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(expected) != null) return expected;
        }

        var guids = AssetDatabase.FindAssets("t:Material " + prefix, new[] { folder });
        if (guids.Length == 1) return AssetDatabase.GUIDToAssetPath(guids[0]);
        return string.Empty;
    }

    private static void ValidateSettings(string group, BuildingCardAtlasBuildSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(group)) throw new InvalidOperationException("Atlas Group cannot be empty.");
        if (string.IsNullOrEmpty(settings.outputFolder) ||
            settings.outputFolder != "Assets" && !settings.outputFolder.StartsWith("Assets/", StringComparison.Ordinal))
            throw new InvalidOperationException("Atlas output folder must be inside Assets.");
        if (settings.shader == null)
            throw new InvalidOperationException("Building Shader is not assigned.");
        if (settings.maxSize < 64 || settings.maxSize > 8192 || !Mathf.IsPowerOfTwo(settings.maxSize))
            throw new InvalidOperationException("Atlas Size must be a power of two from 64 to 8192.");
        if (settings.pixelsPerCell < 16 || settings.pixelsPerCell > 2048)
            throw new InvalidOperationException("Pixels / Cell must be from 16 to 2048.");
        if (settings.padding < 0 || settings.extrude < 0)
            throw new InvalidOperationException("Atlas Padding and Extrude cannot be negative.");
    }

    private static List<Item> CollectItems(string group, IReadOnlyList<string> prefabPaths)
    {
        var items = new List<Item>();
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < prefabPaths.Count; i++)
        {
            var prefabPath = prefabPaths[i];
            if (string.IsNullOrEmpty(prefabPath) || !uniquePaths.Add(prefabPath)) continue;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var filter = prefab == null ? null : prefab.GetComponentInChildren<MeshFilter>(true);
            var renderer = prefab == null ? null : prefab.GetComponentInChildren<MeshRenderer>(true);
            var mesh = filter == null ? null : filter.sharedMesh;
            if (prefab == null || mesh == null || renderer == null)
                throw new InvalidOperationException("Building renderer or mesh is missing: " + prefabPath);

            if (!BuildingCardMetadataStore.TryLoad(prefabPath, out var metadata))
                metadata = CaptureStandaloneMetadata(prefabPath, renderer);
            if (metadata.gridWidth <= 0 || metadata.gridDepth <= 0)
            {
                if (!TryRecoverGridSize(metadata.sourceTextureGuid, out metadata.gridWidth, out metadata.gridDepth))
                    throw new InvalidOperationException(
                        "Grid size is missing for " + prefab.name + ". Generate this prefab once with its Grid W × D settings.");
                metadata.version = 2;
                BuildingCardMetadataStore.Save(prefabPath, metadata);
            }
            var sourcePath = AssetDatabase.GUIDToAssetPath(metadata.sourceTextureGuid);
            var sourceAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath);
            if (sourceAsset == null)
                throw new InvalidOperationException("Building source texture is missing: " + prefabPath);

            metadata.atlasGroup = group.Trim();
            metadata.renderMode = BuildingCardRenderMode.Atlas;

            var item = CreateItem(prefabPath, metadata, sourcePath, sourceAsset, mesh);
            items.Add(item);
        }

        return items;
    }

    private static bool TryRecoverGridSize(string sourceTextureGuid, out int gridWidth, out int gridDepth)
    {
        gridWidth = 0;
        gridDepth = 0;
        if (string.IsNullOrEmpty(sourceTextureGuid)) return false;
        var json = EditorPrefs.GetString(WorkspacePrefsKey, string.Empty);
        if (string.IsNullOrEmpty(json)) return false;
        if (!string.Equals(cachedWorkspaceJson, json, StringComparison.Ordinal))
        {
            cachedWorkspaceJson = json;
            CachedGridSizes.Clear();
            try
            {
                var workspace = JsonUtility.FromJson<SavedWorkspace>(json);
                if (workspace != null && workspace.entries != null)
                {
                    for (var i = 0; i < workspace.entries.Count; i++)
                    {
                        var entry = workspace.entries[i];
                        if (string.IsNullOrEmpty(entry.textureGuid)) continue;
                        CachedGridSizes[entry.textureGuid] = new Vector2Int(
                            Mathf.Max(1, entry.gridWidth), Mathf.Max(1, entry.gridDepth));
                    }
                }
            }
            catch { }
        }
        if (!CachedGridSizes.TryGetValue(sourceTextureGuid, out var size)) return false;
        gridWidth = size.x;
        gridDepth = size.y;
        return true;
    }

    private static BuildingCardAssetMetadata CaptureStandaloneMetadata(string prefabPath, MeshRenderer renderer)
    {
        var material = renderer.sharedMaterial;
        if (material == null)
            throw new InvalidOperationException("Standalone material is missing: " + prefabPath);
        var texture = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") as Texture2D : null;
        if (texture == null) texture = material.mainTexture as Texture2D;
        var materialPath = AssetDatabase.GetAssetPath(material);
        var texturePath = AssetDatabase.GetAssetPath(texture);
        if (string.IsNullOrEmpty(materialPath) || string.IsNullOrEmpty(texturePath))
            throw new InvalidOperationException("Standalone material or source texture is not a project asset: " + prefabPath);

        return new BuildingCardAssetMetadata
        {
            sourceTextureGuid = AssetDatabase.AssetPathToGUID(texturePath),
            standaloneMaterialGuid = AssetDatabase.AssetPathToGUID(materialPath),
            renderMode = BuildingCardRenderMode.Standalone
        };
    }

    private static void RestoreRemovedGroupMembers(string group, IReadOnlyList<string> selectedPaths, string outputFolder)
    {
        var selected = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
        var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { outputFolder });
        for (var i = 0; i < prefabGuids.Length; i++)
        {
            var path = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
            if (selected.Contains(path) || !BuildingCardMetadataStore.TryLoad(path, out var metadata) ||
                metadata.renderMode != BuildingCardRenderMode.Atlas ||
                !string.Equals(metadata.atlasGroup, group.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            SetStandaloneMode(path, false);
            if (!BuildingCardMetadataStore.TryLoad(path, out metadata)) continue;
            metadata.atlasGroup = string.Empty;
            metadata.atlasPage = -1;
            metadata.atlasUvRect = default;
            metadata.atlasMaterialGuid = string.Empty;
            metadata.atlasRuleSuffix = string.Empty;
            BuildingCardMetadataStore.Save(path, metadata);
        }
    }

    private static Item CreateItem(string prefabPath, BuildingCardAssetMetadata metadata, string sourcePath,
        Texture2D sourceAsset, Mesh mesh)
    {
        var uvs = new List<Vector2>();
        mesh.GetUVs(0, uvs);
        if (mesh.vertexCount != 4 || uvs.Count != 4)
            throw new InvalidOperationException("Building Card mesh must contain four UV0 coordinates: " + prefabPath);

        var min = uvs[0];
        var max = uvs[0];
        for (var i = 1; i < uvs.Count; i++)
        {
            min = Vector2.Min(min, uvs[i]);
            max = Vector2.Max(max, uvs[i]);
        }

        var readable = LoadSourceImage(sourcePath);
        try
        {
            var cropX = Mathf.Clamp(Mathf.RoundToInt(min.x * Mathf.Max(1, readable.width - 1)), 0, readable.width - 1);
            var cropY = Mathf.Clamp(Mathf.RoundToInt(min.y * Mathf.Max(1, readable.height - 1)), 0, readable.height - 1);
            var maxX = Mathf.Clamp(Mathf.RoundToInt(max.x * Mathf.Max(1, readable.width - 1)), cropX, readable.width - 1);
            var maxY = Mathf.Clamp(Mathf.RoundToInt(max.y * Mathf.Max(1, readable.height - 1)), cropY, readable.height - 1);
            return new Item
            {
                prefabPath = prefabPath,
                metadata = metadata,
                mesh = mesh,
                sourceAsset = sourceAsset,
                sourcePixels = readable.GetPixels32(),
                sourceWidth = readable.width,
                sourceHeight = readable.height,
                cropX = cropX,
                cropY = cropY,
                sourceCropWidth = maxX - cropX + 1,
                sourceCropHeight = maxY - cropY + 1
            };
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(readable);
        }
    }

    private static Texture2D LoadSourceImage(string assetPath)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Source image file is missing.", assetPath);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(fullPath), false))
        {
            UnityEngine.Object.DestroyImmediate(texture);
            throw new InvalidOperationException("Cannot decode source image: " + assetPath);
        }
        return texture;
    }

    private static void CalculateBaseTargetSizes(List<Item> items, int pixelsPerCell)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var aspect = item.sourceCropWidth / (double)Math.Max(1, item.sourceCropHeight);
            var gridArea = (double)Math.Max(1, item.metadata.gridWidth) * Math.Max(1, item.metadata.gridDepth);
            var pixelArea = gridArea * pixelsPerCell * pixelsPerCell;
            item.baseTargetWidth = Math.Max(1, (int)Math.Round(Math.Sqrt(pixelArea * aspect)));
            item.baseTargetHeight = Math.Max(1, (int)Math.Round(Math.Sqrt(pixelArea / aspect)));
        }
    }

    private static List<Page> PackFixedSizePages(List<Item> items, int atlasSize, int margin)
    {
        ConfigureSlots(items, 1f, margin);
        ValidateSlotSizes(items, atlasSize);
        var remaining = GetPackingOrder(items);
        var pages = new List<Page>();
        while (remaining.Count > 0)
        {
            PackSubset(remaining, atlasSize, out var placed, out var placements);
            if (placed.Count == 0)
                throw new InvalidOperationException("Atlas packing failed. Increase Atlas Size or reduce Pixels / Cell.");

            var page = new Page { size = atlasSize };
            var pageIndex = pages.Count;
            for (var i = 0; i < placed.Count; i++)
            {
                AddToPage(page, placed[i], placements[placed[i]], margin, pageIndex);
                remaining.Remove(placed[i]);
            }
            pages.Add(page);
        }
        return pages;
    }

    private static List<Page> PackSingleFilledPage(List<Item> items, int atlasSize, int margin)
    {
        List<Item> order;
        Dictionary<Item, RectInt> placements;
        if (!TryPackAllAtScale(items, atlasSize, margin, 0.0001f, out order, out placements))
            throw new InvalidOperationException(
                "Too many buildings and padding blocks for one Atlas page. Increase Atlas Size or disable Fill Atlas.");

        var low = 0.0001f;
        var high = 1f;
        if (TryPackAllAtScale(items, atlasSize, margin, high, out order, out placements))
        {
            low = high;
            while (high < 4096f)
            {
                high *= 2f;
                if (!TryPackAllAtScale(items, atlasSize, margin, high, out order, out placements)) break;
                low = high;
            }
        }

        for (var iteration = 0; iteration < 24; iteration++)
        {
            var middle = (low + high) * 0.5f;
            if (TryPackAllAtScale(items, atlasSize, margin, middle, out order, out placements)) low = middle;
            else high = middle;
        }

        // Stay slightly below the packing boundary so integer rounding remains deterministic.
        var finalScale = Mathf.Max(0.0001f, low * 0.9999f);
        if (!TryPackAllAtScale(items, atlasSize, margin, finalScale, out order, out placements))
            throw new InvalidOperationException("Fill Atlas packing failed. Increase Atlas Size or reduce Padding.");

        var page = new Page { size = atlasSize };
        for (var i = 0; i < order.Count; i++)
            AddToPage(page, order[i], placements[order[i]], margin, 0);
        return new List<Page> { page };
    }

    private static bool TryPackAllAtScale(List<Item> items, int atlasSize, int margin, float scale,
        out List<Item> order, out Dictionary<Item, RectInt> placements)
    {
        ConfigureSlots(items, scale, margin);
        order = GetPackingOrder(items);
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].slotWidth > atlasSize || order[i].slotHeight > atlasSize)
            {
                placements = new Dictionary<Item, RectInt>();
                return false;
            }
        }
        PackSubset(order, atlasSize, out var placed, out placements);
        return placed.Count == order.Count;
    }

    private static void ConfigureSlots(List<Item> items, float scale, int margin)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            item.targetWidth = Mathf.Max(1, Mathf.RoundToInt(item.baseTargetWidth * scale));
            item.targetHeight = Mathf.Max(1, Mathf.RoundToInt(item.baseTargetHeight * scale));
            item.slotWidth = AlignUp(item.targetWidth + margin * 2, CompressionBlockSize);
            item.slotHeight = AlignUp(item.targetHeight + margin * 2, CompressionBlockSize);
        }
    }

    private static void ValidateSlotSizes(List<Item> items, int atlasSize)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].slotWidth <= atlasSize && items[i].slotHeight <= atlasSize) continue;
            throw new InvalidOperationException(items[i].sourceAsset.name +
                " exceeds Atlas Size at the current Pixels / Cell. Increase Atlas Size or reduce Pixels / Cell.");
        }
    }

    private static List<Item> GetPackingOrder(List<Item> items)
    {
        var order = new List<Item>(items);
        order.Sort((a, b) =>
        {
            // Keep the order independent from Fill scale so the binary search remains monotonic.
            var maxCompare = Mathf.Max(b.baseTargetWidth, b.baseTargetHeight)
                .CompareTo(Mathf.Max(a.baseTargetWidth, a.baseTargetHeight));
            if (maxCompare != 0) return maxCompare;
            var areaCompare = (b.baseTargetWidth * b.baseTargetHeight)
                .CompareTo(a.baseTargetWidth * a.baseTargetHeight);
            return areaCompare != 0 ? areaCompare : string.Compare(a.prefabPath, b.prefabPath, StringComparison.Ordinal);
        });
        return order;
    }

    private static void AddToPage(Page page, Item item, RectInt placement, int margin, int pageIndex)
    {
        item.pageIndex = pageIndex;
        item.slotRect = placement;
        item.contentRect = new RectInt(
            placement.x + margin,
            placement.y + margin,
            item.targetWidth,
            item.targetHeight);
        item.atlasUvRect = new Rect(
            item.contentRect.x / (float)Mathf.Max(1, page.size - 1),
            item.contentRect.y / (float)Mathf.Max(1, page.size - 1),
            (item.contentRect.width - 1) / (float)Mathf.Max(1, page.size - 1),
            (item.contentRect.height - 1) / (float)Mathf.Max(1, page.size - 1));
        page.items.Add(item);
    }

    private static void PackSubset(List<Item> items, int size, out List<Item> placed,
        out Dictionary<Item, RectInt> placements)
    {
        placed = new List<Item>();
        placements = new Dictionary<Item, RectInt>();
        var bin = new MaxRectsBin(size, size);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!bin.TryInsert(item.slotWidth, item.slotHeight, out var rect)) continue;
            placed.Add(item);
            placements.Add(item, rect);
        }
    }

    private static void BuildPagePixels(Page page, int extrude)
    {
        page.pixels = new Color32[page.size * page.size];
        for (var itemIndex = 0; itemIndex < page.items.Count; itemIndex++)
        {
            var item = page.items[itemIndex];
            for (var y = -extrude; y < item.targetHeight + extrude; y++)
            {
                var destinationY = item.contentRect.y + y;
                if (destinationY < 0 || destinationY >= page.size) continue;
                for (var x = -extrude; x < item.targetWidth + extrude; x++)
                {
                    var destinationX = item.contentRect.x + x;
                    if (destinationX < 0 || destinationX >= page.size) continue;
                    page.pixels[destinationY * page.size + destinationX] = SampleResizedPixel(item, x, y);
                }
            }
        }
    }

    private static Color32 SampleResizedPixel(Item item, int targetX, int targetY)
    {
        targetX = Mathf.Clamp(targetX, 0, item.targetWidth - 1);
        targetY = Mathf.Clamp(targetY, 0, item.targetHeight - 1);
        var sourceX = item.cropX + (targetX + 0.5f) * item.sourceCropWidth / item.targetWidth - 0.5f;
        var sourceY = item.cropY + (targetY + 0.5f) * item.sourceCropHeight / item.targetHeight - 0.5f;
        var minX = item.cropX;
        var minY = item.cropY;
        var maxX = item.cropX + item.sourceCropWidth - 1;
        var maxY = item.cropY + item.sourceCropHeight - 1;
        sourceX = Mathf.Clamp(sourceX, minX, maxX);
        sourceY = Mathf.Clamp(sourceY, minY, maxY);
        var x0 = Mathf.Clamp(Mathf.FloorToInt(sourceX), minX, maxX);
        var y0 = Mathf.Clamp(Mathf.FloorToInt(sourceY), minY, maxY);
        var x1 = Mathf.Min(x0 + 1, maxX);
        var y1 = Mathf.Min(y0 + 1, maxY);
        var tx = Mathf.Clamp01(sourceX - Mathf.Floor(sourceX));
        var ty = Mathf.Clamp01(sourceY - Mathf.Floor(sourceY));

        var c00 = item.sourcePixels[y0 * item.sourceWidth + x0];
        var c10 = item.sourcePixels[y0 * item.sourceWidth + x1];
        var c01 = item.sourcePixels[y1 * item.sourceWidth + x0];
        var c11 = item.sourcePixels[y1 * item.sourceWidth + x1];
        var w00 = (1f - tx) * (1f - ty);
        var w10 = tx * (1f - ty);
        var w01 = (1f - tx) * ty;
        var w11 = tx * ty;
        var a00 = c00.a / 255f;
        var a10 = c10.a / 255f;
        var a01 = c01.a / 255f;
        var a11 = c11.a / 255f;
        var alpha = a00 * w00 + a10 * w10 + a01 * w01 + a11 * w11;
        if (alpha <= 0.00001f) return new Color32(0, 0, 0, 0);

        var red = (c00.r * a00 * w00 + c10.r * a10 * w10 + c01.r * a01 * w01 + c11.r * a11 * w11) / alpha;
        var green = (c00.g * a00 * w00 + c10.g * a10 * w10 + c01.g * a01 * w01 + c11.g * a11 * w11) / alpha;
        var blue = (c00.b * a00 * w00 + c10.b * a10 * w10 + c01.b * a01 * w01 + c11.b * a11 * w11) / alpha;
        return new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(red), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(green), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(blue), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255));
    }

    private static void WritePageAssets(List<Page> pages, string atlasFolder, string cleanGroup, string ruleSuffix,
        BuildingCardAtlasBuildSettings settings, Shader shader)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var texturePath = atlasFolder + "/T_" + cleanGroup + "_Atlas_" + ruleSuffix + "_" + i + ".png";
            var materialPath = atlasFolder + "/M_" + cleanGroup + "_Atlas_" + ruleSuffix + "_" + i + ".mat";

            var generated = new Texture2D(page.size, page.size, TextureFormat.RGBA32, false, false);
            generated.SetPixels32(page.pixels);
            generated.Apply(false, false);
            var bytes = generated.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(generated);
            File.WriteAllBytes(AssetPathToFullPath(texturePath), bytes);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Cannot configure atlas texture: " + texturePath);
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = settings.mipmaps;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = page.size;
            importer.userData = "ZXX_BUILDING_CARD_ATLAS:" + cleanGroup + ":" + ruleSuffix;
            importer.SaveAndReimport();
            page.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);

            page.material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (page.material == null)
            {
                page.material = new Material(shader) { name = Path.GetFileNameWithoutExtension(materialPath) };
                AssetDatabase.CreateAsset(page.material, materialPath);
            }
            else if (page.material.shader != shader)
            {
                page.material.shader = shader;
            }
            page.material.SetTexture("_BaseMap", page.texture);
            page.material.SetTextureScale("_BaseMap", Vector2.one);
            page.material.SetTextureOffset("_BaseMap", Vector2.zero);
            page.material.SetColor("_BaseColor", Color.white);
            page.material.SetFloat("_UseAtlasUV", 1f);
            EditorUtility.SetDirty(page.material);
        }
    }

    private static void ApplyAtlasResults(List<Item> items, List<Page> pages, string ruleSuffix)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var rect = item.atlasUvRect;
            item.mesh.SetUVs(AtlasUvChannel, new[]
            {
                new Vector2(rect.xMin, rect.yMin),
                new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax),
                new Vector2(rect.xMin, rect.yMax)
            });
            item.mesh.SetUVs(LegacyAtlasUvChannel, new List<Vector2>());
            EditorUtility.SetDirty(item.mesh);

            item.metadata.atlasPage = item.pageIndex;
            item.metadata.atlasUvRect = rect;
            item.metadata.atlasMaterialGuid = AssetDatabase.AssetPathToGUID(
                AssetDatabase.GetAssetPath(pages[item.pageIndex].material));
            item.metadata.atlasRuleSuffix = ruleSuffix;
            item.metadata.renderMode = BuildingCardRenderMode.Atlas;
            ApplyMaterial(item.prefabPath, pages[item.pageIndex].material);
            BuildingCardMetadataStore.Save(item.prefabPath, item.metadata);
        }
    }

    private static void ApplyMaterial(string prefabPath, Material material)
    {
        var root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            var renderer = root.GetComponentInChildren<MeshRenderer>(true);
            if (renderer == null) throw new InvalidOperationException("Building Card renderer is missing: " + prefabPath);
            BuildingCardTransparentSortUtility.AlignBoundsCenterToRoot(root);
            if (renderer.sharedMaterial == material) return;
            renderer.sharedMaterial = material;
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static void DeleteObsoletePages(string atlasFolder, string cleanGroup, string ruleSuffix, int pageCount)
    {
        DeleteObsoleteAssets(atlasFolder, "T_" + cleanGroup + "_Atlas_" + ruleSuffix + "_", ".png", pageCount);
        DeleteObsoleteAssets(atlasFolder, "M_" + cleanGroup + "_Atlas_" + ruleSuffix + "_", ".mat", pageCount);
    }

    private static void DeleteLegacyPages(string atlasFolder, string cleanGroup)
    {
        DeleteObsoleteAssets(atlasFolder, "T_" + cleanGroup + "_Atlas_", ".png", 0);
        DeleteObsoleteAssets(atlasFolder, "M_" + cleanGroup + "_Atlas_", ".mat", 0);
    }

    private static void DeleteObsoleteAssets(string atlasFolder, string prefix, string extension, int pageCount)
    {
        var fullFolder = AssetPathToFullPath(atlasFolder);
        if (!Directory.Exists(fullFolder)) return;
        var files = Directory.GetFiles(fullFolder, prefix + "*" + extension, SearchOption.TopDirectoryOnly);
        for (var i = 0; i < files.Length; i++)
        {
            var fileName = Path.GetFileNameWithoutExtension(files[i]);
            if (!int.TryParse(fileName.Substring(prefix.Length), out var page) || page < pageCount) continue;
            AssetDatabase.DeleteAsset(FullPathToAssetPath(files[i]));
        }
    }

    private static int AlignUp(int value, int alignment)
    {
        return (value + alignment - 1) / alignment * alignment;
    }

    private static string GetRuleSuffix(bool fillAtlas)
    {
        return fillAtlas ? "Fill" : "Fixed";
    }

    private static string SanitizeAssetName(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        return value;
    }

    private static void EnsureAssetFolder(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath)) return;
        Directory.CreateDirectory(AssetPathToFullPath(assetPath));
        AssetDatabase.Refresh();
    }

    private static string AssetPathToFullPath(string assetPath)
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
    }

    private static string FullPathToAssetPath(string fullPath)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
        var normalized = Path.GetFullPath(fullPath).Replace('\\', '/');
        return normalized.Substring(projectRoot.Length + 1);
    }
}

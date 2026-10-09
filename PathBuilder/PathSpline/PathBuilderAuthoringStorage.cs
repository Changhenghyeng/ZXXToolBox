using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ZXXTools.PathBuilder
{
    /// <summary>
    /// Keeps unbaked Path Builder data independent from the EditorWindow.
    /// Scene objects are associated with project assets through a hidden marker
    /// because a project asset cannot serialize a reference to a scene Transform.
    /// </summary>
    internal static class PathBuilderAuthoringStorage
    {
        private const string AssetFolder =
            "Assets/Generated/PathBuilder/Working/Settings";
        private const string MarkerPrefix = "__PathBuilderSession_";
        private const string MountainPreviewName = "[Live Preview] Mountains";
        private const string RiverPreviewName = "[Live Preview] River";
        private const int MaximumRecoveredKnots = 32;

        public static PathSpline Create(PathBuildMode mode)
        {
            EnsureAssetFolder(AssetFolder);

            PathSpline builder = ScriptableObject.CreateInstance<PathSpline>();
            builder.mode = mode;
            builder.EnsureDefaults();
            string sessionId = builder.EnsureSessionId();
            string modeName = mode == PathBuildMode.River
                ? "River"
                : "Mountain";
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{AssetFolder}/{modeName}_{sessionId.Substring(0, 8)}.asset");
            AssetDatabase.CreateAsset(builder, assetPath);
            EditorUtility.SetDirty(builder);
            return builder;
        }

        public static void SetMountainFacingCamera(
            PathSpline builder,
            Camera camera)
        {
            if (!builder)
                return;

            builder.MountainFacingCamera = camera;
            builder.MountainFacingCameraGlobalId = camera
                ? GlobalObjectId.GetGlobalObjectIdSlow(camera).ToString()
                : string.Empty;
            EditorUtility.SetDirty(builder);
        }

        public static Camera ResolveMountainFacingCamera(PathSpline builder)
        {
            if (!builder)
                return null;
            if (builder.MountainFacingCamera)
                return builder.MountainFacingCamera;

            string identifier = builder.MountainFacingCameraGlobalId;
            if (string.IsNullOrEmpty(identifier)
                || !GlobalObjectId.TryParse(
                    identifier,
                    out GlobalObjectId globalObjectId))
            {
                return null;
            }

            Camera camera = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(
                globalObjectId) as Camera;
            builder.MountainFacingCamera = camera;
            return camera;
        }

        public static void Bind(PathSpline builder, Transform anchor)
        {
            if (!builder || !anchor)
                return;

            EnsurePersistent(builder);
            builder.Anchor = anchor;
            builder.GeneratedRoot = FindPreviewRoot(anchor, builder.mode);
            anchor.gameObject.hideFlags |= HideFlags.DontSaveInBuild;
            EnsureMarker(anchor, builder.EnsureSessionId());
            EditorUtility.SetDirty(builder);
        }

        public static bool TryRebind(PathSpline builder)
        {
            if (!builder)
                return false;

            if (IsValidSceneTransform(builder.Anchor))
            {
                Bind(builder, builder.Anchor);
                return true;
            }

            string sessionId = builder.EnsureSessionId();
            Transform anchor = FindAnchor(sessionId);
            if (!anchor)
                return false;

            Bind(builder, anchor);
            return true;
        }

        public static bool TryRestoreSelected(
            PathBuildMode mode,
            out PathSpline builder)
        {
            if (!TryRestoreSelected(out builder)
                || builder.mode != mode)
            {
                builder = null;
                return false;
            }
            return true;
        }

        public static bool TryRestoreSelected(out PathSpline builder)
        {
            builder = null;
            Transform selected = Selection.activeTransform;
            if (!IsValidSceneTransform(selected))
                return false;

            Transform anchor = FindMarkedAncestor(selected, out string sessionId);
            if (!anchor || string.IsNullOrEmpty(sessionId))
                return false;

            builder = LoadSession(sessionId);
            if (!builder)
            {
                builder = null;
                return false;
            }

            Transform previewRoot = FindPreviewRoot(anchor, builder.mode);
            bool selectedAuthoringRoot = selected == anchor;
            bool selectedGeneratedObject = previewRoot
                && (selected == previewRoot || selected.IsChildOf(previewRoot));
            if (!selectedAuthoringRoot && !selectedGeneratedObject)
            {
                builder = null;
                return false;
            }

            Transform boundAnchor = IsValidSceneTransform(builder.Anchor)
                ? builder.Anchor
                : FindAnchor(sessionId);
            if (boundAnchor && boundAnchor != anchor)
            {
                return TryForkDuplicatedSession(
                    builder, anchor, sessionId, out builder);
            }

            Bind(builder, anchor);
            return true;
        }

        public static bool IsAuthoringHierarchy(Transform transform)
        {
            return IsValidSceneTransform(transform)
                && FindMarkedAncestor(transform, out _) != null;
        }

        public static List<PathSpline> GetSelectedBuilders(PathBuildMode mode)
        {
            List<PathSpline> result = new List<PathSpline>();
            HashSet<int> anchors = new HashSet<int>();
            Transform[] selectedTransforms = Selection.transforms;
            for (int i = 0; i < selectedTransforms.Length; i++)
            {
                Transform selected = selectedTransforms[i];
                if (!IsValidSceneTransform(selected))
                    continue;

                Transform anchor = FindMarkedAncestor(
                    selected,
                    out string sessionId);
                // Batch operations intentionally accept only the authoring roots.
                // Selecting generated children must not silently include a path.
                if (!anchor || selected != anchor
                    || string.IsNullOrEmpty(sessionId)
                    || !anchors.Add(anchor.GetInstanceID()))
                {
                    continue;
                }

                PathSpline builder = LoadSession(sessionId);
                if (!builder || builder.mode != mode)
                    continue;

                if (IsValidSceneTransform(builder.Anchor)
                    && builder.Anchor != anchor)
                {
                    if (!TryForkDuplicatedSession(
                        builder,
                        anchor,
                        sessionId,
                        out builder))
                    {
                        continue;
                    }
                }
                else
                {
                    Bind(builder, anchor);
                }
                result.Add(builder);
            }

            return result;
        }

        public static List<PathSpline> GetSceneBuilders(
            PathBuildMode mode,
            Scene scene)
        {
            List<PathSpline> result = new List<PathSpline>();
            if (!scene.IsValid())
                return result;

            HashSet<int> anchors = new HashSet<int>();
            Transform[] transforms = Resources.FindObjectsOfTypeAll<Transform>();
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform marker = transforms[i];
                if (!IsValidSceneTransform(marker)
                    || marker.gameObject.scene != scene
                    || !marker.parent
                    || !marker.name.StartsWith(
                        MarkerPrefix,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                Transform anchor = marker.parent;
                if (!anchors.Add(anchor.GetInstanceID()))
                    continue;
                string sessionId = marker.name.Substring(MarkerPrefix.Length);
                PathSpline builder = LoadSession(sessionId);
                if (!builder || builder.mode != mode)
                    continue;

                if (IsValidSceneTransform(builder.Anchor)
                    && builder.Anchor != anchor)
                {
                    if (!TryForkDuplicatedSession(
                        builder,
                        anchor,
                        sessionId,
                        out builder))
                    {
                        continue;
                    }
                }
                else
                {
                    Bind(builder, anchor);
                }
                result.Add(builder);
            }

            result.Sort((left, right) => string.Compare(
                left && left.Anchor ? left.Anchor.name : string.Empty,
                right && right.Anchor ? right.Anchor.name : string.Empty,
                StringComparison.OrdinalIgnoreCase));
            return result;
        }

        public static bool TryRestore(
            PathBuildMode mode,
            out PathSpline builder)
        {
            if (TryRestoreSelected(mode, out builder))
                return true;

            // A window can be closed without a domain reload. In that case the
            // former transient object may still contain the exact spline data.
            PathSpline[] loaded = Resources.FindObjectsOfTypeAll<PathSpline>();
            for (int i = 0; i < loaded.Length; i++)
            {
                PathSpline candidate = loaded[i];
                if (!candidate || EditorUtility.IsPersistent(candidate)
                    || candidate.mode != mode
                    || !IsValidSceneTransform(candidate.Anchor))
                {
                    continue;
                }

                EnsurePersistent(candidate);
                Bind(candidate, candidate.Anchor);
                builder = candidate;
                return true;
            }

            if (AssetDatabase.IsValidFolder(AssetFolder))
            {
                string[] guids = AssetDatabase.FindAssets(
                    "t:PathSpline",
                    new[] { AssetFolder });
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    PathSpline candidate =
                        AssetDatabase.LoadAssetAtPath<PathSpline>(path);
                    if (!candidate || candidate.mode != mode)
                        continue;
                    if (!TryRebind(candidate))
                        continue;

                    builder = candidate;
                    return true;
                }
            }

            if (mode == PathBuildMode.River
                && TryRecoverLegacyRiver(out builder))
            {
                return true;
            }

            builder = null;
            return false;
        }

        public static void Save(PathSpline builder)
        {
            if (!builder)
                return;
            EnsurePersistent(builder);
            if (builder.Anchor)
                Bind(builder, builder.Anchor);
            EditorUtility.SetDirty(builder);
            AssetDatabase.SaveAssets();
        }

        public static void Delete(PathSpline builder)
        {
            if (!builder)
                return;

            string assetPath = AssetDatabase.GetAssetPath(builder);
            if (!string.IsNullOrEmpty(assetPath))
                AssetDatabase.DeleteAsset(assetPath);
            else
                Object.DestroyImmediate(builder);
        }

        private static void EnsurePersistent(PathSpline builder)
        {
            if (!builder || EditorUtility.IsPersistent(builder))
                return;

            EnsureAssetFolder(AssetFolder);
            builder.hideFlags = HideFlags.None;
            string id = builder.EnsureSessionId();
            string modeName = builder.mode == PathBuildMode.River
                ? "River"
                : "Mountain";
            string path = AssetDatabase.GenerateUniqueAssetPath(
                $"{AssetFolder}/{modeName}_{id.Substring(0, 8)}.asset");
            AssetDatabase.CreateAsset(builder, path);
            EditorUtility.SetDirty(builder);
        }

        private static PathSpline LoadSession(string sessionId)
        {
            if (!AssetDatabase.IsValidFolder(AssetFolder))
                return null;

            string[] guids = AssetDatabase.FindAssets(
                "t:PathSpline",
                new[] { AssetFolder });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                PathSpline candidate =
                    AssetDatabase.LoadAssetAtPath<PathSpline>(path);
                if (candidate && candidate.SessionId == sessionId)
                    return candidate;
            }
            return null;
        }

        private static Transform FindAnchor(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;

            string markerName = MarkerPrefix + sessionId;
            Transform[] transforms = Resources.FindObjectsOfTypeAll<Transform>();
            Transform best = null;
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform marker = transforms[i];
                if (!IsValidSceneTransform(marker)
                    || marker.name != markerName
                    || !marker.parent)
                {
                    continue;
                }
                Transform candidate = marker.parent;
                if (!best
                    || candidate.gameObject.scene.handle
                        < best.gameObject.scene.handle
                    || (candidate.gameObject.scene.handle
                            == best.gameObject.scene.handle
                        && candidate.GetSiblingIndex()
                            < best.GetSiblingIndex()))
                {
                    best = candidate;
                }
            }
            return best;
        }

        private static bool TryForkDuplicatedSession(
            PathSpline source,
            Transform duplicatedAnchor,
            string formerSessionId,
            out PathSpline fork)
        {
            fork = null;
            if (!source || !IsValidSceneTransform(duplicatedAnchor))
                return false;

            try
            {
                fork = Object.Instantiate(source);
                fork.name = source.name + " Copy";
                fork.Anchor = null;
                fork.GeneratedRoot = null;
                string newSessionId = fork.RegenerateSessionId();

                MakePreviewResourcesIndependent(
                    FindPreviewRoot(duplicatedAnchor, source.mode));
                ReplaceMarker(
                    duplicatedAnchor,
                    formerSessionId,
                    newSessionId);
                Bind(fork, duplicatedAnchor);
                Save(fork);

                Debug.Log(
                    $"[Path Builder] 已为复制的路径创建独立会话："
                    + $"{duplicatedAnchor.name}",
                    duplicatedAnchor);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, duplicatedAnchor);
                if (fork && !EditorUtility.IsPersistent(fork))
                    Object.DestroyImmediate(fork);
                fork = null;
                return false;
            }
        }

        private static void ReplaceMarker(
            Transform anchor,
            string formerSessionId,
            string newSessionId)
        {
            string formerName = MarkerPrefix + formerSessionId;
            string newName = MarkerPrefix + newSessionId;
            for (int i = 0; i < anchor.childCount; i++)
            {
                Transform child = anchor.GetChild(i);
                if (child.name != formerName)
                    continue;

                Undo.RecordObject(child.gameObject, "Fork Path Builder Session");
                child.name = newName;
                child.gameObject.hideFlags = HideFlags.HideInHierarchy
                    | HideFlags.DontSaveInBuild;
                EditorUtility.SetDirty(child.gameObject);
                EditorSceneManager.MarkSceneDirty(anchor.gameObject.scene);
                return;
            }

            EnsureMarker(anchor, newSessionId);
        }

        private static void MakePreviewResourcesIndependent(Transform preview)
        {
            if (!preview)
                return;

            Dictionary<Mesh, Mesh> meshes = new Dictionary<Mesh, Mesh>();
            MeshFilter[] filters = preview.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                Mesh source = filters[i].sharedMesh;
                if (!source || AssetDatabase.Contains(source))
                    continue;
                if (!meshes.TryGetValue(source, out Mesh copy))
                {
                    copy = Object.Instantiate(source);
                    copy.name = source.name;
                    copy.hideFlags = source.hideFlags;
                    meshes.Add(source, copy);
                }
                Undo.RecordObject(filters[i], "Fork Path Builder Preview");
                filters[i].sharedMesh = copy;
            }

            MeshCollider[] colliders = preview
                .GetComponentsInChildren<MeshCollider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Mesh source = colliders[i].sharedMesh;
                if (!source || !meshes.TryGetValue(source, out Mesh copy))
                    continue;
                Undo.RecordObject(colliders[i], "Fork Path Builder Preview");
                colliders[i].sharedMesh = copy;
            }

            Dictionary<Material, Material> materials =
                new Dictionary<Material, Material>();
            Renderer[] renderers = preview.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] shared = renderers[i].sharedMaterials;
                bool changed = false;
                for (int materialIndex = 0;
                    materialIndex < shared.Length;
                    materialIndex++)
                {
                    Material source = shared[materialIndex];
                    if (!source || AssetDatabase.Contains(source))
                        continue;
                    if (!materials.TryGetValue(source, out Material copy))
                    {
                        copy = Object.Instantiate(source);
                        copy.name = source.name;
                        copy.hideFlags = source.hideFlags;
                        materials.Add(source, copy);
                    }
                    shared[materialIndex] = copy;
                    changed = true;
                }
                if (!changed)
                    continue;
                Undo.RecordObject(renderers[i], "Fork Path Builder Preview");
                renderers[i].sharedMaterials = shared;
            }
        }

        private static Transform FindMarkedAncestor(
            Transform selected,
            out string sessionId)
        {
            sessionId = null;
            for (Transform current = selected;
                 current != null;
                 current = current.parent)
            {
                for (int i = 0; i < current.childCount; i++)
                {
                    Transform child = current.GetChild(i);
                    if (!child.name.StartsWith(
                        MarkerPrefix,
                        StringComparison.Ordinal))
                    {
                        continue;
                    }

                    sessionId = child.name.Substring(MarkerPrefix.Length);
                    return current;
                }
            }
            return null;
        }

        private static void EnsureMarker(Transform anchor, string sessionId)
        {
            string markerName = MarkerPrefix + sessionId;
            for (int i = 0; i < anchor.childCount; i++)
            {
                Transform child = anchor.GetChild(i);
                if (child.name == markerName)
                    return;
            }

            GameObject marker = new GameObject(markerName);
            marker.transform.SetParent(anchor, false);
            marker.hideFlags = HideFlags.HideInHierarchy
                | HideFlags.DontSaveInBuild;
            EditorUtility.SetDirty(anchor.gameObject);
            EditorSceneManager.MarkSceneDirty(anchor.gameObject.scene);
        }

        private static Transform FindPreviewRoot(
            Transform anchor,
            PathBuildMode mode)
        {
            string expectedName = mode == PathBuildMode.River
                ? RiverPreviewName
                : MountainPreviewName;
            for (int i = 0; i < anchor.childCount; i++)
            {
                Transform child = anchor.GetChild(i);
                if (child.name == expectedName)
                    return child;
            }
            return null;
        }

        private static bool TryRecoverLegacyRiver(out PathSpline builder)
        {
            builder = null;
            Transform selected = Selection.activeTransform;
            Transform selectedAnchor = FindLegacyRiverAncestor(selected);
            if (selectedAnchor
                && TryRecoverLegacyRiver(selectedAnchor, out builder))
            {
                return true;
            }

            Transform[] transforms = Resources.FindObjectsOfTypeAll<Transform>();
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (!IsValidSceneTransform(candidate)
                    || candidate.name != "River Spline Authoring")
                {
                    continue;
                }
                if (TryRecoverLegacyRiver(candidate, out builder))
                    return true;
            }
            return false;
        }

        private static Transform FindLegacyRiverAncestor(Transform selected)
        {
            for (Transform current = selected;
                 current != null;
                 current = current.parent)
            {
                if (current.name == "River Spline Authoring"
                    && FindPreviewRoot(current, PathBuildMode.River))
                {
                    return current;
                }
            }
            return null;
        }

        private static bool TryRecoverLegacyRiver(
            Transform anchor,
            out PathSpline builder)
        {
            builder = null;
            Transform preview = FindPreviewRoot(anchor, PathBuildMode.River);
            if (!preview)
                return false;

            MeshFilter filter = preview.GetComponent<MeshFilter>();
            Mesh mesh = filter ? filter.sharedMesh : null;
            if (!mesh || mesh.vertexCount < 4 || mesh.vertexCount % 2 != 0)
                return false;

            Vector3[] vertices = mesh.vertices;
            Vector2[] uv = mesh.uv;
            int sectionCount = vertices.Length / 2;
            List<Vector3> centers = new List<Vector3>(sectionCount);
            List<float> widths = new List<float>(sectionCount);
            for (int section = 0; section < sectionCount; section++)
            {
                Vector3 left = preview.TransformPoint(vertices[section * 2]);
                Vector3 right = preview.TransformPoint(vertices[section * 2 + 1]);
                centers.Add(anchor.InverseTransformPoint((left + right) * 0.5f));
                widths.Add(Vector3.Distance(left, right));
            }

            bool closed = sectionCount > 3
                && Vector3.Distance(centers[0], centers[sectionCount - 1])
                    < Mathf.Max(0.05f, widths[0] * 0.05f);
            int uniqueSectionCount = closed ? sectionCount - 1 : sectionCount;
            int knotCount = Mathf.Min(MaximumRecoveredKnots, uniqueSectionCount);
            if (knotCount < 2)
                return false;

            builder = Create(PathBuildMode.River);
            builder.projectToSurface = false;
            builder.closed = closed;
            MeshRenderer renderer = preview.GetComponent<MeshRenderer>();
            if (renderer)
                builder.riverMaterial = renderer.sharedMaterial;

            for (int knotIndex = 0; knotIndex < knotCount; knotIndex++)
            {
                float normalized = closed
                    ? knotIndex / (float)knotCount
                    : knotIndex / (float)(knotCount - 1);
                int sectionIndex = Mathf.Clamp(
                    Mathf.RoundToInt(normalized * (uniqueSectionCount - 1)),
                    0,
                    uniqueSectionCount - 1);
                PathKnot knot = new PathKnot(centers[sectionIndex])
                {
                    width = Mathf.Max(0.01f, widths[sectionIndex])
                };
                builder.knots.Add(knot);
            }

            for (int i = 0; i < builder.knots.Count; i++)
                builder.SmoothKnot(i);

            if (uv != null && uv.Length == vertices.Length)
            {
                float uvLength = Mathf.Abs(uv[uv.Length - 1].y - uv[0].y);
                float worldLength = 0f;
                for (int i = 1; i < centers.Count; i++)
                    worldLength += Vector3.Distance(centers[i - 1], centers[i]);
                if (uvLength > 0.0001f && worldLength > 0.0001f)
                {
                    builder.riverUvLength = uvLength;
                    builder.riverUvRepeatLength = worldLength / uvLength;
                }
            }

            if (centers.Count > 1)
            {
                float length = 0f;
                for (int i = 1; i < centers.Count; i++)
                    length += Vector3.Distance(centers[i - 1], centers[i]);
                builder.riverSegmentLength = Mathf.Max(
                    0.05f,
                    length / (centers.Count - 1));
            }

            Bind(builder, anchor);
            builder.GeneratedRoot = preview;
            EditorUtility.SetDirty(builder);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static bool IsValidSceneTransform(Transform transform)
        {
            return transform
                && transform.gameObject.scene.IsValid()
                && !EditorUtility.IsPersistent(transform);
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = Path.GetDirectoryName(folder)
                ?.Replace('\\', '/');
            string name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent))
                return;
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}

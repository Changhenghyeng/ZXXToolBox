using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ZXXTools.FBXMeshEditor
{
    /// <summary>保存一次单模型顶点编辑会话的数据与操作。</summary>
    internal sealed class FBXMeshEditSession
    {
        private const string WorkingMeshFolder =
            "Assets/Generated/FBXMeshEditor/Working/Models";
        private const string WorkingRecordFolder =
            "Assets/Generated/FBXMeshEditor/Working/Settings";
        private readonly HashSet<int> _selectedVertices = new HashSet<int>();
        private int[] _logicalVertexRepresentatives = System.Array.Empty<int>();
        private readonly Dictionary<int, List<int>> _logicalVertexGroups = new Dictionary<int, List<int>>();
        private readonly HashSet<int> _selectedEdges = new HashSet<int>();
        private readonly HashSet<int> _selectedFaces = new HashSet<int>();
        private readonly FBXMeshTopology _topology = new FBXMeshTopology();
        private Mesh _sourceMesh;
        private string _workingAssetPath;
        private FBXMeshEditRecord _record;
        private bool _moveHistoryCaptured;
        private FBXMeshSnapshot _pendingUndoSnapshot;
        private readonly Dictionary<int, float> _softMoveWeights = new Dictionary<int, float>();
        private int _selectionRevision;
        private int _softWeightsRevision = -1;
        private bool _softWeightsEnabled;
        private float _softWeightsRadius = -1f;
        private Vector3[] _moveVertices;
        private Vector3[] _bendSourceVertices;
        // 选择历史只保存组件索引，容量可适当提高，避免连续点选过早挤掉模型编辑历史。
        private const int MaxHistorySteps = 50;

        public MeshFilter MeshFilter { get; private set; }
        public Mesh EditableMesh { get; private set; }
        public Mesh SourceMesh => _sourceMesh;
        public string WorkingAssetPath => _workingAssetPath;
        public bool IsActive => MeshFilter != null && EditableMesh != null;
        public bool CanRevert => IsActive && _sourceMesh != null;
        public bool CanUndo => IsActive && _record != null && _record.undoHistory != null
            && _record.undoHistory.Count > 0;
        public bool CanRedo => IsActive && _record != null && _record.redoHistory != null
            && _record.redoHistory.Count > 0;
        public int UndoCount => CanUndo ? _record.undoHistory.Count : 0;
        public int RedoCount => CanRedo ? _record.redoHistory.Count : 0;
        public int SelectionRevision => _selectionRevision;
        public bool HasPendingTransform => _moveHistoryCaptured;
        public IReadOnlyCollection<int> SelectedVertices => _selectedVertices;
        public IReadOnlyCollection<int> SelectedEdges => _selectedEdges;
        public IReadOnlyCollection<int> SelectedFaces => _selectedFaces;
        public FBXMeshTopology Topology => _topology;
        public FBXMeshComponentMode ComponentMode { get; private set; } = FBXMeshComponentMode.Vertex;
        public int SelectedComponentCount => ComponentMode == FBXMeshComponentMode.Vertex
            ? _selectedVertices.Count
            : ComponentMode == FBXMeshComponentMode.Edge ? _selectedEdges.Count : _selectedFaces.Count;

        public bool Begin(GameObject target, out string error)
        {
            error = string.Empty;
            if (target == null)
            {
                error = "请先选择一个静态模型。";
                return false;
            }

            MeshFilter filter = target.GetComponent<MeshFilter>();
            MeshRenderer renderer = target.GetComponent<MeshRenderer>();
            if (filter == null || renderer == null || filter.sharedMesh == null)
            {
                error = "所选对象必须同时包含 MeshFilter、MeshRenderer 和有效 Mesh。";
                return false;
            }

            End(false);

            string currentMeshPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
            if (IsWorkingMeshPath(currentMeshPath))
            {
                MeshFilter = filter;
                EditableMesh = filter.sharedMesh;
                _workingAssetPath = currentMeshPath;
                _record = FindRecord(EditableMesh);
                if (_record == null)
                    _record = CreateRecord(EditableMesh, null);
                _sourceMesh = _record != null ? _record.sourceMesh : null;
                if (_record != null) EnsureHistoryLists();
                _selectedVertices.Clear();
                ClearComponentSelection();
                RebuildTopology();
                return true;
            }

            _sourceMesh = filter.sharedMesh;
            EditableMesh = Object.Instantiate(_sourceMesh);
            EditableMesh.name = _sourceMesh.name + "_MeshEditWorking";
            EnsureWorkingFolder();
            _workingAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{WorkingMeshFolder}/{EditableMesh.name}.asset");
            AssetDatabase.CreateAsset(EditableMesh, _workingAssetPath);
            _record = CreateRecord(EditableMesh, _sourceMesh);
            AssetDatabase.SaveAssetIfDirty(EditableMesh);
            AssetDatabase.SaveAssetIfDirty(_record);
            MeshFilter = filter;

            filter.sharedMesh = EditableMesh;
            EditorUtility.SetDirty(filter);
            _selectedVertices.Clear();
            ClearComponentSelection();
            RebuildTopology();
            return true;
        }

        public void End(bool restoreSource)
        {
            FlushPendingHistory();
            if (EditableMesh != null)
                AssetDatabase.SaveAssetIfDirty(EditableMesh);
            if (_record != null)
                AssetDatabase.SaveAssetIfDirty(_record);

            if (restoreSource && MeshFilter != null && _sourceMesh != null)
            {
                MeshFilter.sharedMesh = _sourceMesh;
                EditorUtility.SetDirty(MeshFilter);
            }

            _selectedVertices.Clear();
            _selectedEdges.Clear();
            _selectedFaces.Clear();
            MeshFilter = null;
            EditableMesh = null;
            _sourceMesh = null;
            _workingAssetPath = null;
            _record = null;
        }

        public void FlushPendingHistory()
        {
            if (_pendingUndoSnapshot == null)
                return;
            CommitPendingUndoSnapshot();
        }

        public bool Revert()
        {
            if (!CanRevert)
                return false;

            string assetPath = _workingAssetPath;
            string recordPath = _record != null ? AssetDatabase.GetAssetPath(_record) : null;
            End(true);
            if (!string.IsNullOrEmpty(assetPath))
                AssetDatabase.DeleteAsset(assetPath);
            if (!string.IsNullOrEmpty(recordPath))
                AssetDatabase.DeleteAsset(recordPath);
            return true;
        }

        public void SaveWorkingMesh()
        {
            if (EditableMesh != null)
                AssetDatabase.SaveAssetIfDirty(EditableMesh);
        }

        private static void EnsureWorkingFolder()
        {
            EnsureAssetFolder(WorkingMeshFolder);
            EnsureAssetFolder(WorkingRecordFolder);
        }

        public static bool IsWorkingMesh(Mesh mesh)
        {
            return mesh != null && IsWorkingMeshPath(AssetDatabase.GetAssetPath(mesh));
        }

        /// <summary>覆盖源 FBX 成功后，将当前对象切回重新导入的 Mesh，并清理工作副本。</summary>
        public void CompleteOverwrite(Mesh importedMesh)
        {
            if (!IsActive || importedMesh == null)
                return;

            FlushPendingHistory();
            string workingPath = _workingAssetPath;
            string recordPath = _record != null ? AssetDatabase.GetAssetPath(_record) : null;
            MeshFilter targetFilter = MeshFilter;

            targetFilter.sharedMesh = importedMesh;
            EditorUtility.SetDirty(targetFilter);
            End(false);

            if (!string.IsNullOrEmpty(workingPath))
                AssetDatabase.DeleteAsset(workingPath);
            if (!string.IsNullOrEmpty(recordPath))
                AssetDatabase.DeleteAsset(recordPath);
            AssetDatabase.SaveAssets();
        }

        public static bool IsAutoResumeSuppressed(Mesh mesh)
        {
            FBXMeshEditRecord record = FindRecord(mesh);
            return record != null && record.manualExitSuppressed;
        }

        public void SetManualExitSuppressed(bool suppressed)
        {
            if (_record == null) return;
            _record.manualExitSuppressed = suppressed;
            EditorUtility.SetDirty(_record);
            AssetDatabase.SaveAssetIfDirty(_record);
        }

        private static bool IsWorkingMeshPath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath)
                && assetPath.StartsWith(WorkingMeshFolder + "/", System.StringComparison.OrdinalIgnoreCase);
        }

        private static FBXMeshEditRecord FindRecord(Mesh workingMesh)
        {
            // 记录类位于 Editor 程序集时，部分 Unity/Tuanjie 版本无法通过自定义类型过滤稳定检索。
            // 因此扫描工作目录中的资产并尝试按真实类型加载。
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { WorkingRecordFolder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("_Record.asset", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                FBXMeshEditRecord record = AssetDatabase.LoadAssetAtPath<FBXMeshEditRecord>(path);
                if (record != null && record.workingMesh == workingMesh)
                    return record;
            }
            return null;
        }

        private static FBXMeshEditRecord CreateRecord(Mesh workingMesh, Mesh sourceMesh)
        {
            var record = ScriptableObject.CreateInstance<FBXMeshEditRecord>();
            record.sourceMesh = sourceMesh;
            record.workingMesh = workingMesh;
            record.undoHistory = new List<FBXMeshSnapshot>();
            record.redoHistory = new List<FBXMeshSnapshot>();
            record.dissolvedEdges = new List<int>();
            AssetDatabase.CreateAsset(
                record,
                AssetDatabase.GenerateUniqueAssetPath(
                    $"{WorkingRecordFolder}/{workingMesh.name}_Record.asset"));
            AssetDatabase.SaveAssetIfDirty(record);
            return record;
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = System.IO.Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string name = System.IO.Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) return;
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        public bool Contains(int index) => _selectedVertices.Contains(index);
        public bool IsLogicalVertexRepresentative(int index)
        {
            return index >= 0 && index < _logicalVertexRepresentatives.Length
                && _logicalVertexRepresentatives[index] == index;
        }
        public bool IsVertexLogicallyDissolved(int index) => _topology.IsVertexLogicallyDissolved(index);
        public float GetSoftSelectionWeight(int index)
        {
            return _softMoveWeights.TryGetValue(index, out float weight) ? weight : 0f;
        }

        public void UpdateSoftSelectionPreview(bool enabled, float radius)
        {
            UpdateSoftSelectionPreview(enabled, radius, null, _selectionRevision);
        }

        public void UpdateSoftSelectionPreview(bool enabled, float radius,
            IReadOnlyList<Vector3> worldSeeds, int collectiveRevision)
        {
            if (_moveHistoryCaptured) return;
            if (_softWeightsRevision == collectiveRevision
                && _softWeightsEnabled == enabled
                && Mathf.Approximately(_softWeightsRadius, radius)) return;
            BuildSoftMoveWeights(enabled, radius, worldSeeds, collectiveRevision);
        }
        public bool ContainsEdge(int index) => _selectedEdges.Contains(index);
        public bool ContainsFace(int index) => _selectedFaces.Contains(index);

        public void SetComponentMode(FBXMeshComponentMode mode)
        {
            if (ComponentMode == mode)
                return;
            FBXMeshSnapshot before = CaptureSelectionSnapshot();
            ComponentMode = mode;
            ClearComponentSelection();
            PushSelectionUndo(before);
        }

        public void ClearComponentSelection()
        {
            _selectedVertices.Clear();
            _selectedEdges.Clear();
            _selectedFaces.Clear();
            InvalidateSoftWeights();
        }

        public void ClearSelection()
        {
            if (GetActiveSelection().Count == 0) return;
            FBXMeshSnapshot before = CaptureSelectionSnapshot();
            GetActiveSelection().Clear();
            InvalidateSoftWeights();
            PushSelectionUndo(before);
        }

        public void SelectComponent(int index, bool additive, bool subtractive)
        {
            FBXMeshSnapshot before = CaptureSelectionSnapshot();
            HashSet<int> selection = GetActiveSelection();
            if (ComponentMode == FBXMeshComponentMode.Vertex)
            {
                if (!additive && !subtractive) selection.Clear();
                foreach (int vertex in GetLogicalVertexGroup(index))
                {
                    if (subtractive) selection.Remove(vertex);
                    else selection.Add(vertex);
                }
                InvalidateSoftWeights();
                PushSelectionUndoIfChanged(before);
                return;
            }
            if (subtractive)
            {
                selection.Remove(index);
                InvalidateSoftWeights();
                PushSelectionUndoIfChanged(before);
                return;
            }
            if (!additive)
                selection.Clear();
            if (index >= 0)
                selection.Add(index);
            InvalidateSoftWeights();
            PushSelectionUndoIfChanged(before);
        }

        public void SelectComponentRange(IEnumerable<int> indices, bool additive, bool subtractive)
        {
            FBXMeshSnapshot before = CaptureSelectionSnapshot();
            HashSet<int> selection = GetActiveSelection();
            if (!additive && !subtractive)
                selection.Clear();
            foreach (int index in indices)
            {
                if (ComponentMode == FBXMeshComponentMode.Vertex)
                {
                    foreach (int vertex in GetLogicalVertexGroup(index))
                    {
                        if (subtractive) selection.Remove(vertex);
                        else selection.Add(vertex);
                    }
                }
                else if (subtractive) selection.Remove(index);
                else selection.Add(index);
            }
            InvalidateSoftWeights();
            PushSelectionUndoIfChanged(before);
        }

        private HashSet<int> GetActiveSelection()
        {
            if (ComponentMode == FBXMeshComponentMode.Edge) return _selectedEdges;
            if (ComponentMode == FBXMeshComponentMode.Face) return _selectedFaces;
            return _selectedVertices;
        }

        private IEnumerable<int> GetLogicalVertexGroup(int index)
        {
            if (index < 0 || index >= _logicalVertexRepresentatives.Length)
                yield break;
            int representative = _logicalVertexRepresentatives[index];
            if (!_logicalVertexGroups.TryGetValue(representative, out List<int> group))
                yield break;
            foreach (int vertex in group) yield return vertex;
        }

        public Vector3 GetSelectionCenterWorld()
        {
            HashSet<int> affected = GetAffectedVertices();
            Vector3[] vertices = EditableMesh.vertices;
            Vector3 center = Vector3.zero;
            foreach (int index in affected)
                center += MeshFilter.transform.TransformPoint(vertices[index]);
            return center / affected.Count;
        }

        public bool TryGetSelectionBoundsWorld(out Bounds bounds)
        {
            HashSet<int> affected = GetAffectedVertices();
            if (!IsActive || affected.Count == 0)
            {
                bounds = default;
                return false;
            }

            Vector3[] vertices = EditableMesh.vertices;
            bool initialized = false;
            bounds = default;
            foreach (int index in affected)
            {
                Vector3 world = MeshFilter.transform.TransformPoint(vertices[index]);
                if (!initialized)
                {
                    bounds = new Bounds(world, Vector3.zero);
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(world);
                }
            }

            // 单点或很短的边也需要一个可用的聚焦范围，避免镜头贴得过近。
            float minimumSize = Mathf.Max(0.05f,
                MeshFilter.transform.TransformVector(EditableMesh.bounds.size).magnitude * 0.03f);
            Vector3 size = bounds.size;
            size.x = Mathf.Max(size.x, minimumSize);
            size.y = Mathf.Max(size.y, minimumSize);
            size.z = Mathf.Max(size.z, minimumSize);
            bounds.size = size;
            return true;
        }

        public void BeginMove(bool softSelectionEnabled, float softSelectionRadius)
        {
            BeginMove(softSelectionEnabled, softSelectionRadius, null, _selectionRevision);
        }

        public void BeginMove(bool softSelectionEnabled, float softSelectionRadius,
            IReadOnlyList<Vector3> worldSeeds, int collectiveRevision)
        {
            if (!IsActive || _moveHistoryCaptured)
                return;

            BuildSoftMoveWeights(softSelectionEnabled, softSelectionRadius,
                worldSeeds, collectiveRevision);
            if (_softMoveWeights.Count == 0) return;
            _pendingUndoSnapshot = CaptureCurrentSnapshot();
            _moveVertices = EditableMesh.vertices;
            EditableMesh.MarkDynamic();
            _moveHistoryCaptured = true;
        }

        public void BeginBend(bool bendWholeMesh, bool softSelectionEnabled,
            float softSelectionRadius, IReadOnlyList<Vector3> worldSeeds, int collectiveRevision)
        {
            if (!IsActive || _moveHistoryCaptured) return;
            if (bendWholeMesh)
            {
                _softMoveWeights.Clear();
                for (int i = 0; i < EditableMesh.vertexCount; i++) _softMoveWeights[i] = 1f;
                _pendingUndoSnapshot = CaptureCurrentSnapshot();
                _moveVertices = EditableMesh.vertices;
                EditableMesh.MarkDynamic();
                _moveHistoryCaptured = true;
            }
            else
            {
                BeginMove(softSelectionEnabled, softSelectionRadius, worldSeeds, collectiveRevision);
            }
            if (_moveHistoryCaptured) _bendSourceVertices = (Vector3[])_moveVertices.Clone();
        }

        public void BendSelection(Vector3 worldCenter, Quaternion bendRotation,
            float angleDegrees, float lowBound, float highBound)
        {
            if (!IsActive || !_moveHistoryCaptured || _bendSourceVertices == null) return;
            // Maya 规则：Low 位于负 Y，High 位于正 Y。Bound 收到 0 时，
            // 对应一侧的截面旋转角也是 0，因此范围外模型完全保持原位。
            lowBound = Mathf.Min(0f, lowBound);
            highBound = Mathf.Max(0f, highBound);
            float span = Mathf.Max(0.0001f, highBound - lowBound);
            float curvature = angleDegrees * Mathf.Deg2Rad / span;
            Quaternion inverseRotation = Quaternion.Inverse(bendRotation);
            foreach (KeyValuePair<int, float> pair in _softMoveWeights)
            {
                int index = pair.Key;
                Vector3 world = MeshFilter.transform.TransformPoint(_bendSourceVertices[index]);
                Vector3 p = inverseRotation * (world - worldCenter);
                float y = p.y;
                float clampedY = Mathf.Clamp(y, lowBound, highBound);
                Vector3 bent = p;
                if (Mathf.Abs(curvature) > 0.000001f)
                {
                    float theta = curvature * clampedY;
                    float sin = Mathf.Sin(theta);
                    float cos = Mathf.Cos(theta);
                    float outside = y - clampedY;
                    // Maya Bend：本地 Y 是长度轴，正/负 X 是曲率方向，Z 保持为弯曲平面的法向。
                    bent.y = sin / curvature - p.x * sin + outside * cos;
                    bent.x = (1f - cos) / curvature + p.x * cos + outside * sin;
                }
                Vector3 bentWorld = worldCenter + bendRotation * bent;
                Vector3 weightedWorld = Vector3.Lerp(world, bentWorld, pair.Value);
                _moveVertices[index] = MeshFilter.transform.InverseTransformPoint(weightedWorld);
            }
            ApplyTransformVertices();
        }

        public void MoveSelection(Vector3 worldDelta)
        {
            if (!IsActive || _softMoveWeights.Count == 0 || worldDelta.sqrMagnitude <= Mathf.Epsilon)
                return;

            Vector3 delta = MeshFilter.transform.InverseTransformVector(worldDelta);
            foreach (KeyValuePair<int, float> pair in _softMoveWeights)
                _moveVertices[pair.Key] += delta * pair.Value;

            // 拖动中只上传顶点；Bounds 和 Normals 在松手时统一计算，减少连续卡顿。
            EditableMesh.vertices = _moveVertices;
            EditorUtility.SetDirty(EditableMesh);
        }

        public void RotateSelection(Vector3 worldCenter, Quaternion worldDeltaRotation)
        {
            if (!IsActive || _softMoveWeights.Count == 0) return;
            foreach (KeyValuePair<int, float> pair in _softMoveWeights)
            {
                Quaternion weightedRotation = Quaternion.Slerp(
                    Quaternion.identity, worldDeltaRotation, pair.Value);
                Vector3 world = MeshFilter.transform.TransformPoint(_moveVertices[pair.Key]);
                world = worldCenter + weightedRotation * (world - worldCenter);
                _moveVertices[pair.Key] = MeshFilter.transform.InverseTransformPoint(world);
            }
            ApplyTransformVertices();
        }

        public void ScaleSelection(Vector3 worldCenter, Quaternion handleRotation, Vector3 scaleRatio)
        {
            if (!IsActive || _softMoveWeights.Count == 0) return;
            Quaternion inverseHandleRotation = Quaternion.Inverse(handleRotation);
            foreach (KeyValuePair<int, float> pair in _softMoveWeights)
            {
                Vector3 weightedScale = Vector3.Lerp(Vector3.one, scaleRatio, pair.Value);
                Vector3 world = MeshFilter.transform.TransformPoint(_moveVertices[pair.Key]);
                Vector3 handleSpace = inverseHandleRotation * (world - worldCenter);
                handleSpace = Vector3.Scale(handleSpace, weightedScale);
                world = worldCenter + handleRotation * handleSpace;
                _moveVertices[pair.Key] = MeshFilter.transform.InverseTransformPoint(world);
            }
            ApplyTransformVertices();
        }

        private void ApplyTransformVertices()
        {
            // 拖动中只上传顶点；Bounds 和 Normals 在松手时统一计算。
            EditableMesh.vertices = _moveVertices;
            EditorUtility.SetDirty(EditableMesh);
        }

        public void EndMove()
        {
            if (!IsActive || !_moveHistoryCaptured)
                return;

            EditableMesh.RecalculateBounds();
            EditableMesh.RecalculateNormals();
            EditorUtility.SetDirty(EditableMesh);
            _moveHistoryCaptured = false;
            _softMoveWeights.Clear();
            _moveVertices = null;
            _bendSourceVertices = null;
            InvalidateSoftWeights();
            // 松开手柄时立刻写入独立历史，确保紧接着按 Undo 也能撤回。
            // 这里只更新内存快照并标脏，不执行 AssetDatabase 导入，不会阻塞 Scene 交互。
            FinalizeMovePersistence();
        }

        public void CancelBendCapture()
        {
            if (!_moveHistoryCaptured) return;
            _moveHistoryCaptured = false;
            _pendingUndoSnapshot = null;
            _softMoveWeights.Clear();
            _moveVertices = null;
            _bendSourceVertices = null;
        }

        private void BuildSoftMoveWeights(bool enabled, float radius,
            IReadOnlyList<Vector3> externalWorldSeeds = null, int collectiveRevision = -1)
        {
            _softMoveWeights.Clear();
            _softWeightsRevision = collectiveRevision >= 0 ? collectiveRevision : _selectionRevision;
            _softWeightsEnabled = enabled;
            _softWeightsRadius = radius;
            HashSet<int> seeds = GetAffectedVertices();
            foreach (int seed in seeds) _softMoveWeights[seed] = 1f;
            if (!enabled || radius <= 0.0001f
                || (seeds.Count == 0 && (externalWorldSeeds == null || externalWorldSeeds.Count == 0)))
                return;

            Vector3[] localVertices = EditableMesh.vertices;
            int vertexCount = localVertices.Length;
            Transform targetTransform = MeshFilter.transform;
            var seedWorldPositions = externalWorldSeeds != null
                ? new List<Vector3>(externalWorldSeeds)
                : new List<Vector3>(seeds.Count);
            if (externalWorldSeeds == null)
            {
                foreach (int seed in seeds)
                {
                    if (seed >= 0 && seed < vertexCount)
                        seedWorldPositions.Add(targetTransform.TransformPoint(localVertices[seed]));
                }
            }
            if (seedWorldPositions.Count == 0) return;

            // 限制在当前 Mesh 内，按世界空间直线距离计算，不再沿拓扑表面绕行。
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 vertexWorld = targetTransform.TransformPoint(localVertices[i]);
                float nearestDistance = float.PositiveInfinity;
                for (int seedIndex = 0; seedIndex < seedWorldPositions.Count; seedIndex++)
                {
                    float distance = Vector3.Distance(vertexWorld, seedWorldPositions[seedIndex]);
                    if (distance < nearestDistance) nearestDistance = distance;
                }

                if (nearestDistance > radius) continue;
                float normalized = Mathf.Clamp01(nearestDistance / radius);
                float weight = 1f - Mathf.SmoothStep(0f, 1f, normalized);
                if (weight > 0.0001f) _softMoveWeights[i] = weight;
            }
        }

        private void InvalidateSoftWeights()
        {
            _selectionRevision++;
            _softWeightsRevision = -1;
        }

        private void FinalizeMovePersistence()
        {
            if (!IsActive)
                return;
            CommitPendingUndoSnapshot();
            // 交互期间只标脏，不触发 AssetDatabase 导入；退出编辑时统一落盘。
            EditorUtility.SetDirty(EditableMesh);
            if (_record != null) EditorUtility.SetDirty(_record);
        }

        public void RefreshAfterUndoRedo()
        {
            if (!IsActive)
                return;

            _moveHistoryCaptured = false;
            SynchronizeEditableMeshReference();
        }

        public bool PerformUndo()
        {
            if (!CanUndo)
                return false;

            EnsureHistoryLists();
            FBXMeshSnapshot snapshot = PopSnapshot(_record.undoHistory);
            AddSnapshot(_record.redoHistory, snapshot.selectionOnly);
            RestoreSnapshot(snapshot);
            return true;
        }

        public bool PerformRedo()
        {
            if (!CanRedo)
                return false;

            EnsureHistoryLists();
            FBXMeshSnapshot snapshot = PopSnapshot(_record.redoHistory);
            AddSnapshot(_record.undoHistory, snapshot.selectionOnly);
            RestoreSnapshot(snapshot);
            return true;
        }

        private void CommitPendingUndoSnapshot()
        {
            if (_record == null || _pendingUndoSnapshot == null)
                return;

            EnsureHistoryLists();
            _record.redoHistory.Clear();
            _record.undoHistory.Add(_pendingUndoSnapshot);
            _pendingUndoSnapshot = null;
            while (_record.undoHistory.Count > MaxHistorySteps)
                _record.undoHistory.RemoveAt(0);
            EditorUtility.SetDirty(_record);
        }

        private void AddSnapshot(List<FBXMeshSnapshot> target, bool selectionOnly = false)
        {
            target.Add(selectionOnly ? CaptureSelectionSnapshot() : CaptureCurrentSnapshot());
            while (target.Count > MaxHistorySteps)
                target.RemoveAt(0);
            EditorUtility.SetDirty(_record);
        }

        private static FBXMeshSnapshot PopSnapshot(List<FBXMeshSnapshot> snapshots)
        {
            int last = snapshots.Count - 1;
            FBXMeshSnapshot snapshot = snapshots[last];
            snapshots.RemoveAt(last);
            return snapshot;
        }

        private void RestoreSnapshot(FBXMeshSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            if (!snapshot.selectionOnly)
            {
                snapshot.ApplyTo(EditableMesh);
                if (_record != null)
                {
                    _record.dissolvedEdges = snapshot.dissolvedEdges != null
                        ? new List<int>(snapshot.dissolvedEdges)
                        : new List<int>();
                }
                RebuildTopology();
                EditorUtility.SetDirty(EditableMesh);
                EditorUtility.SetDirty(_record);
            }
            RestoreSelection(snapshot);
        }

        private void EnsureHistoryLists()
        {
            if (_record.undoHistory == null) _record.undoHistory = new List<FBXMeshSnapshot>();
            if (_record.redoHistory == null) _record.redoHistory = new List<FBXMeshSnapshot>();
            if (_record.dissolvedEdges == null) _record.dissolvedEdges = new List<int>();
        }

        private FBXMeshSnapshot CaptureCurrentSnapshot()
        {
            FBXMeshSnapshot snapshot = FBXMeshSnapshot.Capture(
                EditableMesh,
                _record != null ? _record.dissolvedEdges : null);
            CaptureSelectionInto(snapshot);
            return snapshot;
        }

        private FBXMeshSnapshot CaptureSelectionSnapshot()
        {
            var snapshot = new FBXMeshSnapshot { selectionOnly = true };
            CaptureSelectionInto(snapshot);
            return snapshot;
        }

        private void CaptureSelectionInto(FBXMeshSnapshot snapshot)
        {
            snapshot.componentMode = (int)ComponentMode;
            snapshot.selectedVertices = new List<int>(_selectedVertices).ToArray();
            snapshot.selectedEdges = new List<int>(_selectedEdges).ToArray();
            snapshot.selectedFaces = new List<int>(_selectedFaces).ToArray();
        }

        private void RestoreSelection(FBXMeshSnapshot snapshot)
        {
            ComponentMode = (FBXMeshComponentMode)Mathf.Clamp(snapshot.componentMode, 0, 2);
            _selectedVertices.Clear();
            _selectedEdges.Clear();
            _selectedFaces.Clear();
            if (snapshot.selectedVertices != null) _selectedVertices.UnionWith(snapshot.selectedVertices);
            if (snapshot.selectedEdges != null) _selectedEdges.UnionWith(snapshot.selectedEdges);
            if (snapshot.selectedFaces != null) _selectedFaces.UnionWith(snapshot.selectedFaces);
            _selectedVertices.RemoveWhere(_topology.IsVertexLogicallyDissolved);
            InvalidateSoftWeights();
        }

        private void PushSelectionUndo(FBXMeshSnapshot snapshot)
        {
            EnsureHistoryLists();
            _record.redoHistory.Clear();
            _record.undoHistory.Add(snapshot);
            while (_record.undoHistory.Count > MaxHistorySteps) _record.undoHistory.RemoveAt(0);
            EditorUtility.SetDirty(_record);
        }

        private void PushSelectionUndoIfChanged(FBXMeshSnapshot before)
        {
            if (before.componentMode == (int)ComponentMode
                && SetEquals(_selectedVertices, before.selectedVertices)
                && SetEquals(_selectedEdges, before.selectedEdges)
                && SetEquals(_selectedFaces, before.selectedFaces)) return;
            PushSelectionUndo(before);
        }

        private static bool SetEquals(HashSet<int> current, int[] previous)
        {
            return current.SetEquals(previous ?? System.Array.Empty<int>());
        }

        private void RebuildTopology()
        {
            _topology.SetDissolvedEdges(_record != null ? _record.dissolvedEdges : null);
            RebuildLogicalVertexGroups();
            _topology.Rebuild(EditableMesh, _logicalVertexRepresentatives);
            _selectedVertices.RemoveWhere(_topology.IsVertexLogicallyDissolved);
            InvalidateSoftWeights();
        }

        private void RebuildLogicalVertexGroups()
        {
            Vector3[] vertices = EditableMesh.vertices;
            _logicalVertexRepresentatives = new int[vertices.Length];
            _logicalVertexGroups.Clear();
            var representativeByPosition = new Dictionary<Vector3, int>();
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!representativeByPosition.TryGetValue(vertices[i], out int representative))
                {
                    representative = i;
                    representativeByPosition.Add(vertices[i], representative);
                    _logicalVertexGroups.Add(representative, new List<int>());
                }
                _logicalVertexRepresentatives[i] = representative;
                _logicalVertexGroups[representative].Add(i);
            }
        }

        public void SynchronizeEditableMeshReference()
        {
            if (MeshFilter == null || EditableMesh == null)
                return;

            Mesh filterMesh = MeshFilter.sharedMesh;
            if (filterMesh == EditableMesh)
                return;

            // Undo 可能重新载入工作资产实例；这时以 MeshFilter 当前引用为准。
            if (IsWorkingMesh(filterMesh))
            {
                EditableMesh = filterMesh;
                _workingAssetPath = AssetDatabase.GetAssetPath(filterMesh);
                _record = FindRecord(filterMesh);
                _sourceMesh = _record != null ? _record.sourceMesh : _sourceMesh;
                RebuildTopology();
                return;
            }

            // 如果 Undo 误撤销了“进入编辑模式”的 MeshFilter 赋值，保持会话仍指向安全工作 Mesh。
            MeshFilter.sharedMesh = EditableMesh;
            EditorUtility.SetDirty(MeshFilter);
        }

        public HashSet<int> GetAffectedVertices()
        {
            var result = new HashSet<int>();
            if (ComponentMode == FBXMeshComponentMode.Vertex)
            {
                result.UnionWith(_selectedVertices);
            }
            else if (ComponentMode == FBXMeshComponentMode.Edge)
            {
                foreach (int edgeIndex in _selectedEdges)
                {
                    FBXMeshEdge edge = _topology.Edges[edgeIndex];
                    AddLogicalVertexGroupToSet(edge.A, result);
                    AddLogicalVertexGroupToSet(edge.B, result);
                }
            }
            else
            {
                foreach (int faceIndex in _selectedFaces)
                {
                    if (faceIndex < 0 || faceIndex >= _topology.LogicalFaces.Length) continue;
                    foreach (int vertex in _topology.LogicalFaces[faceIndex].Vertices)
                        AddLogicalVertexGroupToSet(vertex, result);
                }
            }
            return result;
        }

        public bool InsertEdgeLoop(int startEdgeIndex, float position, out string error)
        {
            error = string.Empty;
            if (!IsActive || ComponentMode != FBXMeshComponentMode.Edge)
            {
                error = "请先进入 Edge Mode。";
                return false;
            }

            if (!_topology.TryBuildOrientedLoopCut(startEdgeIndex,
                out List<FBXMeshLoopCutFace> cutFaces, out _, out _))
            {
                error = "当前路径没有可安全切分的四边面带或三角面终点。";
                return false;
            }

            for (int subMesh = 0; subMesh < EditableMesh.subMeshCount; subMesh++)
            {
                if (EditableMesh.GetTopology(subMesh) != MeshTopology.Triangles)
                {
                    error = "当前 Mesh 包含非三角形 SubMesh，暂不支持插入循环边。";
                    return false;
                }
            }

            int invalidTrianglesBefore = CountInvalidMeshTriangles(EditableMesh);
            EnsureHistoryLists();
            _record.redoHistory.Clear();
            _record.undoHistory.Add(CaptureCurrentSnapshot());
            while (_record.undoHistory.Count > MaxHistorySteps) _record.undoHistory.RemoveAt(0);

            Vector3[] oldVertices = EditableMesh.vertices;
            var vertices = new List<Vector3>(oldVertices);
            var normals = new List<Vector3>(EditableMesh.normals);
            var tangents = new List<Vector4>(EditableMesh.tangents);
            var colors = new List<Color32>(EditableMesh.colors32);
            var uv0 = new List<Vector2>(EditableMesh.uv);
            var uv1 = new List<Vector2>(EditableMesh.uv2);
            var uv2 = new List<Vector2>(EditableMesh.uv3);
            var uv3 = new List<Vector2>(EditableMesh.uv4);
            bool hasNormals = normals.Count == oldVertices.Length;
            bool hasTangents = tangents.Count == oldVertices.Length;
            bool hasColors = colors.Count == oldVertices.Length;
            bool hasUv0 = uv0.Count == oldVertices.Length;
            bool hasUv1 = uv1.Count == oldVertices.Length;
            bool hasUv2 = uv2.Count == oldVertices.Length;
            bool hasUv3 = uv3.Count == oldVertices.Length;

            var edgeMidpoints = new Dictionary<FBXMeshEdge, int>();
            var replacements = new Dictionary<int, int[]>();
            foreach (FBXMeshLoopCutFace cutFace in cutFaces)
            {
                FBXMeshLogicalFace face = _topology.LogicalFaces[cutFace.FaceIndex];
                int firstEdgePosition = cutFace.FirstEdgePosition;
                float facePosition = cutFace.FlipPosition ? 1f - position : position;

                if (!face.IsQuad)
                {
                    int t0 = face.Vertices[firstEdgePosition];
                    int t1 = face.Vertices[(firstEdgePosition + 1) % 3];
                    int apex = face.Vertices[(firstEdgePosition + 2) % 3];
                    int midpoint = GetOrCreateInterpolatedVertex(new FBXMeshEdge(t0, t1),
                        t0, t1, facePosition, edgeMidpoints, vertices, normals, tangents,
                        colors, uv0, uv1, uv2, uv3, hasNormals, hasTangents, hasColors,
                        hasUv0, hasUv1, hasUv2, hasUv3);
                    Vector3 triangleNormal = GetTriangleNormal(oldVertices,
                        _topology.Triangles[face.TriangleA * 3],
                        _topology.Triangles[face.TriangleA * 3 + 1],
                        _topology.Triangles[face.TriangleA * 3 + 2]);
                    replacements[face.TriangleA] = FilterDegenerateTriangles(vertices,
                        BuildOrientedTriangles(vertices, triangleNormal,
                            t0, midpoint, apex,
                            midpoint, t1, apex));
                    continue;
                }

                int q0 = face.Vertices[firstEdgePosition];
                int q1 = face.Vertices[(firstEdgePosition + 1) % 4];
                int q2 = face.Vertices[(firstEdgePosition + 2) % 4];
                int q3 = face.Vertices[(firstEdgePosition + 3) % 4];
                int m01 = GetOrCreateInterpolatedVertex(new FBXMeshEdge(q0, q1), q0, q1, facePosition,
                    edgeMidpoints, vertices, normals, tangents, colors, uv0, uv1, uv2, uv3,
                    hasNormals, hasTangents, hasColors, hasUv0, hasUv1, hasUv2, hasUv3);
                int m23 = GetOrCreateInterpolatedVertex(new FBXMeshEdge(q2, q3), q2, q3, 1f - facePosition,
                    edgeMidpoints, vertices, normals, tangents, colors, uv0, uv1, uv2, uv3,
                    hasNormals, hasTangents, hasColors, hasUv0, hasUv1, hasUv2, hasUv3);

                Vector3 referenceNormal = GetTriangleNormal(oldVertices,
                    _topology.Triangles[face.TriangleA * 3],
                    _topology.Triangles[face.TriangleA * 3 + 1],
                    _topology.Triangles[face.TriangleA * 3 + 2]);
                int[] newTriangles = FilterDegenerateTriangles(vertices,
                    BuildOrientedTriangles(vertices, referenceNormal,
                        q0, m01, m23,
                        q0, m23, q3,
                        m01, q1, q2,
                        m01, q2, m23));
                replacements[face.TriangleA] = newTriangles;
                replacements[face.TriangleB] = System.Array.Empty<int>();
            }

            int originalSubMeshCount = EditableMesh.subMeshCount;
            var originalSubMeshIndices = new int[originalSubMeshCount][];
            for (int i = 0; i < originalSubMeshCount; i++)
                originalSubMeshIndices[i] = EditableMesh.GetIndices(i);

            EditableMesh.Clear();
            EditableMesh.SetVertices(vertices);
            if (hasNormals) EditableMesh.SetNormals(normals);
            if (hasTangents) EditableMesh.SetTangents(tangents);
            if (hasColors) EditableMesh.SetColors(colors);
            if (hasUv0) EditableMesh.SetUVs(0, uv0);
            if (hasUv1) EditableMesh.SetUVs(1, uv1);
            if (hasUv2) EditableMesh.SetUVs(2, uv2);
            if (hasUv3) EditableMesh.SetUVs(3, uv3);

            int globalFace = 0;
            int subMeshCount = originalSubMeshCount;
            EditableMesh.subMeshCount = subMeshCount;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                int[] oldIndices = originalSubMeshIndices[subMesh];
                var newIndices = new List<int>(oldIndices.Length + cutFaces.Count * 6);
                for (int i = 0; i < oldIndices.Length; i += 3, globalFace++)
                {
                    if (replacements.TryGetValue(globalFace, out int[] replacement)) newIndices.AddRange(replacement);
                    else { newIndices.Add(oldIndices[i]); newIndices.Add(oldIndices[i + 1]); newIndices.Add(oldIndices[i + 2]); }
                }
                EditableMesh.SetTriangles(newIndices, subMesh, false);
            }
            EditableMesh.RecalculateBounds();
            if (!hasNormals) EditableMesh.RecalculateNormals();
            int invalidTrianglesAfter = CountInvalidMeshTriangles(EditableMesh);
            if (invalidTrianglesAfter > invalidTrianglesBefore)
            {
                FBXMeshSnapshot restore = _record.undoHistory[_record.undoHistory.Count - 1];
                _record.undoHistory.RemoveAt(_record.undoHistory.Count - 1);
                restore.ApplyTo(EditableMesh);
                RebuildTopology();
                error = $"插入循环边新增了 {invalidTrianglesAfter - invalidTrianglesBefore} 个退化或异常面，操作已自动回退。";
                return false;
            }
            RebuildTopology();
            ClearComponentSelection();
            EditorUtility.SetDirty(EditableMesh);
            EditorUtility.SetDirty(_record);
            return true;
        }

        public bool RebuildSelectedPatch(out string error)
        {
            error = string.Empty;
            if (!IsActive || ComponentMode != FBXMeshComponentMode.Face || _selectedFaces.Count == 0)
            {
                error = "请在 Face 模式中选择需要重建的连续面区域。";
                return false;
            }

            var selectedTriangles = new HashSet<int>();
            var boundaryCounts = new Dictionary<FBXMeshEdge, int>();
            foreach (int faceIndex in _selectedFaces)
            {
                if (faceIndex < 0 || faceIndex >= _topology.LogicalFaces.Length) continue;
                FBXMeshLogicalFace face = _topology.LogicalFaces[faceIndex];
                foreach (int triangle in face.SourceTriangles)
                    if (triangle >= 0) selectedTriangles.Add(triangle);
                for (int i = 0; i < face.Vertices.Length; i++)
                {
                    var edge = new FBXMeshEdge(face.Vertices[i],
                        face.Vertices[(i + 1) % face.Vertices.Length]);
                    boundaryCounts.TryGetValue(edge, out int count);
                    boundaryCounts[edge] = count + 1;
                }
            }
            if (selectedTriangles.Count == 0)
            {
                error = "选区中没有可重建的有效面。";
                return false;
            }

            var boundaryEdges = new List<FBXMeshEdge>();
            foreach (KeyValuePair<FBXMeshEdge, int> pair in boundaryCounts)
                if (pair.Value == 1) boundaryEdges.Add(pair.Key);
            if (!TryOrderSingleBoundary(boundaryEdges, out List<int> boundary))
            {
                error = "选区必须只有一个连续且无洞的闭合边界。";
                return false;
            }
            if (!TryFindPatchCorners(boundary, EditableMesh.vertices, out int[] cornerPositions))
            {
                error = boundary.Count % 2 != 0
                    ? $"选区边界共有 {boundary.Count} 条边。纯四边面 Patch 必须使用偶数条边，请增选或减选一个相邻面后重试。"
                    : "无法从选区边界稳定识别四个角，请选择更接近四边形的区域。";
                return false;
            }

            List<int> side0 = SliceBoundary(boundary, cornerPositions[0], cornerPositions[1]);
            List<int> side1 = SliceBoundary(boundary, cornerPositions[1], cornerPositions[2]);
            List<int> side2 = SliceBoundary(boundary, cornerPositions[2], cornerPositions[3]);
            List<int> side3 = SliceBoundary(boundary, cornerPositions[3],
                cornerPositions[0] + boundary.Count);
            int uSegments = side0.Count - 1;
            int vSegments = side1.Count - 1;
            if (uSegments < 1 || vSegments < 1
                || side2.Count - 1 != uSegments || side3.Count - 1 != vSegments)
            {
                error = "选区两组对边的边数必须分别一致，避免在未选区域边界产生 T 形接缝。";
                return false;
            }

            int targetSubMesh = FindSingleSubMeshForTriangles(selectedTriangles);
            if (targetSubMesh < 0)
            {
                error = "选区跨越了多个材质 SubMesh，不能作为一个 Patch 重建。";
                return false;
            }

            FBXMeshSnapshot before = CaptureCurrentSnapshot();
            EnsureHistoryLists();
            _record.redoHistory.Clear();
            _record.undoHistory.Add(before);
            while (_record.undoHistory.Count > MaxHistorySteps) _record.undoHistory.RemoveAt(0);

            Vector3[] oldVertices = EditableMesh.vertices;
            var vertices = new List<Vector3>(oldVertices);
            var normals = new List<Vector3>(EditableMesh.normals);
            var tangents = new List<Vector4>(EditableMesh.tangents);
            var colors = new List<Color32>(EditableMesh.colors32);
            var uv0 = new List<Vector2>(EditableMesh.uv);
            var uv1 = new List<Vector2>(EditableMesh.uv2);
            var uv2 = new List<Vector2>(EditableMesh.uv3);
            var uv3 = new List<Vector2>(EditableMesh.uv4);
            bool hasNormals = normals.Count == oldVertices.Length;
            bool hasTangents = tangents.Count == oldVertices.Length;
            bool hasColors = colors.Count == oldVertices.Length;
            bool hasUv0 = uv0.Count == oldVertices.Length;
            bool hasUv1 = uv1.Count == oldVertices.Length;
            bool hasUv2 = uv2.Count == oldVertices.Length;
            bool hasUv3 = uv3.Count == oldVertices.Length;

            int[,] grid = new int[uSegments + 1, vSegments + 1];
            for (int x = 0; x <= uSegments; x++)
            {
                grid[x, 0] = side0[x];
                grid[x, vSegments] = side2[uSegments - x];
            }
            for (int y = 0; y <= vSegments; y++)
            {
                grid[uSegments, y] = side1[y];
                grid[0, y] = side3[vSegments - y];
            }

            for (int y = 1; y < vSegments; y++)
            {
                float v = y / (float)vSegments;
                for (int x = 1; x < uSegments; x++)
                {
                    float u = x / (float)uSegments;
                    Vector3 bottom = Vector3.Lerp(oldVertices[side0[x]], oldVertices[side2[uSegments - x]], v);
                    Vector3 left = Vector3.Lerp(oldVertices[side3[vSegments - y]], oldVertices[side1[y]], u);
                    Vector3 bilinear = Bilinear(oldVertices[side0[0]], oldVertices[side0[uSegments]],
                        oldVertices[side2[uSegments]], oldVertices[side2[0]], u, v);
                    Vector3 position = bottom + left - bilinear;
                    int index = vertices.Count;
                    vertices.Add(position);
                    if (hasNormals) normals.Add(Bilinear(normals[side0[0]], normals[side0[uSegments]],
                        normals[side2[uSegments]], normals[side2[0]], u, v).normalized);
                    if (hasTangents) tangents.Add(Vector4.Lerp(
                        Vector4.Lerp(tangents[side0[0]], tangents[side0[uSegments]], u),
                        Vector4.Lerp(tangents[side2[uSegments]], tangents[side2[0]], u), v));
                    if (hasColors) colors.Add(Color32.Lerp(
                        Color32.Lerp(colors[side0[0]], colors[side0[uSegments]], u),
                        Color32.Lerp(colors[side2[uSegments]], colors[side2[0]], u), v));
                    if (hasUv0) uv0.Add(Bilinear(uv0[side0[0]], uv0[side0[uSegments]],
                        uv0[side2[uSegments]], uv0[side2[0]], u, v));
                    if (hasUv1) uv1.Add(Bilinear(uv1[side0[0]], uv1[side0[uSegments]],
                        uv1[side2[uSegments]], uv1[side2[0]], u, v));
                    if (hasUv2) uv2.Add(Bilinear(uv2[side0[0]], uv2[side0[uSegments]],
                        uv2[side2[uSegments]], uv2[side2[0]], u, v));
                    if (hasUv3) uv3.Add(Bilinear(uv3[side0[0]], uv3[side0[uSegments]],
                        uv3[side2[uSegments]], uv3[side2[0]], u, v));
                    grid[x, y] = index;
                }
            }

            int referenceTriangle = -1;
            foreach (int triangle in selectedTriangles)
            {
                referenceTriangle = triangle;
                break;
            }
            Vector3 referenceNormal = GetTriangleNormal(oldVertices,
                _topology.Triangles[referenceTriangle * 3],
                _topology.Triangles[referenceTriangle * 3 + 1],
                _topology.Triangles[referenceTriangle * 3 + 2]);
            var patchTriangles = new List<int>(uSegments * vSegments * 6);
            for (int y = 0; y < vSegments; y++)
            for (int x = 0; x < uSegments; x++)
                patchTriangles.AddRange(FilterDegenerateTriangles(vertices,
                    BuildOrientedTriangles(vertices, referenceNormal,
                        grid[x, y], grid[x + 1, y], grid[x + 1, y + 1],
                        grid[x, y], grid[x + 1, y + 1], grid[x, y + 1])));
            if (patchTriangles.Count == 0)
            {
                _record.undoHistory.RemoveAt(_record.undoHistory.Count - 1);
                error = "重建结果没有有效面，操作已取消。";
                return false;
            }

            int subMeshCount = EditableMesh.subMeshCount;
            var rebuiltSubMeshes = new int[subMeshCount][];
            int globalTriangle = 0;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                int[] source = EditableMesh.GetIndices(subMesh);
                var next = new List<int>(source.Length);
                for (int i = 0; i < source.Length; i += 3, globalTriangle++)
                {
                    if (selectedTriangles.Contains(globalTriangle)) continue;
                    next.Add(source[i]); next.Add(source[i + 1]); next.Add(source[i + 2]);
                }
                if (subMesh == targetSubMesh) next.AddRange(patchTriangles);
                rebuiltSubMeshes[subMesh] = next.ToArray();
            }

            EditableMesh.Clear();
            EditableMesh.SetVertices(vertices);
            if (hasNormals) EditableMesh.SetNormals(normals);
            if (hasTangents) EditableMesh.SetTangents(tangents);
            if (hasColors) EditableMesh.SetColors(colors);
            if (hasUv0) EditableMesh.SetUVs(0, uv0);
            if (hasUv1) EditableMesh.SetUVs(1, uv1);
            if (hasUv2) EditableMesh.SetUVs(2, uv2);
            if (hasUv3) EditableMesh.SetUVs(3, uv3);
            EditableMesh.subMeshCount = subMeshCount;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                EditableMesh.SetTriangles(rebuiltSubMeshes[subMesh], subMesh, false);
            RemoveUnusedVertices(EditableMesh);
            EditableMesh.RecalculateBounds();
            if (!hasNormals) EditableMesh.RecalculateNormals();
            if (!ValidateMeshTriangles(EditableMesh))
            {
                _record.undoHistory.RemoveAt(_record.undoHistory.Count - 1);
                before.ApplyTo(EditableMesh);
                RebuildTopology();
                error = "Patch 重建产生了异常面，操作已自动回退。";
                return false;
            }
            if (_record != null) _record.dissolvedEdges.Clear();
            ClearComponentSelection();
            RebuildTopology();
            EditorUtility.SetDirty(EditableMesh);
            EditorUtility.SetDirty(_record);
            return true;
        }

        private static bool TryOrderSingleBoundary(List<FBXMeshEdge> boundaryEdges,
            out List<int> boundary)
        {
            boundary = new List<int>();
            if (boundaryEdges == null || boundaryEdges.Count < 4) return false;

            var neighbours = new Dictionary<int, List<int>>();
            foreach (FBXMeshEdge edge in boundaryEdges)
            {
                if (!neighbours.TryGetValue(edge.A, out List<int> aNeighbours))
                {
                    aNeighbours = new List<int>();
                    neighbours.Add(edge.A, aNeighbours);
                }
                if (!neighbours.TryGetValue(edge.B, out List<int> bNeighbours))
                {
                    bNeighbours = new List<int>();
                    neighbours.Add(edge.B, bNeighbours);
                }
                if (!aNeighbours.Contains(edge.B)) aNeighbours.Add(edge.B);
                if (!bNeighbours.Contains(edge.A)) bNeighbours.Add(edge.A);
            }
            foreach (KeyValuePair<int, List<int>> pair in neighbours)
                if (pair.Value.Count != 2) return false;

            int start = boundaryEdges[0].A;
            int previous = -1;
            int current = start;
            var visitedEdges = new HashSet<FBXMeshEdge>();
            for (int guard = 0; guard <= boundaryEdges.Count; guard++)
            {
                boundary.Add(current);
                List<int> options = neighbours[current];
                int next = options[0] == previous ? options[1] : options[0];
                var traversed = new FBXMeshEdge(current, next);
                if (!visitedEdges.Add(traversed)) return next == start
                    && visitedEdges.Count == boundaryEdges.Count
                    && boundary.Count == boundaryEdges.Count;
                previous = current;
                current = next;
                if (current == start)
                    return visitedEdges.Count == boundaryEdges.Count
                        && boundary.Count == boundaryEdges.Count;
            }
            return false;
        }

        private static bool TryFindPatchCorners(List<int> boundary, Vector3[] vertices,
            out int[] cornerPositions)
        {
            cornerPositions = null;
            int count = boundary == null ? 0 : boundary.Count;
            if (count < 4) return false;
            // 单个纯四边面 Patch 的边界边数必然是偶数。对边原始段数不同时，
            // 不直接拒绝，而是在闭环上寻找一组对置角点，让两组对边自动等长。
            if ((count & 1) != 0) return false;
            if (count == 4)
            {
                cornerPositions = new[] { 0, 1, 2, 3 };
                return true;
            }

            var scores = new float[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 current = vertices[boundary[i]];
                Vector3 incoming = vertices[boundary[(i - 1 + count) % count]] - current;
                Vector3 outgoing = vertices[boundary[(i + 1) % count]] - current;
                if (incoming.sqrMagnitude < 0.0000000001f || outgoing.sqrMagnitude < 0.0000000001f)
                    return false;
                scores[i] = 1f + Vector3.Dot(incoming.normalized, outgoing.normalized);
            }

            int half = count / 2;
            float bestScore = float.NegativeInfinity;
            int[] best = null;
            // c2 与 c0、c3 与 c1 各相隔半圈，因此四段长度天然为 a,b,a,b。
            // 在所有合法组合中优先选择边界转折最明显的四个位置。
            for (int c0 = 0; c0 < count; c0++)
            {
                for (int firstSideLength = 1; firstSideLength < half; firstSideLength++)
                {
                    int c1 = (c0 + firstSideLength) % count;
                    int c2 = (c0 + half) % count;
                    int c3 = (c1 + half) % count;
                    float score = scores[c0] + scores[c1] + scores[c2] + scores[c3];
                    // 极窄网格通常不是用户期望的 Patch，轻微偏好更均衡的分区。
                    int secondSideLength = half - firstSideLength;
                    score -= Mathf.Abs(firstSideLength - secondSideLength) * 0.01f;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = new[] { c0, c1, c2, c3 };
                }
            }
            if (best == null) return false;
            System.Array.Sort(best);
            cornerPositions = best;
            return true;
        }

        private static List<int> SliceBoundary(List<int> boundary, int start, int end)
        {
            var result = new List<int>(end - start + 1);
            for (int position = start; position <= end; position++)
                result.Add(boundary[position % boundary.Count]);
            return result;
        }

        private int FindSingleSubMeshForTriangles(HashSet<int> selectedTriangles)
        {
            int result = -1;
            int globalTriangle = 0;
            for (int subMesh = 0; subMesh < EditableMesh.subMeshCount; subMesh++)
            {
                int triangleCount = EditableMesh.GetIndices(subMesh).Length / 3;
                for (int localTriangle = 0; localTriangle < triangleCount; localTriangle++, globalTriangle++)
                {
                    if (!selectedTriangles.Contains(globalTriangle)) continue;
                    if (result >= 0 && result != subMesh) return -1;
                    result = subMesh;
                }
            }
            return result;
        }

        private static Vector3 Bilinear(Vector3 bottomLeft, Vector3 bottomRight,
            Vector3 topRight, Vector3 topLeft, float u, float v)
        {
            return Vector3.Lerp(Vector3.Lerp(bottomLeft, bottomRight, u),
                Vector3.Lerp(topLeft, topRight, u), v);
        }

        private static Vector2 Bilinear(Vector2 bottomLeft, Vector2 bottomRight,
            Vector2 topRight, Vector2 topLeft, float u, float v)
        {
            return Vector2.Lerp(Vector2.Lerp(bottomLeft, bottomRight, u),
                Vector2.Lerp(topLeft, topRight, u), v);
        }

        private static int[] BuildOrientedTriangles(List<Vector3> vertices, Vector3 referenceNormal,
            params int[] triangleIndices)
        {
            var result = (int[])triangleIndices.Clone();
            for (int i = 0; i < result.Length; i += 3)
            {
                Vector3 normal = GetTriangleNormal(vertices, result[i], result[i + 1], result[i + 2]);
                if (Vector3.Dot(normal, referenceNormal) < 0f)
                {
                    int swap = result[i + 1];
                    result[i + 1] = result[i + 2];
                    result[i + 2] = swap;
                }
            }
            return result;
        }

        /// <summary>
        /// 极点或折叠边切分时可能产生零面积三角形。它们没有可见表面，
        /// 直接从本次替换结果中移除，保留其余有效切线，而不是回滚整次操作。
        /// </summary>
        private static int[] FilterDegenerateTriangles(IList<Vector3> vertices, int[] indices)
        {
            var result = new List<int>(indices.Length);
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i];
                int b = indices[i + 1];
                int c = indices[i + 2];
                if (a == b || b == c || c == a) continue;
                if (Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude
                    < 0.0000000001f)
                    continue;
                result.Add(a);
                result.Add(b);
                result.Add(c);
            }
            return result.ToArray();
        }

        private void AddLogicalVertexGroupToSet(int vertex, HashSet<int> result)
        {
            foreach (int member in GetLogicalVertexGroup(vertex)) result.Add(member);
        }

        public bool Subdivide(bool wholeMesh, bool smooth, out string error)
        {
            error = string.Empty;
            if (!IsActive)
            {
                error = "请先进入 Mesh Edit Mode。";
                return false;
            }
            if (!wholeMesh && (ComponentMode != FBXMeshComponentMode.Face || _selectedFaces.Count == 0))
            {
                error = "请先在 Face Mode 中选择需要细分的面。";
                return false;
            }
            if (smooth && !wholeMesh)
            {
                error = "平滑细分目前只用于整个 Mesh。";
                return false;
            }
            for (int subMesh = 0; subMesh < EditableMesh.subMeshCount; subMesh++)
            {
                if (EditableMesh.GetTopology(subMesh) != MeshTopology.Triangles)
                {
                    error = "当前 Mesh 包含非三角形 SubMesh，暂不支持细分。";
                    return false;
                }
            }

            var targetFaces = new HashSet<int>();
            if (wholeMesh)
            {
                for (int i = 0; i < _topology.LogicalFaces.Length; i++) targetFaces.Add(i);
            }
            else
            {
                foreach (int faceIndex in _selectedFaces)
                    if (faceIndex >= 0 && faceIndex < _topology.LogicalFaces.Length) targetFaces.Add(faceIndex);
            }
            if (targetFaces.Count == 0)
            {
                error = "没有可细分的面。";
                return false;
            }

            int subMeshCount = EditableMesh.subMeshCount;
            var originalSubMeshIndices = new int[subMeshCount][];
            var triangleSubMeshes = new List<int>();
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                originalSubMeshIndices[subMesh] = EditableMesh.GetIndices(subMesh);
                for (int i = 0; i < originalSubMeshIndices[subMesh].Length; i += 3)
                    triangleSubMeshes.Add(subMesh);
            }
            foreach (int faceIndex in targetFaces)
            {
                FBXMeshLogicalFace face = _topology.LogicalFaces[faceIndex];
                if (face.Vertices.Length < 3 || face.SourceTriangles.Length == 0)
                {
                    error = "当前选择包含无有效底层面的逻辑面，无法细分。";
                    return false;
                }
                int material = triangleSubMeshes[face.SourceTriangles[0]];
                foreach (int triangle in face.SourceTriangles)
                {
                    if (triangle < 0 || triangle >= triangleSubMeshes.Count
                        || triangleSubMeshes[triangle] != material)
                    {
                        error = "细分面跨越了材质边界，操作已取消。";
                        return false;
                    }
                }
            }

            EnsureHistoryLists();
            _record.undoHistory.Add(CaptureCurrentSnapshot());
            while (_record.undoHistory.Count > MaxHistorySteps) _record.undoHistory.RemoveAt(0);
            _record.redoHistory.Clear();

            Vector3[] oldVertices = EditableMesh.vertices;
            var vertices = new List<Vector3>(oldVertices);
            var normals = new List<Vector3>(EditableMesh.normals);
            var tangents = new List<Vector4>(EditableMesh.tangents);
            var colors = new List<Color32>(EditableMesh.colors32);
            var uv0 = new List<Vector2>(EditableMesh.uv);
            var uv1 = new List<Vector2>(EditableMesh.uv2);
            var uv2 = new List<Vector2>(EditableMesh.uv3);
            var uv3 = new List<Vector2>(EditableMesh.uv4);
            bool hasNormals = normals.Count == oldVertices.Length;
            bool hasTangents = tangents.Count == oldVertices.Length;
            bool hasColors = colors.Count == oldVertices.Length;
            bool hasUv0 = uv0.Count == oldVertices.Length;
            bool hasUv1 = uv1.Count == oldVertices.Length;
            bool hasUv2 = uv2.Count == oldVertices.Length;
            bool hasUv3 = uv3.Count == oldVertices.Length;

            var edgeMidpoints = new Dictionary<FBXMeshEdge, int>();
            foreach (int faceIndex in targetFaces)
            {
                int[] faceVertices = _topology.LogicalFaces[faceIndex].Vertices;
                for (int i = 0; i < faceVertices.Length; i++)
                {
                    int a = faceVertices[i];
                    int b = faceVertices[(i + 1) % faceVertices.Length];
                    GetOrCreateInterpolatedVertex(new FBXMeshEdge(a, b), a, b, 0.5f,
                        edgeMidpoints, vertices, normals, tangents, colors, uv0, uv1, uv2, uv3,
                        hasNormals, hasTangents, hasColors, hasUv0, hasUv1, hasUv2, hasUv3);
                }
            }

            var replacements = new Dictionary<int, int[]>();
            var faceCenters = new Dictionary<int, int>();
            for (int faceIndex = 0; faceIndex < _topology.LogicalFaces.Length; faceIndex++)
            {
                FBXMeshLogicalFace face = _topology.LogicalFaces[faceIndex];
                bool selected = targetFaces.Contains(faceIndex);
                var expandedBoundary = new List<int>(face.Vertices.Length * 2);
                bool hasSplitBoundary = false;
                for (int i = 0; i < face.Vertices.Length; i++)
                {
                    int a = face.Vertices[i];
                    int b = face.Vertices[(i + 1) % face.Vertices.Length];
                    expandedBoundary.Add(a);
                    if (edgeMidpoints.TryGetValue(new FBXMeshEdge(a, b), out int midpoint))
                    {
                        expandedBoundary.Add(midpoint);
                        hasSplitBoundary = true;
                    }
                }
                if (!selected && !hasSplitBoundary) continue;

                int center = AddAveragedVertex(face.Vertices, vertices, normals, tangents, colors,
                    uv0, uv1, uv2, uv3, hasNormals, hasTangents, hasColors,
                    hasUv0, hasUv1, hasUv2, hasUv3);
                faceCenters[faceIndex] = center;
                if (face.SourceTriangles.Length == 0) continue;
                int referenceTriangle = face.SourceTriangles[0];
                Vector3 referenceNormal = GetTriangleNormal(oldVertices,
                    _topology.Triangles[referenceTriangle * 3],
                    _topology.Triangles[referenceTriangle * 3 + 1],
                    _topology.Triangles[referenceTriangle * 3 + 2]);
                var generated = new List<int>();
                if (selected)
                {
                    for (int i = 0; i < face.Vertices.Length; i++)
                    {
                        int vertex = face.Vertices[i];
                        int nextMid = edgeMidpoints[new FBXMeshEdge(vertex,
                            face.Vertices[(i + 1) % face.Vertices.Length])];
                        int previousMid = edgeMidpoints[new FBXMeshEdge(
                            face.Vertices[(i - 1 + face.Vertices.Length) % face.Vertices.Length], vertex)];
                        generated.AddRange(BuildOrientedTriangles(vertices, referenceNormal,
                            vertex, nextMid, center, vertex, center, previousMid));
                    }
                }
                else
                {
                    for (int i = 0; i < expandedBoundary.Count; i++)
                        generated.AddRange(BuildOrientedTriangles(vertices, referenceNormal,
                            expandedBoundary[i], expandedBoundary[(i + 1) % expandedBoundary.Count], center));
                }
                replacements[referenceTriangle] = generated.ToArray();
                for (int triangleIndex = 1; triangleIndex < face.SourceTriangles.Length; triangleIndex++)
                    replacements[face.SourceTriangles[triangleIndex]] = System.Array.Empty<int>();
            }

            if (smooth)
                ApplyCatmullClarkPositions(oldVertices.Length, vertices, edgeMidpoints, faceCenters);

            EditableMesh.Clear();
            if (vertices.Count > 65535) EditableMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            EditableMesh.SetVertices(vertices);
            if (hasNormals) EditableMesh.SetNormals(normals);
            if (hasTangents) EditableMesh.SetTangents(tangents);
            if (hasColors) EditableMesh.SetColors(colors);
            if (hasUv0) EditableMesh.SetUVs(0, uv0);
            if (hasUv1) EditableMesh.SetUVs(1, uv1);
            if (hasUv2) EditableMesh.SetUVs(2, uv2);
            if (hasUv3) EditableMesh.SetUVs(3, uv3);
            EditableMesh.subMeshCount = subMeshCount;
            int globalTriangle = 0;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                int[] oldIndices = originalSubMeshIndices[subMesh];
                var nextIndices = new List<int>(oldIndices.Length * 2);
                for (int i = 0; i < oldIndices.Length; i += 3, globalTriangle++)
                {
                    if (replacements.TryGetValue(globalTriangle, out int[] replacement)) nextIndices.AddRange(replacement);
                    else { nextIndices.Add(oldIndices[i]); nextIndices.Add(oldIndices[i + 1]); nextIndices.Add(oldIndices[i + 2]); }
                }
                EditableMesh.SetTriangles(nextIndices, subMesh, false);
            }
            EditableMesh.RecalculateBounds();
            if (smooth || !hasNormals) EditableMesh.RecalculateNormals();
            if (smooth && hasTangents && hasUv0) EditableMesh.RecalculateTangents();
            if (!ValidateMeshTriangles(EditableMesh))
            {
                FBXMeshSnapshot restore = _record.undoHistory[_record.undoHistory.Count - 1];
                _record.undoHistory.RemoveAt(_record.undoHistory.Count - 1);
                restore.ApplyTo(EditableMesh);
                RebuildTopology();
                error = "细分后检测到异常面，操作已自动回退。";
                return false;
            }

            _record.dissolvedEdges.Clear();
            ClearComponentSelection();
            RebuildTopology();
            EditorUtility.SetDirty(EditableMesh);
            EditorUtility.SetDirty(_record);
            return true;
        }

        private void ApplyCatmullClarkPositions(int originalVertexCount, List<Vector3> vertices,
            Dictionary<FBXMeshEdge, int> edgePoints, Dictionary<int, int> faceCenters)
        {
            Vector3[] originalPositions = vertices.GetRange(0, originalVertexCount).ToArray();
            // Unity 会在 UV、法线和材质接缝处复制顶点。平滑拓扑必须先把同位置点视为一个点，
            // 否则这些接缝会被误判为开放边界并产生尖刺或裂缝。
            var positionToWelded = new Dictionary<Vector3, int>();
            var weldedPositions = new List<Vector3>();
            var weldedIndex = new int[originalVertexCount];
            for (int i = 0; i < originalVertexCount; i++)
            {
                Vector3 position = originalPositions[i];
                if (!positionToWelded.TryGetValue(position, out int index))
                {
                    index = weldedPositions.Count;
                    positionToWelded.Add(position, index);
                    weldedPositions.Add(position);
                }
                weldedIndex[i] = index;
            }

            var edgeFaces = new Dictionary<FBXMeshEdge, List<int>>();
            var vertexFaces = new List<int>[weldedPositions.Count];
            var vertexNeighbors = new HashSet<int>[weldedPositions.Count];
            for (int i = 0; i < weldedPositions.Count; i++)
            {
                vertexFaces[i] = new List<int>();
                vertexNeighbors[i] = new HashSet<int>();
            }

            for (int faceIndex = 0; faceIndex < _topology.LogicalFaces.Length; faceIndex++)
            {
                int[] faceVertices = _topology.LogicalFaces[faceIndex].Vertices;
                for (int i = 0; i < faceVertices.Length; i++)
                {
                    int a = weldedIndex[faceVertices[i]];
                    int b = weldedIndex[faceVertices[(i + 1) % faceVertices.Length]];
                    if (a == b) continue;
                    var edge = new FBXMeshEdge(a, b);
                    if (!edgeFaces.TryGetValue(edge, out List<int> adjacentFaces))
                    {
                        adjacentFaces = new List<int>(2);
                        edgeFaces.Add(edge, adjacentFaces);
                    }
                    if (!adjacentFaces.Contains(faceIndex)) adjacentFaces.Add(faceIndex);
                    vertexNeighbors[a].Add(b);
                    vertexNeighbors[b].Add(a);
                    if (!vertexFaces[a].Contains(faceIndex)) vertexFaces[a].Add(faceIndex);
                    if (!vertexFaces[b].Contains(faceIndex)) vertexFaces[b].Add(faceIndex);
                }
            }

            var smoothedWelded = new Vector3[weldedPositions.Count];
            for (int vertexIndex = 0; vertexIndex < weldedPositions.Count; vertexIndex++)
            {
                Vector3 position = weldedPositions[vertexIndex];
                var boundaryNeighbors = new List<int>();
                foreach (int neighbor in vertexNeighbors[vertexIndex])
                {
                    if (edgeFaces.TryGetValue(new FBXMeshEdge(vertexIndex, neighbor), out List<int> faces)
                        && faces.Count == 1)
                        boundaryNeighbors.Add(neighbor);
                }

                if (boundaryNeighbors.Count > 0)
                {
                    Vector3 boundaryAverage = Vector3.zero;
                    foreach (int neighbor in boundaryNeighbors) boundaryAverage += weldedPositions[neighbor];
                    boundaryAverage /= boundaryNeighbors.Count;
                    smoothedWelded[vertexIndex] = position * 0.75f + boundaryAverage * 0.25f;
                    continue;
                }

                int count = vertexFaces[vertexIndex].Count;
                if (count == 0 || vertexNeighbors[vertexIndex].Count == 0)
                {
                    smoothedWelded[vertexIndex] = position;
                    continue;
                }
                Vector3 faceAverage = Vector3.zero;
                foreach (int faceIndex in vertexFaces[vertexIndex])
                    faceAverage += vertices[faceCenters[faceIndex]];
                faceAverage /= count;
                Vector3 edgeAverage = Vector3.zero;
                foreach (int neighbor in vertexNeighbors[vertexIndex])
                    edgeAverage += (position + weldedPositions[neighbor]) * 0.5f;
                edgeAverage /= vertexNeighbors[vertexIndex].Count;
                smoothedWelded[vertexIndex] =
                    (faceAverage + edgeAverage * 2f + position * (count - 3f)) / count;
            }

            foreach (KeyValuePair<FBXMeshEdge, int> pair in edgePoints)
            {
                int weldedA = weldedIndex[pair.Key.A];
                int weldedB = weldedIndex[pair.Key.B];
                var weldedEdge = new FBXMeshEdge(weldedA, weldedB);
                Vector3 edgePosition = (weldedPositions[weldedA] + weldedPositions[weldedB]) * 0.5f;
                if (edgeFaces.TryGetValue(weldedEdge, out List<int> adjacentFaces) && adjacentFaces.Count == 2)
                {
                    edgePosition = (weldedPositions[weldedA] + weldedPositions[weldedB]
                        + vertices[faceCenters[adjacentFaces[0]]]
                        + vertices[faceCenters[adjacentFaces[1]]]) * 0.25f;
                }
                vertices[pair.Value] = edgePosition;
            }
            for (int i = 0; i < originalVertexCount; i++) vertices[i] = smoothedWelded[weldedIndex[i]];
        }

        private static int AddAveragedVertex(int[] sourceIndices, List<Vector3> vertices,
            List<Vector3> normals, List<Vector4> tangents, List<Color32> colors,
            List<Vector2> uv0, List<Vector2> uv1, List<Vector2> uv2, List<Vector2> uv3,
            bool hasNormals, bool hasTangents, bool hasColors, bool hasUv0, bool hasUv1,
            bool hasUv2, bool hasUv3)
        {
            int result = vertices.Count;
            Vector3 position = Vector3.zero;
            Vector3 normal = Vector3.zero;
            Vector4 tangent = Vector4.zero;
            Color color = Color.clear;
            Vector2 valueUv0 = Vector2.zero, valueUv1 = Vector2.zero;
            Vector2 valueUv2 = Vector2.zero, valueUv3 = Vector2.zero;
            foreach (int index in sourceIndices)
            {
                position += vertices[index];
                if (hasNormals) normal += normals[index];
                if (hasTangents) tangent += tangents[index];
                if (hasColors) color += (Color)colors[index];
                if (hasUv0) valueUv0 += uv0[index];
                if (hasUv1) valueUv1 += uv1[index];
                if (hasUv2) valueUv2 += uv2[index];
                if (hasUv3) valueUv3 += uv3[index];
            }
            float inverse = 1f / sourceIndices.Length;
            vertices.Add(position * inverse);
            if (hasNormals) normals.Add((normal * inverse).normalized);
            if (hasTangents) tangents.Add(tangent * inverse);
            if (hasColors) colors.Add((Color32)(color * inverse));
            if (hasUv0) uv0.Add(valueUv0 * inverse);
            if (hasUv1) uv1.Add(valueUv1 * inverse);
            if (hasUv2) uv2.Add(valueUv2 * inverse);
            if (hasUv3) uv3.Add(valueUv3 * inverse);
            return result;
        }

        private static Vector3 GetTriangleNormal(IList<Vector3> vertices, int a, int b, int c)
        {
            return Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized;
        }

        private static bool ValidateMeshTriangles(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                int[] indices = mesh.GetIndices(subMesh);
                if (indices.Length % 3 != 0) return false;
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i];
                    int b = indices[i + 1];
                    int c = indices[i + 2];
                    if (a < 0 || b < 0 || c < 0
                        || a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
                        return false;
                    if (a == b || b == c || c == a) return false;
                    if (Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude < 0.0000000001f)
                        return false;
                }
            }
            return true;
        }

        private static int CountInvalidMeshTriangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            int invalid = 0;
            Vector3[] vertices = mesh.vertices;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                int[] indices = mesh.GetIndices(subMesh);
                if (indices.Length % 3 != 0) invalid++;
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i];
                    int b = indices[i + 1];
                    int c = indices[i + 2];
                    if (a < 0 || b < 0 || c < 0
                        || a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
                    {
                        invalid++;
                        continue;
                    }
                    if (a == b || b == c || c == a
                        || Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude
                        < 0.0000000001f)
                        invalid++;
                }
            }
            return invalid;
        }

        private static int GetOrCreateInterpolatedVertex(FBXMeshEdge edge, int a, int b, float t,
            Dictionary<FBXMeshEdge, int> cache, List<Vector3> vertices, List<Vector3> normals,
            List<Vector4> tangents, List<Color32> colors, List<Vector2> uv0, List<Vector2> uv1,
            List<Vector2> uv2, List<Vector2> uv3, bool hasNormals, bool hasTangents, bool hasColors,
            bool hasUv0, bool hasUv1, bool hasUv2, bool hasUv3)
        {
            if (cache.TryGetValue(edge, out int existing)) return existing;
            int index = vertices.Count;
            vertices.Add(Vector3.Lerp(vertices[a], vertices[b], t));
            if (hasNormals) normals.Add(Vector3.Lerp(normals[a], normals[b], t).normalized);
            if (hasTangents) tangents.Add(Vector4.Lerp(tangents[a], tangents[b], t));
            if (hasColors) colors.Add(Color32.Lerp(colors[a], colors[b], t));
            if (hasUv0) uv0.Add(Vector2.Lerp(uv0[a], uv0[b], t));
            if (hasUv1) uv1.Add(Vector2.Lerp(uv1[a], uv1[b], t));
            if (hasUv2) uv2.Add(Vector2.Lerp(uv2[a], uv2[b], t));
            if (hasUv3) uv3.Add(Vector2.Lerp(uv3[a], uv3[b], t));
            cache.Add(edge, index);
            return index;
        }

        public bool DissolveEdgeLoop(int startEdgeIndex, out string error)
        {
            error = string.Empty;
            if (!IsActive || ComponentMode != FBXMeshComponentMode.Edge)
            {
                error = "请先进入 Edge Mode。";
                return false;
            }

            List<int> loop = _topology.GetEdgeLoop(startEdgeIndex);
            if (loop.Count < 3)
            {
                error = "所选边没有形成可安全溶解的完整循环线。";
                return false;
            }

            var vertexDegree = new Dictionary<int, int>();
            foreach (int edgeIndex in loop)
            {
                FBXMeshEdge edge = _topology.Edges[edgeIndex];
                vertexDegree[edge.A] = vertexDegree.TryGetValue(edge.A, out int da) ? da + 1 : 1;
                vertexDegree[edge.B] = vertexDegree.TryGetValue(edge.B, out int db) ? db + 1 : 1;
            }
            foreach (int degree in vertexDegree.Values)
            {
                if (degree != 2)
                {
                    error = "第一版仅支持闭合且无分叉的 Edge Loop；开放边或分叉路径不会执行。";
                    return false;
                }
            }

            int subMeshCount = EditableMesh.subMeshCount;
            var subMeshIndices = new int[subMeshCount][];
            var triangleSubMesh = new Dictionary<int, int>();
            int globalFace = 0;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                subMeshIndices[subMesh] = EditableMesh.GetIndices(subMesh);
                for (int i = 0; i < subMeshIndices[subMesh].Length; i += 3)
                    triangleSubMesh[globalFace++] = subMesh;
            }

            var replacements = new Dictionary<int, int[]>();
            var consumedLogicalFaces = new HashSet<int>();
            Vector3[] vertices = EditableMesh.vertices;
            foreach (int edgeIndex in loop)
            {
                IReadOnlyList<int> faces = _topology.GetAdjacentLogicalFaces(edgeIndex);
                if (faces.Count != 2 || !_topology.LogicalFaces[faces[0]].IsQuad
                    || !_topology.LogicalFaces[faces[1]].IsQuad)
                {
                    error = "循环线经过边界或三角面，无法安全溶解。";
                    return false;
                }
                if (!consumedLogicalFaces.Add(faces[0]) || !consumedLogicalFaces.Add(faces[1]))
                {
                    error = "循环线附近存在重复或分叉面，操作已拒绝。";
                    return false;
                }

                FBXMeshLogicalFace faceA = _topology.LogicalFaces[faces[0]];
                FBXMeshLogicalFace faceB = _topology.LogicalFaces[faces[1]];
                if (!triangleSubMesh.TryGetValue(faceA.TriangleA, out int materialA)
                    || !triangleSubMesh.TryGetValue(faceB.TriangleA, out int materialB)
                    || materialA != materialB)
                {
                    error = "循环线跨越材质边界，暂不支持溶解。";
                    return false;
                }

                FBXMeshEdge edge = _topology.Edges[edgeIndex];
                if (!_topology.TryGetOuterVertices(faces[0], edge, out int a0, out int b0)
                    || !_topology.TryGetOuterVertices(faces[1], edge, out int a1, out int b1))
                {
                    error = "无法确定循环线两侧的四边面顺序。";
                    return false;
                }

                Vector3 referenceNormal = GetTriangleNormal(vertices,
                    _topology.Triangles[faceA.TriangleA * 3],
                    _topology.Triangles[faceA.TriangleA * 3 + 1],
                    _topology.Triangles[faceA.TriangleA * 3 + 2]);
                int[] merged = BuildOrientedTriangles(new List<Vector3>(vertices), referenceNormal,
                    a0, b0, b1, a0, b1, a1);
                replacements[faceA.TriangleA] = merged;
                replacements[faceA.TriangleB] = System.Array.Empty<int>();
                replacements[faceB.TriangleA] = System.Array.Empty<int>();
                replacements[faceB.TriangleB] = System.Array.Empty<int>();
            }

            EnsureHistoryLists();
            _record.redoHistory.Clear();
            _record.undoHistory.Add(CaptureCurrentSnapshot());
            while (_record.undoHistory.Count > MaxHistorySteps) _record.undoHistory.RemoveAt(0);

            globalFace = 0;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                int[] oldIndices = subMeshIndices[subMesh];
                var next = new List<int>(oldIndices.Length);
                for (int i = 0; i < oldIndices.Length; i += 3, globalFace++)
                {
                    if (replacements.TryGetValue(globalFace, out int[] replacement)) next.AddRange(replacement);
                    else { next.Add(oldIndices[i]); next.Add(oldIndices[i + 1]); next.Add(oldIndices[i + 2]); }
                }
                EditableMesh.SetTriangles(next, subMesh, false);
            }

            RemoveUnusedVertices(EditableMesh);
            EditableMesh.RecalculateBounds();
            EditableMesh.RecalculateNormals();
            if (!ValidateMeshTriangles(EditableMesh))
            {
                FBXMeshSnapshot restore = _record.undoHistory[_record.undoHistory.Count - 1];
                _record.undoHistory.RemoveAt(_record.undoHistory.Count - 1);
                restore.ApplyTo(EditableMesh);
                RebuildTopology();
                error = "溶解后检测到异常面，操作已自动回退。";
                return false;
            }
            RebuildTopology();
            ClearComponentSelection();
            EditorUtility.SetDirty(EditableMesh);
            EditorUtility.SetDirty(_record);
            return true;
        }

        public bool MergeSelectedVerticesToCenter(out string error)
        {
            if (_selectedVertices.Count < 2)
            {
                error = "请在 Vertex 模式中至少选择两个顶点。";
                return false;
            }

            return MergeVertexGroups(
                new List<List<int>> { new List<int>(_selectedVertices) }, out error);
        }

        public bool MergeSelectedVerticesByDistance(float distance, out string error)
        {
            error = string.Empty;
            if (!IsActive || ComponentMode != FBXMeshComponentMode.Vertex
                || _selectedVertices.Count < 2)
            {
                error = "请在 Vertex 模式中至少选择两个顶点。";
                return false;
            }
            if (distance <= 0f)
            {
                error = "Merge Distance 必须大于 0。";
                return false;
            }

            Vector3[] vertices = EditableMesh.vertices;
            var selected = new List<int>();
            var selectedRepresentatives = new HashSet<int>();
            foreach (int vertex in _selectedVertices)
            {
                int representative = vertex < _logicalVertexRepresentatives.Length
                    ? _logicalVertexRepresentatives[vertex] : vertex;
                if (selectedRepresentatives.Add(representative)) selected.Add(representative);
            }
            if (selected.Count < 2)
            {
                error = "请至少选择两个不同的逻辑顶点。";
                return false;
            }
            var parent = new int[selected.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            for (int i = 0; i < selected.Count; i++)
            {
                for (int j = i + 1; j < selected.Count; j++)
                {
                    if ((vertices[selected[i]] - vertices[selected[j]]).sqrMagnitude
                        <= distance * distance)
                        UnionMergeVertices(parent, i, j);
                }
            }

            var groupsByRoot = new Dictionary<int, List<int>>();
            for (int i = 0; i < selected.Count; i++)
            {
                int root = FindMergeVertexRoot(parent, i);
                if (!groupsByRoot.TryGetValue(root, out List<int> group))
                {
                    group = new List<int>();
                    groupsByRoot.Add(root, group);
                }
                foreach (int member in GetLogicalVertexGroup(selected[i]))
                    group.Add(member);
            }

            var groups = new List<List<int>>();
            foreach (List<int> group in groupsByRoot.Values)
                if (group.Count > 1) groups.Add(group);
            if (groups.Count == 0)
            {
                error = "所选顶点中没有距离小于 Merge Distance 的点。";
                return false;
            }
            return MergeVertexGroups(groups, out error);
        }

        private bool MergeVertexGroups(List<List<int>> groups, out string error)
        {
            error = string.Empty;
            if (!IsActive || ComponentMode != FBXMeshComponentMode.Vertex)
            {
                error = "请先进入 Vertex 模式。";
                return false;
            }

            CountGeometricTopologyIssues(EditableMesh,
                out int boundaryEdgesBefore, out int nonManifoldEdgesBefore);
            EnsureHistoryLists();
            FBXMeshSnapshot before = CaptureCurrentSnapshot();
            _record.undoHistory.Add(before);
            while (_record.undoHistory.Count > MaxHistorySteps) _record.undoHistory.RemoveAt(0);
            _record.redoHistory.Clear();

            Vector3[] vertices = EditableMesh.vertices;
            Vector3[] normals = EditableMesh.normals;
            Vector4[] tangents = EditableMesh.tangents;
            Color32[] colors = EditableMesh.colors32;
            Vector2[] uv0 = EditableMesh.uv;
            Vector2[] uv1 = EditableMesh.uv2;
            Vector2[] uv2 = EditableMesh.uv3;
            Vector2[] uv3 = EditableMesh.uv4;
            var remap = new int[vertices.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = i;
            foreach (List<int> group in groups)
            {
                Vector3 center = Vector3.zero;
                var uniquePositions = new HashSet<Vector3>();
                foreach (int index in group) uniquePositions.Add(vertices[index]);
                foreach (Vector3 position in uniquePositions) center += position;
                center /= uniquePositions.Count;
                var representatives = new List<int>();
                foreach (int index in group)
                {
                    // Unity 需要重复顶点来保存 UV 接缝和硬边法线；这里只合并几何位置。
                    vertices[index] = center;
                    int representative = -1;
                    foreach (int candidate in representatives)
                    {
                        if (!HaveSameMergeAttributes(index, candidate, vertices.Length,
                            normals, tangents, colors, uv0, uv1, uv2, uv3)) continue;
                        representative = candidate;
                        break;
                    }
                    if (representative < 0)
                        representatives.Add(index);
                    else
                    {
                        remap[index] = representative;
                    }
                }
            }

            int subMeshCount = EditableMesh.subMeshCount;
            var nextSubMeshIndices = new int[subMeshCount][];
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                if (EditableMesh.GetTopology(subMesh) != MeshTopology.Triangles)
                {
                    _record.undoHistory.RemoveAt(_record.undoHistory.Count - 1);
                    error = "当前 Mesh 包含非三角形 SubMesh，无法合并顶点。";
                    return false;
                }
                int[] indices = EditableMesh.GetIndices(subMesh);
                var next = new List<int>(indices.Length);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = remap[indices[i]];
                    int b = remap[indices[i + 1]];
                    int c = remap[indices[i + 2]];
                    if (a == b || b == c || c == a) continue;
                    if (Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude
                        < 0.0000000001f) continue;
                    next.Add(a);
                    next.Add(b);
                    next.Add(c);
                }
                nextSubMeshIndices[subMesh] = next.ToArray();
            }

            EditableMesh.vertices = vertices;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                EditableMesh.SetTriangles(nextSubMeshIndices[subMesh], subMesh, false);
            RemoveUnusedVertices(EditableMesh);
            EditableMesh.RecalculateBounds();

            if (!ValidateMeshTriangles(EditableMesh))
            {
                _record.undoHistory.RemoveAt(_record.undoHistory.Count - 1);
                RestoreSnapshot(before);
                error = "合并后检测到异常面，操作已自动回退。";
                return false;
            }
            CountGeometricTopologyIssues(EditableMesh,
                out int boundaryEdgesAfter, out int nonManifoldEdgesAfter);
            if (boundaryEdgesAfter > boundaryEdgesBefore
                || nonManifoldEdgesAfter > nonManifoldEdgesBefore)
            {
                _record.undoHistory.RemoveAt(_record.undoHistory.Count - 1);
                RestoreSnapshot(before);
                error = "合并会产生新的破洞或非流形边，操作已自动回退。";
                return false;
            }

            _record.dissolvedEdges.Clear();
            ClearComponentSelection();
            RebuildTopology();
            EditorUtility.SetDirty(EditableMesh);
            EditorUtility.SetDirty(_record);
            error = "顶点合并完成。";
            return true;
        }

        private readonly struct GeometricEdgeKey : System.IEquatable<GeometricEdgeKey>
        {
            private readonly Vector3 a;
            private readonly Vector3 b;

            public GeometricEdgeKey(Vector3 first, Vector3 second)
            {
                if (CompareVector3(first, second) <= 0) { a = first; b = second; }
                else { a = second; b = first; }
            }

            public bool Equals(GeometricEdgeKey other) => a.Equals(other.a) && b.Equals(other.b);
            public override bool Equals(object obj) => obj is GeometricEdgeKey other && Equals(other);
            public override int GetHashCode() => (a.GetHashCode() * 397) ^ b.GetHashCode();
        }

        private static int CompareVector3(Vector3 a, Vector3 b)
        {
            int x = a.x.CompareTo(b.x);
            if (x != 0) return x;
            int y = a.y.CompareTo(b.y);
            return y != 0 ? y : a.z.CompareTo(b.z);
        }

        private static void CountGeometricTopologyIssues(Mesh mesh,
            out int boundaryEdges, out int nonManifoldEdges)
        {
            Vector3[] vertices = mesh.vertices;
            var incidence = new Dictionary<GeometricEdgeKey, int>();
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                int[] indices = mesh.GetIndices(subMesh);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    AddGeometricEdge(vertices[indices[i]], vertices[indices[i + 1]], incidence);
                    AddGeometricEdge(vertices[indices[i + 1]], vertices[indices[i + 2]], incidence);
                    AddGeometricEdge(vertices[indices[i + 2]], vertices[indices[i]], incidence);
                }
            }
            boundaryEdges = 0;
            nonManifoldEdges = 0;
            foreach (int count in incidence.Values)
            {
                if (count == 1) boundaryEdges++;
                else if (count > 2) nonManifoldEdges++;
            }
        }

        private static void AddGeometricEdge(Vector3 a, Vector3 b,
            Dictionary<GeometricEdgeKey, int> incidence)
        {
            if (a == b) return;
            var edge = new GeometricEdgeKey(a, b);
            incidence.TryGetValue(edge, out int count);
            incidence[edge] = count + 1;
        }

        public bool BuildTopologyDiagnostic(out string message)
        {
            if (!IsActive || EditableMesh == null || _sourceMesh == null)
            {
                message = "请先选择模型并进入编辑模式。";
                return false;
            }

            CountGeometricTopologyIssues(_sourceMesh,
                out int sourceBoundary, out int sourceNonManifold);
            CountGeometricTopologyIssues(EditableMesh,
                out int workingBoundary, out int workingNonManifold);
            int sourceDegenerate = CountDegenerateTriangles(_sourceMesh);
            int workingDegenerate = CountDegenerateTriangles(EditableMesh);
            int sourceTriangles = CountTriangles(_sourceMesh);
            int workingTriangles = CountTriangles(EditableMesh);

            bool becameWorse = workingBoundary > sourceBoundary
                || workingNonManifold > sourceNonManifold
                || workingDegenerate > sourceDegenerate;
            bool sourceHasIssues = sourceNonManifold > 0 || sourceDegenerate > 0;
            string conclusion = becameWorse
                ? "结论：当前工作 Mesh 比原模型新增了拓扑问题。"
                : sourceHasIssues
                    ? "结论：原模型本身已经包含异常拓扑。"
                    : "结论：未检测到新增破损；如果 Scene 显示异常，属于工具的线面识别问题。";
            message = $"{conclusion}\n"
                + $"原始：开放边 {sourceBoundary}，非流形边 {sourceNonManifold}，退化面 {sourceDegenerate}，三角面 {sourceTriangles}\n"
                + $"当前：开放边 {workingBoundary}，非流形边 {workingNonManifold}，退化面 {workingDegenerate}，三角面 {workingTriangles}";
            return true;
        }

        private static int CountTriangles(Mesh mesh)
        {
            int count = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
                count += mesh.GetIndices(s).Length / 3;
            return count;
        }

        private static int CountDegenerateTriangles(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            int count = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] indices = mesh.GetIndices(s);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i];
                    int b = indices[i + 1];
                    int c = indices[i + 2];
                    if (a == b || b == c || c == a
                        || Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude
                        < 0.0000000001f)
                        count++;
                }
            }
            return count;
        }

        private static bool HaveSameMergeAttributes(int a, int b, int vertexCount,
            Vector3[] normals, Vector4[] tangents, Color32[] colors,
            Vector2[] uv0, Vector2[] uv1, Vector2[] uv2, Vector2[] uv3)
        {
            return SameChannelValue(normals, vertexCount, a, b)
                && SameChannelValue(tangents, vertexCount, a, b)
                && SameChannelValue(colors, vertexCount, a, b)
                && SameChannelValue(uv0, vertexCount, a, b)
                && SameChannelValue(uv1, vertexCount, a, b)
                && SameChannelValue(uv2, vertexCount, a, b)
                && SameChannelValue(uv3, vertexCount, a, b);
        }

        private static bool SameChannelValue<T>(T[] channel, int vertexCount, int a, int b)
        {
            return channel == null || channel.Length != vertexCount
                || EqualityComparer<T>.Default.Equals(channel[a], channel[b]);
        }

        public bool DissolveSelectedVertices(out string error)
        {
            error = string.Empty;
            if (!IsActive || ComponentMode != FBXMeshComponentMode.Vertex
                || _selectedVertices.Count == 0)
            {
                error = "请在 Vertex 模式中选择需要溶解的顶点。";
                return false;
            }

            int subMeshCount = EditableMesh.subMeshCount;
            var sourceIndices = new int[subMeshCount][];
            var removedTriangles = new HashSet<int>[subMeshCount];
            var appendedTriangles = new List<int>[subMeshCount];
            for (int s = 0; s < subMeshCount; s++)
            {
                if (EditableMesh.GetTopology(s) != MeshTopology.Triangles)
                {
                    error = "当前 Mesh 包含非三角形 SubMesh，无法溶解顶点。";
                    return false;
                }
                sourceIndices[s] = EditableMesh.GetIndices(s);
                removedTriangles[s] = new HashSet<int>();
                appendedTriangles[s] = new List<int>();
            }

            var selected = new HashSet<int>(_selectedVertices);
            foreach (int vertex in selected)
            {
                int ownerSubMesh = -1;
                var incidentTriangles = new List<int>();
                var nextBoundaryVertex = new Dictionary<int, int>();
                for (int s = 0; s < subMeshCount; s++)
                {
                    int[] indices = sourceIndices[s];
                    for (int triangle = 0; triangle < indices.Length / 3; triangle++)
                    {
                        int offset = triangle * 3;
                        int a = indices[offset];
                        int b = indices[offset + 1];
                        int c = indices[offset + 2];
                        if (a != vertex && b != vertex && c != vertex) continue;
                        if ((a != vertex && selected.Contains(a))
                            || (b != vertex && selected.Contains(b))
                            || (c != vertex && selected.Contains(c)))
                        {
                            error = "相邻顶点不能同时溶解，请分开操作。";
                            return false;
                        }
                        if (ownerSubMesh >= 0 && ownerSubMesh != s)
                        {
                            error = "材质边界上的顶点不能安全溶解。";
                            return false;
                        }
                        ownerSubMesh = s;
                        incidentTriangles.Add(triangle);

                        int from;
                        int to;
                        if (a == vertex) { from = b; to = c; }
                        else if (b == vertex) { from = c; to = a; }
                        else { from = a; to = b; }
                        if (nextBoundaryVertex.ContainsKey(from))
                        {
                            error = "所选顶点周围不是规则流形结构，无法安全溶解。";
                            return false;
                        }
                        nextBoundaryVertex.Add(from, to);
                    }
                }

                if (ownerSubMesh < 0 || incidentTriangles.Count < 3)
                {
                    error = "边界点或孤立点不能安全溶解。";
                    return false;
                }

                var boundary = new List<int>();
                int start = -1;
                foreach (int candidate in nextBoundaryVertex.Keys) { start = candidate; break; }
                int current = start;
                for (int i = 0; i < incidentTriangles.Count; i++)
                {
                    if (current < 0 || !nextBoundaryVertex.TryGetValue(current, out int next))
                    {
                        error = "所选顶点位于开放边界，无法安全溶解。";
                        return false;
                    }
                    boundary.Add(current);
                    current = next;
                }
                if (current != start || boundary.Count != nextBoundaryVertex.Count)
                {
                    error = "所选顶点位于开放边界，无法安全溶解。";
                    return false;
                }

                foreach (int triangle in incidentTriangles)
                    removedTriangles[ownerSubMesh].Add(triangle);
                for (int i = 1; i < boundary.Count - 1; i++)
                {
                    appendedTriangles[ownerSubMesh].Add(boundary[0]);
                    appendedTriangles[ownerSubMesh].Add(boundary[i]);
                    appendedTriangles[ownerSubMesh].Add(boundary[i + 1]);
                }
            }

            EnsureHistoryLists();
            FBXMeshSnapshot before = CaptureCurrentSnapshot();
            _record.undoHistory.Add(before);
            while (_record.undoHistory.Count > MaxHistorySteps) _record.undoHistory.RemoveAt(0);
            _record.redoHistory.Clear();

            for (int s = 0; s < subMeshCount; s++)
            {
                int[] indices = sourceIndices[s];
                var next = new List<int>(indices.Length + appendedTriangles[s].Count);
                for (int triangle = 0; triangle < indices.Length / 3; triangle++)
                {
                    if (removedTriangles[s].Contains(triangle)) continue;
                    int offset = triangle * 3;
                    next.Add(indices[offset]);
                    next.Add(indices[offset + 1]);
                    next.Add(indices[offset + 2]);
                }
                next.AddRange(appendedTriangles[s]);
                EditableMesh.SetTriangles(next, s, false);
            }
            RemoveUnusedVertices(EditableMesh);
            EditableMesh.RecalculateBounds();
            if (!ValidateMeshTriangles(EditableMesh))
            {
                _record.undoHistory.RemoveAt(_record.undoHistory.Count - 1);
                RestoreSnapshot(before);
                error = "溶解后检测到异常面，操作已自动回退。";
                return false;
            }

            _record.dissolvedEdges.Clear();
            ClearComponentSelection();
            RebuildTopology();
            EditorUtility.SetDirty(EditableMesh);
            EditorUtility.SetDirty(_record);
            error = $"已溶解 {selected.Count} 个顶点。";
            return true;
        }

        private static int FindMergeVertexRoot(int[] parent, int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        private static void UnionMergeVertices(int[] parent, int a, int b)
        {
            int rootA = FindMergeVertexRoot(parent, a);
            int rootB = FindMergeVertexRoot(parent, b);
            if (rootA != rootB) parent[rootB] = rootA;
        }

        public bool DissolveSelectedEdges(out string error)
        {
            error = string.Empty;
            if (!IsActive || ComponentMode != FBXMeshComponentMode.Edge || _selectedEdges.Count == 0)
            {
                error = "请先在 Edge Mode 中选择需要溶解的边。";
                return false;
            }

            var edges = new List<FBXMeshEdge>();
            int skippedBoundaryEdges = 0;
            foreach (int edgeIndex in _selectedEdges)
            {
                if (edgeIndex < 0 || edgeIndex >= _topology.Edges.Length) continue;
                IReadOnlyList<int> adjacent = _topology.GetAdjacentLogicalFaces(edgeIndex);
                if (adjacent.Count != 2)
                {
                    skippedBoundaryEdges++;
                    continue;
                }
                edges.Add(_topology.Edges[edgeIndex]);
            }
            if (edges.Count == 0)
            {
                error = "当前选择只有边界边或非流形边，无法进行 Dissolve。边界边会保留，以免模型产生破洞。";
                return false;
            }

            EnsureHistoryLists();
            _record.undoHistory.Add(CaptureCurrentSnapshot());
            while (_record.undoHistory.Count > MaxHistorySteps) _record.undoHistory.RemoveAt(0);
            _record.redoHistory.Clear();
            foreach (FBXMeshEdge edge in edges)
            {
                bool exists = false;
                for (int i = 0; i + 1 < _record.dissolvedEdges.Count; i += 2)
                {
                    if (new FBXMeshEdge(_record.dissolvedEdges[i], _record.dissolvedEdges[i + 1]).Equals(edge))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    _record.dissolvedEdges.Add(edge.A);
                    _record.dissolvedEdges.Add(edge.B);
                }
            }

            ClearComponentSelection();
            RebuildTopology();
            // 多边同时溶解必须全部成功；否则整体回退，避免只隐藏线却没有正确合面。
            foreach (FBXMeshEdge requestedEdge in edges)
            {
                bool stillVisible = false;
                foreach (FBXMeshEdge visibleEdge in _topology.Edges)
                {
                    if (!visibleEdge.Equals(requestedEdge)) continue;
                    stillVisible = true;
                    break;
                }
                if (!stillVisible) continue;

                FBXMeshSnapshot restore = _record.undoHistory[_record.undoHistory.Count - 1];
                _record.undoHistory.RemoveAt(_record.undoHistory.Count - 1);
                RestoreSnapshot(restore);
                error = "所选边无法全部合并为规整面，操作已自动回退。请减少选择后分段溶解。";
                return false;
            }
            EditorUtility.SetDirty(_record);
            EditorUtility.SetDirty(EditableMesh);
            if (skippedBoundaryEdges > 0)
                error = $"已溶解 {edges.Count} 条内部边；保留 {skippedBoundaryEdges} 条边界或非流形边。";
            return true;
        }

        private static void RemoveUnusedVertices(Mesh mesh)
        {
            Vector3[] oldVertices = mesh.vertices;
            bool[] used = new bool[oldVertices.Length];
            var subIndices = new int[mesh.subMeshCount][];
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                subIndices[s] = mesh.GetIndices(s);
                foreach (int index in subIndices[s]) used[index] = true;
            }
            var remap = new int[oldVertices.Length];
            var newVertices = new List<Vector3>();
            for (int i = 0; i < used.Length; i++)
            {
                remap[i] = -1;
                if (!used[i]) continue;
                remap[i] = newVertices.Count;
                newVertices.Add(oldVertices[i]);
            }
            if (newVertices.Count == oldVertices.Length) return;

            Vector3[] oldNormals = mesh.normals;
            Vector4[] oldTangents = mesh.tangents;
            Color32[] oldColors = mesh.colors32;
            Vector2[] oldUv0 = mesh.uv;
            Vector2[] oldUv1 = mesh.uv2;
            Vector2[] oldUv2 = mesh.uv3;
            Vector2[] oldUv3 = mesh.uv4;
            mesh.Clear();
            mesh.SetVertices(newVertices);
            CopyRemappedChannel(oldNormals, used, mesh.SetNormals);
            CopyRemappedChannel(oldTangents, used, mesh.SetTangents);
            CopyRemappedChannel(oldColors, used, mesh.SetColors);
            CopyRemappedChannel(oldUv0, used, values => mesh.SetUVs(0, values));
            CopyRemappedChannel(oldUv1, used, values => mesh.SetUVs(1, values));
            CopyRemappedChannel(oldUv2, used, values => mesh.SetUVs(2, values));
            CopyRemappedChannel(oldUv3, used, values => mesh.SetUVs(3, values));
            mesh.subMeshCount = subIndices.Length;
            for (int s = 0; s < subIndices.Length; s++)
            {
                for (int i = 0; i < subIndices[s].Length; i++) subIndices[s][i] = remap[subIndices[s][i]];
                mesh.SetTriangles(subIndices[s], s, false);
            }
        }

        private static void CopyRemappedChannel<T>(T[] source, bool[] used, System.Action<List<T>> setter)
        {
            if (source == null || source.Length != used.Length) return;
            var result = new List<T>();
            for (int i = 0; i < used.Length; i++) if (used[i]) result.Add(source[i]);
            setter(result);
        }
    }
}

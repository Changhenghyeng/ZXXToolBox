using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ZXXTools.FBXMeshEditor
{
    /// <summary>负责 Scene View 顶点绘制、点选和 Unity Move Handle。</summary>
    [InitializeOnLoad]
    internal static class FBXMeshEditorController
    {
        private const float PickRadius = 12f;
        private const int SceneOverlayWindowId = 0x5A58F301;
        private static Rect _sceneOverlayRect = new Rect(12, 12, 36, 163);
        private static Rect SceneOverlayRect => _sceneOverlayRect;
        private static GUIStyle _overlayPanelStyle;
        private static GUIStyle _overlayCellStyle;
        private static GUIStyle _overlayCellHoverStyle;
        private static GUIStyle _overlayCellActiveStyle;
        private static GUIStyle _overlayTopCellStyle;
        private static GUIStyle _overlayTopCellHoverStyle;
        private static GUIStyle _overlayTopCellActiveStyle;
        private static GUIStyle _overlayBottomCellStyle;
        private static GUIStyle _overlayBottomCellHoverStyle;
        private static readonly List<FBXMeshEditSession> SessionInstances = new List<FBXMeshEditSession>();
        private static FBXMeshEditSession _activeSession = new FBXMeshEditSession();
        private static FBXMeshEditSession SessionInstance => _activeSession;
        private static readonly HashSet<int> ManualExitTargets = new HashSet<int>();
        private static bool _dragging;
        private static Vector3 _lastHandlePosition;
        private static Quaternion _lastHandleRotation = Quaternion.identity;
        private static Vector3 _lastHandleScale = Vector3.one;
        private static bool _boxSelecting;
        private static bool _pendingPointerGesture;
        private static Vector2 _boxStart;
        private static Vector2 _boxCurrent;
        private static bool _boxAdditive;
        private static bool _boxSubtractive;
        private static int _pendingClickCount;
        private static bool _previousToolsHidden;
        private static bool _ownsToolsHidden;
        private static bool _changingSelection;
        private static bool _loopPreviewEnabled;
        private static int _hoveredLoopEdge = -1;
        private static float _edgeLoopPosition = 0.5f;
        private static bool _loopCutDragging;
        private const int LoopCutControlHint = 0x5A58E101;
        private static bool _loopCutShortcutHeld;
        private static bool _mergeShortcutHeld;
        private static float _mergeDistance = 0.001f;
        private static bool _lockEditTarget;
        private static bool _lockShortcutHeld;
        private static bool _subdivideShortcutHeld;
        private static bool _softSelectionEnabled;
        private static float _softSelectionRadius = 1f;
        private static bool _softRadiusKeyHeld;
        private static bool _softRadiusGesture;
        private static bool _softRadiusDragged;
        private static Vector2 _softRadiusStartMouse;
        private static float _softRadiusStartValue;
        private const int FreeRotateControlHint = 0x5A58F201;
        private static Vector2 _freeRotateStartMouse;
        private static Quaternion _freeRotateStartRotation;
        private static Vector3 _freeRotateStartVector;
        private static bool _bendModeEnabled;
        private static bool _bendShortcutHeld;
        private static bool _bendInitialized;
        private static Vector3 _bendCenter;
        private static Quaternion _bendRotation = Quaternion.identity;
        private static float _bendAngle;
        private static float _bendLow;
        private static float _bendHigh;
        private static bool _bendChanged;
        private struct BendHandleState
        {
            public Vector3 center;
            public Quaternion rotation;
            public float angle;
            public float low;
            public float high;
        }
        private static readonly List<BendHandleState> BendUndoStates = new List<BendHandleState>();
        private static readonly List<BendHandleState> BendRedoStates = new List<BendHandleState>();
        private static readonly Dictionary<FBXMeshEditSession, int> BendGroupUndoTops
            = new Dictionary<FBXMeshEditSession, int>();
        private static readonly Dictionary<FBXMeshEditSession, int> BendGroupRedoTops
            = new Dictionary<FBXMeshEditSession, int>();
        private static readonly Dictionary<FBXMeshEditSession, int> JointEditStartUndoCounts
            = new Dictionary<FBXMeshEditSession, int>();
        private static readonly HashSet<FBXMeshEditSession> JointEditParticipants
            = new HashSet<FBXMeshEditSession>();

        public static bool BendModeEnabled => _bendModeEnabled;
        public static int BendUndoCount => BendUndoStates.Count;
        public static int BendRedoCount => BendRedoStates.Count;
        public static float BendAngle => _bendAngle;
        public static float BendLowBound => _bendLow;
        public static float BendHighBound => _bendHigh;

        public static void SetBendNumericParameters(float angle, float low, float high)
        {
            if (!_bendModeEnabled || !_bendInitialized) return;
            angle = Mathf.Clamp(angle, -720f, 720f);
            low = Mathf.Min(0f, low);
            high = Mathf.Max(0f, high);
            if (Mathf.Approximately(angle, _bendAngle)
                && Mathf.Approximately(low, _bendLow)
                && Mathf.Approximately(high, _bendHigh)) return;
            BendUndoStates.Add(CaptureBendHandleState());
            if (BendUndoStates.Count > 50) BendUndoStates.RemoveAt(0);
            BendRedoStates.Clear();
            _bendAngle = angle;
            _bendLow = low;
            _bendHigh = high;
            _bendChanged = true;
            foreach (FBXMeshEditSession session in SessionInstances)
                session.BendSelection(_bendCenter, _bendRotation,
                    _bendAngle, _bendLow, _bendHigh);
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        public static void ToggleBendMode()
        {
            if (!SessionInstance.IsActive) return;
            if (_bendModeEnabled)
            {
                foreach (FBXMeshEditSession session in SessionInstances)
                {
                    if (_bendChanged) session.EndMove();
                    else session.CancelBendCapture();
                }
                if (_bendChanged) FinalizeJointUndoGroup();
                else
                {
                    JointEditStartUndoCounts.Clear();
                    JointEditParticipants.Clear();
                }
                _dragging = false;
                _bendModeEnabled = false;
                _bendInitialized = false;
                BendUndoStates.Clear();
                BendRedoStates.Clear();
            }
            else
            {
                _bendModeEnabled = true;
                _bendInitialized = false;
                _bendChanged = false;
                BendUndoStates.Clear();
                BendRedoStates.Clear();
                int revision = GetCollectiveSelectionRevision();
                CaptureJointUndoStart();
                foreach (FBXMeshEditSession session in SessionInstances)
                    session.BeginBend(true, false, 0f, null, revision);
                RegisterPendingJointParticipants();
                EnsureBendHandleInitialized();
            }
            LoopPreviewEnabled = false;
            SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(
                _bendModeEnabled ? "Bend Mode：开启" : "Bend Mode：关闭"), 1.5f);
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        public static bool SoftSelectionEnabled
        {
            get => _softSelectionEnabled;
            set
            {
                _softSelectionEnabled = value;
                SceneView.RepaintAll();
            }
        }

        public static float SoftSelectionRadius
        {
            get => _softSelectionRadius;
            set => _softSelectionRadius = Mathf.Max(0.001f, value);
        }

        public static bool LoopPreviewEnabled
        {
            get => _loopPreviewEnabled;
            set
            {
                if (_loopPreviewEnabled == value) return;
                _loopPreviewEnabled = value;
                _hoveredLoopEdge = -1;
                _loopCutDragging = false;
                _edgeLoopPosition = 0.5f;
                SceneView.RepaintAll();
            }
        }

        public static float EdgeLoopPosition
        {
            get => _edgeLoopPosition;
            set => _edgeLoopPosition = Mathf.Clamp(value, 0.05f, 0.95f);
        }

        public static bool LockEditTarget
        {
            get => _lockEditTarget;
            set => _lockEditTarget = value;
        }

        public static FBXMeshEditSession Session => SessionInstance;
        public static IReadOnlyList<FBXMeshEditSession> Sessions => SessionInstances;

        public static void SetActiveSession(FBXMeshEditSession session)
        {
            if (session == null || !SessionInstances.Contains(session)) return;
            _activeSession = session;
            FBXMeshEditorWindow.RepaintOpenWindow();
            SceneView.RepaintAll();
        }

        public static void SaveSessionsAsNewFBX(IReadOnlyList<FBXMeshEditSession> sessions)
        {
            bool succeeded = FBXMeshExporter.SaveMultipleAsNewFBX(sessions, out string message);
            if (!string.IsNullOrEmpty(message))
                EditorUtility.DisplayDialog("FBX Mesh Editor", message, "确定");
            if (succeeded) SceneView.RepaintAll();
        }

        public static void OverwriteSessionsSourceFBX(IReadOnlyList<FBXMeshEditSession> sessions)
        {
            bool succeeded = FBXMeshExporter.OverwriteMultipleSourceFBX(sessions, out string message);
            SessionInstances.RemoveAll(item => item == null || !item.IsActive);
            if (!SessionInstances.Contains(_activeSession))
                _activeSession = SessionInstances.Count > 0
                    ? SessionInstances[SessionInstances.Count - 1]
                    : new FBXMeshEditSession();
            if (SessionInstances.Count == 0) ReleaseToolsHidden();
            if (!string.IsNullOrEmpty(message))
                EditorUtility.DisplayDialog("FBX Mesh Editor", message, "确定");
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        public static float MergeDistance
        {
            get => _mergeDistance;
            set => _mergeDistance = Mathf.Max(0.000001f, value);
        }

        public static void MergeSelectedVerticesToCenter()
        {
            bool succeeded = SessionInstance.MergeSelectedVerticesToCenter(out string message);
            SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(message), 1.8f);
            if (succeeded)
            {
                SceneView.RepaintAll();
                FBXMeshEditorWindow.RepaintOpenWindow();
            }
        }

        public static void MergeSelectedVerticesByDistance()
        {
            bool succeeded = SessionInstance.MergeSelectedVerticesByDistance(_mergeDistance, out string message);
            SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(message), 1.8f);
            if (succeeded)
            {
                SceneView.RepaintAll();
                FBXMeshEditorWindow.RepaintOpenWindow();
            }
        }

        public static void SetComponentMode(FBXMeshComponentMode mode)
        {
            if (SessionInstance.ComponentMode != mode)
                LoopPreviewEnabled = false;
            foreach (FBXMeshEditSession session in SessionInstances)
                session.SetComponentMode(mode);
            if (SessionInstances.Count == 0)
                SessionInstance.SetComponentMode(mode);
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        public static void PerformUndo()
        {
            bool succeeded = TryPerformBendGroupUndo();
            if (!succeeded) succeeded = SessionInstance.PerformUndo();
            if (succeeded)
            {
                _dragging = false;
                SceneView.RepaintAll();
                FBXMeshEditorWindow.RepaintOpenWindow();
            }
        }

        public static void PerformRedo()
        {
            bool succeeded = TryPerformBendGroupRedo();
            if (!succeeded) succeeded = SessionInstance.PerformRedo();
            if (succeeded)
            {
                _dragging = false;
                SceneView.RepaintAll();
                FBXMeshEditorWindow.RepaintOpenWindow();
            }
        }

        public static void DissolveSelectedEdges()
        {
            bool succeeded = SessionInstance.DissolveSelectedEdges(out string message);
            if (!succeeded)
                EditorUtility.DisplayDialog("FBX Mesh Editor", message, "确定");
            else if (!string.IsNullOrEmpty(message))
                SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(message), 2.5f);
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        public static void DissolveSelectedVertices()
        {
            bool succeeded = SessionInstance.DissolveSelectedVertices(out string message);
            if (succeeded)
            {
                SceneView.RepaintAll();
                FBXMeshEditorWindow.RepaintOpenWindow();
            }
            else
            {
                SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(message), 1.8f);
            }
        }

        public static void CheckTopology()
        {
            SessionInstance.BuildTopologyDiagnostic(out string message);
            EditorUtility.DisplayDialog("Topology Check", message, "确定");
        }

        public static void SaveAsNewFBX()
        {
            bool succeeded = FBXMeshExporter.SaveAsNewFBX(SessionInstance, out string message);
            if (!string.IsNullOrEmpty(message))
                EditorUtility.DisplayDialog("FBX Mesh Editor", message, "确定");
            if (succeeded) SceneView.RepaintAll();
        }

        public static void OverwriteSourceFBX()
        {
            bool succeeded = FBXMeshExporter.OverwriteSourceFBX(SessionInstance, out string message);
            if (!string.IsNullOrEmpty(message))
                EditorUtility.DisplayDialog("FBX Mesh Editor", message, "确定");
            if (succeeded)
            {
                SessionInstances.RemoveAll(item => item == null || !item.IsActive);
                _activeSession = SessionInstances.Count > 0
                    ? SessionInstances[SessionInstances.Count - 1]
                    : new FBXMeshEditSession();
                if (SessionInstances.Count == 0) ReleaseToolsHidden();
                SceneView.RepaintAll();
                FBXMeshEditorWindow.RepaintOpenWindow();
            }
        }

        public static void SubdivideSelectedFaces()
        {
            RunSubdivide(false);
        }

        public static void SubdivideWholeMesh()
        {
            RunSubdivide(true, false);
        }

        public static void SmoothSubdivideWholeMesh()
        {
            RunSubdivide(true, true);
        }

        public static void RebuildSelectedPatch()
        {
            bool succeeded = SessionInstance.RebuildSelectedPatch(out string message);
            if (!succeeded)
                EditorUtility.DisplayDialog("FBX Mesh Editor", message, "确定");
            else
                SceneView.lastActiveSceneView?.ShowNotification(
                    new GUIContent("已将选中区域重建为规整四边面"), 2f);
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        private static void RunSubdivide(bool wholeMesh, bool smooth = false)
        {
            bool succeeded = SessionInstance.Subdivide(wholeMesh, smooth, out string message);
            if (!succeeded)
                EditorUtility.DisplayDialog("FBX Mesh Editor", message, "确定");
            else
                SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(
                    smooth ? "已平滑细分整个 Mesh" : wholeMesh ? "已细分整个 Mesh" : "已细分选中面"), 1.8f);
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        static FBXMeshEditorController()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedo;
            AssemblyReloadEvents.beforeAssemblyReload += Exit;
            Selection.selectionChanged += OnUnitySelectionChanged;
            EditorApplication.delayCall += OnUnitySelectionChanged;
        }

        private static void OnUndoRedo()
        {
            foreach (FBXMeshEditSession session in SessionInstances)
                session.RefreshAfterUndoRedo();
            _dragging = false;
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
            // Unity 可能在 undoRedoPerformed 后一帧才完成 Mesh 资产引用恢复，再同步一次。
            EditorApplication.delayCall += RefreshAfterDelayedUndo;
        }

        private static void RefreshAfterDelayedUndo()
        {
            foreach (FBXMeshEditSession session in SessionInstances)
                session.SynchronizeEditableMeshReference();
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        public static bool Enter(GameObject target, out string error)
        {
            return EnterTargets(target != null ? new[] { target } : System.Array.Empty<GameObject>(),
                out error, true);
        }

        public static bool EnterSelection(out string error)
        {
            return EnterTargets(Selection.gameObjects, out error, true);
        }

        public static bool AddSelectionToEditMode(out string message)
        {
            return AddTargetsToEditMode(Selection.gameObjects, out message);
        }

        private static bool AddTargetsToEditMode(IEnumerable<GameObject> targets, out string message)
        {
            message = string.Empty;
            int added = 0;
            int skipped = 0;
            FBXMeshComponentMode mode = SessionInstance.ComponentMode;
            foreach (GameObject target in targets)
            {
                if (target == null || FindSessionForObject(target) != null) continue;
                MeshFilter filter = target.GetComponent<MeshFilter>();
                MeshRenderer renderer = target.GetComponent<MeshRenderer>();
                if (filter == null || renderer == null || filter.sharedMesh == null)
                {
                    skipped++;
                    continue;
                }

                var session = new FBXMeshEditSession();
                if (!session.Begin(target, out _))
                {
                    skipped++;
                    continue;
                }
                session.SetComponentMode(mode);
                session.SetManualExitSuppressed(false);
                ManualExitTargets.Remove(target.GetInstanceID());
                SessionInstances.Add(session);
                _activeSession = session;
                added++;
            }

            if (added == 0)
            {
                message = skipped > 0 ? "所选对象中没有可加入的静态 Mesh。" : "所选模型已在编辑列表中。";
                return false;
            }
            AcquireToolsHidden();
            message = $"已加入 {added} 个模型" + (skipped > 0 ? $"，跳过 {skipped} 个无效对象。" : "。");
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
            return true;
        }

        private static bool EnterInternal(GameObject target, out string error, bool explicitRequest)
        {
            return EnterTargets(target != null ? new[] { target } : System.Array.Empty<GameObject>(),
                out error, explicitRequest);
        }

        private static bool EnterTargets(GameObject[] targets, out string error, bool explicitRequest)
        {
            error = string.Empty;
            var validTargets = new List<GameObject>();
            var seen = new HashSet<int>();
            foreach (GameObject target in targets)
            {
                if (target == null || !seen.Add(target.GetInstanceID())) continue;
                MeshFilter filter = target.GetComponent<MeshFilter>();
                MeshRenderer renderer = target.GetComponent<MeshRenderer>();
                if (filter != null && renderer != null && filter.sharedMesh != null)
                    validTargets.Add(target);
            }
            if (validTargets.Count == 0)
            {
                error = "请选择至少一个包含 MeshFilter、MeshRenderer 和有效 Mesh 的对象。";
                return false;
            }

            Exit();
            foreach (GameObject target in validTargets)
            {
                var session = new FBXMeshEditSession();
                if (!session.Begin(target, out string beginError))
                {
                    foreach (FBXMeshEditSession created in SessionInstances) created.End(false);
                    SessionInstances.Clear();
                    _activeSession = new FBXMeshEditSession();
                    error = beginError;
                    return false;
                }
                if (explicitRequest)
                {
                    ManualExitTargets.Remove(target.GetInstanceID());
                    session.SetManualExitSuppressed(false);
                }
                SessionInstances.Add(session);
            }
            _activeSession = SessionInstances[SessionInstances.Count - 1];
            AcquireToolsHidden();
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
            return true;
        }

        public static void ManualExit()
        {
            foreach (FBXMeshEditSession session in SessionInstances)
            {
                if (!session.IsActive || session.MeshFilter == null) continue;
                ManualExitTargets.Add(session.MeshFilter.gameObject.GetInstanceID());
                session.SetManualExitSuppressed(true);
            }
            Exit();
        }

        public static void Exit()
        {
            if (_bendModeEnabled)
            {
                foreach (FBXMeshEditSession session in SessionInstances)
                {
                    if (_bendChanged) session.EndMove();
                    else session.CancelBendCapture();
                }
            }
            foreach (FBXMeshEditSession session in SessionInstances)
            {
                if (_dragging && !_bendModeEnabled) session.EndMove();
                session.End(false);
            }
            SessionInstances.Clear();
            _activeSession = new FBXMeshEditSession();
            ReleaseToolsHidden();
            _dragging = false;
            _boxSelecting = false;
            _pendingPointerGesture = false;
            _pendingClickCount = 0;
            _bendModeEnabled = false;
            _bendInitialized = false;
            SceneView.RepaintAll();
        }

        public static bool Revert()
        {
            if (!SessionInstance.Revert())
                return false;
            SessionInstances.Remove(SessionInstance);
            _activeSession = SessionInstances.Count > 0
                ? SessionInstances[SessionInstances.Count - 1]
                : new FBXMeshEditSession();
            if (SessionInstances.Count == 0)
                ReleaseToolsHidden();
            _dragging = false;
            _boxSelecting = false;
            _pendingPointerGesture = false;
            _pendingClickCount = 0;
            SceneView.RepaintAll();
            return true;
        }

        private static void AcquireToolsHidden()
        {
            if (_ownsToolsHidden)
            {
                Tools.hidden = true;
                return;
            }

            _previousToolsHidden = Tools.hidden;
            _ownsToolsHidden = true;
            Tools.hidden = true;
        }

        private static void ReleaseToolsHidden()
        {
            if (!_ownsToolsHidden) return;
            Tools.hidden = _previousToolsHidden;
            _ownsToolsHidden = false;
        }

        private static void OnUnitySelectionChanged()
        {
            if (_changingSelection || SessionInstance.IsActive)
                return;

            GameObject selected = Selection.activeGameObject;
            MeshFilter filter = selected != null ? selected.GetComponent<MeshFilter>() : null;
            if (filter == null || !FBXMeshEditSession.IsWorkingMesh(filter.sharedMesh))
                return;
            if (ManualExitTargets.Contains(selected.GetInstanceID()))
                return;
            if (FBXMeshEditSession.IsAutoResumeSuppressed(filter.sharedMesh))
                return;

            EnterInternal(selected, out _, false);
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!FBXMeshEditorWindow.IsOpen)
                return;

            Event evt = Event.current;
            DrawSceneOverlay();
            if (IsPointerEventInsideOverlay(evt))
                return;
            if (!SessionInstance.IsActive)
                return;
            if (HandleBendShortcut(sceneView, evt)) return;
            if (_bendModeEnabled)
            {
                if (HandleBendHistoryShortcut(evt)) return;
                DrawComponents();
                if (IsSceneNavigationEvent(evt))
                {
                    if (evt.type == EventType.Repaint) DrawBendHandle(evt, false);
                    return;
                }
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
                DrawBendHandle(evt, true);
                return;
            }
            if (HandleSoftSelectionShortcut(sceneView, evt)) return;
            if (HandleLockEditTargetShortcut(sceneView, evt)) return;
            if (HandleLoopCutShortcut(sceneView, evt)) return;
            if (HandleSubdivideShortcut(sceneView, evt)) return;
            if (HandleMergeShortcut(evt)) return;
            if (HandleTransformToolShortcut(evt)) return;
            if (HandleHistoryShortcut(evt))
                return;
            if (HandleFrameSelectedShortcut(sceneView, evt))
                return;
            if (HandleDeleteShortcut(evt))
                return;
            // 浮层必须优先接收 IMGUI 事件，否则场景选择逻辑会抢走按钮的 MouseDown。
            DrawComponents();
            if (UpdateAndDrawEdgeLoopPreview(evt)) return;

            // Scene View 导航始终交还给 Unity：Alt 旋转、右键观察、中键平移、滚轮缩放。
            if (IsSceneNavigationEvent(evt))
            {
                // Alt/右键/中键导航时不接管鼠标，但 Repaint 仍绘制组件手柄，避免视角旋转时闪烁消失。
                if (evt.type == EventType.Repaint)
                    DrawMoveHandle(evt, false);
                return;
            }

            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            // 必须先让 Unity Handle 处理事件，否则顶点点击逻辑会吃掉 MouseDown，导致手柄无法开始拖拽。
            DrawMoveHandle(evt, true);
            HandleSelection(evt);
            DrawSelectionBox();
        }

        private static bool HandleBendShortcut(SceneView sceneView, Event evt)
        {
            if (evt.type == EventType.KeyUp && evt.keyCode == KeyCode.B && _bendShortcutHeld)
            {
                _bendShortcutHeld = false;
                evt.Use();
                return true;
            }
            if (_bendModeEnabled && evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
            {
                ToggleBendMode();
                evt.Use();
                return true;
            }
            if (evt.type != EventType.KeyDown || evt.keyCode != KeyCode.B
                || (!evt.control && !evt.command) || evt.alt) return false;
            if (!_bendShortcutHeld)
            {
                _bendShortcutHeld = true;
                ToggleBendMode();
            }
            evt.Use();
            return true;
        }

        private static bool HandleBendHistoryShortcut(Event evt)
        {
            if (evt.type != EventType.KeyDown || (!evt.control && !evt.command)) return false;
            bool redo = evt.keyCode == KeyCode.Y || (evt.keyCode == KeyCode.Z && evt.shift);
            bool undo = evt.keyCode == KeyCode.Z && !evt.shift;
            if (!undo && !redo) return false;

            List<BendHandleState> source = redo ? BendRedoStates : BendUndoStates;
            List<BendHandleState> target = redo ? BendUndoStates : BendRedoStates;
            if (source.Count > 0)
            {
                target.Add(CaptureBendHandleState());
                BendHandleState state = source[source.Count - 1];
                source.RemoveAt(source.Count - 1);
                ApplyBendHandleState(state);
                SceneView.lastActiveSceneView?.ShowNotification(
                    new GUIContent(redo ? "Bend Redo" : "Bend Undo"), 1f);
            }
            evt.Use();
            return true;
        }

        private static BendHandleState CaptureBendHandleState()
        {
            return new BendHandleState
            {
                center = _bendCenter,
                rotation = _bendRotation,
                angle = _bendAngle,
                low = _bendLow,
                high = _bendHigh
            };
        }

        private static void ApplyBendHandleState(BendHandleState state)
        {
            _bendCenter = state.center;
            _bendRotation = state.rotation;
            _bendAngle = state.angle;
            _bendLow = Mathf.Min(0f, state.low);
            _bendHigh = Mathf.Max(0f, state.high);
            foreach (FBXMeshEditSession session in SessionInstances)
                session.BendSelection(_bendCenter, _bendRotation,
                    _bendAngle, _bendLow, _bendHigh);
            SceneView.RepaintAll();
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        private static bool HandleLoopCutShortcut(SceneView sceneView, Event evt)
        {
            if (evt.type == EventType.KeyUp && evt.keyCode == KeyCode.C && _loopCutShortcutHeld)
            {
                _loopCutShortcutHeld = false;
                evt.Use();
                return true;
            }
            if (evt.type != EventType.KeyDown || evt.keyCode != KeyCode.C
                || evt.control || evt.command || evt.alt)
                return false;
            if (_loopCutShortcutHeld)
            {
                evt.Use();
                return true;
            }

            _loopCutShortcutHeld = true;
            if (SessionInstance.ComponentMode != FBXMeshComponentMode.Edge)
                SetComponentMode(FBXMeshComponentMode.Edge);
            LoopPreviewEnabled = !LoopPreviewEnabled;
            sceneView.ShowNotification(new GUIContent(
                LoopPreviewEnabled ? "Insert Edge Loop：开启" : "Insert Edge Loop：取消"), 1.2f);
            FBXMeshEditorWindow.RepaintOpenWindow();
            evt.Use();
            return true;
        }

        private static bool HandleTransformToolShortcut(Event evt)
        {
            if (evt.type != EventType.KeyDown || evt.control || evt.command || evt.alt)
                return false;

            Tool nextTool;
            if (evt.keyCode == KeyCode.W) nextTool = Tool.Move;
            else if (evt.keyCode == KeyCode.E) nextTool = Tool.Rotate;
            else if (evt.keyCode == KeyCode.R) nextTool = Tool.Scale;
            else return false;

            Tools.current = nextTool;
            SceneView.RepaintAll();
            evt.Use();
            return true;
        }

        private static bool HandleLockEditTargetShortcut(SceneView sceneView, Event evt)
        {
            if (evt.type == EventType.KeyUp && evt.keyCode == KeyCode.L && _lockShortcutHeld)
            {
                _lockShortcutHeld = false;
                evt.Use();
                return true;
            }
            if (evt.type != EventType.KeyDown || evt.keyCode != KeyCode.L
                || (!evt.control && !evt.command) || evt.alt)
                return false;
            if (_lockShortcutHeld)
            {
                evt.Use();
                return true;
            }

            _lockShortcutHeld = true;
            LockEditTarget = !LockEditTarget;
            sceneView.ShowNotification(new GUIContent(
                LockEditTarget ? "Edit Target：已锁定" : "Edit Target：已解锁"), 1.2f);
            FBXMeshEditorWindow.RepaintOpenWindow();
            evt.Use();
            return true;
        }

        private static bool HandleSoftSelectionShortcut(SceneView sceneView, Event evt)
        {
            if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.B
                && !evt.control && !evt.command && !evt.alt)
            {
                // Windows 会连续发送按键重复事件；只接受第一次 KeyDown。
                if (_softRadiusKeyHeld)
                {
                    evt.Use();
                    return true;
                }
                _softRadiusKeyHeld = true;
                _softRadiusDragged = false;
                evt.Use();
                return true;
            }

            if (_softRadiusKeyHeld && evt.type == EventType.MouseDown && evt.button == 0)
            {
                _softRadiusGesture = true;
                _softRadiusStartMouse = evt.mousePosition;
                _softRadiusStartValue = _softSelectionRadius;
                GUIUtility.hotControl = GUIUtility.GetControlID(FocusType.Passive);
                evt.Use();
                return true;
            }

            if (_softRadiusGesture && evt.type == EventType.MouseDrag && evt.button == 0)
            {
                float delta = evt.mousePosition.x - _softRadiusStartMouse.x;
                SoftSelectionEnabled = true;
                SoftSelectionRadius = _softRadiusStartValue * Mathf.Pow(2f, delta / 100f);
                _softRadiusDragged = true;
                sceneView.ShowNotification(
                    new GUIContent($"Soft Radius: {_softSelectionRadius:0.###}"), 0.25f);
                FBXMeshEditorWindow.RepaintOpenWindow();
                evt.Use();
                return true;
            }

            if (_softRadiusGesture && evt.rawType == EventType.MouseUp && evt.button == 0)
            {
                _softRadiusGesture = false;
                GUIUtility.hotControl = 0;
                evt.Use();
                return true;
            }

            if (evt.type == EventType.KeyUp && evt.keyCode == KeyCode.B)
            {
                if (!_softRadiusDragged)
                {
                    SoftSelectionEnabled = !SoftSelectionEnabled;
                    sceneView.ShowNotification(new GUIContent(
                        SoftSelectionEnabled ? "Soft Selection：开启" : "Soft Selection：关闭"), 1.2f);
                }
                _softRadiusKeyHeld = false;
                _softRadiusGesture = false;
                _softRadiusDragged = false;
                GUIUtility.hotControl = 0;
                FBXMeshEditorWindow.RepaintOpenWindow();
                evt.Use();
                return true;
            }
            return false;
        }

        private static bool HandleDeleteShortcut(Event evt)
        {
            if (evt.type != EventType.KeyDown
                || (evt.keyCode != KeyCode.Delete && evt.keyCode != KeyCode.Backspace))
                return false;

            if (SessionInstance.ComponentMode == FBXMeshComponentMode.Vertex
                && SessionInstance.SelectedVertices.Count > 0)
                DissolveSelectedVertices();
            else if (SessionInstance.ComponentMode == FBXMeshComponentMode.Edge
                && SessionInstance.SelectedEdges.Count > 0)
                DissolveSelectedEdges();
            else
                return false;
            evt.Use();
            return true;
        }

        private static bool HandleFrameSelectedShortcut(SceneView sceneView, Event evt)
        {
            if (evt.type != EventType.KeyDown || evt.keyCode != KeyCode.F
                || evt.control || evt.command || evt.alt)
                return false;

            if (!SessionInstance.TryGetSelectionBoundsWorld(out Bounds bounds))
                return false;

            sceneView.Frame(bounds, false);
            evt.Use();
            sceneView.Repaint();
            return true;
        }

        private static bool IsSceneNavigationEvent(Event evt)
        {
            return evt.alt
                || evt.button == 1
                || evt.button == 2
                || evt.type == EventType.ScrollWheel;
        }

        private static bool UpdateAndDrawEdgeLoopPreview(Event evt)
        {
            if (!_loopPreviewEnabled || SessionInstance.ComponentMode != FBXMeshComponentMode.Edge
                || _dragging || _boxSelecting || _pendingPointerGesture)
            {
                _hoveredLoopEdge = -1;
                return false;
            }
            bool navigationEvent = evt.alt || evt.button == 2 || evt.type == EventType.ScrollWheel;

            if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
            {
                LoopPreviewEnabled = false;
                evt.Use();
                return true;
            }
            if (evt.type == EventType.MouseDown && evt.button == 1)
            {
                LoopPreviewEnabled = false;
                evt.Use();
                return true;
            }

            if (!_loopCutDragging && (evt.type == EventType.MouseMove
                || evt.type == EventType.Repaint || evt.type == EventType.MouseDown))
                _hoveredLoopEdge = FindNearestComponent(evt.mousePosition);
            if (_hoveredLoopEdge < 0)
            {
                if (!navigationEvent && evt.type == EventType.MouseDown && evt.button == 0)
                    evt.Use();
                return !navigationEvent;
            }

            int controlId = GUIUtility.GetControlID(LoopCutControlHint, FocusType.Passive);
            if (!_loopCutDragging && evt.type == EventType.MouseDown && evt.button == 0 && !evt.alt)
            {
                _loopCutDragging = true;
                _edgeLoopPosition = 0.5f;
                GUIUtility.hotControl = controlId;
                evt.Use();
            }
            else if (_loopCutDragging && evt.type == EventType.MouseDrag && evt.button == 0)
            {
                if (TryGetLoopCutReferenceScreenEdge(_hoveredLoopEdge, out Vector2 a, out Vector2 b))
                {
                    Vector2 direction = b - a;
                    float position = direction.sqrMagnitude > 0.0001f
                        ? Vector2.Dot(evt.mousePosition - a, direction) / direction.sqrMagnitude
                        : 0.5f;
                    if (evt.shift) position = Mathf.Round(position * 10f) * 0.1f;
                    _edgeLoopPosition = Mathf.Clamp(position, evt.shift ? 0.1f : 0.05f,
                        evt.shift ? 0.9f : 0.95f);
                }
                evt.Use();
                SceneView.RepaintAll();
            }
            else if (_loopCutDragging && evt.rawType == EventType.MouseUp && evt.button == 0)
            {
                int edgeIndex = _hoveredLoopEdge;
                float position = _edgeLoopPosition;
                _loopCutDragging = false;
                GUIUtility.hotControl = 0;
                LoopPreviewEnabled = false;
                if (!SessionInstance.InsertEdgeLoop(edgeIndex, position, out string error))
                    EditorUtility.DisplayDialog("FBX Mesh Editor", error, "确定");
                FBXMeshEditorWindow.RepaintOpenWindow();
                SceneView.RepaintAll();
                evt.Use();
                return true;
            }

            if (!SessionInstance.Topology.TryBuildOrientedLoopCut(_hoveredLoopEdge,
                out List<FBXMeshLoopCutFace> cutFaces, out _, out _))
                return !navigationEvent;
            Vector3[] vertices = SessionInstance.EditableMesh.vertices;
            Transform targetTransform = SessionInstance.MeshFilter.transform;
            Handles.color = new Color(1f, 0.85f, 0.05f, 1f);
            Vector3 labelPosition = Vector3.zero;
            bool hasLabelPosition = false;
            foreach (FBXMeshLoopCutFace cutFace in cutFaces)
            {
                FBXMeshLogicalFace face = SessionInstance.Topology.LogicalFaces[cutFace.FaceIndex];
                int edgePosition = cutFace.FirstEdgePosition;
                float facePosition = cutFace.FlipPosition
                    ? 1f - _edgeLoopPosition : _edgeLoopPosition;
                if (!face.IsQuad)
                {
                    int t0 = face.Vertices[edgePosition];
                    int t1 = face.Vertices[(edgePosition + 1) % 3];
                    int apex = face.Vertices[(edgePosition + 2) % 3];
                    Vector3 terminalA = targetTransform.TransformPoint(
                        Vector3.Lerp(vertices[t0], vertices[t1], facePosition));
                    Vector3 terminalB = targetTransform.TransformPoint(vertices[apex]);
                    Handles.DrawDottedLine(terminalA, terminalB, 3f);
                    if (!hasLabelPosition)
                    {
                        labelPosition = (terminalA + terminalB) * 0.5f;
                        hasLabelPosition = true;
                    }
                    continue;
                }
                int q0 = face.Vertices[edgePosition];
                int q1 = face.Vertices[(edgePosition + 1) % 4];
                int q2 = face.Vertices[(edgePosition + 2) % 4];
                int q3 = face.Vertices[(edgePosition + 3) % 4];
                Vector3 a = targetTransform.TransformPoint(
                    Vector3.Lerp(vertices[q0], vertices[q1], facePosition));
                Vector3 b = targetTransform.TransformPoint(
                    Vector3.Lerp(vertices[q2], vertices[q3], 1f - facePosition));
                Handles.DrawDottedLine(a, b, 3f);
                if (!hasLabelPosition)
                {
                    labelPosition = (a + b) * 0.5f;
                    hasLabelPosition = true;
                }
            }
            if (hasLabelPosition)
                Handles.Label(labelPosition, $"{_edgeLoopPosition * 100f:0}%");
            return !navigationEvent;
        }

        private static bool HandleMergeShortcut(Event evt)
        {
            if (evt.type == EventType.KeyUp && evt.keyCode == KeyCode.M && _mergeShortcutHeld)
            {
                _mergeShortcutHeld = false;
                evt.Use();
                return true;
            }
            if (evt.type != EventType.KeyDown || evt.keyCode != KeyCode.M
                || evt.alt || SessionInstance.ComponentMode != FBXMeshComponentMode.Vertex)
                return false;
            if (_mergeShortcutHeld)
            {
                evt.Use();
                return true;
            }

            _mergeShortcutHeld = true;
            if (evt.control || evt.command) MergeSelectedVerticesByDistance();
            else MergeSelectedVerticesToCenter();
            evt.Use();
            return true;
        }

        private static bool TryGetLoopCutReferenceScreenEdge(
            int startEdgeIndex, out Vector2 screenA, out Vector2 screenB)
        {
            screenA = default;
            screenB = default;
            if (startEdgeIndex < 0 || startEdgeIndex >= SessionInstance.Topology.Edges.Length)
                return false;

            if (!SessionInstance.Topology.TryBuildOrientedLoopCut(startEdgeIndex,
                out _, out int referenceA, out int referenceB))
                return false;
            Vector3[] vertices = SessionInstance.EditableMesh.vertices;
            Transform targetTransform = SessionInstance.MeshFilter.transform;
            screenA = HandleUtility.WorldToGUIPoint(
                targetTransform.TransformPoint(vertices[referenceA]));
            screenB = HandleUtility.WorldToGUIPoint(
                targetTransform.TransformPoint(vertices[referenceB]));
            return true;
        }

        private static bool HandleSubdivideShortcut(SceneView sceneView, Event evt)
        {
            if (evt.type == EventType.KeyUp && evt.keyCode == KeyCode.D && _subdivideShortcutHeld)
            {
                _subdivideShortcutHeld = false;
                evt.Use();
                return true;
            }
            if (evt.type != EventType.KeyDown || evt.keyCode != KeyCode.D
                || evt.control || evt.command || evt.alt
                || SessionInstance.ComponentMode != FBXMeshComponentMode.Face)
                return false;
            if (_subdivideShortcutHeld)
            {
                evt.Use();
                return true;
            }

            _subdivideShortcutHeld = true;
            if (evt.shift)
            {
                if (SessionInstance.SelectedFaces.Count > 0)
                {
                    sceneView.ShowNotification(new GUIContent(
                        "Smooth Subdivide 仅支持整个 Mesh，请先清空面选择。"), 2.5f);
                    FBXMeshEditorWindow.RepaintOpenWindow();
                }
                else
                {
                    SmoothSubdivideWholeMesh();
                }
            }
            else if (SessionInstance.SelectedFaces.Count > 0)
                SubdivideSelectedFaces();
            else
                SubdivideWholeMesh();
            sceneView.Repaint();
            evt.Use();
            return true;
        }

        internal static bool HandleHistoryShortcut(Event evt)
        {
            if (evt.type != EventType.KeyDown || (!evt.control && !evt.command))
                return false;
            if (_bendModeEnabled) return HandleBendHistoryShortcut(evt);

            if (evt.keyCode == KeyCode.Z)
            {
                if (evt.shift) PerformRedo();
                else PerformUndo();
                evt.Use();
                return true;
            }

            if (evt.keyCode == KeyCode.Y)
            {
                PerformRedo();
                evt.Use();
                return true;
            }

            return false;
        }

        private static bool IsPointerEventInsideOverlay(Event evt)
        {
            if (!evt.isMouse)
                return false;

            return SceneOverlayRect.Contains(evt.mousePosition);
        }

        private static void DrawComponents()
        {
            List<Vector3> worldSeeds = CollectSelectionWorldSeeds();
            int collectiveRevision = GetCollectiveSelectionRevision();
            foreach (FBXMeshEditSession session in SessionInstances)
                DrawComponents(session, worldSeeds, collectiveRevision);
        }

        private static void DrawComponents(FBXMeshEditSession session,
            IReadOnlyList<Vector3> worldSeeds, int collectiveRevision)
        {
            if (!session.IsActive || session.EditableMesh == null
                || session.MeshFilter == null || session.Topology == null)
                return;

            Vector3[] vertices = session.EditableMesh.vertices;
            Transform targetTransform = session.MeshFilter.transform;
            session.UpdateSoftSelectionPreview(_softSelectionEnabled, _softSelectionRadius,
                worldSeeds, collectiveRevision);
            if (session.ComponentMode == FBXMeshComponentMode.Face)
            {
                FBXMeshLogicalFace[] faces = session.Topology.LogicalFaces;
                for (int face = 0; face < faces.Length; face++)
                {
                    int[] indices = faces[face].Vertices;
                    var polygon = new Vector3[indices.Length];
                    for (int i = 0; i < indices.Length; i++)
                        polygon[i] = targetTransform.TransformPoint(vertices[indices[i]]);
                    if (session.ContainsFace(face))
                    {
                        Handles.color = new Color(1f, 0.5f, 0.05f, 0.45f);
                        Handles.DrawAAConvexPolygon(polygon);
                    }
                    Handles.color = new Color(0.1f, 0.7f, 1f, 0.65f);
                    var outline = new Vector3[polygon.Length + 1];
                    for (int i = 0; i < polygon.Length; i++) outline[i] = polygon[i];
                    outline[polygon.Length] = polygon[0];
                    Handles.DrawAAPolyLine(1.5f, outline);
                }
                DrawSoftInfluenceVertices(session, vertices, targetTransform);
                return;
            }

            if (session.ComponentMode == FBXMeshComponentMode.Edge)
            {
                FBXMeshEdge[] edges = session.Topology.Edges;
                var lines = new Vector3[edges.Length * 2];
                for (int i = 0; i < edges.Length; i++)
                {
                    Vector3 a = targetTransform.TransformPoint(vertices[edges[i].A]);
                    Vector3 b = targetTransform.TransformPoint(vertices[edges[i].B]);
                    lines[i * 2] = a;
                    lines[i * 2 + 1] = b;
                    if (session.ContainsEdge(i))
                    {
                        Handles.color = new Color(1f, 0.55f, 0.05f, 1f);
                        Handles.DrawAAPolyLine(4f, a, b);
                    }
                }
                Handles.color = new Color(0.1f, 0.75f, 1f, 0.75f);
                Handles.DrawLines(lines);
                DrawSoftInfluenceVertices(session, vertices, targetTransform);
                return;
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                if (session.IsVertexLogicallyDissolved(i)) continue;
                if (!session.IsLogicalVertexRepresentative(i)) continue;
                Vector3 world = targetTransform.TransformPoint(vertices[i]);
                float softWeight = session.GetSoftSelectionWeight(i);
                Handles.color = session.Contains(i)
                    ? new Color(1f, 0.55f, 0.05f, 1f)
                    : softWeight > 0f
                        ? Color.Lerp(new Color(0.1f, 0.75f, 1f, 0.9f),
                            new Color(1f, 0.35f, 0.05f, 1f), softWeight)
                        : new Color(0.1f, 0.75f, 1f, 0.9f);
                Handles.DotHandleCap(0, world, Quaternion.identity,
                    HandleUtility.GetHandleSize(world) * 0.035f, EventType.Repaint);
            }
        }

        private static void DrawSoftInfluenceVertices(FBXMeshEditSession session,
            Vector3[] vertices, Transform targetTransform)
        {
            if (!_softSelectionEnabled) return;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (session.IsVertexLogicallyDissolved(i)) continue;
                if (!session.IsLogicalVertexRepresentative(i)) continue;
                float weight = session.GetSoftSelectionWeight(i);
                if (weight <= 0.0001f) continue;
                Vector3 world = targetTransform.TransformPoint(vertices[i]);
                Handles.color = Color.Lerp(
                    new Color(0.1f, 0.75f, 1f, 0.75f),
                    new Color(1f, 0.3f, 0.02f, 1f), weight);
                Handles.DotHandleCap(0, world, Quaternion.identity,
                    HandleUtility.GetHandleSize(world) * 0.03f, EventType.Repaint);
            }
        }

        private static void HandleSelection(Event evt)
        {
            if (_boxSelecting)
            {
                if (evt.type == EventType.MouseDrag && evt.button == 0)
                {
                    _boxCurrent = evt.mousePosition;
                    evt.Use();
                    SceneView.RepaintAll();
                    return;
                }

                if (evt.rawType == EventType.MouseUp && evt.button == 0)
                {
                    _boxCurrent = evt.mousePosition;
                    ApplyBoxSelection();
                    _boxSelecting = false;
                    GUIUtility.hotControl = 0;
                    evt.Use();
                    FBXMeshEditorWindow.RepaintOpenWindow();
                    SceneView.RepaintAll();
                    return;
                }
            }

            if (_pendingPointerGesture)
            {
                if (evt.type == EventType.MouseDrag && evt.button == 0)
                {
                    _boxCurrent = evt.mousePosition;
                    if (Vector2.Distance(_boxStart, _boxCurrent) >= 4f)
                    {
                        _pendingPointerGesture = false;
                        _boxSelecting = true;
                    }
                    evt.Use();
                    SceneView.RepaintAll();
                    return;
                }

                if (evt.rawType == EventType.MouseUp && evt.button == 0)
                {
                    _pendingPointerGesture = false;
                    HandleSceneClick(evt.mousePosition, _pendingClickCount);
                    GUIUtility.hotControl = 0;
                    evt.Use();
                    FBXMeshEditorWindow.RepaintOpenWindow();
                    SceneView.RepaintAll();
                    return;
                }
            }

            if (evt.type != EventType.MouseDown || evt.button != 0 || evt.alt)
                return;

            // PositionHandle 已经接管鼠标时，不再把这次点击解释为顶点选择。
            if (GUIUtility.hotControl != 0)
                return;

            // Rotate 模式下，旋转圈内部完整归旋转手势；只有圈外才能开始框选。
            if (Tools.current == Tool.Rotate && GetTotalSelectedComponentCount() > 0
                && IsInsideRotationHandle(evt.mousePosition))
                return;

            // 无论起点是否压在组件上，都等待 MouseDrag / MouseUp 再判断点选或框选。
            // 这样可以从顶点、边或面上直接起框，不会被立即点选抢走事件。
            _pendingPointerGesture = true;
            _boxStart = evt.mousePosition;
            _boxCurrent = evt.mousePosition;
            _boxSubtractive = evt.control || evt.command;
            _boxAdditive = evt.shift && !_boxSubtractive;
            _pendingClickCount = evt.clickCount;
            GUIUtility.hotControl = GUIUtility.GetControlID(FocusType.Passive);
            evt.Use();
        }

        private static void HandleSceneClick(Vector2 mousePosition, int clickCount)
        {
            // 明确命中工具绘制的组件时，组件选择优先于 Unity Object Pick。
            // 这样多个模型穿插时，前方 Renderer 不会抢走后方可见点线的点击。
            FindNearestComponentAcrossSessions(mousePosition,
                out FBXMeshEditSession nearestSession, out int nearest);
            if (nearestSession != null && nearest >= 0)
            {
                _activeSession = nearestSession;
                if (clickCount >= 2 && nearestSession.ComponentMode == FBXMeshComponentMode.Edge)
                {
                    List<int> edgeLoop = nearestSession.Topology.GetConnectedEdgeLoop(nearest);
                    nearestSession.SelectComponentRange(
                        edgeLoop,
                        !_boxSubtractive,
                        _boxSubtractive);
                    return;
                }

                nearestSession.SelectComponent(nearest, _boxAdditive, _boxSubtractive);
                return;
            }

            GameObject pickedObject = HandleUtility.PickGameObject(mousePosition, false);
            FBXMeshEditSession pickedTargetSession = FindSessionForObject(pickedObject);
            if (pickedObject != null && pickedTargetSession == null)
            {
                if (_boxAdditive)
                {
                    AddTargetsToEditMode(new[] { pickedObject }, out string addMessage);
                    SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(addMessage), 1.5f);
                    return;
                }
                if (_lockEditTarget) return;
                Exit();
                _changingSelection = true;
                Selection.activeGameObject = pickedObject;
                _changingSelection = false;
                EditorGUIUtility.PingObject(pickedObject);
                OnUnitySelectionChanged();
                return;
            }

            if (!_boxAdditive && !_boxSubtractive)
                ClearAllComponentSelections();
        }

        private static void ApplyBoxSelection()
        {
            Rect rect = GetSelectionRect();
            if (rect.width < 3f && rect.height < 3f)
            {
                if (!_boxAdditive && !_boxSubtractive) ClearAllComponentSelections();
                return;
            }
            foreach (FBXMeshEditSession session in SessionInstances)
                ApplyBoxSelection(session, rect);
        }

        private static void ApplyBoxSelection(FBXMeshEditSession session, Rect rect)
        {
            var indices = new List<int>();
            Vector3[] vertices = session.EditableMesh.vertices;
            Transform targetTransform = session.MeshFilter.transform;
            Camera camera = SceneView.currentDrawingSceneView != null
                ? SceneView.currentDrawingSceneView.camera : null;

            if (session.ComponentMode == FBXMeshComponentMode.Vertex)
            {
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (session.IsVertexLogicallyDissolved(i)) continue;
                    if (!session.IsLogicalVertexRepresentative(i)) continue;
                    Vector3 world = targetTransform.TransformPoint(vertices[i]);
                    if (camera != null && camera.WorldToViewportPoint(world).z <= 0f) continue;
                    if (rect.Contains(HandleUtility.WorldToGUIPoint(world))) indices.Add(i);
                }
            }
            else if (session.ComponentMode == FBXMeshComponentMode.Edge)
            {
                FBXMeshEdge[] edges = session.Topology.Edges;
                for (int i = 0; i < edges.Length; i++)
                {
                    Vector2 a = HandleUtility.WorldToGUIPoint(targetTransform.TransformPoint(vertices[edges[i].A]));
                    Vector2 b = HandleUtility.WorldToGUIPoint(targetTransform.TransformPoint(vertices[edges[i].B]));
                    if (SegmentIntersectsRect(a, b, rect)) indices.Add(i);
                }
            }
            else
            {
                FBXMeshLogicalFace[] faces = session.Topology.LogicalFaces;
                for (int face = 0; face < faces.Length; face++)
                {
                    int[] faceVertices = faces[face].Vertices;
                    var polygon = new Vector2[faceVertices.Length];
                    for (int i = 0; i < faceVertices.Length; i++)
                        polygon[i] = HandleUtility.WorldToGUIPoint(
                            targetTransform.TransformPoint(vertices[faceVertices[i]]));
                    if (PolygonIntersectsRect(polygon, rect)) indices.Add(face);
                }
            }
            session.SelectComponentRange(indices, _boxAdditive, _boxSubtractive);
        }

        private static Rect GetSelectionRect()
        {
            return Rect.MinMaxRect(
                Mathf.Min(_boxStart.x, _boxCurrent.x),
                Mathf.Min(_boxStart.y, _boxCurrent.y),
                Mathf.Max(_boxStart.x, _boxCurrent.x),
                Mathf.Max(_boxStart.y, _boxCurrent.y));
        }

        private static void DrawSelectionBox()
        {
            if (!_boxSelecting || Event.current.type != EventType.Repaint)
                return;

            Rect rect = GetSelectionRect();
            Handles.BeginGUI();
            EditorGUI.DrawRect(rect, new Color(0.15f, 0.55f, 1f, 0.16f));
            Handles.color = new Color(0.2f, 0.7f, 1f, 0.95f);
            Handles.DrawAAPolyLine(2f,
                new Vector3(rect.xMin, rect.yMin), new Vector3(rect.xMax, rect.yMin),
                new Vector3(rect.xMax, rect.yMax), new Vector3(rect.xMin, rect.yMax),
                new Vector3(rect.xMin, rect.yMin));
            Handles.EndGUI();
        }

        private static int FindNearestComponent(Vector2 mousePosition)
        {
            Vector3[] vertices = SessionInstance.EditableMesh.vertices;
            Transform targetTransform = SessionInstance.MeshFilter.transform;
            Camera camera = SceneView.currentDrawingSceneView != null
                ? SceneView.currentDrawingSceneView.camera
                : null;
            float bestDistance = PickRadius;
            int bestIndex = -1;
            if (SessionInstance.ComponentMode == FBXMeshComponentMode.Edge)
            {
                FBXMeshEdge[] edges = SessionInstance.Topology.Edges;
                for (int i = 0; i < edges.Length; i++)
                {
                    Vector2 a = HandleUtility.WorldToGUIPoint(targetTransform.TransformPoint(vertices[edges[i].A]));
                    Vector2 b = HandleUtility.WorldToGUIPoint(targetTransform.TransformPoint(vertices[edges[i].B]));
                    float distance = HandleUtility.DistancePointToLineSegment(mousePosition, a, b);
                    if (distance < bestDistance) { bestDistance = distance; bestIndex = i; }
                }
                return bestIndex;
            }

            if (SessionInstance.ComponentMode == FBXMeshComponentMode.Face)
            {
                FBXMeshLogicalFace[] faces = SessionInstance.Topology.LogicalFaces;
                for (int face = faces.Length - 1; face >= 0; face--)
                {
                    int[] faceVertices = faces[face].Vertices;
                    var polygon = new Vector2[faceVertices.Length];
                    for (int i = 0; i < faceVertices.Length; i++)
                        polygon[i] = HandleUtility.WorldToGUIPoint(
                            targetTransform.TransformPoint(vertices[faceVertices[i]]));
                    if (PointInPolygon(mousePosition, polygon)) return face;
                }
                return -1;
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                if (SessionInstance.IsVertexLogicallyDissolved(i)) continue;
                if (!SessionInstance.IsLogicalVertexRepresentative(i)) continue;
                Vector3 world = targetTransform.TransformPoint(vertices[i]);
                if (camera != null && camera.WorldToViewportPoint(world).z <= 0f)
                    continue;

                float distance = Vector2.Distance(HandleUtility.WorldToGUIPoint(world), mousePosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }

        private static void FindNearestComponentAcrossSessions(Vector2 mousePosition,
            out FBXMeshEditSession bestSession, out int bestIndex)
        {
            bestSession = null;
            bestIndex = -1;
            float bestDistance = PickRadius;
            FBXMeshEditSession previous = _activeSession;
            foreach (FBXMeshEditSession session in SessionInstances)
            {
                _activeSession = session;
                int index = FindNearestComponent(mousePosition);
                if (index < 0) continue;
                float distance = GetComponentScreenDistance(session, index, mousePosition);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestIndex = index;
                bestSession = session;
            }
            _activeSession = previous;
        }

        private static float GetComponentScreenDistance(FBXMeshEditSession session,
            int index, Vector2 mousePosition)
        {
            Vector3[] vertices = session.EditableMesh.vertices;
            Transform transform = session.MeshFilter.transform;
            if (session.ComponentMode == FBXMeshComponentMode.Edge)
            {
                FBXMeshEdge edge = session.Topology.Edges[index];
                return HandleUtility.DistancePointToLineSegment(mousePosition,
                    HandleUtility.WorldToGUIPoint(transform.TransformPoint(vertices[edge.A])),
                    HandleUtility.WorldToGUIPoint(transform.TransformPoint(vertices[edge.B])));
            }
            if (session.ComponentMode == FBXMeshComponentMode.Face)
                return 0f;
            return Vector2.Distance(mousePosition,
                HandleUtility.WorldToGUIPoint(transform.TransformPoint(vertices[index])));
        }

        private static FBXMeshEditSession FindSessionForObject(GameObject gameObject)
        {
            if (gameObject == null) return null;
            foreach (FBXMeshEditSession session in SessionInstances)
                if (session.MeshFilter != null && session.MeshFilter.gameObject == gameObject)
                    return session;
            return null;
        }

        private static void ClearAllComponentSelections()
        {
            foreach (FBXMeshEditSession session in SessionInstances)
                session.ClearSelection();
        }

        private static bool PointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(point, a, b);
            float d2 = Sign(point, b, c);
            float d3 = Sign(point, c, a);
            bool hasNegative = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPositive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(hasNegative && hasPositive);
        }

        private static bool PointInPolygon(Vector2 point, Vector2[] polygon)
        {
            if (polygon.Length == 3)
                return PointInTriangle(point, polygon[0], polygon[1], polygon[2]);
            for (int i = 1; i < polygon.Length - 1; i++)
                if (PointInTriangle(point, polygon[0], polygon[i], polygon[i + 1])) return true;
            return false;
        }

        private static bool PolygonIntersectsRect(Vector2[] polygon, Rect rect)
        {
            foreach (Vector2 point in polygon)
                if (rect.Contains(point)) return true;

            Vector2[] corners =
            {
                new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax)
            };
            foreach (Vector2 corner in corners)
                if (PointInPolygon(corner, polygon)) return true;

            for (int i = 0; i < polygon.Length; i++)
                if (SegmentIntersectsRect(polygon[i], polygon[(i + 1) % polygon.Length], rect)) return true;
            return false;
        }

        private static bool SegmentIntersectsRect(Vector2 a, Vector2 b, Rect rect)
        {
            if (rect.Contains(a) || rect.Contains(b)) return true;
            Vector2 minMin = new Vector2(rect.xMin, rect.yMin);
            Vector2 maxMin = new Vector2(rect.xMax, rect.yMin);
            Vector2 maxMax = new Vector2(rect.xMax, rect.yMax);
            Vector2 minMax = new Vector2(rect.xMin, rect.yMax);
            return SegmentsIntersect(a, b, minMin, maxMin)
                || SegmentsIntersect(a, b, maxMin, maxMax)
                || SegmentsIntersect(a, b, maxMax, minMax)
                || SegmentsIntersect(a, b, minMax, minMin);
        }

        private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float abC = Sign(c, a, b);
            float abD = Sign(d, a, b);
            float cdA = Sign(a, c, d);
            float cdB = Sign(b, c, d);
            return abC * abD <= 0f && cdA * cdB <= 0f;
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
        }

        private static void DrawBendHandle(Event evt, bool interactive)
        {
            if (!EnsureBendHandleInitialized()) return;

            float handleSize = HandleUtility.GetHandleSize(_bendCenter);
            Vector3 axisWorld = _bendRotation * Vector3.up;
            Vector3 bendWorld = _bendRotation * Vector3.right;
            Vector3 lowWorld = _bendCenter + axisWorld * _bendLow;
            Vector3 highWorld = _bendCenter + axisWorld * _bendHigh;
            float endBarSize = handleSize * 0.28f;
            Handles.color = new Color(0.15f, 0.8f, 1f, 0.98f);
            Handles.DrawAAPolyLine(3f, lowWorld, highWorld);
            Handles.DrawAAPolyLine(3f,
                lowWorld - bendWorld * endBarSize, lowWorld + bendWorld * endBarSize);
            Handles.DrawAAPolyLine(3f,
                highWorld - bendWorld * endBarSize, highWorld + bendWorld * endBarSize);
            Handles.Label(lowWorld - bendWorld * endBarSize, $"Low  {_bendLow:0.00}");
            Handles.Label(highWorld - bendWorld * endBarSize, $"High  {_bendHigh:0.00}");
            Handles.color = new Color(1f, 0.65f, 0.1f, 0.95f);
            Vector3 visualBendDirection = _bendAngle < 0f ? -bendWorld : bendWorld;
            Handles.ArrowHandleCap(0, _bendCenter, Quaternion.LookRotation(visualBendDirection),
                handleSize * 1.2f, EventType.Repaint);
            Handles.Label(_bendCenter + visualBendDirection * handleSize * 1.25f,
                _bendAngle >= 0f ? "+X Bend" : "-X Bend");
            DrawBendCurvePreview(_bendCenter, _bendRotation,
                _bendAngle, _bendLow, _bendHigh);

            EditorGUI.BeginChangeCheck();
            Vector3 nextCenter = Handles.PositionHandle(_bendCenter, _bendRotation);
            Quaternion nextRotation = Handles.RotationHandle(_bendRotation, _bendCenter);
            Vector3 nextAxis = nextRotation * Vector3.up;
            Vector3 lowPosition = Handles.Slider(nextCenter + nextAxis * _bendLow,
                -nextAxis, handleSize * 0.12f, Handles.ConeHandleCap, 0f);
            Vector3 highPosition = Handles.Slider(nextCenter + nextAxis * _bendHigh,
                nextAxis, handleSize * 0.12f, Handles.ConeHandleCap, 0f);
            float nextLow = Mathf.Min(0f, Vector3.Dot(lowPosition - nextCenter, nextAxis));
            float nextHigh = Mathf.Max(0f, Vector3.Dot(highPosition - nextCenter, nextAxis));
            float centerSnap = handleSize * 0.04f;
            if (Mathf.Abs(nextLow) < centerSnap) nextLow = 0f;
            if (Mathf.Abs(nextHigh) < centerSnap) nextHigh = 0f;

            Vector3 nextBendDirection = nextRotation * Vector3.right;
            float curvatureOffset = handleSize * (1.5f + _bendAngle / 180f);
            Vector3 curvaturePosition = Handles.Slider(
                nextCenter + nextBendDirection * curvatureOffset,
                nextBendDirection, handleSize * 0.14f, Handles.SphereHandleCap, 0f);
            float nextAngle = Mathf.Clamp(
                (Vector3.Dot(curvaturePosition - nextCenter, nextBendDirection) / handleSize - 1.5f)
                * 180f, -720f, 720f);
            Handles.Label(curvaturePosition, $"Bend  {nextAngle:0.0}°");

            if (EditorGUI.EndChangeCheck())
            {
                if (!interactive) return;
                if (!_dragging)
                {
                    // Bend Mode 在进入时已固定基准顶点；后续每次拖动都从同一基准重算。
                    BendUndoStates.Add(CaptureBendHandleState());
                    if (BendUndoStates.Count > 50) BendUndoStates.RemoveAt(0);
                    BendRedoStates.Clear();
                }
                _dragging = true;
                _bendChanged = true;
                _bendCenter = nextCenter;
                _bendRotation = nextRotation;
                _bendLow = nextLow;
                _bendHigh = nextHigh;
                _bendAngle = nextAngle;
                foreach (FBXMeshEditSession session in SessionInstances)
                    session.BendSelection(_bendCenter, _bendRotation,
                        _bendAngle, _bendLow, _bendHigh);
            }

            if (_dragging && (evt.rawType == EventType.MouseUp
                || (evt.type == EventType.Repaint && GUIUtility.hotControl == 0)))
            {
                _dragging = false;
            }
        }

        private static bool TryPerformBendGroupUndo()
        {
            if (BendGroupUndoTops.Count < 2) return false;
            foreach (KeyValuePair<FBXMeshEditSession, int> pair in BendGroupUndoTops)
            {
                if (!SessionInstances.Contains(pair.Key) || pair.Key.UndoCount != pair.Value)
                {
                    BendGroupUndoTops.Clear();
                    return false;
                }
            }
            BendGroupRedoTops.Clear();
            foreach (FBXMeshEditSession session in new List<FBXMeshEditSession>(BendGroupUndoTops.Keys))
            {
                if (!session.PerformUndo()) return false;
                BendGroupRedoTops[session] = session.RedoCount;
            }
            BendGroupUndoTops.Clear();
            SceneView.lastActiveSceneView?.ShowNotification(
                new GUIContent("已撤回全部联合编辑目标"), 1.4f);
            return true;
        }

        private static bool TryPerformBendGroupRedo()
        {
            if (BendGroupRedoTops.Count < 2) return false;
            foreach (KeyValuePair<FBXMeshEditSession, int> pair in BendGroupRedoTops)
            {
                if (!SessionInstances.Contains(pair.Key) || pair.Key.RedoCount != pair.Value)
                {
                    BendGroupRedoTops.Clear();
                    return false;
                }
            }
            BendGroupUndoTops.Clear();
            foreach (FBXMeshEditSession session in new List<FBXMeshEditSession>(BendGroupRedoTops.Keys))
            {
                if (!session.PerformRedo()) return false;
                BendGroupUndoTops[session] = session.UndoCount;
            }
            BendGroupRedoTops.Clear();
            SceneView.lastActiveSceneView?.ShowNotification(
                new GUIContent("已恢复全部联合编辑目标"), 1.4f);
            return true;
        }

        private static void CaptureJointUndoStart()
        {
            JointEditStartUndoCounts.Clear();
            JointEditParticipants.Clear();
            foreach (FBXMeshEditSession session in SessionInstances)
                if (session != null && session.IsActive)
                    JointEditStartUndoCounts[session] = session.UndoCount;
        }

        private static void RegisterPendingJointParticipants()
        {
            foreach (FBXMeshEditSession session in SessionInstances)
                if (session != null && session.IsActive && session.HasPendingTransform)
                    JointEditParticipants.Add(session);
        }

        private static void FinalizeJointUndoGroup()
        {
            BendGroupUndoTops.Clear();
            BendGroupRedoTops.Clear();
            foreach (FBXMeshEditSession session in JointEditParticipants)
                if (session != null && session.IsActive && session.UndoCount > 0)
                    BendGroupUndoTops[session] = session.UndoCount;
            JointEditStartUndoCounts.Clear();
            JointEditParticipants.Clear();
            // 只有一个模型实际改变时仍按普通独立历史处理。
            if (BendGroupUndoTops.Count < 2) BendGroupUndoTops.Clear();
            else SceneView.lastActiveSceneView?.ShowNotification(
                new GUIContent($"已记录 {BendGroupUndoTops.Count} 个模型的联合 Undo"), 1.6f);
        }

        private static void DrawBendCurvePreview(Vector3 center, Quaternion rotation,
            float angleDegrees, float lowBound, float highBound)
        {
            const int segmentCount = 32;
            var points = new Vector3[segmentCount + 1];
            float span = Mathf.Max(0.0001f, highBound - lowBound);
            float curvature = angleDegrees * Mathf.Deg2Rad / span;
            for (int i = 0; i <= segmentCount; i++)
            {
                float y = Mathf.Lerp(lowBound, highBound, i / (float)segmentCount);
                Vector3 local;
                if (Mathf.Abs(curvature) < 0.000001f)
                    local = new Vector3(0f, y, 0f);
                else
                {
                    float theta = curvature * y;
                    local = new Vector3((1f - Mathf.Cos(theta)) / curvature,
                        Mathf.Sin(theta) / curvature, 0f);
                }
                points[i] = center + rotation * local;
            }
            Handles.color = new Color(1f, 0.65f, 0.1f, 0.9f);
            Handles.DrawAAPolyLine(3f, points);
            for (int i = 0; i < segmentCount; i += 4)
                Handles.DrawDottedLine(points[i], points[Mathf.Min(i + 4, segmentCount)], 4f);
        }

        private static bool TryGetBendWorldBounds(out Bounds bounds)
        {
            bool initialized = false;
            bounds = default;
            foreach (FBXMeshEditSession session in SessionInstances)
            {
                if (!session.IsActive) continue;
                Vector3[] vertices = session.EditableMesh.vertices;
                foreach (Vector3 vertex in vertices)
                {
                    Vector3 world = session.MeshFilter.transform.TransformPoint(vertex);
                    if (!initialized) { bounds = new Bounds(world, Vector3.zero); initialized = true; }
                    else bounds.Encapsulate(world);
                }
            }
            return initialized;
        }

        private static bool EnsureBendHandleInitialized()
        {
            if (_bendInitialized) return true;
            if (!TryGetBendWorldBounds(out Bounds bounds)) return false;
            _bendCenter = bounds.center;
            _bendRotation = Tools.pivotRotation == PivotRotation.Local
                ? SessionInstance.MeshFilter.transform.rotation : Quaternion.identity;
            Vector3 axis = _bendRotation * Vector3.up;
            GetBendAxisRange(axis, _bendCenter, out _bendLow, out _bendHigh);
            _bendLow = Mathf.Min(0f, _bendLow);
            _bendHigh = Mathf.Max(0f, _bendHigh);
            if (_bendHigh - _bendLow < 0.01f)
            {
                float fallback = Mathf.Max(0.1f, bounds.size.magnitude * 0.5f);
                _bendLow = -fallback;
                _bendHigh = fallback;
            }
            _bendAngle = 0f;
            _bendInitialized = true;
            return true;
        }

        private static void GetBendAxisRange(Vector3 axis, Vector3 center,
            out float low, out float high)
        {
            low = float.PositiveInfinity;
            high = float.NegativeInfinity;
            foreach (FBXMeshEditSession session in SessionInstances)
            {
                if (!session.IsActive) continue;
                Vector3[] vertices = session.EditableMesh.vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    float coordinate = Vector3.Dot(
                        session.MeshFilter.transform.TransformPoint(vertices[i]) - center, axis);
                    low = Mathf.Min(low, coordinate);
                    high = Mathf.Max(high, coordinate);
                }
            }
        }

        private static void DrawMoveHandle(Event evt, bool interactive)
        {
            if (GetTotalSelectedComponentCount() == 0)
                return;

            Vector3 center = GetCombinedSelectionCenterWorld();
            if (!_dragging)
            {
                _lastHandlePosition = center;
                _lastHandleRotation = Tools.pivotRotation == PivotRotation.Local
                    ? SessionInstance.MeshFilter.transform.rotation
                    : Quaternion.identity;
                _lastHandleScale = Vector3.one;
            }

            Quaternion rotation = Tools.pivotRotation == PivotRotation.Local
                ? SessionInstance.MeshFilter.transform.rotation
                : Quaternion.identity;
            EditorGUI.BeginChangeCheck();
            Vector3 nextPosition = center;
            Quaternion nextRotation = _lastHandleRotation;
            Vector3 nextScale = _lastHandleScale;
            Tool activeTool = Tools.current;
            if (activeTool == Tool.Rotate)
            {
                nextRotation = Handles.RotationHandle(_lastHandleRotation, center);
                nextRotation = HandleFreeRotationArea(nextRotation, center, interactive);
            }
            else if (activeTool == Tool.Scale)
                nextScale = Handles.ScaleHandle(_lastHandleScale, center, rotation,
                    HandleUtility.GetHandleSize(center));
            else
                nextPosition = Handles.PositionHandle(center, rotation);
            if (EditorGUI.EndChangeCheck())
            {
                if (!interactive) return;
                if (!_dragging)
                {
                    CaptureJointUndoStart();
                    List<Vector3> worldSeeds = CollectSelectionWorldSeeds();
                    int collectiveRevision = GetCollectiveSelectionRevision();
                    foreach (FBXMeshEditSession session in SessionInstances)
                        session.BeginMove(_softSelectionEnabled, _softSelectionRadius,
                            worldSeeds, collectiveRevision);
                    RegisterPendingJointParticipants();
                }
                _dragging = true;
                if (activeTool == Tool.Rotate)
                {
                    Quaternion delta = nextRotation * Quaternion.Inverse(_lastHandleRotation);
                    foreach (FBXMeshEditSession session in SessionInstances)
                        session.RotateSelection(center, delta);
                    _lastHandleRotation = nextRotation;
                }
                else if (activeTool == Tool.Scale)
                {
                    Vector3 ratio = new Vector3(
                        SafeScaleRatio(nextScale.x, _lastHandleScale.x),
                        SafeScaleRatio(nextScale.y, _lastHandleScale.y),
                        SafeScaleRatio(nextScale.z, _lastHandleScale.z));
                    foreach (FBXMeshEditSession session in SessionInstances)
                        session.ScaleSelection(center, rotation, ratio);
                    _lastHandleScale = nextScale;
                }
                else
                {
                    foreach (FBXMeshEditSession session in SessionInstances)
                        session.MoveSelection(nextPosition - _lastHandlePosition);
                    _lastHandlePosition = nextPosition;
                }
            }

            if (_dragging && (evt.rawType == EventType.MouseUp
                || (evt.type == EventType.Repaint && GUIUtility.hotControl == 0)))
            {
                _dragging = false;
                foreach (FBXMeshEditSession session in SessionInstances)
                    session.EndMove();
                FinalizeJointUndoGroup();
            }
        }

        private static int GetTotalSelectedComponentCount()
        {
            int count = 0;
            foreach (FBXMeshEditSession session in SessionInstances)
                count += session.SelectedComponentCount;
            return count;
        }

        private static List<Vector3> CollectSelectionWorldSeeds()
        {
            var result = new List<Vector3>();
            foreach (FBXMeshEditSession session in SessionInstances)
            {
                if (!session.IsActive || session.EditableMesh == null || session.MeshFilter == null)
                    continue;
                Vector3[] vertices = session.EditableMesh.vertices;
                Transform transform = session.MeshFilter.transform;
                foreach (int index in session.GetAffectedVertices())
                    if (index >= 0 && index < vertices.Length)
                        result.Add(transform.TransformPoint(vertices[index]));
            }
            return result;
        }

        private static int GetCollectiveSelectionRevision()
        {
            unchecked
            {
                int hash = 17;
                foreach (FBXMeshEditSession session in SessionInstances)
                {
                    hash = hash * 31 + session.SelectionRevision;
                    hash = hash * 31 + (session.MeshFilter != null
                        ? session.MeshFilter.GetInstanceID() : 0);
                }
                return hash;
            }
        }

        private static Vector3 GetCombinedSelectionCenterWorld()
        {
            Vector3 sum = Vector3.zero;
            int total = 0;
            foreach (FBXMeshEditSession session in SessionInstances)
            {
                int count = session.SelectedComponentCount;
                if (count <= 0) continue;
                sum += session.GetSelectionCenterWorld() * count;
                total += count;
            }
            return total > 0 ? sum / total : Vector3.zero;
        }

        private static float SafeScaleRatio(float current, float previous)
        {
            return Mathf.Abs(previous) > 0.00001f ? current / previous : 1f;
        }

        private static Quaternion HandleFreeRotationArea(
            Quaternion rotation, Vector3 center, bool interactive)
        {
            Event evt = Event.current;
            int controlId = GUIUtility.GetControlID(FreeRotateControlHint, FocusType.Passive);
            SceneView sceneView = SceneView.currentDrawingSceneView;
            Camera camera = sceneView != null ? sceneView.camera : null;
            if (camera == null) return rotation;

            GetRotationHandleGuiCircle(center, camera, out Vector2 guiCenter, out float guiRadius);
            bool inside = Vector2.Distance(evt.mousePosition, guiCenter) <= guiRadius;

            if (evt.type == EventType.Layout && interactive && inside)
            {
                // 给轴环更高优先级；内部空白区域才会选择这个控制器。
                HandleUtility.AddControl(controlId, 10f);
            }
            else if (evt.type == EventType.MouseDown && evt.button == 0
                && interactive && inside
                && (HandleUtility.nearestControl == controlId || GUIUtility.hotControl == 0))
            {
                GUIUtility.hotControl = controlId;
                _freeRotateStartMouse = evt.mousePosition;
                _freeRotateStartRotation = rotation;
                _freeRotateStartVector = MapToArcball(
                    _freeRotateStartMouse, guiCenter, guiRadius);
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && GUIUtility.hotControl == controlId)
            {
                Vector3 currentVector = MapToArcball(evt.mousePosition, guiCenter, guiRadius);
                Quaternion cameraDelta = Quaternion.FromToRotation(
                    currentVector, _freeRotateStartVector);
                Quaternion worldDelta = camera.transform.rotation * cameraDelta
                    * Quaternion.Inverse(camera.transform.rotation);
                rotation = worldDelta * _freeRotateStartRotation;
                GUI.changed = true;
                evt.Use();
            }
            else if (evt.rawType == EventType.MouseUp && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                evt.Use();
            }
            return rotation;
        }

        private static bool IsInsideRotationHandle(Vector2 mousePosition)
        {
            SceneView sceneView = SceneView.currentDrawingSceneView;
            Camera camera = sceneView != null ? sceneView.camera : null;
            if (camera == null) return false;
            Vector3 center = GetCombinedSelectionCenterWorld();
            GetRotationHandleGuiCircle(center, camera, out Vector2 guiCenter, out float guiRadius);
            return Vector2.Distance(mousePosition, guiCenter) <= guiRadius;
        }

        private static void GetRotationHandleGuiCircle(
            Vector3 center, Camera camera, out Vector2 guiCenter, out float guiRadius)
        {
            guiCenter = HandleUtility.WorldToGUIPoint(center);
            float worldRadius = HandleUtility.GetHandleSize(center) * 0.72f;
            Vector2 guiEdge = HandleUtility.WorldToGUIPoint(
                center + camera.transform.right * worldRadius);
            guiRadius = Mathf.Max(18f, Vector2.Distance(guiCenter, guiEdge));
        }

        private static Vector3 MapToArcball(Vector2 mouse, Vector2 center, float radius)
        {
            float x = (mouse.x - center.x) / radius;
            float y = (center.y - mouse.y) / radius;
            float lengthSquared = x * x + y * y;
            if (lengthSquared <= 1f)
                return new Vector3(x, y, Mathf.Sqrt(1f - lengthSquared)).normalized;
            float inverseLength = 1f / Mathf.Sqrt(lengthSquared);
            return new Vector3(x * inverseLength, y * inverseLength, 0f);
        }

        private static void DrawSceneOverlay()
        {
            Handles.BeginGUI();
            int buttonCount = SessionInstance.ComponentMode == FBXMeshComponentMode.Vertex ? 10 : 9;
            _sceneOverlayRect.width = 19f + buttonCount * 32f;
            _sceneOverlayRect.height = 29f;
            _sceneOverlayRect = GUI.Window(SceneOverlayWindowId, _sceneOverlayRect,
                DrawCompactSceneOverlay, GUIContent.none, GUIStyle.none);

            SceneView sceneView = SceneView.currentDrawingSceneView;
            if (sceneView != null)
            {
                _sceneOverlayRect.x = Mathf.Clamp(_sceneOverlayRect.x, 0f,
                    Mathf.Max(0f, sceneView.position.width - _sceneOverlayRect.width));
                _sceneOverlayRect.y = Mathf.Clamp(_sceneOverlayRect.y, 0f,
                    Mathf.Max(0f, sceneView.position.height - _sceneOverlayRect.height));
            }
            Handles.EndGUI();
        }

        private static void DrawCompactSceneOverlay(int windowId)
        {
            DrawHorizontalSceneOverlay(windowId);
        }

        private static void DrawHorizontalSceneOverlay(int windowId)
        {
            Rect panelRect = new Rect(0f, 0f, _sceneOverlayRect.width, _sceneOverlayRect.height);
            EnsureOverlayStyles();
            GUI.Box(panelRect, GUIContent.none, _overlayPanelStyle);

            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(1, 1, 1, 1)
            };
            const float y = 4f;
            const float width = 30f;
            const float height = 21f;
            const float step = 32f;
            float x = 17f;
            bool previousEnabled = GUI.enabled;
            GUI.enabled = SessionInstance.IsActive;

            DrawComponentModeButton(new Rect(x, y, width, height), style,
                FBXMeshComponentMode.Vertex, "V", "切换到顶点模式");
            x += step;
            DrawComponentModeButton(new Rect(x, y, width, height), style,
                FBXMeshComponentMode.Edge, "E", "切换到边模式");
            x += step;
            DrawComponentModeButton(new Rect(x, y, width, height), style,
                FBXMeshComponentMode.Face, "F", "切换到面模式");
            x += step;

            string toolIcon = Tools.current == Tool.Rotate ? "RotateTool"
                : Tools.current == Tool.Scale ? "ScaleTool" : "MoveTool";
            if (DrawOverlayActionIcon(new Rect(x, y, width, height), style,
                toolIcon, true, "T", "W 移动，E 旋转，R 缩放；点击切换"))
            {
                Tools.current = Tools.current == Tool.Move ? Tool.Rotate
                    : Tools.current == Tool.Rotate ? Tool.Scale : Tool.Move;
                ShowToolbarNotification(Tools.current == Tool.Rotate ? "旋转工具"
                    : Tools.current == Tool.Scale ? "缩放工具" : "移动工具");
                SceneView.RepaintAll();
            }
            x += step;

            if (DrawOverlayActionIcon(new Rect(x, y, width, height), style,
                "d_SceneViewFx", _softSelectionEnabled, "B", "点击切换软选择；按住 B 拖动调整半径"))
            {
                SoftSelectionEnabled = !SoftSelectionEnabled;
                ShowToolbarNotification(SoftSelectionEnabled ? "软选择：开启" : "软选择：关闭");
            }
            x += step;

            if (DrawOverlayActionIcon(new Rect(x, y, width, height), style,
                _lockEditTarget ? "LockIcon-On" : "LockIcon", _lockEditTarget, "L",
                "点击锁定或解锁编辑目标；快捷键 Ctrl+L"))
            {
                LockEditTarget = !LockEditTarget;
                ShowToolbarNotification(LockEditTarget ? "编辑目标：已锁定" : "编辑目标：已解锁");
            }
            x += step;

            if (SessionInstance.ComponentMode == FBXMeshComponentMode.Vertex)
            {
                if (DrawOverlayActionText(new Rect(x, y, width, height), style,
                    "M", "合并所选顶点到中心；快捷键 M"))
                    MergeSelectedVerticesToCenter();
                x += step;
                if (DrawOverlayActionText(new Rect(x, y, width, height), style,
                    "⌃M", "按距离合并所选顶点；快捷键 Ctrl+M"))
                    MergeSelectedVerticesByDistance();
                x += step;
                if (DrawOverlayActionText(new Rect(x, y, width, height), style,
                    "Del", "溶解所选顶点；快捷键 Delete"))
                    DissolveSelectedVertices();
                x += step;
            }
            else if (SessionInstance.ComponentMode == FBXMeshComponentMode.Edge)
            {
                if (DrawOverlayActionIcon(new Rect(x, y, width, height), style,
                    "d_Grid.BoxTool", _loopPreviewEnabled, "C", "点击切换循环边工具；快捷键 C"))
                {
                    LoopPreviewEnabled = !LoopPreviewEnabled;
                    ShowToolbarNotification(LoopPreviewEnabled ? "循环边工具：开启" : "循环边工具：关闭");
                }
                x += step;
                if (DrawOverlayActionText(new Rect(x, y, width, height), style,
                    "Del", "溶解所选边；快捷键 Delete"))
                    DissolveSelectedEdges();
                x += step;
            }
            else if (SessionInstance.ComponentMode == FBXMeshComponentMode.Face)
            {
                if (DrawOverlayActionText(new Rect(x, y, width, height), style,
                    "D", "细分所选面；未选面时细分整个 Mesh"))
                {
                    if (SessionInstance.SelectedFaces.Count > 0) SubdivideSelectedFaces();
                    else SubdivideWholeMesh();
                }
                x += step;
                if (DrawOverlayActionText(new Rect(x, y, width, height), style,
                    "⇧D", "平滑细分整个 Mesh；需要清空面选择"))
                {
                    if (SessionInstance.SelectedFaces.Count == 0) SmoothSubdivideWholeMesh();
                    else SceneView.lastActiveSceneView?.ShowNotification(
                        new GUIContent("请先清空面选择，再执行平滑细分"), 1.8f);
                }
                x += step;
            }

            if (SessionInstance.IsActive)
            {
            Rect exitRect = new Rect(x, y, width, height);
            DrawOverlayCell(exitRect, false);
            GUI.Label(exitRect, GetOverlayIcon("d_winbtn_win_close", "X", "退出编辑模式"), style);
            if (GUI.Button(exitRect, new GUIContent(string.Empty, "退出编辑模式"), GUIStyle.none))
                ManualExit();
            }

            // 未进入编辑模式时仅禁用功能按钮，拖动区域始终保持可用。
            GUI.enabled = previousEnabled;
            if (!SessionInstance.IsActive)
            {
                bool canEnterEditMode = false;
                foreach (GameObject selectedObject in Selection.gameObjects)
                {
                    if (selectedObject == null) continue;
                    MeshFilter selectedFilter = selectedObject.GetComponent<MeshFilter>();
                    if (selectedFilter == null || selectedFilter.sharedMesh == null
                        || selectedObject.GetComponent<MeshRenderer>() == null) continue;
                    canEnterEditMode = true;
                    break;
                }

                Rect enterRect = new Rect(x, y, width, height);
                GUI.enabled = previousEnabled && canEnterEditMode;
                DrawOverlayCell(enterRect, false);
                GUI.Label(enterRect, GetOverlayIcon("d_editicon.sml", "↗", "进入编辑模式"), style);
                if (GUI.Button(enterRect,
                    new GUIContent(string.Empty, "进入编辑模式"), GUIStyle.none))
                {
                    if (EnterSelection(out string error))
                        ShowToolbarNotification($"已进入编辑模式：{SessionInstances.Count} 个模型");
                    else
                        ShowToolbarNotification(error);
                }
                GUI.enabled = previousEnabled;

                Rect disabledButtonsRect = new Rect(17f, y,
                    Mathf.Max(0f, x - 17f), height);
                if (GUI.Button(disabledButtonsRect,
                    new GUIContent(string.Empty, "请选择模型并进入编辑模式"), GUIStyle.none))
                    ShowToolbarNotification("请选择模型并进入编辑模式");
                if (!canEnterEditMode && GUI.Button(enterRect,
                    new GUIContent(string.Empty, "请选择模型并进入编辑模式"), GUIStyle.none))
                    ShowToolbarNotification("请选择模型并进入编辑模式");
            }
            Rect gripRect = new Rect(2f, 2f, 13f, 25f);
            bool gripHover = gripRect.Contains(Event.current.mousePosition);
            Color gripColor = gripHover
                ? new Color(0.92f, 0.92f, 0.92f, 0.95f)
                : new Color(0.48f, 0.50f, 0.50f, 0.65f);
            EditorGUI.DrawRect(new Rect(6f, 7f, 1f, 15f), gripColor);
            EditorGUI.DrawRect(new Rect(9f, 7f, 1f, 15f), gripColor);
            GUI.Label(gripRect, new GUIContent(string.Empty, "拖动工具栏"));
            GUI.DragWindow(new Rect(0f, 0f, 16f, _sceneOverlayRect.height));
        }

        private static void DrawInteractiveCompactSceneOverlay(int windowId)
        {
            Rect panelRect = new Rect(0f, 0f, _sceneOverlayRect.width, _sceneOverlayRect.height);
            EnsureOverlayStyles();
            GUI.Box(panelRect, GUIContent.none, _overlayPanelStyle);

            var buttonStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(1, 1, 1, 1)
            };
            const float x = 4f;
            const float width = 28f;
            const float buttonHeight = 21f;
            const float step = 21f;
            float y = 15f;

            int cellCount = SessionInstance.ComponentMode == FBXMeshComponentMode.Vertex ? 7 : 9;
            EditorGUI.DrawRect(new Rect(3f, 14f, 30f, cellCount * step + 1f),
                new Color(0.035f, 0.04f, 0.045f, 1f));

            DrawComponentModeButton(new Rect(x, y, width, buttonHeight), buttonStyle,
                FBXMeshComponentMode.Vertex, "V", "切换到顶点模式");
            y += step;
            DrawComponentModeButton(new Rect(x, y, width, buttonHeight), buttonStyle,
                FBXMeshComponentMode.Edge, "E", "切换到边模式");
            y += step;
            DrawComponentModeButton(new Rect(x, y, width, buttonHeight), buttonStyle,
                FBXMeshComponentMode.Face, "F", "切换到面模式");
            y += step;

            string toolIcon = Tools.current == Tool.Rotate ? "RotateTool"
                : Tools.current == Tool.Scale ? "ScaleTool" : "MoveTool";
            if (DrawOverlayActionIcon(new Rect(x, y, width, buttonHeight), buttonStyle,
                toolIcon, true, "T", "W 移动，E 旋转，R 缩放；点击切换"))
            {
                Tools.current = Tools.current == Tool.Move ? Tool.Rotate
                    : Tools.current == Tool.Rotate ? Tool.Scale : Tool.Move;
                SceneView.RepaintAll();
            }
            y += step;

            if (DrawOverlayActionIcon(new Rect(x, y, width, buttonHeight), buttonStyle,
                "d_SceneViewFx", _softSelectionEnabled, "B", "点击切换软选择；按住 B 拖动调整半径"))
                SoftSelectionEnabled = !SoftSelectionEnabled;
            y += step;

            if (DrawOverlayActionIcon(new Rect(x, y, width, buttonHeight), buttonStyle,
                _lockEditTarget ? "LockIcon-On" : "LockIcon", _lockEditTarget, "L",
                "点击锁定或解锁编辑目标；快捷键 Ctrl+L"))
                LockEditTarget = !LockEditTarget;
            y += step;

            if (SessionInstance.ComponentMode == FBXMeshComponentMode.Edge)
            {
                if (DrawOverlayActionIcon(new Rect(x, y, width, buttonHeight), buttonStyle,
                    "d_Grid.BoxTool", _loopPreviewEnabled, "C", "点击切换循环边工具；快捷键 C"))
                    LoopPreviewEnabled = !LoopPreviewEnabled;
                y += step;
                if (DrawOverlayActionText(new Rect(x, y, width, buttonHeight), buttonStyle,
                    "Del", "溶解所选边；快捷键 Delete"))
                    DissolveSelectedEdges();
                y += step;
            }
            else if (SessionInstance.ComponentMode == FBXMeshComponentMode.Face)
            {
                if (DrawOverlayActionText(new Rect(x, y, width, buttonHeight), buttonStyle,
                    "D", "细分所选面；未选面时细分整个 Mesh"))
                {
                    if (SessionInstance.SelectedFaces.Count > 0) SubdivideSelectedFaces();
                    else SubdivideWholeMesh();
                }
                y += step;
                if (DrawOverlayActionText(new Rect(x, y, width, buttonHeight), buttonStyle,
                    "⇧D", "平滑细分整个 Mesh；需要清空面选择"))
                {
                    if (SessionInstance.SelectedFaces.Count == 0)
                        SmoothSubdivideWholeMesh();
                    else
                        SceneView.lastActiveSceneView?.ShowNotification(
                            new GUIContent("请先清空面选择，再执行平滑细分"), 1.8f);
                }
                y += step;
            }

            Rect exitRect = new Rect(x, y, width, buttonHeight);
            DrawOverlayCell(exitRect, false);
            GUI.Label(exitRect, GetOverlayIcon("d_winbtn_win_close", "X", "退出编辑模式"), buttonStyle);
            if (GUI.Button(exitRect, new GUIContent(string.Empty, "退出编辑模式"), GUIStyle.none))
                ManualExit();

            Rect gripRect = new Rect(5f, 3f, 28f, 10f);
            EditorGUI.DrawRect(new Rect(11f, 6f, 16f, 1f), new Color(0.65f, 0.65f, 0.65f, 0.8f));
            EditorGUI.DrawRect(new Rect(11f, 9f, 16f, 1f), new Color(0.65f, 0.65f, 0.65f, 0.8f));
            GUI.Label(gripRect, new GUIContent(string.Empty, "拖动工具栏"));
            GUI.DragWindow(new Rect(0f, 0f, _sceneOverlayRect.width, 15f));
        }

        // 旧版绘制代码暂时保留，便于对照样式；当前入口使用上面的交互版本。
        private static void DrawLegacyCompactSceneOverlay(int windowId)
        {
            Rect panelRect = new Rect(0f, 0f, _sceneOverlayRect.width, _sceneOverlayRect.height);
            EnsureOverlayStyles();
            GUI.Box(panelRect, GUIContent.none, _overlayPanelStyle);

            var buttonStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(1, 1, 1, 1)
            };
            const float x = 4f;
            const float width = 30f;
            const float buttonHeight = 21f;
            const float step = 22f;
            float y = 15f;

            int cellCount = SessionInstance.ComponentMode == FBXMeshComponentMode.Vertex ? 7 : 9;
            EditorGUI.DrawRect(new Rect(3f, 14f, 32f, cellCount * step + 1f),
                new Color(0.035f, 0.04f, 0.045f, 1f));

            DrawComponentModeButton(new Rect(x, y, width, buttonHeight), buttonStyle,
                FBXMeshComponentMode.Vertex, "V", "切换到顶点模式");
            y += step;
            DrawComponentModeButton(new Rect(x, y, width, buttonHeight), buttonStyle,
                FBXMeshComponentMode.Edge, "E", "切换到边模式");
            y += step;
            DrawComponentModeButton(new Rect(x, y, width, buttonHeight), buttonStyle,
                FBXMeshComponentMode.Face, "F", "切换到面模式");
            y += step + 2f;

            string toolIcon = Tools.current == Tool.Rotate ? "RotateTool"
                : Tools.current == Tool.Scale ? "ScaleTool" : "MoveTool";
            DrawOverlayStatusIcon(new Rect(x, y, width, buttonHeight), buttonStyle,
                toolIcon, true, "T", "W 移动，E 旋转，R 缩放");
            y += step;
            DrawOverlayStatusIcon(new Rect(x, y, width, buttonHeight), buttonStyle,
                "d_SceneViewFx", _softSelectionEnabled, "B",
                _softSelectionEnabled
                    ? "软选择已开启。按 B 关闭；按住 B 并左右拖动调整半径"
                    : "软选择已关闭。按 B 开启；按住 B 并左右拖动调整半径");
            y += step;
            DrawOverlayStatusIcon(new Rect(x, y, width, buttonHeight), buttonStyle,
                _lockEditTarget ? "LockIcon-On" : "LockIcon",
                _lockEditTarget, "L", _lockEditTarget
                    ? "编辑目标已锁定。按 Ctrl+L 解锁"
                    : "编辑目标未锁定。按 Ctrl+L 锁定");
            y += step;

            if (SessionInstance.ComponentMode == FBXMeshComponentMode.Edge)
            {
                DrawOverlayStatusIcon(new Rect(x, y, width, buttonHeight), buttonStyle,
                    "d_Grid.BoxTool", _loopPreviewEnabled, "C",
                    _loopPreviewEnabled ? "循环边工具已开启。按 C 关闭" : "按 C 开启循环边工具");
                y += step;
                DrawOverlayTextStatus(new Rect(x, y, width, buttonHeight), buttonStyle,
                    "Del", "按 Delete 溶解选中的边");
                y += step;
            }
            else if (SessionInstance.ComponentMode == FBXMeshComponentMode.Face)
            {
                DrawOverlayTextStatus(new Rect(x, y, width, buttonHeight), buttonStyle,
                    "D", "按 D 细分选中面；未选面时细分整个 Mesh");
                y += step;
                DrawOverlayTextStatus(new Rect(x, y, width, buttonHeight), buttonStyle,
                    "⇧D", "按 Shift+D 平滑细分整个 Mesh；选中面时只显示提醒");
                y += step;
            }

            Rect exitRect = new Rect(x, y, width, buttonHeight);
            DrawOverlayCell(exitRect, false);
            GUIContent exitContent = GetOverlayIcon("d_winbtn_win_close", "X", "退出编辑模式");
            GUI.Label(exitRect, exitContent, buttonStyle);
            if (GUI.Button(exitRect, new GUIContent(string.Empty, "退出编辑模式"), GUIStyle.none))
                ManualExit();

            Rect gripRect = new Rect(5f, 3f, 28f, 10f);
            EditorGUI.DrawRect(new Rect(11f, 6f, 16f, 1f), new Color(0.65f, 0.65f, 0.65f, 0.8f));
            EditorGUI.DrawRect(new Rect(11f, 9f, 16f, 1f), new Color(0.65f, 0.65f, 0.65f, 0.8f));
            GUI.Label(gripRect, new GUIContent(string.Empty, "拖动工具条"));
            GUI.DragWindow(new Rect(0f, 0f, _sceneOverlayRect.width, 15f));
        }

        private static void DrawComponentModeButton(Rect rect, GUIStyle style,
            FBXMeshComponentMode mode, string text, string tooltip)
        {
            bool active = SessionInstance.ComponentMode == mode;
            DrawOverlayCell(rect, active);
            GUI.Label(rect, new GUIContent(text, tooltip), style);
            if (GUI.Button(rect, new GUIContent(string.Empty, tooltip), GUIStyle.none)
                && SessionInstance.ComponentMode != mode)
            {
                SetComponentMode(mode);
                ShowToolbarNotification(mode == FBXMeshComponentMode.Vertex ? "顶点模式"
                    : mode == FBXMeshComponentMode.Edge ? "边模式" : "面模式");
            }
        }

        private static void ShowToolbarNotification(string message)
        {
            SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(message), 1.2f);
        }

        private static bool DrawOverlayActionIcon(Rect rect, GUIStyle style, string iconName,
            bool active, string fallback, string tooltip)
        {
            DrawOverlayCell(rect, active);
            Color previous = GUI.color;
            GUI.color = active ? Color.white : new Color(1f, 1f, 1f, 0.72f);
            GUI.Label(rect, GetOverlayIcon(iconName, fallback, tooltip), style);
            GUI.color = previous;
            return GUI.Button(rect, new GUIContent(string.Empty, tooltip), GUIStyle.none);
        }

        private static bool DrawOverlayActionText(Rect rect, GUIStyle style,
            string text, string tooltip)
        {
            DrawOverlayCell(rect, false);
            GUI.Label(rect, new GUIContent(text, tooltip), style);
            return GUI.Button(rect, new GUIContent(string.Empty, tooltip), GUIStyle.none);
        }

        private static void DrawOverlayStatusIcon(Rect rect, GUIStyle style, string iconName,
            bool active, string fallback, string tooltip)
        {
            DrawOverlayCell(rect, active);
            Color previous = GUI.color;
            GUI.color = active ? Color.white : new Color(1f, 1f, 1f, 0.45f);
            GUI.Label(rect, GetOverlayIcon(iconName, fallback, tooltip), style);
            GUI.color = previous;
            GUI.Label(rect, new GUIContent(string.Empty, tooltip));
        }

        private static void DrawOverlayTextStatus(Rect rect, GUIStyle style,
            string text, string tooltip)
        {
            DrawOverlayCell(rect, false);
            GUI.Label(rect, new GUIContent(text, tooltip), style);
        }

        private static void DrawOverlayCell(Rect rect, bool active)
        {
            active = active && SessionInstance.IsActive;
            bool hover = rect.Contains(Event.current.mousePosition);
            GUI.Box(rect, GUIContent.none, active
                ? _overlayCellActiveStyle
                : hover ? _overlayCellHoverStyle : _overlayCellStyle);
        }

        private static void EnsureOverlayStyles()
        {
            if (_overlayPanelStyle != null)
                return;

            _overlayPanelStyle = CreateRoundedStyle(
                new Color32(0, 0, 0, 190), new Color32(0, 0, 0, 220), 1, 1);
            _overlayCellActiveStyle = CreateRoundedStyle(
                new Color32(45, 91, 132, 255), new Color32(24, 43, 60, 255), 1, 1);
            _overlayCellStyle = CreateRoundedStyle(
                new Color32(91, 94, 95, 255), new Color32(24, 27, 28, 255), 1, 1);
            _overlayCellHoverStyle = CreateRoundedStyle(
                new Color32(112, 115, 116, 255), new Color32(29, 32, 33, 255), 1, 1);

            _overlayTopCellStyle = CreateRoundedStyle(
                new Color32(91, 94, 95, 255), new Color32(24, 27, 28, 255), 2, 1, true, false);
            _overlayTopCellHoverStyle = CreateRoundedStyle(
                new Color32(112, 115, 116, 255), new Color32(29, 32, 33, 255), 2, 1, true, false);
            _overlayTopCellActiveStyle = CreateRoundedStyle(
                new Color32(45, 91, 132, 255), new Color32(24, 43, 60, 255), 2, 1, true, false);
            _overlayBottomCellStyle = CreateRoundedStyle(
                new Color32(91, 94, 95, 255), new Color32(24, 27, 28, 255), 2, 1, false, true);
            _overlayBottomCellHoverStyle = CreateRoundedStyle(
                new Color32(112, 115, 116, 255), new Color32(29, 32, 33, 255), 2, 1, false, true);
        }

        // 生成九宫格圆角底图，缩放面板时圆角和描边不会变形。
        private static GUIStyle CreateRoundedStyle(Color32 fill, Color32 border, int radius,
            int borderWidth, bool roundTop = true, bool roundBottom = true)
        {
            int size = Mathf.Max(3, radius * 2 + 3);
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "FBX Mesh Editor Overlay Style",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            float center = (size - 1) * 0.5f;
            float outerRadius = radius + 0.5f;
            float innerRadius = Mathf.Max(0f, outerRadius - borderWidth);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool roundedRow = y <= center ? roundTop : roundBottom;
                    float dx = roundedRow
                        ? Mathf.Max(0f, Mathf.Abs(x - center) - (center - outerRadius)) : 0f;
                    float dy = roundedRow
                        ? Mathf.Max(0f, Mathf.Abs(y - center) - (center - outerRadius)) : 0f;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    Color32 color = distance > outerRadius
                        ? new Color32(0, 0, 0, 0)
                        : distance > innerRadius ? border : fill;
                    texture.SetPixel(x, y, color);
                }
            }
            texture.Apply(false, true);

            return new GUIStyle
            {
                normal = { background = texture },
                border = new RectOffset(radius + 1, radius + 1, radius + 1, radius + 1)
            };
        }

        private static GUIContent GetOverlayIcon(string iconName, string fallback, string tooltip)
        {
            GUIContent builtIn = EditorGUIUtility.IconContent(iconName);
            return builtIn != null && builtIn.image != null
                ? new GUIContent(builtIn.image, tooltip)
                : new GUIContent(fallback, tooltip);
        }
    }
}

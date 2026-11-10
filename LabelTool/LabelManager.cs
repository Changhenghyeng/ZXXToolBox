using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using TMPro;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace LabelTool
{
    /// <summary>
    /// 场景物体名称标签系统。
    /// 使用 World Space Canvas + TextMeshPro 在 Scene 视图中渲染物体名称。
    /// Edit Mode 可预览，Play Mode 正常运行。
    /// </summary>
    [ExecuteAlways]
    public class LabelManager : MonoBehaviour
    {
        // ── 配置字段 ──────────────────────────────

        /// <summary>标签的视觉样式配置。</summary>
        public LabelStyle style = new LabelStyle();

        [Header("标签列表")]
        public List<Entry> entries = new List<Entry>();

        [Header("扫描")]
        public List<GameObject> scanRoots = new List<GameObject>();
        public bool onlyDirectChildren;

        [Header("显示名")]
        public bool usePrefabName = true;
        public List<Alias> aliases = new List<Alias>();

        [Header("定位")]
        public bool useBounds = true;
        public float heightMargin = 0.2f;
        public Vector3 worldOffset = new Vector3(0, 0, 0);

        [Header("LOD")]
        public float maxDistance = 60f;
        public int recomputeInterval = 4;
        public float viewportMargin = 0.05f;

        /// <summary>标签条目：目标物体 + 是否显示。</summary>
        [Serializable]
        public class Entry
        {
            public GameObject target;
            public bool show = true;
        }

        /// <summary>Prefab 别名映射：将 Prefab 资产映射为自定义显示名称。</summary>
        [Serializable]
        public class Alias
        {
            public GameObject prefab;
            public string alias;
        }

        // ── 内部状态 ──────────────────────────────

        private Canvas _canvas;
        private Sprite _bgSprite;
        private Material _textMat;
        [System.NonSerialized] private int _styleFingerprint;
        private Camera _mainCam;
        private Camera _labelCam;
        private int _labelLayer;
        private int _frameCount;

        private readonly List<Widget> _widgets = new List<Widget>();
        private readonly Stack<Widget> _pool = new Stack<Widget>();
        private readonly Dictionary<GameObject, Widget> _active = new Dictionary<GameObject, Widget>();

        // ── 生命周期 ──────────────────────────────

        private void OnEnable()
        {
            _labelLayer = GetOrCreateLabelLayer();
            BuildCanvas();
            Refresh();
        }

        private void OnDisable()
        {
            if (_mainCam && _labelCam)
            {
                var urpData = _mainCam.GetUniversalAdditionalCameraData();
                if (urpData) urpData.cameraStack.Remove(_labelCam);
            }
            if (_labelCam) DestroySafely(_labelCam.gameObject);
            _labelCam = null;
            foreach (var w in _widgets)
                if (w?.go) w.go.SetActive(false);
        }

        private void LateUpdate()
        {
            if (_canvas == null) return;

            // 找主相机
            if (_mainCam == null || !_mainCam.isActiveAndEnabled)
                _mainCam = FindMainCamera();
            if (_mainCam == null) return;

            // URP Camera Stacking：Overlay 相机叠在主相机上，只渲染 Label 层
            if (_labelCam == null)
            {
                // 清理之前可能残留的旧 Label Camera（domain reload 导致引用丢失时旧 GO 未销毁）
                // 同时从 URP Camera Stack 中清理残留的 null 或 Label 相机引用
                var mainUrp = _mainCam.GetUniversalAdditionalCameraData();
                if (mainUrp != null)
                {
                    for (int i = mainUrp.cameraStack.Count - 1; i >= 0; i--)
                    {
                        var c = mainUrp.cameraStack[i];
                        if (c == null || c.name == "[Label Camera]")
                            mainUrp.cameraStack.RemoveAt(i);
                    }
                }
                for (int i = _mainCam.transform.childCount - 1; i >= 0; i--)
                {
                    var child = _mainCam.transform.GetChild(i);
                    if (child.name == "[Label Camera]")
                        DestroySafely(child.gameObject);
                }

                var go = new GameObject("[Label Camera]");
                go.transform.SetParent(_mainCam.transform, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                _labelCam = go.AddComponent<Camera>();
                _labelCam.cullingMask = 1 << _labelLayer;
                _labelCam.depth = _mainCam.depth + 1;
                _labelCam.allowHDR = false;
                _labelCam.allowMSAA = false;
                // URP Overlay 类型
                var urpData = _labelCam.GetUniversalAdditionalCameraData();
                urpData.renderType = CameraRenderType.Overlay;
                // 注册到主相机 Stack
                var mainUrpData = _mainCam.GetUniversalAdditionalCameraData();
                if (!mainUrpData.cameraStack.Contains(_labelCam))
                    mainUrpData.cameraStack.Add(_labelCam);
            }
            _labelCam.orthographic = _mainCam.orthographic;
            _labelCam.orthographicSize = _mainCam.orthographicSize;
            _labelCam.fieldOfView = _mainCam.fieldOfView;
            _labelCam.nearClipPlane = _mainCam.nearClipPlane;
            _labelCam.farClipPlane = _mainCam.farClipPlane;

            // 样式变更检测
            int fp = style.GetFingerprint();
            if (fp != _styleFingerprint)
            {
                RebuildStyle();
                _styleFingerprint = fp;
            }

            // 可见性检测节流
            bool checkVis = (_frameCount++ % Mathf.Max(1, recomputeInterval)) == 0;

            for (int i = _widgets.Count - 1; i >= 0; i--)
            {
                var w = _widgets[i];
                if (w == null || w.go == null) { _widgets.RemoveAt(i); continue; }

                UpdateWidget(w, checkVis);
            }
        }

        private void OnDestroy()
        {
            foreach (var w in _widgets) DestroyWidget(w);
            _widgets.Clear();
            _pool.Clear();
            _active.Clear();

            if (_bgSprite != null)
            {
                if (_bgSprite.texture) DestroySafely(_bgSprite.texture);
                DestroySafely(_bgSprite);
            }
            if (_textMat != null) DestroySafely(_textMat);
            if (_labelCam) DestroySafely(_labelCam.gameObject);
            if (_canvas != null) DestroySafely(_canvas.gameObject);
        }

        // ── 公开 API ──────────────────────────────

        /// <summary>
        /// 扫描根节点（或整个场景）下的所有 GameObject，添加到标签条目列表。
        /// 已存在的条目不会重复添加。
        /// </summary>
        public void Scan()
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            var seen = new HashSet<GameObject>();
            foreach (var e in entries)
                if (e.target) seen.Add(e.target);

            if (scanRoots.Count > 0)
            {
                foreach (var r in scanRoots)
                    if (r) ScanRecursive(r.transform, seen);
            }
            else
            {
                foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    if (go) ScanRecursive(go.transform, seen);
            }

            ZXXToolUsageStats.Complete("Label Tool", entries.Count, usageTimer);
            Refresh();
        }

        /// <summary>
        /// 根据当前条目列表刷新标签控件的创建/销毁。
        /// </summary>
        public void Refresh()
        {
            if (_canvas == null) BuildCanvas();

            var keep = new HashSet<GameObject>();
            foreach (var e in entries)
                if (e.show && e.target) keep.Add(e.target);

            // 移除不再需要的控件
            var dead = new List<GameObject>();
            foreach (var kv in _active)
                if (!keep.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var k in dead)
            {
                ReleaseWidget(_active[k]);
                _active.Remove(k);
            }

            // 创建新的控件
            foreach (var e in entries)
            {
                if (!e.show || !e.target || _active.ContainsKey(e.target)) continue;

                var w = AllocWidget();
                w.go.SetActive(true);
                w.target = e.target;
                w.go.name = DisplayName(e.target);
                w.text.text = w.go.name;
                ApplyStyleToWidget(w);
                SizeWidget(w);
                _active[e.target] = w;
            }
        }

        /// <summary>
        /// 强制重建底板贴图和文字材质，并应用到所有控件。
        /// 在编辑器中通过上下文菜单或工具栏按钮调用。
        /// </summary>
        [ContextMenu("Rebuild Style")]
        public void ForceRebuildStyle()
        {
            _styleFingerprint = 0;
            RebuildStyle();
            _styleFingerprint = style.GetFingerprint();
        }

        /// <summary>
        /// 获取物体的显示名称。优先使用 Prefab 别名，其次使用 Prefab 名称，最后使用物体名称。
        /// </summary>
        public string DisplayName(GameObject go)
        {
            if (!go) return "";
#if UNITY_EDITOR
            var pf = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (pf)
            {
                foreach (var a in aliases)
                    if (a.prefab == pf && !string.IsNullOrWhiteSpace(a.alias))
                        return a.alias;
                if (usePrefabName) return pf.name;
            }
#endif
            return go.name;
        }

        /// <summary>
        /// 计算标签的世界坐标位置。
        /// 如果启用包围盒定位，Y 轴取物体包围盒顶部 + 边距；否则取物体位置 + 偏移。
        /// </summary>
        public Vector3 GetWorldPos(GameObject target)
        {
            if (!target) return Vector3.zero;
            var p = target.transform.position;
            float y = (useBounds ? TopY(target) + heightMargin : p.y) + worldOffset.y;
            return new Vector3(p.x + worldOffset.x, y, p.z + worldOffset.z);
        }

        /// <summary>清理所有 target 为 null 的条目。</summary>
        /// <returns>清理的数量</returns>
        public int CleanInvalid()
        {
            return entries.RemoveAll(e => !e.target);
        }

        /// <summary>
        /// 将当前可见标签烘培为静态 TextMeshPro GameObject，写入场景。
        /// 供没有 LabelTool 代码的协作者直接查看标签。
        /// </summary>
        public void BakeToScene()
        {
            var usageTimer = ZXXToolUsageStats.Begin();
#if UNITY_EDITOR
            var parent = new GameObject("[Baked Labels]");
            Undo.RegisterCreatedObjectUndo(parent, "烘培标签");

            foreach (var e in entries)
            {
                if (!e.show || !e.target) continue;
                var worldPos = GetWorldPos(e.target);
                var name = DisplayName(e.target);

                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "烘培标签");
                go.transform.SetParent(parent.transform);
                go.transform.position = worldPos;
                go.transform.localScale = Vector3.one * style.baseScale;
                go.AddComponent<LabelBillboard>();

                var tmp = go.AddComponent<TextMeshPro>();
                tmp.text = name;
                tmp.fontSize = style.fontSize;
                tmp.color = style.textColor;
                tmp.fontStyle = style.fontStyle;
                tmp.alignment = style.textAlignment;
                if (style.fontAsset) tmp.font = style.fontAsset;
                if (style.outlineWidth > 0.001f)
                {
                    tmp.outlineWidth = style.outlineWidth;
                    tmp.outlineColor = style.outlineColor;
                }
            }

            ZXXToolUsageStats.Complete("Label Tool", parent.transform.childCount, usageTimer);
            Selection.activeGameObject = parent;
            enabled = false;
            Debug.Log($"[Label] 烘培完成，共 {parent.transform.childCount} 个标签。LabelManager 已自动禁用。");
#endif
        }

        /// <summary>
        /// 计算物体所有子 Renderer 包围盒的最高 Y 坐标。
        /// 如果没有 Renderer，返回物体 transform 的 Y 坐标。
        /// </summary>
        public static float TopY(GameObject go)
        {
            if (!go) return 0f;
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return go.transform.position.y;

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return bounds.max.y;
        }

        // ── 控件池 ────────────────────────────────

        private Widget AllocWidget()
        {
            if (_pool.Count > 0) return _pool.Pop();

            var w = CreateWidget();
            _widgets.Add(w);
            return w;
        }

        private void ReleaseWidget(Widget w)
        {
            w.target = null;
            w.text.text = "";
            w.go.name = "(idle)";
            w.go.SetActive(false);
            _pool.Push(w);
        }

        private Widget CreateWidget()
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.layer = _labelLayer;
            go.transform.SetParent(_canvas.transform, false);
            var rt = (RectTransform)go.transform;

            var img = go.AddComponent<Image>();
            img.sprite = _bgSprite;
            img.type = Image.Type.Sliced;
            img.raycastTarget = false;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.layer = _labelLayer;
            textGo.transform.SetParent(go.transform, false);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;
            if (_textMat) tmp.fontSharedMaterial = _textMat;

            // 文字填满整个标签底板
            var textRt = (RectTransform)textGo.transform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            return new Widget { go = go, rt = rt, bg = img, text = tmp };
        }

        private static void DestroyWidget(Widget w)
        {
            if (w?.go) DestroySafely(w.go);
        }

        // ── 样式应用 ──────────────────────────────

        private void RebuildStyle()
        {
            // 销毁旧的底板贴图（仅程序化生成的贴图需要销毁，外部 Sprite 是资产不销毁）
            if (_bgSprite != null)
            {
                if (style.bgMode == BackgroundMode.Procedural && _bgSprite.texture)
                    DestroySafely(_bgSprite.texture);
                if (style.bgMode == BackgroundMode.Procedural)
                    DestroySafely(_bgSprite);
            }
            _bgSprite = GetBgSprite();

            if (_textMat != null) DestroySafely(_textMat);
            _textMat = BuildTextMaterial();

            // 应用到所有控件
            foreach (var w in _widgets)
            {
                if (w?.bg == null) continue;
                w.bg.sprite = _bgSprite;
                if (_textMat) w.text.fontSharedMaterial = _textMat;
                ApplyStyleToWidget(w);
                if (w.go.activeSelf) SizeWidget(w);
            }
        }

        private void ApplyStyleToWidget(Widget w)
        {
            var s = style;
            // 贴图颜色已烘焙进像素，Image.color 保持白色避免二次乘色
            w.bg.color = Color.white;
            w.text.color = s.textColor;
            w.text.fontSize = s.fontSize;
            w.text.fontStyle = s.fontStyle;
            w.text.alignment = s.textAlignment;
            if (s.fontAsset) w.text.font = s.fontAsset;
        }

        /// <summary>根据文字内容计算并设置标签控件尺寸。</summary>
        private void SizeWidget(Widget w)
        {
            var s = style;
            var pref = w.text.GetPreferredValues(w.text.text ?? "");
            w.rt.sizeDelta = pref + s.padding * 2f;
        }

        // ── 每帧更新 ──────────────────────────────

        private void UpdateWidget(Widget w, bool checkVisibility)
        {
            if (!w.target) return;

            // 物体隐藏时不再显示标签
            if (!w.target.activeInHierarchy)
            {
                w.go.SetActive(false);
                return;
            }

            var worldPos = GetWorldPos(w.target);

            // 可见性 LOD 检测：
            // - 当前隐藏的标签：每帧检测，确保移动摄像机后立即显示
            // - 当前显示的标签：节流检测，减少性能开销
            bool wasHidden = !w.go.activeSelf;
            if (wasHidden || checkVisibility)
            {
                if (!IsVisible(worldPos))
                {
                    w.go.SetActive(false);
                    return;
                }
                w.go.SetActive(true);
            }

            w.rt.position = worldPos;
            w.rt.rotation = _mainCam ? _mainCam.transform.rotation : Quaternion.identity;
            w.rt.localScale = Vector3.one * style.baseScale;
        }

        private bool IsVisible(Vector3 worldPos)
        {
            var vp = _mainCam.WorldToViewportPoint(worldPos);
            if (vp.z <= 0f) return false;
            if (vp.x < -viewportMargin || vp.x > 1f + viewportMargin) return false;
            if (vp.y < -viewportMargin || vp.y > 1f + viewportMargin) return false;
            if (maxDistance > 0f && vp.z > maxDistance) return false;
            return true;
        }

        // ── Canvas ────────────────────────────────

        /// <summary>根据当前底板模式获取 Sprite：Procedural 模式程序化生成，Sprite 模式使用外部资产。</summary>
        private Sprite GetBgSprite()
        {
            if (style.bgMode == BackgroundMode.Sprite && style.bgSprite != null)
                return style.bgSprite;
            return LabelBgBuilder.Build(style);
        }

        private void BuildCanvas()
        {
            if (_canvas != null) return;

            // 清理编译重载时残留的旧 Canvas（引用丢失导致无法追踪）
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name == "[Label Canvas]")
                    DestroySafely(child.gameObject);
            }

            var go = new GameObject("[Label Canvas]");
            go.layer = _labelLayer;
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;

            _bgSprite = GetBgSprite();
            _textMat = BuildTextMaterial();
        }

        // ── 材质 ──────────────────────────────────

        private Material BuildTextMaterial()
        {
            var font = style.fontAsset ? style.fontAsset : TMP_Settings.defaultFontAsset;
            if (!font || !font.material) return null;

            var mat = new Material(font.material);
            if (style.outlineWidth > 0.001f)
            {
                mat.EnableKeyword("OUTLINE_ON");
                mat.SetFloat(ShaderUtilities.ID_OutlineWidth, style.outlineWidth);
                mat.SetColor(ShaderUtilities.ID_OutlineColor, style.outlineColor);
            }
            return mat;
        }

        // ── 扫描 ──────────────────────────────────

        private void ScanRecursive(Transform t, HashSet<GameObject> seen)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (seen.Add(child.gameObject))
                    entries.Add(new Entry { target = child.gameObject, show = true });
                if (!onlyDirectChildren)
                    ScanRecursive(child, seen);
            }
        }

        // ── 工具 ──────────────────────────────────

        private int GetOrCreateLabelLayer()
        {
            int layer = LayerMask.NameToLayer("Label");
            if (layer >= 0) return layer;
#if UNITY_EDITOR
            Debug.LogWarning("[Label] 场景中未找到 'Label' Layer，正在 ProjectSettings/TagManager.asset 中自动创建。如需撤销，手动删除此 Layer 即可。");
            var asset = UnityEditor.AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0];
            var so = new SerializedObject(asset);
            var layers = so.FindProperty("layers");
            for (int i = 8; i < 32; i++)
            {
                var prop = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(prop.stringValue))
                {
                    prop.stringValue = "Label";
                    so.ApplyModifiedProperties();
                    return i;
                }
            }
#endif
            return gameObject.layer;
        }

        /// <summary>查找第一个启用且激活的相机（Game 视图所用的相机）。不依赖 tag。</summary>
        private Camera FindMainCamera()
        {
            // 优先用 Camera.main（Game 视图相机），没有则取第一个非 Label 的激活相机
            if (Camera.main && Camera.main.isActiveAndEnabled) return Camera.main;
            var cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
                if (cameras[i].isActiveAndEnabled && cameras[i] != _labelCam)
                    return cameras[i];
            return null;
        }

        private static void DestroySafely(UnityEngine.Object obj)
        {
            if (!obj) return;
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        // ── Widget 内部类 ──────────────────────────

        private sealed class Widget
        {
            public GameObject go;
            public RectTransform rt;
            public Image bg;
            public TextMeshProUGUI text;
            public GameObject target;
        }

    }

    // ══════════════════════════════════════════════
    // 底板贴图生成器
    // ══════════════════════════════════════════════

    /// <summary>
    /// 程序化生成标签底板 Sprite。支持圆角、描边和边缘模糊。
    /// </summary>
    internal static class LabelBgBuilder
    {
        private const float ExpandFactor = 2.5f;
        private const int MinSize = 6;
        private const int CornerMargin = 4;

        /// <summary>根据样式参数生成 Sliced Sprite。</summary>
        public static Sprite Build(LabelStyle s)
        {
            int radius = Mathf.Max(2, Mathf.RoundToInt(s.cornerRadius));
            int outlineWidth = Mathf.Max(0, Mathf.RoundToInt(s.bgOutlineWidth));
            float blur = Mathf.Max(0f, s.edgeBlur);

            int outerRadius = radius + outlineWidth;
            int innerSize = outerRadius * 2 + CornerMargin;
            int pad = blur > 0.01f ? Mathf.CeilToInt(blur * ExpandFactor) : 0;
            int texSize = Mathf.Max(innerSize + pad * 2, MinSize);

            var pixels = new Color[texSize * texSize];

            for (int y = 0; y < texSize; y++)
            {
                for (int x = 0; x < texSize; x++)
                {
                    int lx = x - pad;
                    int ly = y - pad;

                    if (lx >= 0 && lx < innerSize && ly >= 0 && ly < innerSize)
                    {
                        float innerDist = SdfRoundRect(lx - outlineWidth, ly - outlineWidth,
                            innerSize - 2 * outlineWidth, radius);
                        float outerDist = SdfRoundRect(lx, ly, innerSize, outerRadius);

                        if (innerDist <= 0)
                            pixels[y * texSize + x] = s.backgroundColor;
                        else if (outlineWidth > 0 && outerDist <= 0)
                            pixels[y * texSize + x] = s.bgOutlineColor;
                    }
                }
            }

            if (blur > 0.01f)
                GaussianBlurAlpha(pixels, texSize, texSize, blur);

            var tex = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            tex.SetPixels(pixels);
            tex.Apply();

            int border = Mathf.Clamp(
                radius + outlineWidth + 1 + Mathf.CeilToInt(blur * ExpandFactor),
                1,
                texSize / 2 - 1);

            return Sprite.Create(tex,
                new Rect(0, 0, texSize, texSize),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
        }

        /// <summary>计算点到圆角矩形边缘的有符号距离（负值 = 内部）。</summary>
        private static float SdfRoundRect(float x, float y, int size, int radius)
        {
            float cx = Mathf.Clamp(x, radius, size - 1 - radius);
            float cy = Mathf.Clamp(y, radius, size - 1 - radius);
            return Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - radius;
        }

        /// <summary>分离式高斯模糊（仅操作 Alpha 通道）。</summary>
        private static void GaussianBlurAlpha(Color[] pixels, int w, int h, float sigma)
        {
            int kernelRadius = Mathf.Max(1, Mathf.CeilToInt(sigma * ExpandFactor));
            int kernelSize = kernelRadius * 2 + 1;

            // 构建一维高斯核
            var kernel = new float[kernelSize];
            float sum = 0f;
            float twoSigmaSq = 2f * sigma * sigma;
            for (int i = 0; i < kernelSize; i++)
            {
                float v = Mathf.Exp(-(i - kernelRadius) * (i - kernelRadius) / twoSigmaSq);
                kernel[i] = v;
                sum += v;
            }
            for (int i = 0; i < kernelSize; i++)
                kernel[i] /= sum;

            // 水平 pass
            var alpha = new float[w * h];
            var temp = new float[w * h];
            for (int i = 0; i < pixels.Length; i++)
                alpha[i] = pixels[i].a;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    for (int j = 0; j < kernelSize; j++)
                    {
                        int sx = Mathf.Clamp(x + j - kernelRadius, 0, w - 1);
                        acc += alpha[y * w + sx] * kernel[j];
                    }
                    temp[y * w + x] = acc;
                }
            }

            // 垂直 pass
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    for (int j = 0; j < kernelSize; j++)
                    {
                        int sy = Mathf.Clamp(y + j - kernelRadius, 0, h - 1);
                        acc += temp[sy * w + x] * kernel[j];
                    }
                    pixels[y * w + x] = new Color(
                        pixels[y * w + x].r,
                        pixels[y * w + x].g,
                        pixels[y * w + x].b,
                        acc);
                }
            }
        }
    }
}

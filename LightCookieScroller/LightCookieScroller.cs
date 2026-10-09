using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(Light))]
public class LightCookieScroller : MonoBehaviour
{
    [Header("Shader (可选)")]
    public Shader scrollShader;

    [Header("Cookie 纹理 (可选，留空则用 Light 上已有的)")]
    public Texture2D cookieTexture;

    [Header("滚动速度和方向 (X=水平, Y=垂直)")]
    public Vector2 scrollSpeed = new Vector2(0.05f, 0.03f);

    [Header("显示")]
    [SerializeField] private bool _visible = true;

    private Light _light;
    private Material _material;
    private RenderTexture _rt;
    private Vector2 _offset;
    private bool _wasVisible;

    public bool visible
    {
        get => _visible;
        set => _visible = value;
    }

    void Start()
    {
        _light = GetComponent<Light>();
        _wasVisible = _visible;

        if (!TryGetTexture()) return;
        if (!TryCreateMaterial()) return;

        _rt = new RenderTexture(cookieTexture.width, cookieTexture.height, 0)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = cookieTexture.filterMode
        };

        EnsureRepeatWrapMode();
    }

    void Update()
    {
        if (_material == null || _rt == null) return;

        if (_visible != _wasVisible)
        {
            _light.cookie = _visible ? _rt : null;
            _wasVisible = _visible;
        }

        if (!_visible) return;

        _offset += scrollSpeed * Time.deltaTime;
        _material.SetFloat("_OffsetX", _offset.x);
        _material.SetFloat("_OffsetY", _offset.y);

        Graphics.Blit(cookieTexture, _rt, _material);
        _light.cookie = _rt;
    }

    void OnDestroy()
    {
        if (_material != null) Destroy(_material);
        if (_rt != null) _rt.Release();
    }

    bool TryGetTexture()
    {
        if (cookieTexture == null)
            cookieTexture = _light.cookie as Texture2D;

        if (cookieTexture == null)
        {
            Debug.LogWarning("LightCookieScroller: 未找到 Cookie 纹理", this);
            enabled = false;
            return false;
        }
        return true;
    }

    bool TryCreateMaterial()
    {
        var shader = scrollShader != null ? scrollShader : Shader.Find("Hidden/LightCookieScroller");
        if (shader == null)
        {
            Debug.LogError("LightCookieScroller: 找不到 Shader，请将 LightCookieScroller.shader 拖到 scrollShader 字段", this);
            enabled = false;
            return false;
        }
        _material = new Material(shader);
        return true;
    }

    void EnsureRepeatWrapMode()
    {
        if (cookieTexture != null && cookieTexture.wrapMode != TextureWrapMode.Repeat)
            Debug.LogWarning("LightCookieScroller: Cookie 纹理 Wrap Mode 不是 Repeat，滚动可能不正常。请在纹理导入设置中改为 Repeat。", cookieTexture);
    }

#if UNITY_EDITOR
    public static void AddToSelectedLights()
    {
        var targets = new System.Collections.Generic.List<GameObject>();
        foreach (var go in Selection.gameObjects ?? new GameObject[0])
        {
            if (go.GetComponent<Light>() != null && go.GetComponent<LightCookieScroller>() == null)
                targets.Add(go);
        }

        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("Light Cookie Scroller",
                "请在 Hierarchy 中选中至少一个带 Light 组件、且未挂载 LightCookieScroller 的物体。", "确定");
            return;
        }

        foreach (var go in targets)
            Undo.AddComponent<LightCookieScroller>(go);

        Debug.Log($"LightCookieScroller: 已添加到 {targets.Count} 个物体。");
    }
#endif
}

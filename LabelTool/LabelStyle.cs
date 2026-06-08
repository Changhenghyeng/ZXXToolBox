using UnityEngine;
using TMPro;

namespace LabelTool
{
    /// <summary>底板渲染模式。</summary>
    public enum BackgroundMode
    {
        /// <summary>程序化生成圆角矩形贴图。</summary>
        Procedural,
        /// <summary>使用外部 Sprite 资产。</summary>
        Sprite
    }

    /// <summary>
    /// 标签的视觉样式。
    /// </summary>
    [System.Serializable]
    public class LabelStyle
    {
        [Header("底板")]
        public Color backgroundColor = new Color(0f, 0f, 0f, 0.7f);
        [Tooltip("底板圆角（像素）")]
        public float cornerRadius = 12f;
        [Tooltip("文字四周内边距（像素）")]
        public Vector2 padding = new Vector2(20f, 10f);
        [Tooltip("底板描边颜色")]
        public Color bgOutlineColor = new Color(1f, 1f, 1f, 0.6f);
        [Tooltip("底板描边粗细（像素，0=无）")]
        public float bgOutlineWidth = 0f;
        [Tooltip("底板边缘模糊强度（0=不模糊）")]
        public float edgeBlur = 0f;

        [Header("底板模式")]
        [Tooltip("Procedural = 代码生成圆角矩形；Sprite = 使用下方自定义贴图")]
        public BackgroundMode bgMode = BackgroundMode.Procedural;
        [Tooltip("当 bgMode = Sprite 时使用的底板贴图")]
        public Sprite bgSprite;

        [Header("文字")]
        [Tooltip("TMP 字体资产。留空则使用项目默认字体")]
        public TMP_FontAsset fontAsset;
        public Color textColor = Color.white;
        [Tooltip("基础字号，再被 baseScale 缩到世界单位")]
        public int fontSize = 28;
        public FontStyles fontStyle = FontStyles.Normal;
        [Tooltip("文字在底板内的对齐方式")]
        public TextAlignmentOptions textAlignment = TextAlignmentOptions.Center;
        public Color outlineColor = new Color(0f, 0f, 0f, 0.6f);
        [Tooltip("TMP 文字描边宽度（材质单位 0~0.5，0=无）")]
        [Range(0f, 0.5f)]
        public float outlineWidth = 0.18f;

        [Header("缩放")]
        [Tooltip("世界空间基础缩放：把 UI 像素单位缩到合理世界尺寸")]
        public float baseScale = 0.01f;

        /// <summary>所有样式属性的组合哈希，用于运行时检测变化触发重建。</summary>
        public int GetFingerprint()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + backgroundColor.GetHashCode();
                h = h * 31 + cornerRadius.GetHashCode();
                h = h * 31 + padding.GetHashCode();
                h = h * 31 + bgOutlineColor.GetHashCode();
                h = h * 31 + bgOutlineWidth.GetHashCode();
                h = h * 31 + edgeBlur.GetHashCode();
                h = h * 31 + bgMode.GetHashCode();
                h = h * 31 + (bgSprite ? bgSprite.GetHashCode() : 0);
                h = h * 31 + (fontAsset ? fontAsset.GetHashCode() : 0);
                h = h * 31 + textColor.GetHashCode();
                h = h * 31 + fontSize.GetHashCode();
                h = h * 31 + fontStyle.GetHashCode();
                h = h * 31 + textAlignment.GetHashCode();
                h = h * 31 + outlineColor.GetHashCode();
                h = h * 31 + outlineWidth.GetHashCode();
                h = h * 31 + baseScale.GetHashCode();
                return h;
            }
        }
    }
}

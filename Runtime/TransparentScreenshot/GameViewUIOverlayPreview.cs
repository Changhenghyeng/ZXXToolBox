#if UNITY_EDITOR
using UnityEngine;

// Lives in ArtCode so Unity can attach it; excluded from player builds.
[ExecuteAlways]
public sealed class GameViewUIOverlayPreview : MonoBehaviour
{
    public Texture2D Image { get; set; }
    public bool Stretch { get; set; }
    public int OutputWidth { get; set; }
    public int OutputHeight { get; set; }
    public int LastDrawnFrame { get; private set; } = -1;

    private void OnGUI()
    {
        GUI.depth = int.MinValue;
        if (Image == null || Event.current.type != EventType.Repaint) return;
        Color previousColor = GUI.color;
        int previousDepth = GUI.depth;
        Matrix4x4 previousMatrix = GUI.matrix;
        try
        {
            GUI.color = Color.white;
            GUI.depth = int.MinValue;
            int width = OutputWidth > 0 && OutputHeight > 0 ? OutputWidth : Screen.width;
            int height = OutputWidth > 0 && OutputHeight > 0 ? OutputHeight : Screen.height;
            GUI.matrix = Matrix4x4.Scale(new Vector3((float)Screen.width / width, (float)Screen.height / height, 1));
            GUI.DrawTexture(new Rect(0, 0, width, height), Image,
                Stretch ? ScaleMode.StretchToFill : ScaleMode.ScaleToFit, true);
            LastDrawnFrame = Time.frameCount;
        }
        finally
        {
            GUI.color = previousColor;
            GUI.depth = previousDepth;
            GUI.matrix = previousMatrix;
        }
    }
}
#endif

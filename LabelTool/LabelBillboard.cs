using UnityEngine;

/// <summary>让物体始终面朝渲染相机。仅用于烘培后的标签。</summary>
[ExecuteAlways]
public class LabelBillboard : MonoBehaviour
{
    private void OnWillRenderObject()
    {
        var cam = Camera.current;
        if (cam) transform.rotation = cam.transform.rotation;
    }
}

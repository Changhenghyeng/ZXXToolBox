using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

internal static class PathBuilderSplineUI
{
    internal static readonly GUIContent[] MainTabs =
    {
        new GUIContent("Wall", "围墙与直线物体队列"),
        new GUIContent("Mountain", "使用样条生成模型山或面片山"),
        new GUIContent("River / Road", "使用样条生成河流或道路网格")
    };

    internal static readonly GUIContent[] SplineStages =
    {
        new GUIContent("1  Draw", "绘制和续接样条控制点"),
        new GUIContent("2  Adjust", "调整样条和生成参数"),
        new GUIContent("3  Output", "检查并烘焙最终结果")
    };

    internal static void PickSceneObject(Vector2 guiPosition, bool toggle)
    {
        GameObject picked = HandleUtility.PickGameObject(guiPosition, false);
        if (!picked)
        {
            if (!toggle)
                Selection.activeObject = null;
            return;
        }

        if (!toggle)
        {
            Selection.activeGameObject = picked;
            return;
        }

        List<Object> selected = new List<Object>(Selection.objects);
        if (selected.Contains(picked))
        {
            selected.Remove(picked);
            Selection.objects = selected.ToArray();
        }
        else
        {
            selected.Add(picked);
            Selection.objects = selected.ToArray();
            Selection.activeGameObject = picked;
        }
    }
}

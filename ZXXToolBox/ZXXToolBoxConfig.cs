using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ZXX 工具箱配置文件 — 团队共享设置（跟 Git 走）。
/// </summary>
[CreateAssetMenu(fileName = "ZXXToolBoxConfig", menuName = "ZXX/工具箱配置")]
public class ZXXToolBoxConfig : ScriptableObject
{
    [Tooltip("工具箱窗口标题和菜单名称")]
    public string displayName = "ZXX ToolBox";

    [Tooltip("工具覆盖：分类 / 描述 / 可见性（团队共享）")]
    public List<ToolOverride> toolOverrides = new List<ToolOverride>();

    [Tooltip("自定义工作流列表")]
    public List<WorkflowDef> workflows = new List<WorkflowDef>();
}

/// <summary>单个工具的共享配置覆盖。键为工具 id。</summary>
[System.Serializable]
public class ToolOverride
{
    public string id;
    public string desc;
    public string category;
    public bool visible = true;
}

[System.Serializable]
public class WorkflowStepData
{
    public string stepType; // "tool" or "manual"
    public string toolId;
    public string label;
    public string settingsJson;
}

[System.Serializable]
public class WorkflowDef
{
    public string name;
    public List<WorkflowStepData> steps = new List<WorkflowStepData>();
}

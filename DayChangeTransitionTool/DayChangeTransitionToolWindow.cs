using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DayChangeTransitionTool.Editor
{
    public class DayChangeTransitionToolWindow : EditorWindow
    {
        private const string PrefPrefix = "DayChangeTransitionTool.";

        private string _saveFolder = "Assets/ArtRes/DayChangeTransition";
        private string _controllerName = "DayChangeTransitionController";
        private string _duskVolumeName = "DayChange_DuskVolume";
        private string _nightVolumeName = "DayChange_NightVolume";
        private string _duskProfileName = "DayChange_DuskVolumeProfile";
        private string _nightProfileName = "DayChange_NightVolumeProfile";
        private Light _mainLight;
        private VolumeProfile _baseDayProfile;
        private VolumeProfile _duskProfile;
        private VolumeProfile _nightProfile;
        private bool _useDuskVolume = true;
        private float _fadeToNightSeconds = 8f;
        private float _nightHoldSeconds = 3f;
        private float _fadeToDaySeconds = 8f;
        private bool _autoSyncSceneComponent = true;
        private Vector2 _scroll;
        private Component _editorPreviewController;
        private double _editorPreviewStartTime;
        private float _editorPreviewProgress;
        private bool _editorPreviewPlaying;
        private bool _loadingFromSceneComponent;

        public static void Open()
        {
            GetWindow<DayChangeTransitionToolWindow>("Day Change Transition");
        }

        private void OnEnable()
        {
            _saveFolder = EditorPrefs.GetString(PrefPrefix + "SaveFolder", _saveFolder);
            _controllerName = EditorPrefs.GetString(PrefPrefix + "ControllerName", _controllerName);
            _duskVolumeName = EditorPrefs.GetString(PrefPrefix + "DuskVolumeName", _duskVolumeName);
            _nightVolumeName = EditorPrefs.GetString(PrefPrefix + "NightVolumeName", _nightVolumeName);
            _duskProfileName = EditorPrefs.GetString(PrefPrefix + "DuskProfileName", _duskProfileName);
            _nightProfileName = EditorPrefs.GetString(PrefPrefix + "NightProfileName", _nightProfileName);
            string baseDayProfilePath = EditorPrefs.GetString(PrefPrefix + "BaseDayProfilePath", "");
            _baseDayProfile = string.IsNullOrEmpty(baseDayProfilePath) ? null : AssetDatabase.LoadAssetAtPath<VolumeProfile>(baseDayProfilePath);
            string duskProfilePath = EditorPrefs.GetString(PrefPrefix + "DuskProfilePath", "");
            string nightProfilePath = EditorPrefs.GetString(PrefPrefix + "NightProfilePath", "");
            _duskProfile = string.IsNullOrEmpty(duskProfilePath) ? null : AssetDatabase.LoadAssetAtPath<VolumeProfile>(duskProfilePath);
            _nightProfile = string.IsNullOrEmpty(nightProfilePath) ? null : AssetDatabase.LoadAssetAtPath<VolumeProfile>(nightProfilePath);
            _useDuskVolume = EditorPrefs.GetBool(PrefPrefix + "UseDuskVolume", _useDuskVolume);
            _fadeToNightSeconds = EditorPrefs.GetFloat(PrefPrefix + "FadeToNightSeconds", _fadeToNightSeconds);
            _nightHoldSeconds = EditorPrefs.GetFloat(PrefPrefix + "NightHoldSeconds", _nightHoldSeconds);
            _fadeToDaySeconds = EditorPrefs.GetFloat(PrefPrefix + "FadeToDaySeconds", _fadeToDaySeconds);
            _autoSyncSceneComponent = EditorPrefs.GetBool(PrefPrefix + "AutoSyncSceneComponent", _autoSyncSceneComponent);
            _mainLight = FindMainLight();
            SyncFromSceneComponent();
        }

        private void OnDisable()
        {
            StopEditorPreview(true);
            SavePrefs();
        }

        private void OnFocus()
        {
            SyncFromSceneComponent();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginVertical(GUILayout.ExpandHeight(true));

            DrawHeader();

            EditorGUI.BeginChangeCheck();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawSetupFields();
            DrawMaterialTransitionSection();
            DrawPreviewButtons();
            EditorGUILayout.EndScrollView();
            if (EditorGUI.EndChangeCheck() && !_loadingFromSceneComponent)
            {
                SavePrefs();
                ApplySettingsToSceneComponent();
            }

            EditorGUILayout.Space(6f);
            Color previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.72f, 0.9f, 0.72f, 1f);
            if (GUILayout.Button(new GUIContent("Create / Repair Setup", "一键创建或修复场景中的昼夜过渡 Controller、Dusk/Night Volume 和默认 Profile。已有对象会尽量复用。"), GUILayout.Height(34f)))
            {
                CreateOrRepairSetup(true);
            }
            GUI.backgroundColor = previousBackground;

            DrawSignature();
            EditorGUILayout.EndVertical();
        }

        private static void DrawHeader()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Day Change Transition", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("用于 SLG 的短昼夜过渡：白天 -> 黄昏 -> 夜晚 -> 白天。灯光变化只在过渡中临时生效，结束后会还原。", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6f);
        }

        private static void DrawSignature()
        {
            var authorStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
            };
            EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
            EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
        }

        private void DrawSetupFields()
        {
            BeginSection("Scene References");
            _autoSyncSceneComponent = EditorGUILayout.ToggleLeft(new GUIContent("Auto Sync Scene Component", "开启后，工具窗口里的设置会自动同步到场景里的 DayChangeTransitionController。"), _autoSyncSceneComponent);
            using (new EditorGUILayout.HorizontalScope())
            {
                _mainLight = (Light)EditorGUILayout.ObjectField(new GUIContent("Main Light", "昼夜过渡期间临时旋转和调整的主光源。过渡结束后会恢复原始状态。"), _mainLight, typeof(Light), true);
                if (GUILayout.Button(new GUIContent("Auto", "自动查找场景主方向光，优先使用 RenderSettings.sun。"), GUILayout.Width(58f)))
                {
                    _mainLight = FindMainLight();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _baseDayProfile = (VolumeProfile)EditorGUILayout.ObjectField(new GUIContent("Base Day Profile", "白天的后处理模板。创建或修复时会用它给 Dusk/Night Profile 补齐共享组件，减少过渡突变。"), _baseDayProfile, typeof(VolumeProfile), false);
                if (GUILayout.Button(new GUIContent("Auto", "从场景中的全局 Volume 里尝试找一个可作为白天模板的 Profile。"), GUILayout.Width(58f)))
                {
                    _baseDayProfile = FindBaseDayProfile();
                }
            }
            EditorGUILayout.LabelField("Base Day Profile 用来给黄昏和夜晚补齐共享后处理组件，避免过渡时组件突然出现或消失。", EditorStyles.wordWrappedMiniLabel);
            int syncAction = DrawTwoColumnButtons(
                "Sync From Scene", "Apply To Scene", 24f,
                "从场景里的 Controller 和 Volume 读取当前引用与参数到工具窗口。",
                "把工具窗口里的引用与参数写回场景里的 Controller 和 Volume。");
            if (syncAction == 1)
            {
                SyncFromSceneComponent(true);
            }
            else if (syncAction == 2)
            {
                ApplySettingsToSceneComponent(true);
            }
            EndSection();

            BeginSection("Generated Assets");
            using (new EditorGUILayout.HorizontalScope())
            {
                _saveFolder = EditorGUILayout.TextField(new GUIContent("Save Folder", "没有指定 Dusk/Night Profile 文件时，自动创建资源的保存目录。"), _saveFolder);
                if (GUILayout.Button(new GUIContent("...", "选择自动生成资源保存目录，必须在当前项目 Assets 内。"), GUILayout.Width(30f)))
                {
                    string selected = EditorUtility.OpenFolderPanel("Select Save Folder", Application.dataPath, "");
                    if (!string.IsNullOrEmpty(selected))
                    {
                        string dataPath = Application.dataPath.Replace('\\', '/');
                        selected = selected.Replace('\\', '/');
                        if (selected.StartsWith(dataPath))
                        {
                            _saveFolder = "Assets" + selected.Substring(dataPath.Length);
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("Day Change Transition", "请选择当前项目 Assets 目录内的文件夹。", "OK");
                        }
                    }
                }
            }

            _controllerName = EditorGUILayout.TextField(new GUIContent("Controller", "场景中昼夜过渡控制器 GameObject 的名称。"), _controllerName);
            _duskVolumeName = EditorGUILayout.TextField(new GUIContent("Dusk Volume", "黄昏阶段使用的 Volume 对象名称。"), _duskVolumeName);
            _nightVolumeName = EditorGUILayout.TextField(new GUIContent("Night Volume", "夜晚阶段使用的 Volume 对象名称。"), _nightVolumeName);
            EditorGUI.BeginChangeCheck();
            _duskProfile = (VolumeProfile)EditorGUILayout.ObjectField(new GUIContent("Dusk Profile", "黄昏过渡使用的 VolumeProfile。可直接拖已有 Profile；为空时会自动创建默认资源。"), _duskProfile, typeof(VolumeProfile), false);
            _nightProfile = (VolumeProfile)EditorGUILayout.ObjectField(new GUIContent("Night Profile", "夜晚阶段使用的 VolumeProfile。可直接拖已有 Profile；为空时会自动创建默认资源。"), _nightProfile, typeof(VolumeProfile), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (_duskProfile != null)
                {
                    ApplyProfileAssetToFields(_duskProfile, true);
                }
                if (_nightProfile != null)
                {
                    ApplyProfileAssetToFields(_nightProfile, false);
                }
            }
            EndSection();

            BeginSection("Timing");
            _useDuskVolume = EditorGUILayout.ToggleLeft(new GUIContent("Use Dusk Transition", "开启后，白天到夜晚会先经过 Dusk Profile；关闭则直接白天过渡到夜晚。"), _useDuskVolume);
            _fadeToNightSeconds = EditorGUILayout.FloatField(new GUIContent("Fade To Night", "从白天过渡到夜晚的时长，包含黄昏段。单位：秒。"), Mathf.Max(0f, _fadeToNightSeconds));
            _nightHoldSeconds = EditorGUILayout.FloatField(new GUIContent("Night Hold", "夜晚停留时间。0 表示不停留，负数表示停在夜晚直到外部调用 ResumeToDay()。"), _nightHoldSeconds);
            _fadeToDaySeconds = EditorGUILayout.FloatField(new GUIContent("Fade To Day", "从夜晚恢复到白天的时长。单位：秒。"), Mathf.Max(0f, _fadeToDaySeconds));
            EditorGUILayout.LabelField("Night Hold：0 表示不停留；负数表示停在夜晚，直到外部调用 ResumeToDay()。", EditorStyles.wordWrappedMiniLabel);
            EndSection();
        }

        private void DrawPreviewButtons()
        {
            BeginSection("Preview & Selection");

            EditorGUILayout.LabelField("State Preview", EditorStyles.miniBoldLabel);
            int stateAction = DrawThreeColumnButtons(
                "Day", _useDuskVolume ? "Dusk" : "Mid", "Night", 28f,
                "恢复到白天状态，并停止编辑器预览。",
                _useDuskVolume ? "预览白天到夜晚中间的黄昏状态。" : "预览白天到夜晚的中间过渡状态。",
                "预览夜晚状态。");
            if (stateAction == 1)
            {
                PreviewDayState();
            }
            else if (stateAction == 2)
            {
                ApplyEditorPreviewProgress(0.25f);
            }
            else if (stateAction == 3)
            {
                ApplyEditorPreviewProgress(0.5f);
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Transition Preview", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Play Transition", "在编辑器里播放一次完整过渡预览：Day -> Dusk -> Night -> Day。"), GUILayout.Height(28f)))
                {
                    StartEditorPreview();
                }

                if (_editorPreviewPlaying)
                {
                    if (GUILayout.Button(new GUIContent("Stop", "停止正在播放的编辑器过渡预览，并恢复到白天状态。"), GUILayout.Width(58f), GUILayout.Height(28f)))
                    {
                        StopEditorPreview(true);
                    }
                }
            }

            EditorGUI.BeginChangeCheck();
            float nextProgress = EditorGUILayout.Slider(new GUIContent("Scrub", "手动拖动预览进度。0=白天，约0.25=黄昏，0.5=夜晚，1=回到白天。"), _editorPreviewProgress, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                ApplyEditorPreviewProgress(nextProgress);
            }
            EditorGUILayout.LabelField(GetPreviewProgressLabel(_editorPreviewProgress), EditorStyles.miniLabel);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Select", EditorStyles.miniBoldLabel);
            if (GUILayout.Button(new GUIContent("Select Controller", "在 Hierarchy 里选中 DayChangeTransitionController 所在对象。"), GUILayout.Height(26f)))
            {
                Component controller = GetController(false);
                if (controller != null) Selection.activeObject = controller.gameObject;
            }

            int selectProfileAction = DrawTwoColumnButtons(
                "Select Dusk Profile", "Select Night Profile", 26f,
                "在 Project 里选中当前 Dusk Profile 资源。",
                "在 Project 里选中当前 Night Profile 资源。");
            if (selectProfileAction == 1)
            {
                VolumeProfile profile = LoadDuskProfile();
                if (profile != null) Selection.activeObject = profile;
            }
            else if (selectProfileAction == 2)
            {
                VolumeProfile profile = LoadNightProfile();
                if (profile != null) Selection.activeObject = profile;
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Runtime", EditorStyles.miniBoldLabel);
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button(new GUIContent("Trigger Transition (Play Mode)", "运行模式下触发一次正式昼夜过渡。编辑器非运行模式下请用 Preview Transition。"), GUILayout.Height(28f)))
                {
                    Component controller = GetController(false);
                    if (controller != null) InvokeControllerMethod(controller, "TriggerDayChange");
                }
            }

            EndSection();
        }

        private void DrawMaterialTransitionSection()
        {
            BeginSection("Material Transitions");

            Component controller = GetController(false);
            if (controller == null)
            {
                EditorGUILayout.LabelField("请先创建或修复昼夜过渡系统，再配置材质变化。", EditorStyles.wordWrappedMiniLabel);
                EndSection();
                return;
            }

            SerializedObject serializedController = new SerializedObject(controller);
            SerializedProperty transitions = serializedController.FindProperty("MaterialTransitions");
            if (transitions == null)
            {
                EditorGUILayout.LabelField("当前运行时组件没有 MaterialTransitions 字段，请确认脚本已更新并编译通过。", EditorStyles.wordWrappedMiniLabel);
                EndSection();
                return;
            }

            for (int i = 0; i < transitions.arraySize; i++)
            {
                SerializedProperty transition = transitions.GetArrayElementAtIndex(i);
                SerializedProperty materialProp = transition.FindPropertyRelative("Material");
                SerializedProperty propertiesProp = transition.FindPropertyRelative("Properties");
                Material material = materialProp.objectReferenceValue as Material;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (material == null)
                    {
                        transition.isExpanded = true;
                    }

                    transition.isExpanded = EditorGUILayout.Foldout(
                        transition.isExpanded,
                        new GUIContent(GetMaterialTransitionTitle(material, propertiesProp), "折叠后只保留材质球摘要；展开后可以编辑材质和 Shader 属性变化。"),
                        true);

                    if (GUILayout.Button(new GUIContent("Remove", "移除这个材质球过渡槽。"), GUILayout.Width(68f)))
                    {
                        transitions.DeleteArrayElementAtIndex(i);
                        serializedController.ApplyModifiedProperties();
                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.EndVertical();
                        break;
                    }
                }

                if (transition.isExpanded)
                {
                    EditorGUILayout.PropertyField(materialProp, new GUIContent("Material", "参与昼夜过渡的材质球。多个物体共用同一材质时，只需要配置一次。"));
                    DrawAddShaderPropertyButton(propertiesProp, material);
                    DrawMaterialProperties(propertiesProp, material);
                }
                EditorGUILayout.EndVertical();
            }

            serializedController.ApplyModifiedProperties();
            int listAction = DrawMaterialListFooter();
            if (listAction == 1)
            {
                RemoveEmptyMaterialTransitions(controller);
                Repaint();
            }
            else if (listAction == 2)
            {
                AddSelectedMaterialsOrSlot(controller);
                Repaint();
            }

            EndSection();
        }

        private static void DrawAddShaderPropertyButton(SerializedProperty propertiesProp, Material material)
        {
            using (new EditorGUI.DisabledScope(material == null || material.shader == null))
            {
                if (GUILayout.Button(new GUIContent("Add Shader Property", "从当前材质球的 Shader 中选择一个可过渡的属性。支持 Float、Range、Color、Vector。"), GUILayout.Height(24f)))
                {
                    GenericMenu menu = new GenericMenu();
                    if (material != null && material.shader != null)
                    {
                        int count = ShaderUtil.GetPropertyCount(material.shader);
                        for (int i = 0; i < count; i++)
                        {
                            ShaderUtil.ShaderPropertyType shaderType = ShaderUtil.GetPropertyType(material.shader, i);
                            if (!IsSupportedShaderProperty(shaderType))
                            {
                                continue;
                            }

                            string propertyName = ShaderUtil.GetPropertyName(material.shader, i);
                            string displayName = ShaderUtil.GetPropertyDescription(material.shader, i);
                            if (string.IsNullOrEmpty(displayName))
                            {
                                displayName = propertyName;
                            }

                            menu.AddItem(new GUIContent(displayName + "  (" + propertyName + ")"), false, () =>
                            {
                                AddShaderProperty(propertiesProp, material, propertyName, shaderType);
                                propertiesProp.serializedObject.ApplyModifiedProperties();
                            });
                        }
                    }
                    menu.ShowAsContext();
                }
            }
        }

        private static string GetMaterialTransitionTitle(Material material, SerializedProperty propertiesProp)
        {
            string materialName = material != null ? material.name : "None";
            int propertyCount = propertiesProp != null ? propertiesProp.arraySize : 0;
            string propertyLabel = propertyCount == 1 ? "1 property" : propertyCount + " properties";
            return materialName + "  (" + propertyLabel + ")";
        }

        private static int DrawTwoColumnButtons(string leftLabel, string rightLabel, float height, string leftTooltip = "", string rightTooltip = "")
        {
            Rect row = EditorGUILayout.GetControlRect(false, height);
            const float gap = 4f;
            float width = Mathf.Floor((row.width - gap) * 0.5f);
            Rect leftRect = new Rect(row.x, row.y, width, row.height);
            Rect rightRect = new Rect(row.x + width + gap, row.y, width, row.height);

            if (GUI.Button(leftRect, new GUIContent(leftLabel, leftTooltip)))
            {
                return 1;
            }

            if (GUI.Button(rightRect, new GUIContent(rightLabel, rightTooltip)))
            {
                return 2;
            }

            return 0;
        }

        private static int DrawThreeColumnButtons(string leftLabel, string centerLabel, string rightLabel, float height, string leftTooltip = "", string centerTooltip = "", string rightTooltip = "")
        {
            Rect row = EditorGUILayout.GetControlRect(false, height);
            const float gap = 4f;
            float width = Mathf.Floor((row.width - gap * 2f) / 3f);
            Rect leftRect = new Rect(row.x, row.y, width, row.height);
            Rect centerRect = new Rect(row.x + width + gap, row.y, width, row.height);
            Rect rightRect = new Rect(row.x + (width + gap) * 2f, row.y, width, row.height);

            if (GUI.Button(leftRect, new GUIContent(leftLabel, leftTooltip)))
            {
                return 1;
            }

            if (GUI.Button(centerRect, new GUIContent(centerLabel, centerTooltip)))
            {
                return 2;
            }

            if (GUI.Button(rightRect, new GUIContent(rightLabel, rightTooltip)))
            {
                return 3;
            }

            return 0;
        }

        private static void DrawMaterialProperties(SerializedProperty propertiesProp, Material material)
        {
            for (int i = 0; i < propertiesProp.arraySize; i++)
            {
                SerializedProperty property = propertiesProp.GetArrayElementAtIndex(i);
                SerializedProperty propertyName = property.FindPropertyRelative("PropertyName");
                SerializedProperty valueType = property.FindPropertyRelative("ValueType");
                SerializedProperty useCurrent = property.FindPropertyRelative("UseCurrentValueAsDay");
                SerializedProperty useDusk = property.FindPropertyRelative("UseDuskValue");

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(propertyName.stringValue, "当前正在过渡的 Shader 属性名。"), EditorStyles.boldLabel);
                    if (GUILayout.Button(new GUIContent("Capture Day", "把材质球当前这个属性的值记录为 Day 值。适合用当前美术状态作为白天基准。"), GUILayout.Width(88f)))
                    {
                        CaptureMaterialDayValue(property, material);
                    }
                    if (GUILayout.Button(new GUIContent("X", "移除这个 Shader 属性过渡配置。"), GUILayout.Width(24f)))
                    {
                        propertiesProp.DeleteArrayElementAtIndex(i);
                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.EndVertical();
                        break;
                    }
                }

                EditorGUILayout.PropertyField(useCurrent, new GUIContent("Use Current As Day", "过渡开始时读取材质球当前值作为 Day 值，不使用下面手填的 Day。"));
                EditorGUILayout.PropertyField(useDusk, new GUIContent("Use Dusk", "开启后 Day -> Night 会先经过 Dusk 值；关闭则直接从 Day 过渡到 Night。"));

                int type = valueType.enumValueIndex;
                if (type == 0)
                {
                    if (!useCurrent.boolValue)
                    {
                        EditorGUILayout.PropertyField(property.FindPropertyRelative("DayFloat"), new GUIContent("Day", "白天状态的 Float 值。"));
                    }
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("DuskFloat"), new GUIContent("Dusk", "黄昏过渡中的 Float 值。"));
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("NightFloat"), new GUIContent("Night", "夜晚状态的 Float 值。"));
                }
                else if (type == 1)
                {
                    if (!useCurrent.boolValue)
                    {
                        EditorGUILayout.PropertyField(property.FindPropertyRelative("DayColor"), new GUIContent("Day", "白天状态的 Color 值。"));
                    }
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("DuskColor"), new GUIContent("Dusk", "黄昏过渡中的 Color 值。"));
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("NightColor"), new GUIContent("Night", "夜晚状态的 Color 值。"));
                }
                else
                {
                    if (!useCurrent.boolValue)
                    {
                        EditorGUILayout.PropertyField(property.FindPropertyRelative("DayVector"), new GUIContent("Day", "白天状态的 Vector 值。"));
                    }
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("DuskVector"), new GUIContent("Dusk", "黄昏过渡中的 Vector 值。"));
                    EditorGUILayout.PropertyField(property.FindPropertyRelative("NightVector"), new GUIContent("Night", "夜晚状态的 Vector 值。"));
                }

                EditorGUILayout.EndVertical();
            }
        }

        private static bool IsSupportedShaderProperty(ShaderUtil.ShaderPropertyType shaderType)
        {
            return shaderType == ShaderUtil.ShaderPropertyType.Color
                   || shaderType == ShaderUtil.ShaderPropertyType.Vector
                   || shaderType == ShaderUtil.ShaderPropertyType.Float
                   || shaderType == ShaderUtil.ShaderPropertyType.Range;
        }

        private static int ToRuntimeValueType(ShaderUtil.ShaderPropertyType shaderType)
        {
            if (shaderType == ShaderUtil.ShaderPropertyType.Color)
            {
                return 1;
            }

            if (shaderType == ShaderUtil.ShaderPropertyType.Vector)
            {
                return 2;
            }

            return 0;
        }

        private static void AddShaderProperty(SerializedProperty propertiesProp, Material material, string propertyName, ShaderUtil.ShaderPropertyType shaderType)
        {
            for (int i = 0; i < propertiesProp.arraySize; i++)
            {
                SerializedProperty existing = propertiesProp.GetArrayElementAtIndex(i);
                if (existing.FindPropertyRelative("PropertyName").stringValue == propertyName)
                {
                    return;
                }
            }

            int index = propertiesProp.arraySize;
            propertiesProp.InsertArrayElementAtIndex(index);
            SerializedProperty property = propertiesProp.GetArrayElementAtIndex(index);
            property.isExpanded = true;
            property.FindPropertyRelative("PropertyName").stringValue = propertyName;
            property.FindPropertyRelative("ValueType").enumValueIndex = ToRuntimeValueType(shaderType);
            property.FindPropertyRelative("UseCurrentValueAsDay").boolValue = true;
            property.FindPropertyRelative("UseDuskValue").boolValue = true;
            CaptureMaterialDayValue(property, material);
        }

        private static void CaptureMaterialDayValue(SerializedProperty property, Material material)
        {
            if (material == null)
            {
                return;
            }

            string propertyName = property.FindPropertyRelative("PropertyName").stringValue;
            if (string.IsNullOrEmpty(propertyName) || !material.HasProperty(propertyName))
            {
                return;
            }

            property.FindPropertyRelative("UseCurrentValueAsDay").boolValue = false;
            int type = property.FindPropertyRelative("ValueType").enumValueIndex;
            if (type == 0)
            {
                property.FindPropertyRelative("DayFloat").floatValue = material.GetFloat(propertyName);
            }
            else if (type == 1)
            {
                property.FindPropertyRelative("DayColor").colorValue = material.GetColor(propertyName);
            }
            else
            {
                property.FindPropertyRelative("DayVector").vector4Value = material.GetVector(propertyName);
            }
        }

        private static int DrawMaterialListFooter()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("-", "清理空材质槽，或没有配置属性的材质槽"), GUILayout.Width(28f), GUILayout.Height(22f)))
                {
                    return 1;
                }

                if (GUILayout.Button(new GUIContent("+", "有选中材质时添加选中材质；没有选中材质时添加空槽"), GUILayout.Width(28f), GUILayout.Height(22f)))
                {
                    return 2;
                }
            }

            return 0;
        }

        private void AddSelectedMaterialsOrSlot(Component controller)
        {
            HashSet<Material> materials = CollectSelectedMaterials();
            if (materials.Count == 0)
            {
                AddMaterialSlotToController(controller);
                return;
            }

            foreach (Material material in materials)
            {
                AddMaterialToController(controller, material, false);
            }
        }

        private HashSet<Material> CollectSelectedMaterials()
        {
            HashSet<Material> materials = new HashSet<Material>();
            foreach (UnityEngine.Object selected in Selection.objects)
            {
                if (selected == null)
                {
                    continue;
                }

                if (selected is Material selectedMaterial)
                {
                    materials.Add(selectedMaterial);
                    continue;
                }

                if (selected is Renderer selectedRenderer)
                {
                    AddRendererMaterials(materials, selectedRenderer);
                    continue;
                }

                if (selected is GameObject selectedGameObject)
                {
                    Renderer[] renderers = selectedGameObject.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        AddRendererMaterials(materials, renderers[i]);
                    }
                }
            }

            return materials;
        }

        private static void AddRendererMaterials(HashSet<Material> materials, Renderer renderer)
        {
            if (renderer == null || renderer.sharedMaterials == null)
            {
                return;
            }

            foreach (Material material in renderer.sharedMaterials)
            {
                if (material != null)
                {
                    materials.Add(material);
                }
            }
        }

        private static void AddMaterialSlotToController(Component controller)
        {
            if (controller == null)
            {
                return;
            }

            SerializedObject serializedController = new SerializedObject(controller);
            serializedController.Update();
            SerializedProperty transitions = serializedController.FindProperty("MaterialTransitions");
            if (transitions == null)
            {
                EditorUtility.DisplayDialog("Day Change Transition", "当前运行时组件没有 MaterialTransitions 字段，请确认脚本已更新并编译通过。", "OK");
                return;
            }

            Undo.RecordObject(controller, "Add Day Change Material Slot");
            int index = transitions.arraySize;
            transitions.InsertArrayElementAtIndex(index);
            SerializedProperty transition = transitions.GetArrayElementAtIndex(index);
            transition.isExpanded = true;
            transition.FindPropertyRelative("Material").objectReferenceValue = null;
            transition.FindPropertyRelative("Properties").arraySize = 0;
            serializedController.ApplyModifiedProperties();
            EditorUtility.SetDirty(controller);
        }

        private bool AddMaterialToController(Component controller, Material material, bool showMessage)
        {
            if (controller == null || material == null)
            {
                return false;
            }

            SerializedObject serializedController = new SerializedObject(controller);
            serializedController.Update();
            SerializedProperty transitions = serializedController.FindProperty("MaterialTransitions");
            if (transitions == null)
            {
                if (showMessage)
                {
                    EditorUtility.DisplayDialog("Day Change Transition", "当前运行时组件没有 MaterialTransitions 字段，请确认脚本已更新并编译通过。", "OK");
                }
                return false;
            }

            for (int i = 0; i < transitions.arraySize; i++)
            {
                SerializedProperty existing = transitions.GetArrayElementAtIndex(i);
                if (existing.FindPropertyRelative("Material").objectReferenceValue == material)
                {
                    if (showMessage)
                    {
                        EditorUtility.DisplayDialog("Day Change Transition", "这个材质球已经在 Material Transitions 里了。", "OK");
                    }
                    return false;
                }
            }

            Undo.RecordObject(controller, "Add Day Change Material Transition");
            int index = transitions.arraySize;
            transitions.InsertArrayElementAtIndex(index);
            SerializedProperty transition = transitions.GetArrayElementAtIndex(index);
            transition.isExpanded = true;
            transition.FindPropertyRelative("Material").objectReferenceValue = material;
            transition.FindPropertyRelative("Properties").arraySize = 0;
            serializedController.ApplyModifiedProperties();
            EditorUtility.SetDirty(controller);
            return true;
        }

        private static void RemoveEmptyMaterialTransitions(Component controller)
        {
            if (controller == null)
            {
                return;
            }

            SerializedObject serializedController = new SerializedObject(controller);
            SerializedProperty transitions = serializedController.FindProperty("MaterialTransitions");
            if (transitions == null)
            {
                return;
            }

            Undo.RecordObject(controller, "Remove Empty Day Change Material Transitions");
            for (int i = transitions.arraySize - 1; i >= 0; i--)
            {
                SerializedProperty transition = transitions.GetArrayElementAtIndex(i);
                SerializedProperty material = transition.FindPropertyRelative("Material");
                SerializedProperty properties = transition.FindPropertyRelative("Properties");
                if (material.objectReferenceValue == null || properties.arraySize == 0)
                {
                    transitions.DeleteArrayElementAtIndex(i);
                }
            }

            serializedController.ApplyModifiedProperties();
            EditorUtility.SetDirty(controller);
        }

        private static void BeginSection(string title)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.Space(2f);
        }

        private static void EndSection()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(6f);
        }

        private void CreateOrRepairSetup(bool selectController)
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            SavePrefs();

            if (_mainLight == null)
            {
                _mainLight = FindMainLight();
            }

            EnsureAssetFolder(_saveFolder);
            VolumeProfile duskProfile = GetOrCreateDuskProfile();
            VolumeProfile nightProfile = GetOrCreateNightProfile();
            VolumeProfile baseDayProfile = _baseDayProfile != null ? _baseDayProfile : FindBaseDayProfile();
            CopyMissingProfileComponents(baseDayProfile, duskProfile);
            CopyMissingProfileComponents(baseDayProfile, nightProfile);
            ConfigureDuskProfile(duskProfile);
            ConfigureNightProfile(nightProfile);

            Volume duskVolume = GetOrCreateVolume(_duskVolumeName, "DayChange_DuskVolume", duskProfile, 45f);
            Volume nightVolume = GetOrCreateVolume(_nightVolumeName, "DayChange_NightVolume", nightProfile, 50f);
            Component controller = GetOrCreateController();

            Undo.RecordObject(controller, "Configure Day Change Controller");
            bool wasNewDefault = GetFieldValue(controller, "MainLight") == null && GetFieldValue(controller, "NightVolume") == null;
            SetFieldValue(controller, "MainLight", _mainLight);
            SetFieldValue(controller, "DuskVolume", duskVolume);
            SetFieldValue(controller, "NightVolume", nightVolume);
            SetFieldValue(controller, "UseDuskVolume", _useDuskVolume);
            SetFieldValue(controller, "FadeToNightSeconds", Mathf.Max(0f, _fadeToNightSeconds));
            SetFieldValue(controller, "NightHoldSeconds", _nightHoldSeconds);
            SetFieldValue(controller, "FadeToDaySeconds", Mathf.Max(0f, _fadeToDaySeconds));
            SetFieldValue(controller, "UseFullCycleLightRotation", true);
            SetFieldValue(controller, "FullCycleEulerOffset", new Vector3(360f, 0f, 0f));
            SetFieldValue(controller, "FullCycleWorldAxis", Vector3.up);
            SetFieldValue(controller, "BakedShadowObjects", CollectBakedShadowObjects());
            SetFieldValue(controller, "RealtimeShadowRenderers", CollectRealtimeShadowRenderers());

            if (wasNewDefault && _mainLight != null)
            {
                SetLightLook(controller, "NightLight", Vector3.zero, new Color(0.62f, 0.72f, 1f, 1f), Mathf.Max(0f, _mainLight.intensity * 0.45f));
            }

            InvokeControllerMethod(controller, "PreviewDay");
            EditorUtility.SetDirty(controller);
            EditorUtility.SetDirty(duskVolume);
            EditorUtility.SetDirty(nightVolume);
            EditorUtility.SetDirty(duskProfile);
            EditorUtility.SetDirty(nightProfile);
            ZXXToolUsageStats.Complete("Day Change Transition", 1, usageTimer);
            AssetDatabase.SaveAssets();

            if (selectController)
            {
                Selection.activeObject = controller.gameObject;
            }
        }

        private VolumeProfile GetOrCreateNightProfile()
        {
            if (_nightProfile != null)
            {
                return _nightProfile;
            }

            string path = GetNightProfilePath();
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile != null)
            {
                _nightProfile = profile;
                return profile;
            }

            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            _nightProfile = profile;
            return profile;
        }

        private VolumeProfile GetOrCreateDuskProfile()
        {
            if (_duskProfile != null)
            {
                return _duskProfile;
            }

            string path = GetDuskProfilePath();
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile != null)
            {
                _duskProfile = profile;
                return profile;
            }

            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            _duskProfile = profile;
            return profile;
        }

        private VolumeProfile LoadNightProfile()
        {
            if (_nightProfile != null)
            {
                return _nightProfile;
            }
            return AssetDatabase.LoadAssetAtPath<VolumeProfile>(GetNightProfilePath());
        }

        private VolumeProfile LoadDuskProfile()
        {
            if (_duskProfile != null)
            {
                return _duskProfile;
            }
            return AssetDatabase.LoadAssetAtPath<VolumeProfile>(GetDuskProfilePath());
        }

        private string GetNightProfilePath()
        {
            if (_nightProfile != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(_nightProfile);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    return assetPath;
                }
            }

            string folder = string.IsNullOrWhiteSpace(_saveFolder) ? "Assets" : _saveFolder.Trim().Replace('\\', '/');
            string profileName = string.IsNullOrWhiteSpace(_nightProfileName) ? "DayChange_NightVolumeProfile" : _nightProfileName.Trim();
            if (!profileName.EndsWith(".asset"))
            {
                profileName += ".asset";
            }
            return folder.TrimEnd('/') + "/" + profileName;
        }

        private string GetDuskProfilePath()
        {
            if (_duskProfile != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(_duskProfile);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    return assetPath;
                }
            }

            string folder = string.IsNullOrWhiteSpace(_saveFolder) ? "Assets" : _saveFolder.Trim().Replace('\\', '/');
            string profileName = string.IsNullOrWhiteSpace(_duskProfileName) ? "DayChange_DuskVolumeProfile" : _duskProfileName.Trim();
            if (!profileName.EndsWith(".asset"))
            {
                profileName += ".asset";
            }
            return folder.TrimEnd('/') + "/" + profileName;
        }

        private static void ConfigureDuskProfile(VolumeProfile profile)
        {
            ColorAdjustments colorAdjustments = GetOrAdd<ColorAdjustments>(profile, out bool addedColorAdjustments);
            if (addedColorAdjustments)
            {
                colorAdjustments.SetAllOverridesTo(true);
                colorAdjustments.postExposure.value = 0.08f;
                colorAdjustments.colorFilter.value = new Color(1f, 0.72f, 0.42f, 1f);
                colorAdjustments.saturation.value = 6f;
                colorAdjustments.contrast.value = 5f;
            }
        }

        private static void CopyMissingProfileComponents(VolumeProfile source, VolumeProfile target)
        {
            if (source == null || target == null || source == target)
            {
                return;
            }

            foreach (VolumeComponent sourceComponent in source.components)
            {
                if (sourceComponent == null)
                {
                    continue;
                }

                Type componentType = sourceComponent.GetType();
                if (componentType == typeof(ColorAdjustments) || HasComponent(target, componentType))
                {
                    continue;
                }

                VolumeComponent targetComponent = target.Add(componentType, true);
                EditorUtility.CopySerialized(sourceComponent, targetComponent);
            }
        }

        private static bool HasComponent(VolumeProfile profile, Type componentType)
        {
            foreach (VolumeComponent component in profile.components)
            {
                if (component != null && component.GetType() == componentType)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ConfigureNightProfile(VolumeProfile profile)
        {
            ColorAdjustments colorAdjustments = GetOrAdd<ColorAdjustments>(profile, out bool addedColorAdjustments);
            if (addedColorAdjustments)
            {
                colorAdjustments.SetAllOverridesTo(true);
                colorAdjustments.postExposure.value = -0.65f;
                colorAdjustments.colorFilter.value = new Color(0.72f, 0.80f, 1f, 1f);
                colorAdjustments.saturation.value = -15f;
                colorAdjustments.contrast.value = 8f;
            }

            // Keep the generated transition profile minimal. Extra color grading components can
            // be added manually later, but Color Adjustments is the safest default for smooth blends.
        }

        private static T GetOrAdd<T>(VolumeProfile profile, out bool added) where T : VolumeComponent
        {
            if (!profile.TryGet(out T component))
            {
                component = profile.Add<T>(true);
                added = true;
                return component;
            }

            added = false;
            return component;
        }

        private Volume GetOrCreateVolume(string configuredName, string fallbackName, VolumeProfile profile, float priority)
        {
            string volumeName = string.IsNullOrWhiteSpace(configuredName) ? fallbackName : configuredName.Trim();
            GameObject go = GameObject.Find(volumeName);
            if (go == null)
            {
                go = new GameObject(volumeName);
                Undo.RegisterCreatedObjectUndo(go, "Create Day Change Volume");
            }

            Volume volume = go.GetComponent<Volume>();
            if (volume == null)
            {
                volume = Undo.AddComponent<Volume>(go);
            }

            volume.isGlobal = true;
            volume.priority = priority;
            volume.weight = 0f;
            volume.sharedProfile = profile;
            return volume;
        }

        private Component GetOrCreateController()
        {
            Type controllerType = GetControllerType();
            if (controllerType == null)
            {
                throw new InvalidOperationException("没有找到 ArtCode.DayChangeTransitionController。请先确认 Unity 已经编译运行时脚本，然后再重试。");
            }

            GameObject go = GameObject.Find(string.IsNullOrWhiteSpace(_controllerName) ? "DayChangeTransitionController" : _controllerName.Trim());
            if (go == null)
            {
                go = new GameObject(string.IsNullOrWhiteSpace(_controllerName) ? "DayChangeTransitionController" : _controllerName.Trim());
                Undo.RegisterCreatedObjectUndo(go, "Create Day Change Controller");
            }

            Component controller = go.GetComponent(controllerType);
            if (controller == null)
            {
                controller = Undo.AddComponent(go, controllerType);
            }

            return controller;
        }

        private Component GetController(bool logIfMissing)
        {
            Type controllerType = GetControllerType();
            Component controller = controllerType == null ? null : FindObjectOfType(controllerType) as Component;
            if (controller == null && logIfMissing)
            {
                Debug.LogWarning("[DayChangeTransitionTool] No DayChangeTransitionController found. Click Create / Repair Setup first.");
            }
            return controller;
        }

        private static Type GetControllerType()
        {
            return Type.GetType("ArtCode.DayChangeTransitionController, ArtCode")
                   ?? Type.GetType("ArtCode.DayChangeTransitionController, Assembly-CSharp");
        }

        private void SyncFromSceneComponent(bool force = false)
        {
            if (!force && !_autoSyncSceneComponent)
            {
                return;
            }

            Component controller = GetController(false);
            if (controller == null)
            {
                return;
            }

            _loadingFromSceneComponent = true;

            object mainLight = GetFieldValue(controller, "MainLight");
            if (mainLight is Light light)
            {
                _mainLight = light;
            }

            object duskVolume = GetFieldValue(controller, "DuskVolume");
            if (duskVolume is Volume dusk)
            {
                _duskVolumeName = dusk.gameObject.name;
                if (dusk.sharedProfile != null)
                {
                    _duskProfile = dusk.sharedProfile;
                    ApplyProfileAssetToFields(dusk.sharedProfile, true);
                }
            }

            object nightVolume = GetFieldValue(controller, "NightVolume");
            if (nightVolume is Volume night)
            {
                _nightVolumeName = night.gameObject.name;
                if (night.sharedProfile != null)
                {
                    _nightProfile = night.sharedProfile;
                    ApplyProfileAssetToFields(night.sharedProfile, false);
                }
            }

            _fadeToNightSeconds = GetFloatFieldValue(controller, "FadeToNightSeconds", _fadeToNightSeconds);
            _nightHoldSeconds = GetFloatFieldValue(controller, "NightHoldSeconds", _nightHoldSeconds);
            _fadeToDaySeconds = GetFloatFieldValue(controller, "FadeToDaySeconds", _fadeToDaySeconds);
            _useDuskVolume = GetBoolFieldValue(controller, "UseDuskVolume", _useDuskVolume);

            _loadingFromSceneComponent = false;
            SavePrefs();
        }

        private void ApplySettingsToSceneComponent(bool logIfMissing = false)
        {
            if (!_autoSyncSceneComponent && !logIfMissing)
            {
                return;
            }

            Component controller = GetController(logIfMissing);
            if (controller == null)
            {
                return;
            }

            Undo.RecordObject(controller, "Apply Day Change Tool Settings");
            SetFieldValue(controller, "MainLight", _mainLight);
            SetFieldValue(controller, "UseDuskVolume", _useDuskVolume);
            SetFieldValue(controller, "FadeToNightSeconds", Mathf.Max(0f, _fadeToNightSeconds));
            SetFieldValue(controller, "NightHoldSeconds", _nightHoldSeconds);
            SetFieldValue(controller, "FadeToDaySeconds", Mathf.Max(0f, _fadeToDaySeconds));

            Volume duskVolume = FindVolumeByName(_duskVolumeName);
            Volume nightVolume = FindVolumeByName(_nightVolumeName);
            if (duskVolume != null)
            {
                Undo.RecordObject(duskVolume, "Apply Day Change Dusk Profile");
                if (_duskProfile != null)
                {
                    duskVolume.sharedProfile = _duskProfile;
                }
                SetFieldValue(controller, "DuskVolume", duskVolume);
                EditorUtility.SetDirty(duskVolume);
            }

            if (nightVolume != null)
            {
                Undo.RecordObject(nightVolume, "Apply Day Change Night Profile");
                if (_nightProfile != null)
                {
                    nightVolume.sharedProfile = _nightProfile;
                }
                SetFieldValue(controller, "NightVolume", nightVolume);
                EditorUtility.SetDirty(nightVolume);
            }

            EditorUtility.SetDirty(controller);
        }

        private void ApplyProfileAssetToFields(VolumeProfile profile, bool isDusk)
        {
            string path = AssetDatabase.GetAssetPath(profile);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            string folder = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder))
            {
                _saveFolder = folder;
            }

            string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (isDusk)
            {
                _duskProfile = profile;
                _duskProfileName = fileName;
            }
            else
            {
                _nightProfile = profile;
                _nightProfileName = fileName;
            }
        }

        private static float GetFloatFieldValue(Component target, string fieldName, float fallback)
        {
            object value = GetFieldValue(target, fieldName);
            return value is float number ? number : fallback;
        }

        private static bool GetBoolFieldValue(Component target, string fieldName, bool fallback)
        {
            object value = GetFieldValue(target, fieldName);
            return value is bool flag ? flag : fallback;
        }

        private static Volume FindVolumeByName(string volumeName)
        {
            if (string.IsNullOrWhiteSpace(volumeName))
            {
                return null;
            }

            GameObject go = GameObject.Find(volumeName.Trim());
            return go != null ? go.GetComponent<Volume>() : null;
        }

        private static object GetFieldValue(Component target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null ? field.GetValue(target) : null;
        }

        private static void SetFieldValue(Component target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(target, value);
            }
        }

        private static void SetLightLook(Component target, string fieldName, Vector3 eulerAngles, Color color, float intensity)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                return;
            }

            object look = field.GetValue(target);
            Type lookType = field.FieldType;
            lookType.GetField("EulerAngles")?.SetValue(look, eulerAngles);
            lookType.GetField("Color")?.SetValue(look, color);
            lookType.GetField("Intensity")?.SetValue(look, intensity);
            field.SetValue(target, look);
        }

        private static void InvokeControllerMethod(Component target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(target, null);
        }

        private static void InvokeControllerMethod(Component target, string methodName, object argument)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(target, new[] { argument });
        }

        private void StartEditorPreview()
        {
            Component controller = GetController(false);
            if (controller == null)
            {
                CreateOrRepairSetup(false);
                controller = GetController(true);
            }

            if (controller == null)
            {
                return;
            }

            StopEditorPreview(false);
            _editorPreviewController = controller;
            _editorPreviewProgress = 0f;
            _editorPreviewPlaying = true;
            _editorPreviewStartTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += TickEditorPreview;
        }

        private void StopEditorPreview(bool restoreDay)
        {
            EditorApplication.update -= TickEditorPreview;
            _editorPreviewPlaying = false;
            if (restoreDay && _editorPreviewController != null)
            {
                InvokeControllerMethod(_editorPreviewController, "PreviewDay");
                EditorUtility.SetDirty(_editorPreviewController);
                SceneView.RepaintAll();
                _editorPreviewProgress = 0f;
            }

            _editorPreviewController = null;
        }

        private void PreviewDayState()
        {
            StopEditorPreview(false);
            Component controller = GetController(false);
            if (controller == null)
            {
                return;
            }

            Undo.RecordObject(controller, "Preview Day Change Day");
            InvokeControllerMethod(controller, "PreviewDay");
            EditorUtility.SetDirty(controller);
            SceneView.RepaintAll();
            _editorPreviewController = null;
            _editorPreviewProgress = 0f;
        }

        private void ApplyEditorPreviewProgress(float progress)
        {
            Component controller = GetController(false);
            if (controller == null)
            {
                CreateOrRepairSetup(false);
                controller = GetController(true);
            }

            if (controller == null)
            {
                return;
            }

            StopEditorPreview(false);
            _editorPreviewController = controller;
            _editorPreviewProgress = Mathf.Clamp01(progress);
            Undo.RecordObject(controller, "Preview Day Change Progress");
            InvokeControllerMethod(controller, "SetTransitionProgress", _editorPreviewProgress);
            EditorUtility.SetDirty(controller);
            SceneView.RepaintAll();
        }

        private void TickEditorPreview()
        {
            if (_editorPreviewController == null)
            {
                StopEditorPreview(false);
                return;
            }

            double elapsed = EditorApplication.timeSinceStartup - _editorPreviewStartTime;
            float hold = Mathf.Max(0f, _nightHoldSeconds);
            float fadeToNight = Mathf.Max(0.0001f, _fadeToNightSeconds);
            float fadeToDay = Mathf.Max(0.0001f, _fadeToDaySeconds);
            float total = fadeToNight + hold + fadeToDay;
            float progress;

            if (elapsed <= fadeToNight)
            {
                progress = EvaluateControllerCurve(_editorPreviewController, (float)(elapsed / fadeToNight)) * 0.5f;
            }
            else if (elapsed <= fadeToNight + hold)
            {
                progress = 0.5f;
            }
            else if (elapsed <= total)
            {
                float t = (float)((elapsed - fadeToNight - hold) / fadeToDay);
                progress = 0.5f + EvaluateControllerCurve(_editorPreviewController, t) * 0.5f;
            }
            else
            {
                StopEditorPreview(true);
                return;
            }

            _editorPreviewProgress = progress;
            InvokeControllerMethod(_editorPreviewController, "SetTransitionProgress", progress);
            EditorUtility.SetDirty(_editorPreviewController);
            SceneView.RepaintAll();
            Repaint();
        }

        private string GetPreviewProgressLabel(float progress)
        {
            progress = Mathf.Clamp01(progress);
            if (progress < 0.01f || progress > 0.99f)
            {
                return "Preview: Day";
            }

            if (progress < 0.5f)
            {
                return _useDuskVolume ? "Preview: Day -> Dusk -> Night" : "Preview: Day -> Night";
            }

            if (Mathf.Abs(progress - 0.5f) < 0.01f)
            {
                return "Preview: Night";
            }

            return "Preview: Night -> Day";
        }

        private static float EvaluateControllerCurve(Component controller, float t)
        {
            AnimationCurve curve = GetFieldValue(controller, "TransitionCurve") as AnimationCurve;
            return curve != null ? curve.Evaluate(Mathf.Clamp01(t)) : Mathf.Clamp01(t);
        }

        private static GameObject[] CollectBakedShadowObjects()
        {
            List<GameObject> results = new List<GameObject>();
            foreach (Renderer renderer in FindObjectsOfType<Renderer>(true))
            {
                if (IsBakedShadowRenderer(renderer) && !results.Contains(renderer.gameObject))
                {
                    results.Add(renderer.gameObject);
                }
            }

            return results.ToArray();
        }

        private static Renderer[] CollectRealtimeShadowRenderers()
        {
            List<Renderer> results = new List<Renderer>();
            foreach (Renderer renderer in FindObjectsOfType<Renderer>(true))
            {
                if (renderer == null || IsBakedShadowRenderer(renderer))
                {
                    continue;
                }

                results.Add(renderer);
            }

            return results.ToArray();
        }

        private static bool IsBakedShadowRenderer(Renderer renderer)
        {
            if (renderer == null)
            {
                return false;
            }

            if (renderer.gameObject.name.EndsWith("_ShadowDecal", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (Material material in renderer.sharedMaterials)
            {
                if (material != null && material.shader != null && material.shader.name == "Custom/ShadowDecal_Gradient_URP")
                {
                    return true;
                }
            }

            return false;
        }

        private static Light FindMainLight()
        {
            Light best = null;
            float bestIntensity = float.MinValue;
            foreach (Light light in FindObjectsOfType<Light>())
            {
                if (light == null || light.type != LightType.Directional || !light.enabled)
                {
                    continue;
                }

                if (light.intensity > bestIntensity)
                {
                    best = light;
                    bestIntensity = light.intensity;
                }
            }
            return best;
        }

        private VolumeProfile FindBaseDayProfile()
        {
            Volume best = null;
            float bestPriority = float.MinValue;
            foreach (Volume volume in FindObjectsOfType<Volume>())
            {
                if (volume == null || !volume.isGlobal || volume.sharedProfile == null)
                {
                    continue;
                }

                if (volume.name == _duskVolumeName || volume.name == _nightVolumeName)
                {
                    continue;
                }

                if (volume.priority >= bestPriority)
                {
                    best = volume;
                    bestPriority = volume.priority;
                }
            }

            return best != null ? best.sharedProfile : null;
        }

        private static void EnsureAssetFolder(string folder)
        {
            folder = string.IsNullOrWhiteSpace(folder) ? "Assets" : folder.Trim().Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private void SavePrefs()
        {
            EditorPrefs.SetString(PrefPrefix + "SaveFolder", _saveFolder);
            EditorPrefs.SetString(PrefPrefix + "ControllerName", _controllerName);
            EditorPrefs.SetString(PrefPrefix + "DuskVolumeName", _duskVolumeName);
            EditorPrefs.SetString(PrefPrefix + "NightVolumeName", _nightVolumeName);
            EditorPrefs.SetString(PrefPrefix + "DuskProfileName", _duskProfileName);
            EditorPrefs.SetString(PrefPrefix + "NightProfileName", _nightProfileName);
            EditorPrefs.SetString(PrefPrefix + "BaseDayProfilePath", _baseDayProfile == null ? "" : AssetDatabase.GetAssetPath(_baseDayProfile));
            EditorPrefs.SetString(PrefPrefix + "DuskProfilePath", _duskProfile == null ? "" : AssetDatabase.GetAssetPath(_duskProfile));
            EditorPrefs.SetString(PrefPrefix + "NightProfilePath", _nightProfile == null ? "" : AssetDatabase.GetAssetPath(_nightProfile));
            EditorPrefs.SetBool(PrefPrefix + "UseDuskVolume", _useDuskVolume);
            EditorPrefs.SetFloat(PrefPrefix + "FadeToNightSeconds", _fadeToNightSeconds);
            EditorPrefs.SetFloat(PrefPrefix + "NightHoldSeconds", _nightHoldSeconds);
            EditorPrefs.SetFloat(PrefPrefix + "FadeToDaySeconds", _fadeToDaySeconds);
            EditorPrefs.SetBool(PrefPrefix + "AutoSyncSceneComponent", _autoSyncSceneComponent);
        }
    }
}

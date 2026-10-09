# 源码位置索引

2026-10-09：按 X26 项目的全量代码 Git 历史、重命名记录及现有工具仓库整理。

只同步源码和说明，不同步 .meta、模型、贴图、材质、场景、Prefab 或生成配置。

仓库按现有工具目录、Runtime 和 Shaders 结构保存。表中的原项目路径相对于 Assets；复制回原路径可保留工具的项目路径约定。

| 仓库路径 | 原项目路径 | 依据 |
|---|---|---|
| Shaders/BakedLightMap_PlantWind.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/BakedLightMap_PlantWind.shader | 本人添加；已追踪移动历史 |
| Shaders/BakedLightMap.shadergraph | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/BakedLightMap.shadergraph | 本人添加；已追踪移动历史 |
| Shaders/BuildingCard_FogDepth_URP.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/BuildingCard_FogDepth_URP.shader | 本人添加；已追踪移动历史 |
| Shaders/BuildingCard_URP.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/BuildingCard_URP.shader | 本人添加；已追踪移动历史 |
| Shaders/Character Color Adjustment.shadergraph | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/Character Color Adjustment.shadergraph | 本人添加；已追踪移动历史 |
| Shaders/Editor/LitTransparentDepthShaderGUI.cs | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/Editor/LitTransparentDepthShaderGUI.cs | 本人添加；已追踪移动历史 |
| Shaders/LitTransparentDepth.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/LitTransparentDepth.shader | 本人添加；已追踪移动历史 |
| Shaders/LitTransparentDepthForwardPass.hlsl | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/LitTransparentDepthForwardPass.hlsl | 本人添加；已追踪移动历史 |
| Shaders/LitTransparentDepthInput.hlsl | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/LitTransparentDepthInput.hlsl | 本人添加；已追踪移动历史 |
| Shaders/ShadowDecal_Gradient_URP.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/ShadowDecal_Gradient_URP.shader | 本人添加；已追踪移动历史 |
| Shaders/SimpleLit_BottomFade_Shadow.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/SimpleLit_BottomFade_Shadow.shader | 本人修改维护的 Shader |
| Shaders/SimpleLit_BottomFade_Wave.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/SimpleLit_BottomFade_Wave.shader | 本人添加；已追踪移动历史 |
| Shaders/SimpleLit_BottomFade.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/SimpleLit_BottomFade.shader | 本人修改维护的 Shader |
| Shaders/SimpleTopBlend.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/SimpleTopBlend.shader | 本人添加；已追踪移动历史 |
| Shaders/SLG_TerrainBlend_Lite.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/SLG_TerrainBlend_Lite.shader | 本人添加；已追踪移动历史 |
| Shaders/SLG_TerrainBlend_Mobile_Transparent.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/SLG_TerrainBlend_Mobile_Transparent.shader | 本人添加；已追踪移动历史 |
| Shaders/SLG_TerrainBlend_Mobile.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/SLG_TerrainBlend_Mobile.shader | 本人添加；已追踪移动历史 |
| Shaders/StylizedRiver.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/StylizedRiver.shader | 本人添加；已追踪移动历史 |
| Shaders/StylizedRiverIntersection.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/StylizedRiverIntersection.shader | 本人添加；已追踪移动历史 |
| Shaders/StylizedWaterfall.shader | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/StylizedWaterfall.shader | 本人添加；已追踪移动历史 |
| Shaders/TerrainBlender.shadergraph | ArtRes/Art_Scene/Art_MainScene/Scene_City/Shader/TerrainBlender.shadergraph | 本人添加；已追踪移动历史 |
| Shaders/Editor/WallWorldVariationShaderGUI.cs | ArtRes/Art_Shader/Build/Editor/WallWorldVariationShaderGUI.cs | 本人添加；已追踪移动历史 |
| Shaders/WallWorldVariation_URP.shader | ArtRes/Art_Shader/Build/WallWorldVariation_URP.shader | 本人添加；已追踪移动历史 |
| Shaders/MiniGameDeathFade.hlsl | ArtRes/Art_Shader/Hero/URP_Outline/MiniGameDeathFade.hlsl | 必要 include 配套代码 |
| Shaders/ToonOutline_URP_Packed.shader | ArtRes/Art_Shader/Hero/URP_Outline/ToonOutline_URP_Packed.shader | 本人添加；已追踪移动历史 |
| Shaders/ToonOutline_URP.shader | ArtRes/Art_Shader/Hero/URP_Outline/ToonOutline_URP.shader | 本人添加；已追踪移动历史 |
| BatchRenameTool/BatchRenameTool.cs | Scripts/Editor/ArtTools/ZXXTools/BatchRenameTool/BatchRenameTool.cs | 本人添加；已追踪移动历史 |
| BoxColliderGenerator/BoxColliderGenerator.cs | Scripts/Editor/ArtTools/ZXXTools/BoxColliderGenerator/BoxColliderGenerator.cs | 本人添加；已追踪移动历史 |
| BuildingCardTool/BuildingCardAtlasSupport.cs | Scripts/Editor/ArtTools/ZXXTools/BuildingCardTool/BuildingCardAtlasSupport.cs | 本人添加；已追踪移动历史 |
| BuildingCardTool/BuildingCardTool.cs | Scripts/Editor/ArtTools/ZXXTools/BuildingCardTool/BuildingCardTool.cs | 本人添加；已追踪移动历史 |
| CardGroundBaker/CardGroundBakerWindow.cs | Scripts/Editor/ArtTools/ZXXTools/CardGroundBaker/CardGroundBakerWindow.cs | 本人添加；已追踪移动历史 |
| ConvertVariantToNormalPrefab/ConvertVariantToNormalPrefab.cs | Scripts/Editor/ArtTools/ZXXTools/ConvertVariantToNormalPrefab/ConvertVariantToNormalPrefab.cs | 本人添加；已追踪移动历史 |
| DayChangeTransitionTool/DayChangeTransitionToolWindow.cs | Scripts/Editor/ArtTools/ZXXTools/DayChangeTransitionTool/DayChangeTransitionToolWindow.cs | 本人添加；已追踪移动历史 |
| FBXMeshEditor/FBXMeshEditorController.cs | Scripts/Editor/ArtTools/ZXXTools/FBXMeshEditor/FBXMeshEditorController.cs | 本人添加；已追踪移动历史 |
| FBXMeshEditor/FBXMeshEditorWindow.cs | Scripts/Editor/ArtTools/ZXXTools/FBXMeshEditor/FBXMeshEditorWindow.cs | 本人添加；已追踪移动历史 |
| FBXMeshEditor/FBXMeshEditRecord.cs | Scripts/Editor/ArtTools/ZXXTools/FBXMeshEditor/FBXMeshEditRecord.cs | 本人添加；已追踪移动历史 |
| FBXMeshEditor/FBXMeshEditSession.cs | Scripts/Editor/ArtTools/ZXXTools/FBXMeshEditor/FBXMeshEditSession.cs | 本人添加；已追踪移动历史 |
| FBXMeshEditor/FBXMeshExporter.cs | Scripts/Editor/ArtTools/ZXXTools/FBXMeshEditor/FBXMeshExporter.cs | 本人添加；已追踪移动历史 |
| FBXMeshEditor/FBXMeshSceneToolbarOverlay.cs | Scripts/Editor/ArtTools/ZXXTools/FBXMeshEditor/FBXMeshSceneToolbarOverlay.cs | 本人添加；已追踪移动历史 |
| FBXMeshEditor/FBXMeshTopology.cs | Scripts/Editor/ArtTools/ZXXTools/FBXMeshEditor/FBXMeshTopology.cs | 本人添加；已追踪移动历史 |
| FogOfWarTool/FogGridDebugOverlay.shader | Scripts/Editor/ArtTools/ZXXTools/FogOfWarTool/FogGridDebugOverlay.shader | 本人添加；已追踪移动历史 |
| FogOfWarTool/FogOfWarToolWindow.cs | Scripts/Editor/ArtTools/ZXXTools/FogOfWarTool/FogOfWarToolWindow.cs | 本人添加；已追踪移动历史 |
| GitPathNavigator/GitPathNavigator.cs | Scripts/Editor/ArtTools/ZXXTools/GitPathNavigator/GitPathNavigator.cs | 本人添加；已追踪移动历史 |
| LabelTool/LabelBillboard.cs | Scripts/Editor/ArtTools/ZXXTools/LabelTool/LabelBillboard.cs | 本人添加；已追踪移动历史 |
| LabelTool/LabelManager.cs | Scripts/Editor/ArtTools/ZXXTools/LabelTool/LabelManager.cs | 本人添加；已追踪移动历史 |
| LabelTool/LabelManagerEditor.cs | Scripts/Editor/ArtTools/ZXXTools/LabelTool/LabelManagerEditor.cs | 本人添加；已追踪移动历史 |
| LabelTool/LabelStyle.cs | Scripts/Editor/ArtTools/ZXXTools/LabelTool/LabelStyle.cs | 本人添加；已追踪移动历史 |
| MeasureTool/MeasureTool.cs | Scripts/Editor/ArtTools/ZXXTools/MeasureTool/MeasureTool.cs | 本人添加；已追踪移动历史 |
| OutlineTool/OutlineToolWindow.cs | Scripts/Editor/ArtTools/ZXXTools/OutlineTool/OutlineToolWindow.cs | 本人添加；已追踪移动历史 |
| OutlineTool/ToonMaterialConverterWindow.cs | Scripts/Editor/ArtTools/ZXXTools/OutlineTool/ToonMaterialConverterWindow.cs | 本人添加；已追踪移动历史 |
| OutlineTool/ToonTextureConversion.shader | Scripts/Editor/ArtTools/ZXXTools/OutlineTool/ToonTextureConversion.shader | 本人添加；已追踪移动历史 |
| PathBuilder/PathBuilderWindow.cs | Scripts/Editor/ArtTools/ZXXTools/PathBuilder/PathBuilderWindow.cs | 本人添加；已追踪移动历史 |
| PathBuilder/PathSpline/MountainDecalSet.cs | Scripts/Editor/ArtTools/ZXXTools/PathBuilder/PathSpline/MountainDecalSet.cs | 本人添加；已追踪移动历史 |
| PathBuilder/PathSpline/PathBuilderAuthoringStorage.cs | Scripts/Editor/ArtTools/ZXXTools/PathBuilder/PathSpline/PathBuilderAuthoringStorage.cs | 本人添加；已追踪移动历史 |
| PathBuilder/PathSpline/PathBuilderGenerator.cs | Scripts/Editor/ArtTools/ZXXTools/PathBuilder/PathSpline/PathBuilderGenerator.cs | 本人添加；已追踪移动历史 |
| PathBuilder/PathSpline/PathBuilderSplineUI.cs | Scripts/Editor/ArtTools/ZXXTools/PathBuilder/PathSpline/PathBuilderSplineUI.cs | 本人添加；已追踪移动历史 |
| PathBuilder/PathSpline/PathSpline.cs | Scripts/Editor/ArtTools/ZXXTools/PathBuilder/PathSpline/PathSpline.cs | 本人添加；已追踪移动历史 |
| PathBuilder/PathSpline/PathSplineEditor.cs | Scripts/Editor/ArtTools/ZXXTools/PathBuilder/PathSpline/PathSplineEditor.cs | 本人添加；已追踪移动历史 |
| PrefabBatchReplacer/PrefabBatchReplacer.cs | Scripts/Editor/ArtTools/ZXXTools/PrefabBatchReplacer/PrefabBatchReplacer.cs | 本人添加；已追踪移动历史 |
| PrefabExtractorTool/PrefabExtractorTool.cs | Scripts/Editor/ArtTools/ZXXTools/PrefabExtractorTool/PrefabExtractorTool.cs | 本人添加；已追踪移动历史 |
| PrefabNameSyncTool/PrefabNameSyncTool.cs | Scripts/Editor/ArtTools/ZXXTools/PrefabNameSyncTool/PrefabNameSyncTool.cs | 本人添加；已追踪移动历史 |
| PrefabRootNormalizer/PrefabRootNormalizer.cs | Scripts/Editor/ArtTools/ZXXTools/PrefabRootNormalizer/PrefabRootNormalizer.cs | 本人添加；已追踪移动历史 |
| ProcGen/ProcGenDebugManager.cs | Scripts/Editor/ArtTools/ZXXTools/ProcGen/ProcGenDebugManager.cs | 本人添加；已追踪移动历史 |
| ProcGen/ProcGenerator.cs | Scripts/Editor/ArtTools/ZXXTools/ProcGen/ProcGenerator.cs | 本人添加；已追踪移动历史 |
| ProcGen/ProcGenSettings.cs | Scripts/Editor/ArtTools/ZXXTools/ProcGen/ProcGenSettings.cs | 本人添加；已追踪移动历史 |
| ProcGen/ProcGenSlot.cs | Scripts/Editor/ArtTools/ZXXTools/ProcGen/ProcGenSlot.cs | 本人添加；已追踪移动历史 |
| ProcGen/ProcGenWindow.cs | Scripts/Editor/ArtTools/ZXXTools/ProcGen/ProcGenWindow.cs | 本人添加；已追踪移动历史 |
| ProcGen/SpatialHash.cs | Scripts/Editor/ArtTools/ZXXTools/ProcGen/SpatialHash.cs | 本人添加；已追踪移动历史 |
| README/CaptureHelper.cs | Scripts/Editor/ArtTools/ZXXTools/README/CaptureHelper.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/Attributes/NotifyChangeAttribute.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Attributes/NotifyChangeAttribute.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/Cell.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Cell.cs | 本人添加；已追踪移动历史 |
| RockTools/Scripts/Editor/ContextMenu.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Editor/ContextMenu.cs | 本人添加；已追踪移动历史 |
| RockTools/Scripts/Editor/LogicEditorBase.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Editor/LogicEditorBase.cs | 本人添加；已追踪移动历史 |
| RockTools/Scripts/Editor/LogicEditorType01.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Editor/LogicEditorType01.cs | 本人添加；已追踪移动历史 |
| RockTools/Scripts/Editor/LogicEditorType03.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Editor/LogicEditorType03.cs | 本人添加；已追踪移动历史 |
| RockTools/Scripts/Editor/MeshExporter.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Editor/MeshExporter.cs | 本人添加；已追踪移动历史 |
| RockTools/Scripts/Editor/RockGeneratorEditor.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Editor/RockGeneratorEditor.cs | 本人添加；已追踪移动历史 |
| RockTools/Scripts/Editor/RockGeneratorWindow.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Editor/RockGeneratorWindow.cs | 本人添加；已追踪移动历史 |
| RockTools/Scripts/Editor/RockTools.Editor.asmdef | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Editor/RockTools.Editor.asmdef | 本人添加；已追踪移动历史 |
| RockTools/Scripts/Editor/ToolsMenu.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Editor/ToolsMenu.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/Extensions.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/Extensions.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/LogicBase.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/LogicBase.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/LogicType01.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/LogicType01.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/LogicType02.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/LogicType02.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/LogicType03.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/LogicType03.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/LogicTypeCustom.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/LogicTypeCustom.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/LogicTypeCustomCrystal.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/LogicTypeCustomCrystal.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/MeshBuffer.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/MeshBuffer.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/MeshSplitter.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/MeshSplitter.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/RockBaker.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/RockBaker.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/RockGenerator.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/RockGenerator.cs | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/RockTools.Runtime.asmdef | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/RockTools.Runtime.asmdef | 本人添加；已追踪移动历史 |
| Runtime/RockTools/Scripts/RockType.cs | Scripts/Editor/ArtTools/ZXXTools/RockTools/Scripts/RockType.cs | 本人添加；已追踪移动历史 |
| SelfShadowBaker/SelfShadowBaker.shader | Scripts/Editor/ArtTools/ZXXTools/SelfShadowBaker/SelfShadowBaker.shader | 本人添加；已追踪移动历史 |
| SelfShadowBaker/SelfShadowBakerCore.cs | Scripts/Editor/ArtTools/ZXXTools/SelfShadowBaker/SelfShadowBakerCore.cs | 本人添加；已追踪移动历史 |
| SelfShadowBaker/SelfShadowBakerWindow.cs | Scripts/Editor/ArtTools/ZXXTools/SelfShadowBaker/SelfShadowBakerWindow.cs | 本人添加；已追踪移动历史 |
| ShadingModeToggle/ShadingModeToggle.cs | Scripts/Editor/ArtTools/ZXXTools/ShadingModeToggle/ShadingModeToggle.cs | 本人添加；已追踪移动历史 |
| ShadowBakerTool/Blur_URP.shader | Scripts/Editor/ArtTools/ZXXTools/ShadowBakerTool/Blur_URP.shader | 本人添加；已追踪移动历史 |
| ShadowBakerTool/ShadowBakerCore.cs | Scripts/Editor/ArtTools/ZXXTools/ShadowBakerTool/ShadowBakerCore.cs | 本人添加；已追踪移动历史 |
| ShadowBakerTool/ShadowBakerWindow.cs | Scripts/Editor/ArtTools/ZXXTools/ShadowBakerTool/ShadowBakerWindow.cs | 本人添加；已追踪移动历史 |
| ShadowBakerTool/ShadowDecalGenerator.cs | Scripts/Editor/ArtTools/ZXXTools/ShadowBakerTool/ShadowDecalGenerator.cs | 本人添加；已追踪移动历史 |
| ShadowBakerTool/Silhouette_URP.shader | Scripts/Editor/ArtTools/ZXXTools/ShadowBakerTool/Silhouette_URP.shader | 本人添加；已追踪移动历史 |
| ShadowCastOffTool/ShadowCastOffTool.cs | Scripts/Editor/ArtTools/ZXXTools/ShadowCastOffTool/ShadowCastOffTool.cs | 本人添加；已追踪移动历史 |
| SLGCityEditor/CityBoundaryExtractor.cs | Scripts/Editor/ArtTools/ZXXTools/SLGCityEditor/CityBoundaryExtractor.cs | 本人添加；已追踪移动历史 |
| SLGCityEditor/CityGroundGenerator.cs | Scripts/Editor/ArtTools/ZXXTools/SLGCityEditor/CityGroundGenerator.cs | 本人添加；已追踪移动历史 |
| SLGCityEditor/CityLayoutData.cs | Scripts/Editor/ArtTools/ZXXTools/SLGCityEditor/CityLayoutData.cs | 本人添加；已追踪移动历史 |
| SLGCityEditor/CityWallGenerator.cs | Scripts/Editor/ArtTools/ZXXTools/SLGCityEditor/CityWallGenerator.cs | 本人添加；已追踪移动历史 |
| SLGCityEditor/CityWallPrefabSet.cs | Scripts/Editor/ArtTools/ZXXTools/SLGCityEditor/CityWallPrefabSet.cs | 本人添加；已追踪移动历史 |
| SLGCityEditor/GridData.cs | Scripts/Editor/ArtTools/ZXXTools/SLGCityEditor/GridData.cs | 本人添加；已追踪移动历史 |
| SLGCityEditor/SLGCityEditorWindow.cs | Scripts/Editor/ArtTools/ZXXTools/SLGCityEditor/SLGCityEditorWindow.cs | 本人添加；已追踪移动历史 |
| SLGMapTileCutter/SLGMapTileCutterWindow.cs | Scripts/Editor/ArtTools/ZXXTools/SLGMapTileCutter/SLGMapTileCutterWindow.cs | 本人添加；已追踪移动历史 |
| SLGMapTileCutter/SLGMapTileInnerOutline.shader | Scripts/Editor/ArtTools/ZXXTools/SLGMapTileCutter/SLGMapTileInnerOutline.shader | 本人添加；已追踪移动历史 |
| SLGMapTileCutter/SLGMapTileMapAsset.cs | Scripts/Editor/ArtTools/ZXXTools/SLGMapTileCutter/SLGMapTileMapAsset.cs | 本人添加；已追踪移动历史 |
| SLGTerrainBaker/SLG_TerrainComposite_Baker.shader | Scripts/Editor/ArtTools/ZXXTools/SLGTerrainBaker/SLG_TerrainComposite_Baker.shader | 本人添加；已追踪移动历史 |
| SLGTerrainBaker/SLGTerrainBakeCore.cs | Scripts/Editor/ArtTools/ZXXTools/SLGTerrainBaker/SLGTerrainBakeCore.cs | 本人添加；已追踪移动历史 |
| SLGTerrainBaker/SLGTerrainBakeWindow.cs | Scripts/Editor/ArtTools/ZXXTools/SLGTerrainBaker/SLGTerrainBakeWindow.cs | 本人添加；已追踪移动历史 |
| TransparentScreenshot/CapturePipeline.cs | Scripts/Editor/ArtTools/ZXXTools/TransparentScreenshot/CapturePipeline.cs | 本人添加；已追踪移动历史 |
| TransparentScreenshot/ParticleSequenceRender.cs | Scripts/Editor/ArtTools/ZXXTools/TransparentScreenshot/ParticleSequenceRender.cs | 本人添加；已追踪移动历史 |
| TransparentScreenshot/TransparentScreenshot.cs | Scripts/Editor/ArtTools/ZXXTools/TransparentScreenshot/TransparentScreenshot.cs | 本人添加；已追踪移动历史 |
| ZXXToolBox/GeneratedAssetsManagerWindow.cs | Scripts/Editor/ArtTools/ZXXTools/ZXXToolBox/GeneratedAssetsManagerWindow.cs | 本人添加；已追踪移动历史 |
| ZXXToolBox/OverlapSpreaderWindow.cs | Scripts/Editor/ArtTools/ZXXTools/ZXXToolBox/OverlapSpreaderWindow.cs | 本人添加；已追踪移动历史 |
| ZXXToolBox/RandomMaterialWindow.cs | Scripts/Editor/ArtTools/ZXXTools/ZXXToolBox/RandomMaterialWindow.cs | 本人添加；已追踪移动历史 |
| ZXXToolBox/TransparentQueueSorterWindow.cs | Scripts/Editor/ArtTools/ZXXTools/ZXXToolBox/TransparentQueueSorterWindow.cs | 本人添加；已追踪移动历史 |
| ZXXToolBox/ZXXToolBox.cs | Scripts/Editor/ArtTools/ZXXTools/ZXXToolBox/ZXXToolBox.cs | 本人添加；已追踪移动历史 |
| ZXXToolBox/ZXXToolBoxConfig.cs | Scripts/Editor/ArtTools/ZXXTools/ZXXToolBox/ZXXToolBoxConfig.cs | 本人添加；已追踪移动历史 |
| Runtime/DayChangeTransition/DayChangeTransitionController.cs | Scripts/HotUpdate/ArtCode/Helper/DayChangeTransition/DayChangeTransitionController.cs | 本人添加；已追踪移动历史 |
| Runtime/FogOfWar/Fog Of War.shader | Scripts/HotUpdate/ArtCode/Helper/FogOfWar/Fog Of War.shader | 本人添加；已追踪移动历史 |
| Runtime/FogOfWar/FogGridClickSystem.cs | Scripts/HotUpdate/ArtCode/Helper/FogOfWar/FogGridClickSystem.cs | 本人添加；已追踪移动历史 |
| Runtime/FogOfWar/Manual Cloud Fog.shader | Scripts/HotUpdate/ArtCode/Helper/FogOfWar/Manual Cloud Fog.shader | 本人添加；已追踪移动历史 |
| Runtime/LightCookieScroller/LightCookieScroller.cs | Scripts/HotUpdate/ArtCode/Helper/LightCookieScroller/LightCookieScroller.cs | 本人添加；已追踪移动历史 |
| Runtime/LightCookieScroller/LightCookieScroller.shader | Scripts/HotUpdate/ArtCode/Helper/LightCookieScroller/LightCookieScroller.shader | 本人添加；已追踪移动历史 |

## 排除依据

- AnimationTools、EffectTools、ResourceReview、ShaderTools/ZEffect 等：沿移动前历史核对，主要由 hty、ZhengZhi 等其他提交者添加。
- AssetExportTool 中个人提交仅是已有工具整理或小幅维护，没有将其作为个人新工具收录。
- ASE_River、旧沙盘 ASE 地面/区域 Shader：本人提交为搬运或小幅参数调整，原始添加者为他人。
- Unity.Editor.asmdef：项目共享程序集配置，不属于个人工具源码。
- MiniGameDeathFade.hlsl：其他提交者导入的必要配套头文件；两份个人 ToonOutline Shader 直接 include，保留以避免缺依赖。
- 本次没有新增任何模型或图片文件；仓库已有的文档截图保持原样。

## 依赖

- Unity 2022.3、URP 14；ShaderGraph 文件需要 Shader Graph。
- BuildingCardTool 依赖 spine-unity；第三方库不随本次源码同步上传。
- RockTools.Editor 通过 RockTools.Runtime 程序集名称引用运行时源码；所有 .meta 由各 Unity 项目自行生成，不随仓库分发。编辑器 asmdef 是发布整理时的配置转换，项目原文件保持原样。
- 本次核对了源码复制、依赖文件及 Git 提交范围，未声称完成其他项目中的 Unity 编译或运行测试。

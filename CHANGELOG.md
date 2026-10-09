# Changelog


## [Unreleased] — 2026-10-09

- 从当前项目同步最新个人工具代码，并沿文件迁移历史补齐工具箱外的运行时脚本与 Shader。
- 新增 RockTools、SLGCityEditor、CardGroundBaker 及批量处理工具源码。
- 补齐自定义 Lit 深度透明、建筑面片、河流/瀑布、地形、烘焙光照/植物风动 Shader 及配套 ShaderGUI、HLSL。
- 保留两个程序集 GUID 元数据及 ToonOutline 的必要 include，新增源码位置索引和操作说明。
- 排除其他作者工具、模型、贴图、场景、Prefab 和生成配置。

## [v0.5.0] — 2026-08-24

### 新增工具

- **FBXMeshEditor**：FBX 网格编辑器，场景中直接选择并移动静态模型顶点，支持 Undo（V1 第一阶段）
- **ToonMaterialConverter**：ToonOutline 普通/打包 Shader 双向转换，自动输出 BC 与 N_M_R 贴图
- **GeneratedAssetsManager**：按工具、生命周期和资源类型整理、审计并清理 Assets/Generated 产物

### 新增 Shader

- **ToonOutline_URP_Packed**：卡通描边打包版（BC + N_M_R 合并贴图）
- **ToonTextureConversion**：ToonOutline 贴图转换（Hidden 辅助 Shader）

### 变更

- **ToonOutline_URP**：从 OutlineTool 移至通用 Shaders/ 目录
- **PathBuilder**：新增山脉/河流生成与贴花系统（MountainDecalSet、PathBuilderAuthoringStorage），Spline 系统重构
- **BuildingCardTool**：引入 Spine 骨骼动画支持与预览，图集构建改为延迟调用
- **SLGMapTileCutter**：产物输出目录结构优化（分类到 Models/Materials/Textures/Settings 子目录）
- **ZXXToolBox**：面板注册 3 个新工具

---

## [v0.4.0] — 2026-07-30

### 新增

- **PathBuilder**：路径生成工具，含 PathSpline 曲线编辑子系统，替代 WallBuilderTool
- **SLGMapTileCutter**：沙盘地图地块切割工具，含内描边 Shader（5248 行）
- **SLGTerrainBaker**：沙盘地形烘焙工具，合成贴图生成

### 新增 Shader

- **BuildingCard_URP**：建筑面片图集 URP Shader
- **SLG_TerrainBlend_Lite**：SLG 地形混合（轻量版）
- **SLG_TerrainBlend_Mobile**：SLG 地形混合（移动版）
- **SLGMapTileInnerOutline**：沙盘地块内描边
- **SLG_TerrainComposite_Baker**：地形合成烘焙

### 变更

- **FogOfWarTool**：战争迷雾边缘控制优化
- **MeasureTool**：测量工具功能增强
- **Fog Of War.shader**：迷雾 Shader 边缘控制
- **FogGridClickSystem**：迷雾点击交互优化
- **ZXXToolBox**：面板配置更新，注册新工具

### 移除

- **WallBuilderTool**：被 PathBuilder 替代

---

## [v0.3.1] — 2026-07-15

### 新增

- **面板优化**：ZXXToolBox 面板交互细节增强
- **工具截图参考**：新增 19 个工具面板操作截图

### 变更

- **.gitignore**：忽略 ProcGenSettings.asset（工具自动生成配置）

---

## [v0.3.0] — 2026-07-15

### 新增

- **BuildingCardTool 图集面板**：AtlasPanel 批量管理多张建筑面片，渲染顺序优化
- **全工具 Tooltip**：Content() 双语提示，所有按钮/标签带中文说明

### 变更

- **ZXXToolBox 分类重构**：生成工具 → 场景搭建 / 资源制作 / 视觉烘焙
- **英文注释中文化**：BuildingCardTool / SelfShadowBaker / ShadowBaker 英文 XML doc 改为中文

---

## [v0.2.0] — 2026-07-14

### 新增

- **DayChangeTransitionTool**：昼夜变化过渡编辑器工具 + Runtime Controller
- **FogOfWarTool**：战争迷雾编辑器工具 + Runtime ClickSystem + Fog Shader
- **BuildingCardTool**：建筑面片预制体批量生成

### 变更

- **OutlineTool**：`Outline_URP` → `ToonOutline_URP`，升级为卡通渲染描边
- **ZXXToolBox**：统一所有工具入口到面板，移除分散的 `[MenuItem]`
- **GitPathNavigator**：新增 `Ctrl+G` 快捷键
- **ShadowCastOffTool**：新增 `GameObject/Cast Shadows OFF` 右键菜单
- **PrefabRootNormalizer**：新增 `Ctrl+Shift+N` 快捷键
- **TransparentScreenshot**：新增 `Ctrl+Shift+X` 快捷键
- **ZXXToolBoxConfig**：移除 `CreateAssetMenu`
- **SimpleLit_BottomFade_Wave**：水体/地面优化

### 新增 Shader

- **StylizedRiver**：风格化河流渲染
- **ToonOutline_URP**：卡通渲染描边（替代 `Outline_URP`）

---

## [v0.1.0] — 2026-07-02

### 首次发布

- **21 个编辑器工具**：ZXXToolBox（主面板）及全套工具集
- **9 个 Shader**：阴影、植被、水面、描边、模糊、剪影、迷雾、Light Cookie 等
- **2 个运行时组件**：DayChangeTransitionController、FogGridClickSystem


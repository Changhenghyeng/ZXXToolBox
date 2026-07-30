# Changelog

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

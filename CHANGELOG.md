# Changelog

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

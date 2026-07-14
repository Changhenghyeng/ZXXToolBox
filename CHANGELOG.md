# Changelog

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
